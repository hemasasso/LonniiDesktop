using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Client.Views.Dialogs;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Views.Modules;

/// <summary>
/// Bilan &amp; Résultat: the balance sheet and the compte de résultat for one exercice, the
/// manual écritures behind them, and the chart of accounts. Mirrors Lonnii Business's
/// Bilan.jsx. Both statements are fed automatically from the other modules (sales, stock,
/// charges, fixed assets) - see BilanEndpoints for exactly how - and an account shows "auto"
/// where part of its amount came from there.
/// </summary>
public partial class BilanView : UserControl
{
    private readonly AppSession _session;

    private readonly bool _canViewResultat;
    private readonly bool _canAddEcriture;
    private readonly bool _canEditEcriture;
    private readonly bool _canDeleteEcriture;
    private readonly bool _canManageComptes;
    private readonly bool _canExport;
    private readonly bool _canEditResultat;

    private const string ModuleKey = "bilan";

    private string _activeTab = "bilan";
    private int _annee = DateTime.Now.Year;
    private bool _ready;

    private BilanResponse? _bilan;
    private ResultatResponse? _resultat;
    private BilanComptesResponse? _comptes;
    private List<BilanEcritureDto> _ecritures = [];

    private sealed record Choice(int? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record EcritureRow(BilanEcritureDto Ecriture, Visibility CanEdit, Visibility CanDelete)
    {
        public string Date => Ecriture.DateEcriture.ToString("dd/MM/yyyy");
        public string Compte => $"{Ecriture.NumeroCompte} — {Ecriture.CompteLibelle}";
        public string Libelle => Ecriture.Libelle;
        public string Debit => Ecriture.MontantDebit > 0 ? Money.FormatPlain(Ecriture.MontantDebit) : "—";
        public string Credit => Ecriture.MontantCredit > 0 ? Money.FormatPlain(Ecriture.MontantCredit) : "—";
        public string Reference => Ecriture.Reference ?? "—";
        public string? CreatedByName => Ecriture.CreatedByName;
    }

    private sealed record CompteRow(BilanCompteDto Compte, string TableType, Visibility CanEdit, Visibility CanDelete)
    {
        public string Numero => Compte.NumeroCompte;
        public string Libelle => Compte.Libelle;
        public string Type => TableType == TablesCompte.Resultat
            ? TypesCompteResultat.Label(Compte.TypeCompte)
            : TypesCompteBilan.Label(Compte.TypeCompte);
        public string Systeme => Compte.IsSystem ? "Défaut" : string.Empty;
    }

    public BilanView(AppSession session)
    {
        _session = session;
        _canViewResultat = _session.Can(Priv.Gestion.ViewResultat);
        _canAddEcriture = _session.Can(Priv.Gestion.AddBilanEcriture);
        _canEditEcriture = _session.Can(Priv.Gestion.EditBilanEcriture);
        _canDeleteEcriture = _session.Can(Priv.Gestion.DeleteBilanEcriture);
        _canManageComptes = _session.Can(Priv.Gestion.ManageBilanComptes);
        _canExport = _session.Can(Priv.Gestion.ExportBilan);
        _canEditResultat = _session.Can(Priv.Gestion.EditResultatDonnees);
        InitializeComponent();

        SubtitleText.Text = $"{_session.Groupe?.Nom} — bilan de l'entreprise et compte de résultat";
        AddEcritureButton.Visibility = _canAddEcriture ? Visibility.Visible : Visibility.Collapsed;
        AddCompteButton.Visibility = _canManageComptes ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.Visibility = _canExport ? Visibility.Visible : Visibility.Collapsed;
        ResultatTabButton.Visibility = _canViewResultat ? Visibility.Visible : Visibility.Collapsed;

        var thisYear = DateTime.Now.Year;
        YearCombo.ItemsSource = Enumerable.Range(0, 10).Select(i => thisYear - i).ToList();
        YearCombo.SelectedItem = thisYear;

        // Section colours are resolved when a statement is built, so a theme switch rebuilds it.
        ThemeManager.Changed += (_, _) => RenderActiveTab();

        Loaded += async (_, _) =>
        {
            if (_ready) return;
            _ready = true;

            var saved = UiState.For(_session).Tabs.GetValueOrDefault(ModuleKey);
            var initial = saved is "bilan" or "ecritures" or "comptes" || (saved == "resultat" && _canViewResultat)
                ? saved : "bilan";
            await SetActiveTabAsync(initial);
        };
    }

    private Window Owner => Window.GetWindow(this)!;

    // --- Tabs and year ---

    private async void Tab_Click(object sender, RoutedEventArgs e) =>
        await SetActiveTabAsync(((Button)sender).Tag as string ?? "bilan");

    private async Task SetActiveTabAsync(string tab)
    {
        _activeTab = tab;
        ApplyTabVisuals();

        UiState.For(_session).Tabs[ModuleKey] = tab;
        UiState.Save();

        await LoadActiveTabAsync();
    }

    private void ApplyTabVisuals()
    {
        var accent = (Brush)FindResource("Accent");
        var onAccent = (Brush)FindResource("TextOnAccent");
        var muted = (Brush)FindResource("TextMuted");

        void Apply(Button button, bool active)
        {
            button.Background = active ? accent : Brushes.Transparent;
            button.Foreground = active ? onAccent : muted;
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        Apply(BilanTabButton, _activeTab == "bilan");
        Apply(ResultatTabButton, _activeTab == "resultat");
        Apply(EcrituresTabButton, _activeTab == "ecritures");
        Apply(ComptesTabButton, _activeTab == "comptes");

        BilanPanel.Visibility = _activeTab == "bilan" ? Visibility.Visible : Visibility.Collapsed;
        ResultatPanel.Visibility = _activeTab == "resultat" ? Visibility.Visible : Visibility.Collapsed;
        EcrituresPanel.Visibility = _activeTab == "ecritures" ? Visibility.Visible : Visibility.Collapsed;
        ComptesPanel.Visibility = _activeTab == "comptes" ? Visibility.Visible : Visibility.Collapsed;

        StatusBadge.Visibility = _activeTab is "bilan" or "resultat" ? Visibility.Visible : Visibility.Hidden;
        UpdateStatusBadge();
    }

    private void UpdateStatusBadge()
    {
        var enCours = _annee == DateTime.Now.Year;
        StatusBadgeText.Text = enCours ? $"Provisoire — au {DateTime.Now:dd/MM/yyyy}" : $"Clôturé au 31/12/{_annee}";
        var key = enCours ? "Warning" : "Success";
        StatusBadgeText.SetResourceReference(TextBlock.ForegroundProperty, key);
        StatusBadge.SetResourceReference(Border.BorderBrushProperty, key);
    }

    private async void Year_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (YearCombo.SelectedItem is not int year) return;
        _annee = year;
        if (!_ready) return;

        UpdateStatusBadge();
        await LoadActiveTabAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _comptes = null;
        await LoadActiveTabAsync();
    }

    private async Task LoadActiveTabAsync()
    {
        try
        {
            switch (_activeTab)
            {
                case "bilan":
                    _bilan = await _session.Api.GetBilanAsync(_annee);
                    break;
                case "resultat":
                    _resultat = await _session.Api.GetResultatAsync(_annee);
                    break;
                case "ecritures":
                    await EnsureComptesAsync();
                    await LoadEcrituresAsync();
                    break;
                case "comptes":
                    _comptes = await _session.Api.GetBilanComptesAsync();
                    break;
            }

            RenderActiveTab();
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private void RenderActiveTab()
    {
        switch (_activeTab)
        {
            case "bilan" when _bilan is not null: RenderBilan(_bilan); break;
            case "resultat" when _resultat is not null: RenderResultat(_resultat); break;
            case "ecritures": RenderEcritures(); break;
            case "comptes" when _comptes is not null: RenderComptes(_comptes); break;
        }
    }

    private async Task EnsureComptesAsync()
    {
        if (_comptes is not null) return;
        _comptes = await _session.Api.GetBilanComptesAsync();

        var previous = (EcritureCompteFilter.SelectedItem as Choice)?.Id;
        var choices = new[] { new Choice(null, "Tous les comptes") }
            .Concat(_comptes.BilanComptes.Select(c => new Choice(c.Id, $"{c.NumeroCompte} — {c.Libelle}")))
            .ToList();
        EcritureCompteFilter.ItemsSource = choices;
        EcritureCompteFilter.SelectedItem = choices.FirstOrDefault(c => c.Id == previous) ?? choices[0];
    }

    // --- Bilan ---

    private void RenderBilan(BilanResponse b)
    {
        ActifColumn.Children.Clear();
        PassifColumn.Children.Clear();

        ActifColumn.Children.Add(ColumnTitle("ACTIF", "Accent", "Ce que l'entreprise possède"));
        if (b.Immobilisations.ValeurBrute > 0) ActifColumn.Children.Add(ImmobilisationsCard(b.Immobilisations));
        ActifColumn.Children.Add(Section("Actif immobilisé", "Accent", b.ActifImmobilise, TablesCompte.Bilan));
        ActifColumn.Children.Add(Section("Actif circulant", "Success", b.ActifCirculant, TablesCompte.Bilan));
        ActifColumn.Children.Add(Section("Trésorerie", "Warning", b.TresorerieActif, TablesCompte.Bilan));

        PassifColumn.Children.Add(ColumnTitle("PASSIF", "Danger", "Comment l'entreprise est financée"));
        PassifColumn.Children.Add(Section("Capitaux propres", "Accent", b.CapitauxPropres, TablesCompte.Bilan));
        PassifColumn.Children.Add(Section("Dettes à long terme", "Danger", b.DettesLongTerme, TablesCompte.Bilan));
        PassifColumn.Children.Add(Section("Dettes à court terme", "Warning", b.DettesCourtTerme, TablesCompte.Bilan));
        if (b.TresoreriePassif.Count > 0)
            PassifColumn.Children.Add(Section("Trésorerie passif", "Danger", b.TresoreriePassif, TablesCompte.Bilan));

        TotalActifBorder.Child = TotalBar("TOTAL ACTIF", b.TotalActif, "Accent");
        TotalPassifBorder.Child = TotalBar("TOTAL PASSIF", b.TotalPassif, "Danger");

        var equilibre = b.Ecart == 0;
        var key = equilibre ? "Success" : "Warning";
        EquilibreBanner.SetResourceReference(Border.BorderBrushProperty, key);
        EquilibreBanner.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        EquilibreText.SetResourceReference(TextBlock.ForegroundProperty, key);
        EquilibreText.Text = equilibre
            ? "✓ Bilan équilibré : total actif = total passif."
            : $"⚠ Écart de {Money.Format(Math.Abs(b.Ecart))} ({(b.Ecart > 0 ? "actif supérieur au passif" : "passif supérieur à l'actif")}).";
        EquilibreHint.Text = equilibre
            ? string.Empty
            : "Le stock, les créances clients, les immobilisations et le résultat de l'exercice sont repris automatiquement ; "
              + "la trésorerie (caisse, banque), le capital, les réserves et les dettes se saisissent en écritures. "
              + "Un écart signifie qu'une partie de ces montants n'a pas encore été saisie.";
    }

    private Border ImmobilisationsCard(BilanImmobilisationsDto immo)
    {
        var stack = new StackPanel();
        var title = new TextBlock { Text = "Immobilisations (module Amortissement)", FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) };
        stack.Children.Add(title);
        stack.Children.Add(Line("Valeur brute", Money.FormatPlain(immo.ValeurBrute), "TextPrimary"));
        stack.Children.Add(Line("Amortissements cumulés", "−" + Money.FormatPlain(immo.Amortissements), "Danger"));
        stack.Children.Add(Line("Valeur nette comptable", Money.FormatPlain(immo.ValeurNette), "Accent", bold: true));

        var border = new Border
        {
            Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 12),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        return border;
    }

    // --- Compte de résultat ---

    private void RenderResultat(ResultatResponse r)
    {
        var i = r.Integration;
        IntegrationTitle.Text = $"Données intégrées automatiquement ({r.Annee})";
        IntegrationPanel.Children.Clear();

        IntegrationPanel.Children.Add(AutoTile("Ventes de marchandises", "+" + Money.Format(i.Ventes), "Success", "Module Ventes"));
        IntegrationPanel.Children.Add(AutoTile("Coût des marchandises vendues", "−" + Money.Format(i.CoutMarchandisesVendues), "Danger",
            i.VentesEstimees > 0 ? $"dont {i.VentesEstimees} ligne{(i.VentesEstimees > 1 ? "s" : "")} au coût estimé" : "Au prix d'achat du jour de la vente"));
        IntegrationPanel.Children.Add(AutoTile("Charges", "−" + Money.Format(i.Charges), "Danger",
            i.ChargesAchats > 0 ? $"dont achats {Money.Format(i.ChargesAchats)} (compte 60)" : "Module Charges, réparties par catégorie"));
        IntegrationPanel.Children.Add(AutoTile("Dotations aux amortissements", "−" + Money.Format(i.DotationAmortissement), "Danger", "Module Amortissement"));

        var stockTile = AutoTile("Variation de stocks (information)",
            (i.VariationStocks >= 0 ? "+" : "−") + Money.Format(Math.Abs(i.VariationStocks)), "TextSecondary",
            $"{Money.FormatPlain(i.StockDebut)} → {Money.FormatPlain(i.StockFin)}" + (i.StockDebutSaisi ? string.Empty : " · stock début estimé"));
        if (_canEditResultat)
        {
            var edit = new Button
            {
                Style = (Style)FindResource("SecondaryButton"), Content = "Stock au 1er janvier…", FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3), MinWidth = 0, Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            edit.Click += async (_, _) => await EditStockDebutAsync(i.StockDebut);
            ((StackPanel)stockTile.Child).Children.Add(edit);
        }
        IntegrationPanel.Children.Add(stockTile);

        IntegrationNote.Text =
            "Le coût des marchandises vendues est calculé comme dans Marges, au prix d'achat en vigueur le jour de chaque vente. "
            + "La variation de stocks est donnée pour information et n'entre pas dans le résultat : ce coût tient déjà compte de ce qui est sorti du stock. "
            + "Les produits et charges financiers et exceptionnels se saisissent à la main (crayon).";

        ProduitsColumn.Children.Clear();
        ChargesColumn.Children.Clear();

        ProduitsColumn.Children.Add(ColumnTitle("PRODUITS", "Success", "Ce que l'activité a rapporté"));
        ProduitsColumn.Children.Add(Section("Produits d'exploitation", "Success", r.ProduitsExploitation, TablesCompte.Resultat));
        ProduitsColumn.Children.Add(Section("Produits financiers", "Accent", r.ProduitsFinanciers, TablesCompte.Resultat));
        ProduitsColumn.Children.Add(Section("Produits exceptionnels", "Accent", r.ProduitsExceptionnels, TablesCompte.Resultat));

        ChargesColumn.Children.Add(ColumnTitle("CHARGES", "Danger", "Ce que l'activité a coûté"));
        ChargesColumn.Children.Add(Section("Charges d'exploitation", "Danger", r.ChargesExploitation, TablesCompte.Resultat));
        ChargesColumn.Children.Add(Section("Charges financières", "Warning", r.ChargesFinancieres, TablesCompte.Resultat));
        ChargesColumn.Children.Add(Section("Charges exceptionnelles", "Warning", r.ChargesExceptionnelles, TablesCompte.Resultat));

        TotalProduitsBorder.Child = TotalBar("TOTAL PRODUITS", r.TotalProduits, "Success");
        TotalChargesBorder.Child = TotalBar("TOTAL CHARGES", r.TotalCharges, "Danger");

        SoldesPanel.Children.Clear();
        SoldesPanel.Children.Add(new TextBlock { Text = "Soldes intermédiaires", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        SoldesPanel.Children.Add(Line("Résultat d'exploitation", Signed(r.ResultatExploitation), SignKey(r.ResultatExploitation)));
        SoldesPanel.Children.Add(Line("Résultat financier", Signed(r.ResultatFinancier), SignKey(r.ResultatFinancier)));
        SoldesPanel.Children.Add(Line("Résultat exceptionnel", Signed(r.ResultatExceptionnel), SignKey(r.ResultatExceptionnel)));
        var sep = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
        sep.SetResourceReference(Border.BackgroundProperty, "Border");
        SoldesPanel.Children.Add(sep);
        SoldesPanel.Children.Add(Line("Résultat net de l'exercice", Signed(r.ResultatNet), SignKey(r.ResultatNet), bold: true));

        var benefice = r.ResultatNet >= 0;
        var key = benefice ? "Success" : "Danger";
        NetBanner.SetResourceReference(Border.BorderBrushProperty, key);
        NetBanner.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        NetTitle.Text = benefice ? "BÉNÉFICE NET" : "PERTE NETTE";
        NetTitle.SetResourceReference(TextBlock.ForegroundProperty, key);
        NetValue.Text = Money.Format(Math.Abs(r.ResultatNet));
        NetValue.SetResourceReference(TextBlock.ForegroundProperty, key);
        NetDetail.Text = $"Produits {Money.Format(r.TotalProduits)}  −  Charges {Money.Format(r.TotalCharges)}";
    }

    private async Task EditStockDebutAsync(decimal current)
    {
        var text = PromptDialog.Show(Owner, "Stock au 1er janvier",
            $"Valeur du stock (au prix d'achat) au 1er janvier {_annee}.\nC'est aussi le stock de clôture de {_annee - 1} sur son bilan.",
            Money.FormatPlain(current));
        if (text is null) return;

        if (!Money.TryParse(text, out decimal value) || value < 0)
        {
            MessageBox.Show(Owner, "Montant invalide.", "Stock", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _session.Api.SetStockDebutAsync(new StockSnapshotRequest(_annee, value));
            await LoadActiveTabAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Stock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task EditManualAmountAsync(BilanCompteDto compte)
    {
        var text = PromptDialog.Show(Owner, "Montant",
            $"{compte.NumeroCompte} — {compte.Libelle}\n\nCe montant n'est pas propre à un exercice : il s'affiche chaque année jusqu'à ce qu'il soit modifié.",
            Money.FormatPlain(compte.SoldeManuel));
        if (text is null) return;

        if (!Money.TryParse(text, out decimal value))
        {
            MessageBox.Show(Owner, "Montant invalide.", "Montant", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _session.Api.SetResultatCompteSoldeAsync(compte.Id, value);
            await LoadActiveTabAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Montant", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Statement building blocks ---

    private static string Signed(decimal value) => (value < 0 ? "−" : string.Empty) + Money.Format(Math.Abs(value));
    private static string SignKey(decimal value) => value >= 0 ? "Success" : "Danger";

    private static FrameworkElement ColumnTitle(string title, string colorKey, string hint)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var t = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold };
        t.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        stack.Children.Add(t);
        var h = new TextBlock { Text = hint, FontSize = 11 };
        h.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(h);
        return stack;
    }

    private static Grid Line(string label, string value, string colorKey, bool bold = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock { Text = label, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal });
        var v = new TextBlock { Text = value, FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold };
        v.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        return grid;
    }

    private static Border TotalBar(string label, decimal amount, string colorKey)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var l = new TextBlock { Text = label, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        var v = new TextBlock { Text = Money.Format(amount), FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);

        var border = new Border { Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(6), Child = grid };
        border.SetResourceReference(Border.BackgroundProperty, colorKey);
        return border;
    }

    private Border AutoTile(string label, string value, string colorKey, string sub)
    {
        var stack = new StackPanel();
        var l = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(l);

        var v = new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.Bold };
        v.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        stack.Children.Add(v);

        var s = new TextBlock { Text = sub, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        s.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        stack.Children.Add(s);

        var border = new Border
        {
            Width = 215, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        return border;
    }

    /// <summary>One block of a statement: a coloured header with the section total, then one
    /// line per account. Bilan accounts link to their écritures; the hand-entered résultat
    /// accounts get a pencil for whoever may edit them.</summary>
    private Border Section(string title, string colorKey, IReadOnlyList<BilanCompteDto> comptes, string tableType)
    {
        var stack = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var strip = new Border { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0) };
        strip.SetResourceReference(Border.BackgroundProperty, colorKey);
        header.Children.Add(strip);

        var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
        Grid.SetColumn(t, 1);
        header.Children.Add(t);

        var total = new TextBlock { Text = Money.Format(comptes.Sum(c => c.Solde)), FontWeight = FontWeights.Bold };
        total.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        Grid.SetColumn(total, 2);
        header.Children.Add(total);
        stack.Children.Add(header);

        if (comptes.Count == 0)
        {
            var none = new TextBlock { Text = "Aucun compte", FontSize = 11, Margin = new Thickness(12, 2, 0, 2) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            stack.Children.Add(none);
        }

        foreach (var compte in comptes)
            stack.Children.Add(AccountLine(compte, tableType));

        var border = new Border
        {
            Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 12),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        return border;
    }

    private Grid AccountLine(BilanCompteDto compte, string tableType)
    {
        var row = new Grid { Margin = new Thickness(12, 1, 0, 1), MinHeight = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

        var numero = new TextBlock { Text = compte.NumeroCompte, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        numero.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        row.Children.Add(numero);

        var libelle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        libelle.Children.Add(new TextBlock { Text = compte.Libelle, TextTrimming = TextTrimming.CharacterEllipsis });
        if (compte.SoldeAuto != 0)
        {
            var tag = new TextBlock { Text = "auto", FontSize = 10, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            tag.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            libelle.Children.Add(tag);
        }
        Grid.SetColumn(libelle, 1);
        row.Children.Add(libelle);

        var amount = new TextBlock
        {
            Text = Signed(compte.Solde), FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        amount.SetResourceReference(TextBlock.ForegroundProperty, compte.Solde < 0 ? "Danger" : "TextPrimary");
        Grid.SetColumn(amount, 2);
        row.Children.Add(amount);

        row.ToolTip = compte.SoldeAuto != 0
            ? $"Saisi : {Signed(compte.SoldeManuel)}\nIntégré automatiquement : {Signed(compte.SoldeAuto)}"
              + (compte.Description is { } d ? $"\n\n{d}" : string.Empty)
            : compte.Description;

        Button? action = null;
        if (tableType == TablesCompte.Resultat && TypesCompteResultat.IsManuel(compte.TypeCompte) && _canEditResultat)
        {
            action = IconButton("", "Saisir le montant");
            action.Click += async (_, _) => await EditManualAmountAsync(compte);
        }
        else if (tableType == TablesCompte.Bilan)
        {
            action = IconButton("", "Voir les écritures de ce compte");
            action.Click += async (_, _) => await ShowEcrituresOfAsync(compte.Id);
        }

        if (action is not null)
        {
            Grid.SetColumn(action, 3);
            row.Children.Add(action);
        }

        return row;
    }

    private Button IconButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"), Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11, Width = 24, Height = 22, ToolTip = tooltip, HorizontalAlignment = HorizontalAlignment.Right,
        };
        button.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
        return button;
    }

    // --- Écritures ---

    private async Task ShowEcrituresOfAsync(int compteId)
    {
        await EnsureComptesAsync();
        EcritureCompteFilter.SelectedItem = EcritureCompteFilter.Items.Cast<Choice>().FirstOrDefault(c => c.Id == compteId);
        await SetActiveTabAsync("ecritures");
    }

    private async Task LoadEcrituresAsync()
    {
        var compteId = (EcritureCompteFilter.SelectedItem as Choice)?.Id;
        var yearOnly = EcrituresYearOnly.IsChecked == true;
        _ecritures = await _session.Api.GetBilanEcrituresAsync(
            compteId,
            yearOnly ? new DateOnly(_annee, 1, 1) : null,
            yearOnly ? new DateOnly(_annee, 12, 31) : null);
    }

    private void RenderEcritures()
    {
        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        EcrituresGrid.ItemsSource = _ecritures
            .Select(e => new EcritureRow(e, Show(_canEditEcriture), Show(_canDeleteEcriture)))
            .ToList();
        EcrituresEmptyPanel.Visibility = _ecritures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var debit = _ecritures.Sum(e => e.MontantDebit);
        var credit = _ecritures.Sum(e => e.MontantCredit);
        EcrituresTotalText.Text = _ecritures.Count == 0 ? string.Empty
            : $"{_ecritures.Count} écriture{(_ecritures.Count > 1 ? "s" : "")} — total débit {Money.Format(debit)} · total crédit {Money.Format(credit)}";
    }

    private async void EcrituresFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _activeTab != "ecritures") return;
        try
        {
            await LoadEcrituresAsync();
            RenderEcritures();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private async void AddEcriture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureComptesAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
            return;
        }

        var preselect = _activeTab == "ecritures" ? (EcritureCompteFilter.SelectedItem as Choice)?.Id : null;
        var dialog = new BilanEcritureDialog(_comptes!.BilanComptes, null, preselect) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.CreateBilanEcritureAsync(request);
            await LoadActiveTabAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Nouvelle écriture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void EditEcriture_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is EcritureRow row) await EditEcritureAsync(row.Ecriture);
    }

    private async void EcrituresGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_canEditEcriture && EcrituresGrid.SelectedItem is EcritureRow row) await EditEcritureAsync(row.Ecriture);
    }

    private async Task EditEcritureAsync(BilanEcritureDto ecriture)
    {
        await EnsureComptesAsync();
        var dialog = new BilanEcritureDialog(_comptes!.BilanComptes, ecriture) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.UpdateBilanEcritureAsync(ecriture.Id, request);
            await LoadActiveTabAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Modifier l'écriture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteEcriture_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not EcritureRow row) return;

        var confirm = MessageBox.Show(Owner,
            $"Supprimer l'écriture « {row.Libelle} » du {row.Date} ? Cette action est irréversible.",
            "Supprimer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.DeleteBilanEcritureAsync(row.Ecriture.Id);
            await LoadActiveTabAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Supprimer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Plan comptable ---

    private void RenderComptes(BilanComptesResponse c)
    {
        static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        BilanComptesGrid.ItemsSource = c.BilanComptes
            .Select(x => new CompteRow(x, TablesCompte.Bilan, Show(_canManageComptes), Show(_canManageComptes && !x.IsSystem)))
            .ToList();
        ResultatComptesGrid.ItemsSource = c.ResultatComptes
            .Select(x => new CompteRow(x, TablesCompte.Resultat, Show(_canManageComptes), Show(_canManageComptes && !x.IsSystem)))
            .ToList();
    }

    private async void AddCompte_Click(object sender, RoutedEventArgs e)
    {
        var table = _activeTab == "resultat" ? TablesCompte.Resultat : TablesCompte.Bilan;
        var dialog = new BilanCompteDialog(null, table) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.CreateBilanCompteAsync(request);
            _comptes = null;
            await LoadActiveTabAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Nouveau compte", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void EditCompte_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not CompteRow row) return;

        var dialog = new BilanCompteDialog(row.Compte, row.TableType) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.UpdateBilanCompteAsync(row.Compte.Id, request);
            _comptes = null;
            await LoadActiveTabAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Modifier le compte", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void DeleteCompte_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not CompteRow row) return;

        var confirm = MessageBox.Show(Owner,
            $"Supprimer le compte {row.Numero} — {row.Libelle} ?", "Supprimer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.DeleteBilanCompteAsync(row.Compte.Id, row.TableType);
            _comptes = null;
            await LoadActiveTabAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Owner, ex.Message, "Supprimer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Export ---

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var (name, content) = _activeTab switch
        {
            "bilan" when _bilan is not null => ($"bilan_{_annee}", BilanCsv(_bilan)),
            "resultat" when _resultat is not null => ($"resultat_{_annee}", ResultatCsv(_resultat)),
            "ecritures" => ("ecritures", EcrituresCsv()),
            "comptes" when _comptes is not null => ("plan_comptable", ComptesCsv(_comptes)),
            _ => (null, null),
        };
        if (name is null || content is null) return;

        var groupName = _session.Groupe?.Nom ?? "espace";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{name}_{ImmobilisationDetailDialog.Slug(groupName)}.csv",
            Filter = "Fichier CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(Owner) != true) return;

        try
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, content, new System.Text.UTF8Encoding(true));
        }
        catch (System.IO.IOException ex)
        {
            ShowMessage($"Erreur lors de l'export : {ex.Message}");
        }
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string Amount(decimal value) => Money.FormatPlain(value, 2);

    private string Heading(string title, bool provisoire) =>
        $"{title} - {(_session.Groupe?.Nom ?? "").ToUpperInvariant()}\n"
        + $"Exercice;{_annee}{(provisoire ? " (provisoire)" : " (clôturé)")}\n"
        + $"Date d'export;{DateTime.Now:dd/MM/yyyy HH:mm}\n";

    private static void Accounts(System.Text.StringBuilder sb, string title, IEnumerable<BilanCompteDto> comptes)
    {
        sb.AppendLine($"--- {title} ---");
        foreach (var c in comptes)
            sb.AppendLine($"{c.NumeroCompte};{Csv(c.Libelle)};{Amount(c.SoldeManuel)};{Amount(c.SoldeAuto)};{Amount(c.Solde)}");
    }

    private string BilanCsv(BilanResponse b)
    {
        var sb = new System.Text.StringBuilder(Heading("BILAN", b.IsProvisoire));
        sb.AppendLine();
        sb.AppendLine("=== ACTIF ===");
        sb.AppendLine("N° compte;Libellé;Saisi;Automatique;Solde");
        Accounts(sb, "Actif immobilisé", b.ActifImmobilise);
        Accounts(sb, "Actif circulant", b.ActifCirculant);
        Accounts(sb, "Trésorerie", b.TresorerieActif);
        sb.AppendLine($"TOTAL ACTIF;;;;{Amount(b.TotalActif)}");
        sb.AppendLine();
        sb.AppendLine("=== PASSIF ===");
        sb.AppendLine("N° compte;Libellé;Saisi;Automatique;Solde");
        Accounts(sb, "Capitaux propres", b.CapitauxPropres);
        Accounts(sb, "Dettes à long terme", b.DettesLongTerme);
        Accounts(sb, "Dettes à court terme", b.DettesCourtTerme);
        if (b.TresoreriePassif.Count > 0) Accounts(sb, "Trésorerie passif", b.TresoreriePassif);
        sb.AppendLine($"TOTAL PASSIF;;;;{Amount(b.TotalPassif)}");
        sb.AppendLine();
        sb.AppendLine($"ÉCART (actif - passif);;;;{Amount(b.Ecart)}");
        sb.AppendLine();
        sb.AppendLine("=== IMMOBILISATIONS (module Amortissement) ===");
        sb.AppendLine($"Valeur brute;{Amount(b.Immobilisations.ValeurBrute)}");
        sb.AppendLine($"Amortissements cumulés;{Amount(b.Immobilisations.Amortissements)}");
        sb.AppendLine($"Valeur nette comptable;{Amount(b.Immobilisations.ValeurNette)}");
        return sb.ToString();
    }

    private string ResultatCsv(ResultatResponse r)
    {
        var i = r.Integration;
        var sb = new System.Text.StringBuilder(Heading("COMPTE DE RÉSULTAT", r.Annee == DateTime.Now.Year));
        sb.AppendLine();
        sb.AppendLine("=== DONNÉES INTÉGRÉES AUTOMATIQUEMENT ===");
        sb.AppendLine($"Ventes de marchandises;{Amount(i.Ventes)}");
        sb.AppendLine($"Coût des marchandises vendues;{Amount(i.CoutMarchandisesVendues)}");
        sb.AppendLine($"Charges (module Charges);{Amount(i.Charges)}");
        sb.AppendLine($"Dotations aux amortissements;{Amount(i.DotationAmortissement)}");
        sb.AppendLine($"Stock début;{Amount(i.StockDebut)}");
        sb.AppendLine($"Stock fin;{Amount(i.StockFin)}");
        sb.AppendLine($"Variation de stocks (information);{Amount(i.VariationStocks)}");
        sb.AppendLine();
        sb.AppendLine("=== PRODUITS ===");
        sb.AppendLine("N° compte;Libellé;Saisi;Automatique;Solde");
        Accounts(sb, "Produits d'exploitation", r.ProduitsExploitation);
        Accounts(sb, "Produits financiers", r.ProduitsFinanciers);
        Accounts(sb, "Produits exceptionnels", r.ProduitsExceptionnels);
        sb.AppendLine($"TOTAL PRODUITS;;;;{Amount(r.TotalProduits)}");
        sb.AppendLine();
        sb.AppendLine("=== CHARGES ===");
        sb.AppendLine("N° compte;Libellé;Saisi;Automatique;Solde");
        Accounts(sb, "Charges d'exploitation", r.ChargesExploitation);
        Accounts(sb, "Charges financières", r.ChargesFinancieres);
        Accounts(sb, "Charges exceptionnelles", r.ChargesExceptionnelles);
        sb.AppendLine($"TOTAL CHARGES;;;;{Amount(r.TotalCharges)}");
        sb.AppendLine();
        sb.AppendLine($"Résultat d'exploitation;;;;{Amount(r.ResultatExploitation)}");
        sb.AppendLine($"Résultat financier;;;;{Amount(r.ResultatFinancier)}");
        sb.AppendLine($"Résultat exceptionnel;;;;{Amount(r.ResultatExceptionnel)}");
        sb.AppendLine($"{(r.ResultatNet >= 0 ? "BÉNÉFICE NET" : "PERTE NETTE")};;;;{Amount(Math.Abs(r.ResultatNet))}");
        return sb.ToString();
    }

    private string EcrituresCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Date;N° compte;Compte;Libellé;Débit;Crédit;Référence;Notes;Saisi par");
        foreach (var e in _ecritures)
        {
            sb.AppendLine(string.Join(';',
                e.DateEcriture.ToString("dd/MM/yyyy"), e.NumeroCompte, Csv(e.CompteLibelle), Csv(e.Libelle),
                Amount(e.MontantDebit), Amount(e.MontantCredit), Csv(e.Reference ?? ""), Csv(e.Notes ?? ""),
                Csv(e.CreatedByName ?? "")));
        }
        sb.AppendLine($"Total;;;;{Amount(_ecritures.Sum(e => e.MontantDebit))};{Amount(_ecritures.Sum(e => e.MontantCredit))}");
        return sb.ToString();
    }

    private static string ComptesCsv(BilanComptesResponse c)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Tableau;N° compte;Libellé;Type;Sous-type;Par défaut;Description");
        foreach (var x in c.BilanComptes)
            sb.AppendLine($"Bilan;{x.NumeroCompte};{Csv(x.Libelle)};{TypesCompteBilan.Label(x.TypeCompte)};{x.SousType};{(x.IsSystem ? "Oui" : "Non")};{Csv(x.Description ?? "")}");
        foreach (var x in c.ResultatComptes)
            sb.AppendLine($"Résultat;{x.NumeroCompte};{Csv(x.Libelle)};{TypesCompteResultat.Label(x.TypeCompte)};{x.SousType};{(x.IsSystem ? "Oui" : "Non")};{Csv(x.Description ?? "")}");
        return sb.ToString();
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;
}
