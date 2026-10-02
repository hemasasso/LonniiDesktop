using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Amortissement;

/// <summary>
/// Creates or edits a fixed asset, with its tableau d'amortissement recomputed on every
/// keystroke beside the form - the same calculator the server stores the schedule with, so
/// what is previewed is what gets saved.
///
/// Opened with <c>simulation: true</c> it is the source app's "Simuler" screen: only the
/// figures that drive the schedule, nothing saved.
/// </summary>
public partial class ImmobilisationDialog : Window
{
    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly bool _simulation;

    public SaveImmobilisationRequest? Result { get; private set; }

    public ImmobilisationDialog(ImmobilisationDto? existing, bool simulation = false)
    {
        _simulation = simulation;
        InitializeComponent();

        CategorieCombo.ItemsSource = CategoriesImmobilisation.All
            .Select(c => new Choice<string>(c, CategoriesImmobilisation.Label(c))).ToList();
        MethodeCombo.ItemsSource = MethodesAmortissement.All
            .Select(m => new Choice<string>(m, MethodesAmortissement.Label(m))).ToList();
        DureeCombo.ItemsSource = Enumerable.Range(1, AmortissementCalculator.DureeMaximale)
            .Select(n => new Choice<int>(n, n == 1 ? "1 an" : $"{n} ans")).ToList();

        CategorieCombo.SelectedIndex = 0;
        MethodeCombo.SelectedIndex = 0;
        DureeCombo.SelectedIndex = 4;
        AcquisitionPicker.SelectedDate = DateTime.Today;

        if (simulation)
        {
            Title = "Simulation";
            HeaderText.Text = "Simuler un amortissement";
            HeaderHint.Text = "Testez une valeur, une durée et une méthode avant d'enregistrer l'immobilisation. Rien n'est enregistré.";
            IdentityPanel.Visibility = Visibility.Collapsed;
            DetailsPanel.Visibility = Visibility.Collapsed;
            ServicePanel.Visibility = Visibility.Collapsed;
            DatesGrid.Margin = new Thickness(0);
            ValeurLabel.Text = "Valeur d'acquisition *";
            SaveButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Fermer";
        }
        else if (existing is null)
        {
            HeaderText.Text = "Nouvelle immobilisation";
            HeaderHint.Text = "Machine, véhicule, mobilier, matériel informatique… Le tableau d'amortissement se calcule à droite.";
        }
        else
        {
            HeaderText.Text = "Modifier l'immobilisation";
            HeaderHint.Text = $"{existing.Nom} — le tableau d'amortissement sera recalculé à l'enregistrement.";
            Fill(existing);
        }

        UpdateCoefficientChoices();

        // Wired after the initial values are in, so the preview is not rebuilt a dozen times
        // while the constructor fills the form.
        foreach (var box in new[] { ValeurBox, ResiduelleBox })
        {
            box.TextChanged += (_, _) => Recompute();
            box.LostFocus += (_, _) =>
            {
                if (Money.TryParse(box.Text, out decimal value)) box.Text = Money.FormatPlain(value);
            };
        }
        AcquisitionPicker.SelectedDateChanged += (_, _) => Recompute();
        ServicePicker.SelectedDateChanged += (_, _) => Recompute();
        DureeCombo.SelectionChanged += (_, _) => { UpdateCoefficientChoices(); Recompute(); };
        MethodeCombo.SelectionChanged += (_, _) => { UpdateCoefficientChoices(); Recompute(); };
        CoefficientCombo.SelectionChanged += (_, _) => Recompute();

        Recompute();
        Loaded += (_, _) =>
        {
            var first = simulation ? (Control)ValeurBox : NomBox;
            first.Focus();
            if (first is TextBox text) text.SelectAll();
        };
    }

