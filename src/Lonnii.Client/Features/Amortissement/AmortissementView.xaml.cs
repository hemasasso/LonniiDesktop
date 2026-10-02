using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Amortissement;

/// <summary>
/// Amortissements: the group's fixed assets, their depreciation schedules, and their sale or
/// scrapping. Mirrors Lonnii Business's Amortissement.jsx against the same
/// <c>immobilisations</c> and <c>amortissement_echeances</c> tables.
/// </summary>
public partial class AmortissementView : UserControl
{
    private readonly AppSession _session;

    private readonly bool _canAdd;
    private readonly bool _canEdit;
    private readonly bool _canDelete;
    private readonly bool _canCeder;
    private readonly bool _canExport;

    private AmortissementListResponse? _data;
    private bool _ready;

    private sealed record Choice(string? Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ImmoRow(ImmobilisationDto Immo, Brush StatutBrush, Visibility CanEdit, Visibility CanCeder, Visibility CanDelete)
    {
        public string Nom => Immo.Nom;
        public string Categorie => CategoriesImmobilisation.Label(Immo.Categorie);
        public string DateAcquisition => Immo.DateAcquisition.ToString("dd/MM/yyyy");
        public string Valeur => Money.FormatPlain(Immo.ValeurAcquisition);
        public string Duree => Immo.DureeAmortissement == 1 ? "1 an" : $"{Immo.DureeAmortissement} ans";
        public string Methode => MethodesAmortissement.Label(Immo.MethodeAmortissement);
        public string Cumule => Money.FormatPlain(Immo.AmortissementCumule);
        public string Vnc => Money.FormatPlain(Immo.ValeurNetteComptable);
        public string Statut => ImmobilisationDetailDialog.Statut(Immo).Label;
    }

    public AmortissementView(AppSession session)
    {
        _session = session;
        _canAdd = _session.Can(Priv.Gestion.AddImmobilisation);
        _canEdit = _session.Can(Priv.Gestion.EditImmobilisation);
        _canDelete = _session.Can(Priv.Gestion.DeleteImmobilisation);
        _canCeder = _session.Can(Priv.Gestion.CederImmobilisation);
        _canExport = _session.Can(Priv.Gestion.ExportAmortissement);
        InitializeComponent();

        SubtitleText.Text = $"{_session.Groupe?.Nom} — immobilisations et tableaux d'amortissement";
        AddButton.Visibility = _canAdd ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Visibility = _canExport ? Visibility.Visible : Visibility.Collapsed;

        CategorieFilterCombo.ItemsSource = new[] { new Choice(null, "Toutes catégories") }
            .Concat(CategoriesImmobilisation.All.Select(c => new Choice(c, CategoriesImmobilisation.Label(c)))).ToList();
        CategorieFilterCombo.SelectedIndex = 0;

        StatutFilterCombo.ItemsSource = new List<Choice>
        {
            new(null, "Tous statuts"),
            new(StatutsImmobilisation.Actif, "Actif"),
            new(StatutsImmobilisation.TotalementAmorti, "Totalement amorti"),
            new(StatutsImmobilisation.Cede, "Cédé"),
            new(StatutsImmobilisation.Reforme, "Réformé"),
        };
        StatutFilterCombo.SelectedIndex = 0;

        ThemeManager.Changed += (_, _) => Render();

        Loaded += async (_, _) =>
        {
            if (_ready) return;
            _ready = true;
            await LoadAsync();
        };
    }

    // --- Loading ---

    private async Task LoadAsync()
    {
        try
        {
            _data = await _session.Api.GetImmobilisationsAsync();
            Render();
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Filters_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) Render();
    }

    private IEnumerable<ImmobilisationDto> Filtered()
    {
        if (_data is null) return [];

        var search = SearchBox.Text.Trim();
        var categorie = (CategorieFilterCombo.SelectedItem as Choice)?.Value;
        var statut = (StatutFilterCombo.SelectedItem as Choice)?.Value;

        return _data.Immobilisations.Where(i =>
            (categorie is null || i.Categorie == categorie)
            && (statut is null
                || (statut == StatutsImmobilisation.TotalementAmorti ? i.TotalementAmorti
                    : statut == StatutsImmobilisation.Actif ? i.Statut == statut && !i.TotalementAmorti
                    : i.Statut == statut))
            && (search.Length == 0
                || i.Nom.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || (i.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (i.NumeroInventaire?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (i.Localisation?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false)));
    }

    private void Render()
    {
        if (_data is null) return;
        RenderTiles(_data.Stats);

        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        var rows = Filtered().Select(i =>
        {
            var actif = i.Statut == StatutsImmobilisation.Actif;
            return new ImmoRow(i, (Brush)FindResource(ImmobilisationDetailDialog.Statut(i).BrushKey),
                Show(_canEdit && actif), Show(_canCeder && actif), Show(_canDelete));
        }).ToList();

        ImmosGrid.ItemsSource = rows;
        EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _data.Immobilisations.Count == 0
            ? "Aucune immobilisation enregistrée."
            : "Aucune immobilisation ne correspond aux filtres.";

        var brut = rows.Sum(r => r.Immo.ValeurAcquisition);
        var vnc = rows.Sum(r => r.Immo.ValeurNetteComptable);
        CountText.Text = rows.Count == 0 ? string.Empty
            : $"{rows.Count} immobilisation{(rows.Count > 1 ? "s" : "")} — valeur brute {Money.Format(brut)} · VNC {Money.Format(vnc)}";
    }

    private void RenderTiles(AmortissementStatsDto s)
    {
        TilesPanel.Children.Clear();
        TilesPanel.Children.Add(Tile("Immobilisations actives", s.TotalImmobilisations.ToString(), "Accent"));
        TilesPanel.Children.Add(Tile("Valeur brute", Money.Format(s.TotalValeurAcquisition), "TextPrimary"));
        TilesPanel.Children.Add(Tile($"Dotation {s.Annee}", Money.Format(s.DotationAnneeCourante), "Warning",
            "Charge d'amortissement de l'année"));
        TilesPanel.Children.Add(Tile("Amortissements cumulés", Money.Format(s.AmortissementCumule), "Danger",
            $"Au 31/12/{s.Annee}"));
        TilesPanel.Children.Add(Tile("Valeur nette comptable", Money.Format(s.ValeurNetteComptable), "Success",
            s.TotalValeurAcquisition > 0 ? $"{s.ValeurNetteComptable / s.TotalValeurAcquisition * 100m:0} % de la valeur brute" : null));
    }

    private Border Tile(string label, string value, string colorKey, string? sub = null)
    {
        var stack = new StackPanel();

        var labelText = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(labelText);

        var valueText = new TextBlock
        {
            Text = value, FontSize = 18, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value,
        };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        stack.Children.Add(valueText);

        if (sub is not null)
        {
            var subText = new TextBlock { Text = sub, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
            subText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(subText);
        }

        var border = new Border
        {
            Width = 205, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        return border;
    }

    // --- Actions ---

    private Window Owner => Window.GetWindow(this)!;

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ImmobilisationDialog(null) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.CreateImmobilisationAsync(request);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Nouvelle immobilisation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Simulate_Click(object sender, RoutedEventArgs e) =>
        new ImmobilisationDialog(null, simulation: true) { Owner = Owner }.ShowDialog();

    private async void View_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is ImmoRow row) await ViewAsync(row.Immo);
    }

    private async void ImmosGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ImmosGrid.SelectedItem is ImmoRow row) await ViewAsync(row.Immo);
    }

    private async Task ViewAsync(ImmobilisationDto immo)
    {
        try
        {
            var details = await _session.Api.GetImmobilisationAsync(immo.Id);
            new ImmobilisationDetailDialog(details, _canExport, _session.Groupe?.Nom) { Owner = Owner }.ShowDialog();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ImmoRow row) return;

        var dialog = new ImmobilisationDialog(row.Immo) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.UpdateImmobilisationAsync(row.Immo.Id, request);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Modifier l'immobilisation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Ceder_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ImmoRow row) return;

        var dialog = new CederImmobilisationDialog(row.Immo) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.CederImmobilisationAsync(row.Immo.Id, request);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Céder / Réformer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not ImmoRow row) return;

        var confirm = MessageBox.Show(Owner,
            $"Supprimer « {row.Nom} » et son tableau d'amortissement ? Cette action est irréversible.\n\n"
            + "Pour une immobilisation vendue ou mise au rebut, utilisez plutôt « Céder / Réformer » : elle disparaît "
            + "alors du bilan sans effacer son historique.",
            "Supprimer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.DeleteImmobilisationAsync(row.Immo.Id);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Supprimer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var rows = Filtered().ToList();
        if (_data is null || rows.Count == 0)
        {
            MessageBox.Show(Owner, "Aucune immobilisation à exporter.", "Exporter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupName = _session.Groupe?.Nom ?? "espace";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"amortissements_{ImmobilisationDetailDialog.Slug(groupName)}_{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "Fichier CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(Owner) != true) return;

        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        var s = _data.Stats;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"TABLEAU DES IMMOBILISATIONS - {groupName.ToUpperInvariant()}");
        sb.AppendLine($"Date d'export;{DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("=== RÉSUMÉ ===");
        sb.AppendLine($"Immobilisations actives;{s.TotalImmobilisations}");
        sb.AppendLine($"Valeur brute;{Money.FormatPlain(s.TotalValeurAcquisition, 2)}");
        sb.AppendLine($"Dotation {s.Annee};{Money.FormatPlain(s.DotationAnneeCourante, 2)}");
        sb.AppendLine($"Amortissements cumulés;{Money.FormatPlain(s.AmortissementCumule, 2)}");
        sb.AppendLine($"VNC totale;{Money.FormatPlain(s.ValeurNetteComptable, 2)}");
        sb.AppendLine();
        sb.AppendLine("=== IMMOBILISATIONS ===");
        sb.AppendLine("Nom;Catégorie;Statut;N° inventaire;Date acquisition;Valeur acquisition;Valeur résiduelle;Durée (ans);Méthode;"
                      + $"Dotation {s.Annee};Amortissement cumulé;VNC;Date sortie;Prix de cession");

        foreach (var i in rows)
        {
            sb.AppendLine(string.Join(';',
                Csv(i.Nom), CategoriesImmobilisation.Label(i.Categorie), ImmobilisationDetailDialog.Statut(i).Label,
                Csv(i.NumeroInventaire ?? ""), i.DateAcquisition.ToString("dd/MM/yyyy"),
                Money.FormatPlain(i.ValeurAcquisition, 2), Money.FormatPlain(i.ValeurResiduelle, 2),
                i.DureeAmortissement, MethodesAmortissement.Label(i.MethodeAmortissement),
                Money.FormatPlain(i.DotationAnneeCourante, 2), Money.FormatPlain(i.AmortissementCumule, 2),
                Money.FormatPlain(i.ValeurNetteComptable, 2),
                i.DateCession?.ToString("dd/MM/yyyy") ?? "",
                i.ValeurCession is { } v && i.Statut == StatutsImmobilisation.Cede ? Money.FormatPlain(v, 2) : ""));
        }

        try
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
        }
        catch (System.IO.IOException ex)
        {
            ShowMessage($"Erreur lors de l'export : {ex.Message}");
        }
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;
}
