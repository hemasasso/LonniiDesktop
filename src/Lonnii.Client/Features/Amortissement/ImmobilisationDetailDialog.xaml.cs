using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Amortissement;

/// <summary>An asset's identity card and its full tableau d'amortissement, this year's line
/// highlighted. For a sold or scrapped asset it also shows the book value at the exit and the
/// resulting gain or loss; the years after the exit are greyed out, since they are no longer
/// charged.</summary>
public partial class ImmobilisationDetailDialog : Window
{
    private readonly ImmobilisationDetailsResponse _details;
    private readonly string? _groupName;

    public ImmobilisationDetailDialog(ImmobilisationDetailsResponse details, bool canExport, string? groupName)
    {
        _details = details;
        _groupName = groupName;
        InitializeComponent();

        var i = details.Immobilisation;
        HeaderText.Text = i.Nom;
        HeaderHint.Text = string.Join(" · ", new[]
        {
            CategoriesImmobilisation.Label(i.Categorie),
            i.NumeroInventaire is { } inv ? $"Inventaire {inv}" : null,
            i.Localisation,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var (label, brushKey) = Statut(i);
        StatutText.Text = label;
        StatutText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        StatutBadge.SetResourceReference(Border.BorderBrushProperty, brushKey);
        StatutBadge.BorderThickness = new Thickness(1);

        Info("Valeur d'acquisition", Money.Format(i.ValeurAcquisition));
        Info("Valeur résiduelle", Money.Format(i.ValeurResiduelle));
        Info("Acquise le", i.DateAcquisition.ToString("dd/MM/yyyy"));
        if (i.DateMiseEnService is { } service) Info("En service le", service.ToString("dd/MM/yyyy"));
        Info("Méthode", MethodesAmortissement.Label(i.MethodeAmortissement)
                        + (i.MethodeAmortissement == MethodesAmortissement.Degressif
                            ? $" (coef. {(i.TauxDegressif ?? AmortissementCalculator.CoefficientDegressif(i.DureeAmortissement)):0.##})"
                            : string.Empty));
        Info("Durée", i.DureeAmortissement == 1 ? "1 an" : $"{i.DureeAmortissement} ans");
        Info($"Dotation {DateTime.Now.Year}", Money.Format(i.DotationAnneeCourante));
        Info("Amortissement cumulé", Money.Format(i.AmortissementCumule));
        Info("Valeur nette comptable", Money.Format(i.ValeurNetteComptable));
        if (i.Fournisseur is { } fournisseur) Info("Fournisseur", fournisseur);
        if (i.NumeroFacture is { } facture) Info("N° facture", facture);
        if (i.CreatedByName is { } by) Info("Enregistrée par", by);

        int? derniereAnnee = null;
        if (StatutsImmobilisation.IsSortie(i.Statut) && i.DateCession is { } sortie)
        {
            derniereAnnee = sortie.Year;
            SortiePanel.Visibility = Visibility.Visible;

            var gain = details.PlusMoinsValue ?? 0m;
            var texte = i.Statut == StatutsImmobilisation.Cede
                ? $"Cédée le {sortie:dd/MM/yyyy} pour {Money.Format(i.ValeurCession ?? 0m)}. "
                : $"Réformée le {sortie:dd/MM/yyyy}. ";
            texte += $"Valeur nette comptable à la sortie : {Money.Format(details.VncALaSortie ?? 0m)} — "
                     + (gain >= 0 ? $"plus-value de {Money.Format(gain)}." : $"moins-value de {Money.Format(-gain)}.");
            if (!string.IsNullOrWhiteSpace(i.MotifSortie)) texte += $"\nMotif : {i.MotifSortie}";
            texte += $"\nLes dotations postérieures à {sortie.Year} ne sont plus comptées.";
            SortieText.Text = texte;
        }

        ScheduleGrid.ItemsSource = EcheanceRow.From(details.Echeances, derniereAnnee);
        ExportButton.Visibility = canExport ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The label and palette key an asset's status is shown with - also used by the
    /// module's list.</summary>
    internal static (string Label, string BrushKey) Statut(ImmobilisationDto i) =>
        i.TotalementAmorti
            ? ("Totalement amorti", "Accent")
            : StatutsImmobilisation.Normalise(i.Statut) switch
            {
                StatutsImmobilisation.Actif => ("Actif", "Success"),
                StatutsImmobilisation.Cede => ("Cédé", "Warning"),
                StatutsImmobilisation.Reforme => ("Réformé", "Danger"),
                var other => (StatutsImmobilisation.Label(other), "TextSecondary"),
            };

    private void Info(string label, string value)
    {
        var stack = new StackPanel { Width = 190, Margin = new Thickness(0, 0, 12, 10) };
        var l = new TextBlock { Text = label, FontSize = 11 };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(l);
        stack.Children.Add(new TextBlock
        {
            Text = value, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value,
        });
        InfoPanel.Children.Add(stack);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var i = _details.Immobilisation;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"amortissement_{Slug(i.Nom)}_{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "Fichier CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"TABLEAU D'AMORTISSEMENT - {(_groupName ?? "").ToUpperInvariant()}");
        sb.AppendLine($"Immobilisation;{Csv(i.Nom)}");
        sb.AppendLine($"Catégorie;{CategoriesImmobilisation.Label(i.Categorie)}");
        sb.AppendLine($"Valeur d'acquisition;{Money.FormatPlain(i.ValeurAcquisition, 2)}");
        sb.AppendLine($"Valeur résiduelle;{Money.FormatPlain(i.ValeurResiduelle, 2)}");
        sb.AppendLine($"Méthode;{MethodesAmortissement.Label(i.MethodeAmortissement)}");
        sb.AppendLine($"Durée (ans);{i.DureeAmortissement}");
        sb.AppendLine($"Date d'export;{DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("Année;N°;Début;Fin;VNC début;Dotation;Amortissement cumulé;VNC fin");
        foreach (var ech in _details.Echeances)
        {
            sb.AppendLine(string.Join(';',
                ech.Annee, ech.NumeroAnnee, ech.DateDebut.ToString("dd/MM/yyyy"), ech.DateFin.ToString("dd/MM/yyyy"),
                Money.FormatPlain(ech.ValeurDebutPeriode, 2), Money.FormatPlain(ech.DotationAnnuelle, 2),
                Money.FormatPlain(ech.AmortissementCumule, 2), Money.FormatPlain(ech.ValeurNetteComptable, 2)));
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
        }
        catch (System.IO.IOException ex)
        {
            MessageBox.Show(this, $"Erreur lors de l'export : {ex.Message}", "Exporter", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal static string Slug(string value) =>
        string.Concat(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