    private void Fill(ImmobilisationDto e)
    {
        NomBox.Text = e.Nom;
        CategorieCombo.SelectedItem = CategorieCombo.Items.Cast<Choice<string>>().FirstOrDefault(c => c.Value == e.Categorie)
                                      ?? CategorieCombo.SelectedItem;
        AcquisitionPicker.SelectedDate = e.DateAcquisition.ToDateTime(TimeOnly.MinValue);
        ServicePicker.SelectedDate = e.DateMiseEnService?.ToDateTime(TimeOnly.MinValue);
        ValeurBox.Text = Money.FormatPlain(e.ValeurAcquisition);
        ResiduelleBox.Text = Money.FormatPlain(e.ValeurResiduelle);
        DureeCombo.SelectedItem = DureeCombo.Items.Cast<Choice<int>>().FirstOrDefault(d => d.Value == e.DureeAmortissement)
                                  ?? DureeCombo.SelectedItem;
        MethodeCombo.SelectedItem = MethodeCombo.Items.Cast<Choice<string>>().FirstOrDefault(m => m.Value == e.MethodeAmortissement)
                                    ?? MethodeCombo.SelectedItem;
        InventaireBox.Text = e.NumeroInventaire ?? string.Empty;
        LocalisationBox.Text = e.Localisation ?? string.Empty;
        FournisseurBox.Text = e.Fournisseur ?? string.Empty;
        FactureBox.Text = e.NumeroFacture ?? string.Empty;
        DescriptionBox.Text = e.Description ?? string.Empty;
        NotesBox.Text = e.Notes ?? string.Empty;

        UpdateCoefficientChoices();
        if (e.TauxDegressif is { } taux)
        {
            var match = CoefficientCombo.Items.Cast<Choice<decimal?>>().FirstOrDefault(c => c.Value == taux);
            if (match is null)
            {
                // A value the web app stored that is not on the list: keep it selectable.
                match = new Choice<decimal?>(taux, taux.ToString("0.##"));
                ((List<Choice<decimal?>>)CoefficientCombo.ItemsSource).Add(match);
                CoefficientCombo.Items.Refresh();
            }
            CoefficientCombo.SelectedItem = match;
        }
    }

    private string Methode => (MethodeCombo.SelectedItem as Choice<string>)?.Value ?? MethodesAmortissement.Lineaire;
    private int Duree => (DureeCombo.SelectedItem as Choice<int>)?.Value ?? 5;
    private decimal? Coefficient => (CoefficientCombo.SelectedItem as Choice<decimal?>)?.Value;

    /// <summary>The common fiscal coefficients, and "Automatique" showing what the duration
    /// gives - picked, not typed, like the other figures here.</summary>
    private void UpdateCoefficientChoices()
    {
        var isDegressif = Methode == MethodesAmortissement.Degressif;
        CoefficientPanel.Visibility = isDegressif ? Visibility.Visible : Visibility.Collapsed;
        MethodeHint.Text = MethodesAmortissement.Description(Methode)
                           + (Methode == MethodesAmortissement.Degressif ? " — la valeur résiduelle n'est pas prise en compte." : ".");

        var previous = Coefficient;
        var auto = AmortissementCalculator.CoefficientDegressif(Duree);
        var choices = new List<Choice<decimal?>> { new(null, $"Automatique ({auto:0.##})") };
        choices.AddRange(new[] { 1.25m, 1.5m, 1.75m, 2m, 2.25m, 2.5m, 3m }.Select(c => new Choice<decimal?>(c, c.ToString("0.##"))));

        CoefficientCombo.ItemsSource = choices;
        CoefficientCombo.SelectedItem = choices.FirstOrDefault(c => c.Value == previous) ?? choices[0];
    }

    private void Recompute()
    {
        SummaryPanel.Children.Clear();

        if (!Money.TryParse(ValeurBox.Text, out decimal valeur) || valeur <= 0
            || AcquisitionPicker.SelectedDate is not { } acquisition)
        {
            ShowPreview([]);
            return;
        }

        var residuelle = Money.TryParse(ResiduelleBox.Text, out decimal r) ? r : 0m;
        var coefficient = Methode == MethodesAmortissement.Degressif ? Coefficient : null;

        if (AmortissementCalculator.Valider(valeur, residuelle, Duree, Methode, coefficient) is { } error)
        {
            ErrorText.Text = error;
            ShowPreview([]);
            return;
        }
        ErrorText.Text = string.Empty;

        var service = _simulation ? null : ServicePicker.SelectedDate is { } s ? DateOnly.FromDateTime(s) : (DateOnly?)null;
        var echeances = AmortissementCalculator.Calculer(
            valeur, residuelle, Duree, Methode, coefficient, DateOnly.FromDateTime(acquisition), service);

        ShowPreview(echeances);
        if (echeances.Count == 0) return;

        var amortissable = Methode == MethodesAmortissement.Degressif ? valeur : valeur - residuelle;
        Summary("Base amortissable", Money.Format(amortissable));
        Summary(Methode == MethodesAmortissement.Lineaire ? "Dotation annuelle" : "1re dotation",
            Money.Format(Methode == MethodesAmortissement.Lineaire ? amortissable / Duree : echeances[0].DotationAnnuelle));
        Summary("Taux", Methode switch
        {
            MethodesAmortissement.Degressif => $"{(coefficient ?? AmortissementCalculator.CoefficientDegressif(Duree)) / Duree * 100m:0.##} %",
            _ => $"{100m / Duree:0.##} %",
        });
        Summary("Période", $"{echeances[0].Annee} – {echeances[^1].Annee}");
    }

    private void ShowPreview(IReadOnlyList<AmortissementEcheanceDto> echeances)
    {
        PreviewGrid.ItemsSource = EcheanceRow.From(echeances);
        PreviewEmptyText.Visibility = echeances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Summary(string label, string value)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 22, 6) };
        var l = new TextBlock { Text = label, FontSize = 11 };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(l);
        stack.Children.Add(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold });
        SummaryPanel.Children.Add(stack);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var nom = NomBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(nom))
        {
            ErrorText.Text = "Le nom est requis.";
            NomBox.Focus();
            return;
        }

        if (AcquisitionPicker.SelectedDate is not { } acquisition)
        {
            ErrorText.Text = "La date d'acquisition est requise.";
            AcquisitionPicker.Focus();
            return;
        }

        if (!Money.TryParse(ValeurBox.Text, out decimal valeur) || valeur <= 0)
        {
            ErrorText.Text = "Indiquez une valeur d'acquisition valide.";
            ValeurBox.Focus();
            return;
        }

        var residuelle = 0m;
        if (!string.IsNullOrWhiteSpace(ResiduelleBox.Text) && !Money.TryParse(ResiduelleBox.Text, out residuelle))
        {
            ErrorText.Text = "Valeur résiduelle invalide.";
            ResiduelleBox.Focus();
            return;
        }

        DateOnly? service = ServicePicker.SelectedDate is { } s ? DateOnly.FromDateTime(s) : null;
        if (service is { } mise && mise < DateOnly.FromDateTime(acquisition))
        {
            ErrorText.Text = "La mise en service ne peut pas précéder l'acquisition.";
            ServicePicker.Focus();
            return;
        }

        var coefficient = Methode == MethodesAmortissement.Degressif ? Coefficient : null;
        if (AmortissementCalculator.Valider(valeur, residuelle, Duree, Methode, coefficient) is { } error)
        {
            ErrorText.Text = error;
            return;
        }

        static string? Text(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

        Result = new SaveImmobilisationRequest(
            nom,
            ((Choice<string>)CategorieCombo.SelectedItem).Value,
            DateOnly.FromDateTime(acquisition),
            valeur,
            residuelle,
            Duree,
            Methode,
            coefficient,
            service,
            Text(DescriptionBox),
            Text(InventaireBox),
            Text(LocalisationBox),
            Text(FournisseurBox),
            Text(FactureBox),
            Text(NotesBox));
        DialogResult = true;
    }
}
