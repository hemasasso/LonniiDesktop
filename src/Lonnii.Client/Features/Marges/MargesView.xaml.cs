using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lonnii.Client.Common;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Lonnii.Client.Features.Marges;

/// <summary>
/// Analyse des Marges: revenue, cost of sales, gross and net profit over a period, broken
/// down by product, category and month. Mirrors Lonnii Business's Marges page
/// (client/src/components/baro/gestion/marges/Marges.jsx), with the ratios a margin
/// analysis usually needs added on top of its figures - markup rate, multiplier, break-even
/// revenue, charge coverage, comparison with the previous period - and every table
/// filterable rather than fixed to its top ten.
/// </summary>
public partial class MargesView : UserControl
{
    private readonly AppSession _session;
    private readonly bool _canExport;

    private string _activeTab = "overview";
    private MargesResponse? _data;

    private string _dateFilterTag = "month";
    private DateOnly? _dateDebut;
    private DateOnly? _dateFin;
    private bool _suppressFilterEvents;
    private bool _categoriesLoaded;

    private const string ModuleKey = "marges";

    /// <summary>Low-margin threshold of the source app's "Produits à Faible Marge" table.</summary>
    private const decimal LowMarginThreshold = 10m;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Same 8-colour palette as the Stock, Ventes and Charges charts.</summary>
    private static readonly SKColor[] Palette =
    [
        new(0x25, 0x63, 0xEB), new(0x10, 0xB9, 0x81), new(0xF5, 0x9E, 0x0B), new(0xEF, 0x44, 0x44),
        new(0x8B, 0x5C, 0xF6), new(0xEC, 0x48, 0x99), new(0x06, 0xB6, 0xD4), new(0x84, 0xCC, 0x16),
    ];

    /// <summary>Net profit's colour in every chart: the Marges menu entry's own accent.</summary>
    private static readonly SKColor NetColor = new(0x8B, 0x5C, 0xF6);

    // --- Formatting: every amount reads "1 000,25 FCFA", every rate "12,50 %" ---

    private static string Amount(decimal value) => Money.WithLabel(value, Money.FormatPlain(Math.Abs(value), 2));
    private static string Percent(decimal value) => $"{Money.FormatPlain(value, 2)} %";
    private static string SignedPercent(decimal value) => (value > 0 ? "+" : string.Empty) + Percent(value);

    private static string MarginLevelOf(decimal margin) => margin >= 30 ? "high" : margin >= 15 ? "medium" : "low";

    private sealed record ProductRow(MargeLineDto Line, decimal Share)
    {
        public string Nom => Line.Nom;
        public string Categorie => Line.Categorie;
        public int Quantite => Line.QuantiteVendue;
        public decimal Revenue => Line.Revenue;
        public decimal Cost => Line.Cost;
        public decimal Profit => Line.Profit;
        public decimal Margin => Line.Margin;
        public string QuantiteDisplay => Money.FormatPlain(Quantite);
        public string RevenueDisplay => Amount(Revenue);
        public string CostDisplay => Amount(Cost);
        public string ProfitDisplay => Amount(Profit);
        public string MarginDisplay => Percent(Margin);
        public string MarginLevel => MarginLevelOf(Margin);
        public string ShareDisplay => Percent(Share);
        public bool IsLoss => Profit < 0;
        public bool IsEstimated => Line.CostSource == MargeCostSources.Estime;
        public string CostSourceDisplay => Line.CostSource switch
        {
            MargeCostSources.Reel => "Prix d'achat",
            MargeCostSources.Estime => "Estimation",
            _ => "Sans coût",
        };
    }

    private sealed record CategoryRow(MargeCategoryDto Category, decimal Share, decimal RevenueShare)
    {
        public string Categorie => Category.Categorie;
        public int Quantite => Category.QuantiteVendue;
        public decimal Revenue => Category.Revenue;
        public decimal Cost => Category.Cost;
        public decimal Profit => Category.Profit;
        public decimal Margin => Category.Margin;
        public string QuantiteDisplay => Money.FormatPlain(Quantite);
        public string RevenueDisplay => Amount(Revenue);
        public string CostDisplay => Amount(Cost);
        public string ProfitDisplay => Amount(Profit);
        public string MarginDisplay => Percent(Margin);
        public string MarginLevel => MarginLevelOf(Margin);
        public string ShareDisplay => Percent(Share);
        public string RevenueShareDisplay => Percent(RevenueShare);
        public bool IsLoss => Profit < 0;
    }

    public MargesView(AppSession session)
    {
        _session = session;
        _canExport = _session.Can(Priv.Gestion.ExportMarges);
        InitializeComponent();

        SubtitleText.Text = _session.Groupe?.Nom;
        ExportButton.Visibility = _canExport ? Visibility.Visible : Visibility.Collapsed;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Print, Print_Click, (_, e) => e.CanExecute = true));

        var today = DateOnly.FromDateTime(DateTime.Now);
        (_dateDebut, _dateFin) = (new DateOnly(today.Year, today.Month, 1), today);

        _suppressFilterEvents = true;
        CategoryFilterCombo.ItemsSource = new[] { new MargeCategoryOptionDto(string.Empty, "Toutes les catégories") };
        CategoryFilterCombo.SelectedIndex = 0;
        _suppressFilterEvents = false;

        var saved = UiState.For(_session).Tabs.GetValueOrDefault(ModuleKey);
        _activeTab = saved is "products" or "categories" ? saved : "overview";
        ApplyTabVisuals();
        UpdatePeriodText();

        // LiveCharts paints are SkiaSharp colours snapshotted at render time, not
        // DynamicResource-aware - same as Ventes' Statistiques - so a theme toggle re-renders.
        ThemeManager.Changed += (_, _) =>
        {
            if (_data is not null) RenderCharts();
        };

        Loaded += async (_, _) =>
        {
            if (_data is null) await LoadAsync();
        };
    }

    // --- Tabs ---

    private void Tab_Click(object sender, RoutedEventArgs e) => SetActiveTab(((Button)sender).Tag as string ?? "overview");

    private void SetActiveTab(string tab)
    {
        _activeTab = tab;
        ApplyTabVisuals();
        UiState.For(_session).Tabs[ModuleKey] = tab;
        UiState.Save();
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

        Apply(OverviewTabButton, _activeTab == "overview");
        Apply(ProductsTabButton, _activeTab == "products");
        Apply(CategoriesTabButton, _activeTab == "categories");

        OverviewPanel.Visibility = _activeTab == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ProductsPanel.Visibility = _activeTab == "products" ? Visibility.Visible : Visibility.Collapsed;
        CategoriesPanel.Visibility = _activeTab == "categories" ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- Filters ---

    private async void DateFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterEvents) return;

        var tag = (string)((ComboBoxItem)DateFilterCombo.SelectedItem).Tag;
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (tag == "custom")
        {
            var dialog = new CustomDateRangeDialog(_dateDebut ?? today, _dateFin ?? today) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true)
            {
                SelectDateFilter(_dateFilterTag);
                return;
            }
            (_dateDebut, _dateFin) = (dialog.DateDebut, dialog.DateFin);
        }
        else
        {
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var quarterStart = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
            (_dateDebut, _dateFin) = tag switch
            {
                "today" => (today, today),
                "week" => (today.AddDays(-6), today),
                "month" => (monthStart, today),
                "lastmonth" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
                "quarter" => (quarterStart, today),
                "year" => (new DateOnly(today.Year, 1, 1), today),
                _ => ((DateOnly?)null, (DateOnly?)null),
            };
        }

        _dateFilterTag = tag;
        UpdatePeriodText();
        await LoadAsync();
    }

    private void SelectDateFilter(string tag)
    {
        _suppressFilterEvents = true;
        DateFilterCombo.SelectedItem = DateFilterCombo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == tag);
        _suppressFilterEvents = false;
    }

    private void UpdatePeriodText() =>
        PeriodText.Text = _dateDebut is { } debut && _dateFin is { } fin
            ? debut == fin ? $"Le {debut:dd/MM/yyyy}" : $"Du {debut:dd/MM/yyyy} au {fin:dd/MM/yyyy}"
            : "Depuis la première vente";

    private async void CategoryFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterEvents) return;
        await LoadAsync();
    }

    private string? SelectedCategoryId =>
        CategoryFilterCombo.SelectedItem is MargeCategoryOptionDto { Id: { Length: > 0 } id } ? id : null;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    // --- Loading ---

    private async Task LoadAsync()
    {
        try
        {
            _data = await _session.Api.GetMargesAsync(_dateDebut, _dateFin, SelectedCategoryId);
            PopulateCategoryFilter(_data.CategoryOptions);
            HideMessage();
            RenderAll();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    /// <summary>Filled from the first response rather than the Stock categories endpoint, so
    /// the filter works for a user who may see Marges without being granted Stock.</summary>
    private void PopulateCategoryFilter(IReadOnlyList<MargeCategoryOptionDto> options)
    {
        if (_categoriesLoaded) return;
        _categoriesLoaded = true;

        _suppressFilterEvents = true;
        CategoryFilterCombo.ItemsSource = new[] { new MargeCategoryOptionDto(string.Empty, "Toutes les catégories") }
            .Concat(options).ToList();
        CategoryFilterCombo.SelectedIndex = 0;
        _suppressFilterEvents = false;
    }

    private void RenderAll()
    {
        if (_data is not { } data) return;

        ApplyEstimateNoticeVisibility();
        EstimateNoticeIconText.Text = $"{data.EstimatedLines} coût(s) estimé(s)";
        EstimateNoticeIcon.ToolTip = $"{data.EstimatedLines} ligne(s) de vente sans prix d'achat connu - cliquez pour le détail";
        EstimateNoticeText.Text =
            $"{data.EstimatedLines} ligne(s) de vente, soit {Amount(data.EstimatedRevenue)} de chiffre d'affaires, " +
            "n'ont pas de prix d'achat connu (non renseigné, ou produit supprimé ou renommé depuis la vente) : " +
            "leur coût est estimé à partir du prix de vente (marge de 30 % supposée). " +
            "Renseignez le prix d'achat dans Gestion de Stock pour une marge exacte.";

        RenderTiles(data);
        RenderAlerts(data);
        RenderProducts();
        RenderCategories(data);
        RenderCharts();
    }

    // --- Vue d'ensemble: tiles ---

    private void RenderTiles(MargesResponse d)
    {
        MainTilesPanel.Children.Clear();
        RatioTilesPanel.Children.Clear();

        var revenueTrend = Trend(d.TotalRevenue, d.PreviousRevenue);
        var profitTrend = Trend(d.GrossProfit, d.PreviousGrossProfit);

        MainTilesPanel.Children.Add(Tile("Chiffre d'Affaires", Amount(d.TotalRevenue), "Accent",
            $"{Money.FormatPlain(d.NombreVentes)} vente(s)", revenueTrend));
        MainTilesPanel.Children.Add(Tile("Coût des Ventes", Amount(d.TotalCosts), "Warning",
            $"{Percent(Share(d.TotalCosts, d.TotalRevenue))} du CA"));
        MainTilesPanel.Children.Add(Tile("Marge Brute", Amount(d.GrossProfit), d.GrossProfit >= 0 ? "Success" : "Danger",
            $"Taux de marque : {Percent(d.GrossMargin)}", profitTrend));
        MainTilesPanel.Children.Add(Tile("Charges", Amount(d.TotalCharges), "Danger",
            $"Fixes {Amount(d.ChargesFixes)} · Variables {Amount(d.ChargesVariables)}"
                + (SelectedCategoryId is null ? string.Empty : " (toutes catégories)"),
            formula: "Charges de la période, classées fixes ou variables dans le module Charges."));
        MainTilesPanel.Children.Add(Tile("Bénéfice Net", Amount(d.NetProfit), d.NetProfit >= 0 ? "Success" : "Danger",
            $"Marge nette : {Percent(d.NetMargin)}",
            formula: "Marge brute − charges fixes − charges variables"));

        // Rates on cost rather than on revenue: what a shopkeeper applies when pricing.
        var markup = d.TotalCosts > 0 ? d.GrossProfit / d.TotalCosts * 100 : (decimal?)null;
        var coefficient = d.TotalCosts > 0 ? d.TotalRevenue / d.TotalCosts : (decimal?)null;

        RatioTilesPanel.Children.Add(Tile("Taux de Marge (sur coût)", markup is { } m ? Percent(m) : "—", "TextPrimary",
            "Marge brute ÷ coût des ventes"));
        RatioTilesPanel.Children.Add(Tile("Coefficient Multiplicateur", coefficient is { } c ? Money.FormatPlain(c, 2) : "—", "TextPrimary",
            "Chiffre d'affaires ÷ coût des ventes"));
        RatioTilesPanel.Children.Add(Tile("Marge Moyenne par Vente",
            d.NombreVentes > 0 ? Amount(d.GrossProfit / d.NombreVentes) : "—", "TextPrimary",
            d.NombreVentes > 0 ? $"Panier moyen : {Amount(d.TotalRevenue / d.NombreVentes)}" : null));

        // Break-even analysis. The cost of sales and the variable charges move with what is
        // sold; the fixed charges are owed regardless - so the seuil is the revenue whose
        // margin on variable costs exactly pays the fixed charges.
        RatioTilesPanel.Children.Add(Tile("Marge sur Coûts Variables", Amount(d.MargeCoutsVariables),
            d.MargeCoutsVariables >= 0 ? "Success" : "Danger",
            $"Taux de MCV : {Percent(d.TauxMcv)}",
            formula: "MCV = CA − coût des ventes − charges variables\nTaux de MCV = MCV ÷ CA"));

        const string SeuilFormula = "Seuil de rentabilité = charges fixes ÷ taux de MCV\n" +
            "Point mort = jour où ce CA est atteint, les ventes étant supposées réparties régulièrement sur la période.";
        if (d.SeuilRentabilite is { } seuil)
        {
            var reached = d.TotalRevenue >= seuil;
            var sub = reached
                ? d.PointMortDate is { } date ? $"Point mort atteint le {date:dd/MM/yyyy}" : "Atteint sur la période"
                : $"Manque {Amount(seuil - d.TotalRevenue)} de CA";
            RatioTilesPanel.Children.Add(Tile("Seuil de Rentabilité", Amount(seuil), reached ? "Success" : "Warning", sub,
                formula: SeuilFormula));

            // Marge de sécurité: how far revenue could fall before the shop starts losing money.
            var securite = d.TotalRevenue - seuil;
            RatioTilesPanel.Children.Add(Tile("Marge de Sécurité", Amount(securite), securite >= 0 ? "Success" : "Danger",
                $"Indice de sécurité : {Percent(Share(securite, d.TotalRevenue))}",
                formula: "Marge de sécurité = CA − seuil de rentabilité\nIndice de sécurité = marge de sécurité ÷ CA"));
        }
        else
        {
            RatioTilesPanel.Children.Add(Tile("Seuil de Rentabilité", "—", "TextMuted",
                d.TotalRevenue > 0 ? "Taux de MCV nul ou négatif : aucun CA ne couvre les charges fixes" : "Aucune vente sur la période",
                formula: SeuilFormula));
        }

        // Levier opérationnel: how many % the result moves for 1 % more revenue.
        RatioTilesPanel.Children.Add(Tile("Levier Opérationnel",
            d.NetProfit > 0 ? Money.FormatPlain(d.MargeCoutsVariables / d.NetProfit, 2) : "—", "TextPrimary",
            d.NetProfit > 0
                ? $"+1 % de CA → +{Money.FormatPlain(d.MargeCoutsVariables / d.NetProfit, 2)} % de bénéfice"
                : "Non significatif sans bénéfice",
            formula: "Levier opérationnel = MCV ÷ bénéfice net"));
    }

    private static decimal Share(decimal part, decimal whole) => whole != 0 ? part / whole * 100 : 0;

    /// <summary>"+12,50 % vs période préc." against the preceding period of the same length,
    /// or null when there is none to compare with (no bounded range, or nothing sold then).</summary>
    private static (string Text, bool Up)? Trend(decimal current, decimal? previous)
    {
        if (previous is not { } p || p == 0) return null;
        var change = (current - p) / Math.Abs(p) * 100;
        return ($"{(change >= 0 ? "▲" : "▼")} {SignedPercent(change)} vs période préc.", change >= 0);
    }

    private Border Tile(string label, string value, string colorKey, string? sub = null, (string Text, bool Up)? trend = null,
        string? formula = null)
    {
        var stack = new StackPanel();

        var labelText = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
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
            var subText = new TextBlock { Text = sub, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            subText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            stack.Children.Add(subText);
        }

        if (trend is { } t)
        {
            var trendText = new TextBlock { Text = t.Text, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
            trendText.SetResourceReference(TextBlock.ForegroundProperty, t.Up ? "Success" : "Danger");
            stack.Children.Add(trendText);
        }

        var border = new Border
        {
            Width = 215, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        if (formula is not null)
        {
            border.ToolTip = formula;
            ToolTipService.SetShowDuration(border, 30000);
        }
        return border;
    }

    // --- Vue d'ensemble: points d'attention ---

    private void RenderAlerts(MargesResponse d)
    {
        AlertsPanel.Children.Clear();

        var negative = d.Products.Count(p => p.Profit < 0);
        var low = d.Products.Count(p => p.Margin < LowMarginThreshold);
        var estimated = d.Products.Count(p => p.CostSource == MargeCostSources.Estime);

        if (negative > 0)
            AddAlert("Danger", $"{negative} produit(s) vendu(s) à perte sur la période.", "negative");
        if (low > 0)
            AddAlert("Warning", $"{low} produit(s) avec un taux de marque inférieur à {Money.FormatPlain(LowMarginThreshold)} %.", "low");
        if (estimated > 0)
            AddAlert("Warning", $"{estimated} produit(s) sans prix d'achat connu : leur marge est une estimation.", "estimated");
        if (d.TotalCharges > 0 && d.GrossProfit < d.TotalCharges)
            AddAlert("Danger",
                $"La marge brute ne couvre pas les charges : il manque {Amount(d.TotalCharges - d.GrossProfit)} pour atteindre l'équilibre.",
                null);

        if (AlertsPanel.Children.Count == 0)
        {
            var ok = new TextBlock { Text = "Aucun point d'attention sur cette période." };
            ok.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            AlertsPanel.Children.Add(ok);
        }
    }

    private void AddAlert(string colorKey, string text, string? productFilter)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        if (productFilter is not null)
        {
            var show = new Button
            {
                Content = "Voir", Padding = new Thickness(10, 2, 10, 2), FontSize = 11,
                Style = (Style)FindResource("SecondaryButton"),
            };
            show.Click += (_, _) => ShowProducts(productFilter);
            DockPanel.SetDock(show, Dock.Right);
            row.Children.Add(show);
        }

        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, colorKey);
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);

        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        AlertsPanel.Children.Add(row);
    }

    private void ShowEstimated_Click(object sender, RoutedEventArgs e) => ShowProducts("estimated");

    // --- Estimated-cost notice: full banner, or folded to a ⚠ beside Actualiser ---

    private void CollapseEstimateNotice_Click(object sender, RoutedEventArgs e) => SetEstimateNoticeCollapsed(true);

    private void ExpandEstimateNotice_Click(object sender, RoutedEventArgs e) => SetEstimateNoticeCollapsed(false);

    private void SetEstimateNoticeCollapsed(bool collapsed)
    {
        UiState.For(_session).MargesEstimateNoticeCollapsed = collapsed;
        UiState.Save();
        ApplyEstimateNoticeVisibility();
    }

    /// <summary>Neither shows when every cost is known; otherwise exactly one of the two does.</summary>
    private void ApplyEstimateNoticeVisibility()
    {
        var applies = _data is { EstimatedLines: > 0 };
        var collapsed = UiState.For(_session).MargesEstimateNoticeCollapsed;

        EstimateNotice.Visibility = applies && !collapsed ? Visibility.Visible : Visibility.Collapsed;
        EstimateNoticeIcon.Visibility = applies && collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowProducts(string filterTag)
    {
        ProductFilterCombo.SelectedItem = ProductFilterCombo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == filterTag);
        SetActiveTab("products");
    }

    // --- Produits ---

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RenderProducts();
    }

    private void ProductFilter_Changed(object sender, SelectionChangedEventArgs e) => RenderProducts();

    /// <summary>The products the current "Afficher" filter and search select, in the order
    /// that filter implies (the grid's own column sort can then reorder them).</summary>
    private List<MargeLineDto> FilteredProducts()
    {
        if (_data is not { } data) return [];

        var tag = (ProductFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var products = data.Products.AsEnumerable();

        products = tag switch
        {
            "top" => products.OrderByDescending(p => p.Profit).Take(10),
            "least" => products.OrderBy(p => p.Profit).Take(10),
            "low" => products.Where(p => p.Margin < LowMarginThreshold).OrderBy(p => p.Margin),
            "negative" => products.Where(p => p.Profit < 0).OrderBy(p => p.Profit),
            "estimated" => products.Where(p => p.CostSource == MargeCostSources.Estime),
            "nocost" => products.Where(p => p.CostSource == MargeCostSources.Aucun),
            _ => products,
        };

        var term = SearchBox?.Text.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            products = products.Where(p =>
                p.Nom.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || p.Categorie.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        return products.ToList();
    }

    private void RenderProducts()
    {
        if (_data is not { } data || ProductsGrid is null) return;

        var rows = FilteredProducts()
            .Select(p => new ProductRow(p, data.GrossProfit > 0 ? p.Profit / data.GrossProfit * 100 : 0))
            .ToList();

        ProductsGrid.ItemsSource = rows;
        ProductsEmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProductCountText.Text = $"{rows.Count} produit(s) sur {data.Products.Count}";

        var revenue = rows.Sum(r => r.Revenue);
        var profit = rows.Sum(r => r.Profit);
        ProductTotalsText.Text =
            $"Total affiché — CA : {Amount(revenue)}   ·   Coût : {Amount(rows.Sum(r => r.Cost))}   ·   " +
            $"Marge : {Amount(profit)}   ·   Taux : {Percent(Share(profit, revenue))}";
    }

    // --- Catégories ---

    private void RenderCategories(MargesResponse d)
    {
        CategoriesGrid.ItemsSource = d.Categories
            .Select(c => new CategoryRow(c,
                d.GrossProfit > 0 ? c.Profit / d.GrossProfit * 100 : 0,
                Share(c.Revenue, d.TotalRevenue)))
            .ToList();
    }

    // --- Charts ---

    private void RenderCharts()
    {
        if (_data is not { } d) return;

        var axisPaint = new SolidColorPaint(ThemeColor("TextSecondary"));
        var separatorPaint = new SolidColorPaint(ThemeColor("Border")) { StrokeThickness = 1 };
        var revenueColor = ThemeColor("Accent");
        var costColor = ThemeColor("Warning");
        var profitColor = ThemeColor("Success");
        var lossColor = ThemeColor("Danger");

        Axis ValueAxis(double max, double min = 0) => new()
        {
            LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint, TextSize = 11,
            Labeler = v => double.IsFinite(v) ? Money.FormatPlain((decimal)v) : string.Empty,
            MinStep = ChartAxis.NiceStep(Math.Max(Math.Abs(max), Math.Abs(min))),
        };

        Axis LabelAxis(string[] labels, double textSize = 11) => new()
        {
            Labels = labels, LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint, TextSize = textSize,
        };

        // Du Chiffre d'Affaires au Bénéfice Net
        WaterfallTitle.Text = $"Du Chiffre d'Affaires au Bénéfice Net ({Money.Label})";
        RenderWaterfall(d);

        // One colour per category, shared by the pie below and the Top 10 bars, so a product's
        // bar reads as the same category the pie shows it in. Assigned in the pie's own order
        // (most profitable first), loss-making categories last.
        var categoryColors = d.Categories
            .OrderByDescending(c => c.Profit)
            .Select((c, i) => (c.Categorie, Color: Palette[i % Palette.Length]))
            .GroupBy(c => c.Categorie)
            .ToDictionary(g => g.Key, g => g.First().Color);

        // Répartition de la Marge Brute par Catégorie - a pie can only show positive parts,
        // so loss-making categories are listed under it instead of silently dropped.
        var slices = d.Categories.Where(c => c.Profit > 0)
            .Select(c => (Label: c.Categorie, Value: c.Profit, Color: categoryColors[c.Categorie]))
            .ToList();
        var losing = d.Categories.Where(c => c.Profit <= 0 && c.Revenue > 0).ToList();
        CategoryEmptyText.Visibility = slices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CategoryPieChart.Series = slices.Select(s => (ISeries)new PieSeries<double>
        {
            Values = [(double)s.Value], Name = s.Label, Fill = new SolidColorPaint(s.Color),
            InnerRadius = 45,
            ToolTipLabelFormatter = point => Amount((decimal)point.Coordinate.PrimaryValue),
        }).ToArray();
        RenderPieLegend(CategoryLegendPanel, slices, losing);

        // Évolution sur 12 mois
        MonthlyTitle.Text = $"Évolution sur 12 Mois ({Money.Label})";
        var months = d.Monthly;
        var monthLabels = months.Select(m => m.Mois.ToDateTime(TimeOnly.MinValue).ToString("MMM yy", French)).ToArray();
        var gross = months.Select(m => m.Revenue - m.Cost).ToList();
        var net = months.Select(m => m.Revenue - m.Cost - m.Charges).ToList();
        MonthlyEmptyText.Visibility = months.All(m => m.Revenue == 0 && m.Charges == 0) ? Visibility.Visible : Visibility.Collapsed;
        MonthlyChart.Series =
        [
            Column("Chiffre d'affaires", months.Select(m => m.Revenue), revenueColor),
            Column("Coût des ventes", months.Select(m => m.Cost), costColor),
            Column("Marge brute", gross, profitColor),
            new LineSeries<double>
            {
                Name = "Bénéfice net",
                Values = net.Select(v => (double)v).ToArray(),
                Stroke = new SolidColorPaint(NetColor) { StrokeThickness = 3 },
                Fill = null,
                GeometrySize = 8,
                GeometryStroke = new SolidColorPaint(NetColor) { StrokeThickness = 3 },
                GeometryFill = new SolidColorPaint(ThemeColor("Surface")),
                LineSmoothness = 0.3,
                YToolTipLabelFormatter = point => Amount((decimal)point.Coordinate.PrimaryValue),
            },
        ];
        MonthlyChart.XAxes = [LabelAxis(monthLabels, 10)];
        var monthMax = months.Count == 0 ? 0 : (double)months.Max(m => m.Revenue);
        var monthMin = net.Count == 0 ? 0 : (double)Math.Min(0, net.Min());
        MonthlyChart.YAxes = [ValueAxis(monthMax, monthMin)];
        RenderSeriesLegend(MonthlyLegendPanel,
        [
            ("Chiffre d'affaires", revenueColor), ("Coût des ventes", costColor),
            ("Marge brute", profitColor), ("Bénéfice net", NetColor),
        ]);

        // Top 10 Produits par Marge (highest at the top)
        TopProductsTitle.Text = $"Top 10 des Produits par Marge ({Money.Label})";
        var top = d.Products.OrderByDescending(p => p.Profit).Take(10).Reverse().ToList();
        TopProductsEmptyText.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SKColor CategoryColor(string categorie) => categoryColors.GetValueOrDefault(categorie, SKColors.Gray);
        TopProductsChart.Series = top
            // Named after the product alone, as Ventes' "Produits les Plus Vendus" is; the
            // margin rate goes with the amount instead of lengthening the name.
            .Select((p, i) => (ISeries)Bar(i, top.Count, (double)p.Profit, CategoryColor(p.Categorie),
                p.Nom, horizontal: true, tooltip: v => $"{Amount((decimal)v)} ({Percent(p.Margin)})"))
            .ToArray();
        TopProductsChart.YAxes = [LabelAxis(top.Select(p => Shorten(p.Nom, 28)).ToArray(), 10)];
        TopProductsChart.XAxes = [ValueAxis(top.Count == 0 ? 0 : (double)top.Max(p => p.Profit), top.Count == 0 ? 0 : (double)top.Min(p => p.Profit))];
        RenderSeriesLegend(TopProductsLegendPanel,
            top.Select(p => p.Categorie).Distinct().Select(c => (c, CategoryColor(c))).ToList());

        // Répartition des Produits par Taux de Marge - same thresholds as the badges.
        var buckets = new (string Label, Func<decimal, bool> Match, SKColor Color)[]
        {
            ("À perte", m => m < 0, lossColor),
            ("0 – 10 %", m => m >= 0 && m < 10, lossColor.WithAlpha(0xA0)),
            ("10 – 15 %", m => m >= 10 && m < 15, costColor.WithAlpha(0xA0)),
            ("15 – 30 %", m => m >= 15 && m < 30, costColor),
            ("30 % et +", m => m >= 30, profitColor),
        };
        var counts = buckets.Select(b => d.Products.Count(p => b.Match(p.Margin))).ToArray();
        DistributionEmptyText.Visibility = d.Products.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DistributionChart.Series = buckets
            .Select((b, i) => (ISeries)Bar(i, buckets.Length, counts[i], b.Color, b.Label, horizontal: false,
                tooltip: v => $"{v:0} produit(s)"))
            .ToArray();
        DistributionChart.XAxes = [LabelAxis(buckets.Select(b => b.Label).ToArray(), 10)];
        DistributionChart.YAxes =
        [
            new Axis
            {
                LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint, TextSize = 11, MinLimit = 0,
                MinStep = Math.Max(1, ChartAxis.NiceStep(counts.DefaultIfEmpty(0).Max(), 5)),
                Labeler = v => double.IsFinite(v) ? v.ToString("0", French) : string.Empty,
            },
        ];

        // Catégories tab: CA, Coût et Marge par Catégorie
        CategoryChartTitle.Text = $"Chiffre d'Affaires, Coût et Marge par Catégorie ({Money.Label})";
        var cats = d.Categories.OrderByDescending(c => c.Revenue).ToList();
        CategoryChartEmptyText.Visibility = cats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CategoryChart.Series =
        [
            Column("Chiffre d'affaires", cats.Select(c => c.Revenue), revenueColor),
            Column("Coût des ventes", cats.Select(c => c.Cost), costColor),
            Column("Marge brute", cats.Select(c => c.Profit), profitColor),
        ];
        CategoryChart.XAxes = [LabelAxis(cats.Select(c => Shorten(c.Categorie, 18)).ToArray(), 10)];
        CategoryChart.YAxes = [ValueAxis(cats.Count == 0 ? 0 : (double)cats.Max(c => c.Revenue),
            cats.Count == 0 ? 0 : (double)Math.Min(0, cats.Min(c => c.Profit)))];
        RenderSeriesLegend(CategoryChartLegendPanel,
            [("Chiffre d'affaires", revenueColor), ("Coût des ventes", costColor), ("Marge brute", profitColor)]);
    }

    /// <summary>
    /// "Du Chiffre d'Affaires au Bénéfice Net": one horizontal bar per step, every bar drawn
    /// from zero - totals (CA, marge brute, MCV, bénéfice net) at their signed value, each
    /// deduction at its own amount with a "−" - so every row can be read on its own. A
    /// floating waterfall (deductions hung from the previous total) was harder to read.
    ///
    /// Built from WPF elements rather than LiveCharts: every label and amount is real text,
    /// sharp at any display scale (SkiaSharp text blurs at most Windows scaling factors), and
    /// every colour is a theme resource, so it follows the dark-mode toggle without a redraw.
    /// Each bar is positioned with star-sized grid columns, so it rescales with the card
    /// without any size bookkeeping.
    /// </summary>
    private void RenderWaterfall(MargesResponse d)
    {
        WaterfallHost.Children.Clear();
        WaterfallHost.RowDefinitions.Clear();
        WaterfallHost.ColumnDefinitions.Clear();

        var brute = d.TotalRevenue - d.TotalCosts;
        var mcv = brute - d.ChargesVariables;
        var net = mcv - d.ChargesFixes;

        // (label, from, to, isTotal, colour key or null for the net-profit accent). Every bar
        // starts at zero: a total at its signed value, a deduction at its amount - read with
        // the "−" in front of it, not as a step down from the row above.
        var steps = new (string Label, decimal From, decimal To, bool Total, string? ColorKey)[]
        {
            ("Chiffre d'affaires", 0, d.TotalRevenue, true, "Accent"),
            ("Coût des ventes", 0, d.TotalCosts, false, "Warning"),
            ("Marge brute", 0, brute, true, brute >= 0 ? "Success" : "Danger"),
            ("Charges variables", 0, d.ChargesVariables, false, "Warning"),
            ("Marge sur coûts var.", 0, mcv, true, mcv >= 0 ? "Success" : "Danger"),
            ("Charges fixes", 0, d.ChargesFixes, false, "Danger"),
            ("Bénéfice net", 0, net, true, net >= 0 ? null : "Danger"),
        };

        var lo = Math.Min(0, steps.Min(s => Math.Min(s.From, s.To)));
        var hi = Math.Max(0, steps.Max(s => Math.Max(s.From, s.To)));
        WaterfallEmptyText.Visibility = hi == lo ? Visibility.Visible : Visibility.Collapsed;
        if (hi == lo) return;

        WaterfallHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        WaterfallHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        WaterfallHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

        static GridLength Star(decimal span) => new((double)Math.Max(span, 0), GridUnitType.Star);

        for (var i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            WaterfallHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

            var label = new TextBlock
            {
                Text = step.Label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
                FontWeight = step.Total ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(step.Total ? 0 : 12, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, step.Total ? "TextPrimary" : "TextSecondary");
            Grid.SetRow(label, i);
            WaterfallHost.Children.Add(label);

            // The bar: three star columns - empty up to its start, the bar, empty after its end.
            var start = Math.Min(step.From, step.To);
            var end = Math.Max(step.From, step.To);
            var track = new Grid();
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(start - lo) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(end - start) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(hi - end) });

            var bar = new Border
            {
                Height = step.Total ? 20 : 14, CornerRadius = new CornerRadius(3), MinWidth = end > start ? 2 : 0,
                Opacity = step.Total ? 1 : 0.85,
            };
            if (step.ColorKey is { } key) bar.SetResourceReference(Border.BackgroundProperty, key);
            else bar.Background = ToBrush(NetColor);
            bar.ToolTip = $"{step.Label} : {(step.Total ? string.Empty : "− ")}{Amount(step.To)}";
            Grid.SetColumn(bar, 1);
            track.Children.Add(bar);

            Grid.SetRow(track, i);
            Grid.SetColumn(track, 1);
            WaterfallHost.Children.Add(track);

            var amount = step.To;
            var value = new TextBlock
            {
                Text = (step.Total ? string.Empty : "− ") + Amount(amount),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12, FontWeight = step.Total ? FontWeights.Bold : FontWeights.Normal,
            };
            if (step.ColorKey is { } valueKey && (step.Total || valueKey == "Danger"))
                value.SetResourceReference(TextBlock.ForegroundProperty, valueKey);
            else if (step.ColorKey is null)
                value.Foreground = ToBrush(NetColor);
            else
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 2);
            WaterfallHost.Children.Add(value);
        }

        // Zero line, across every row, when the chart dips below zero.
        if (lo < 0)
        {
            var zero = new Grid { IsHitTestVisible = false };
            zero.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(-lo) });
            zero.ColumnDefinitions.Add(new ColumnDefinition { Width = Star(hi) });
            var line = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
            line.SetResourceReference(Border.BorderBrushProperty, "BorderStrong");
            Grid.SetColumn(line, 1);
            zero.Children.Add(line);
            Grid.SetColumn(zero, 1);
            Grid.SetRowSpan(zero, steps.Length);
            WaterfallHost.Children.Add(zero);
        }
    }

    private static ColumnSeries<double> Column(string name, IEnumerable<decimal> values, SKColor color) => new()
    {
        Name = name,
        Values = values.Select(v => (double)v).ToArray(),
        Fill = new SolidColorPaint(color),
        MaxBarWidth = 22,
        Padding = 2,
        YToolTipLabelFormatter = point => Amount((decimal)point.Coordinate.PrimaryValue),
    };

    /// <summary>One bar of a chart whose bars each need their own colour: a series holding
    /// NaN everywhere but at <paramref name="index"/>, drawn in place rather than side by
    /// side - the technique StockView and VentesView already use.</summary>
    private static ISeries Bar(int index, int count, double value, SKColor color, string name, bool horizontal,
        Func<double, string>? tooltip = null)
    {
        var values = new double[count];
        Array.Fill(values, double.NaN);
        values[index] = value;

        string Format(double v) => !double.IsFinite(v) ? string.Empty
            : tooltip is not null ? tooltip(v) : Amount((decimal)v);

        return horizontal
            ? new RowSeries<double>
            {
                Values = values, Name = name, IgnoresBarPosition = true, Fill = new SolidColorPaint(color),
                MaxBarWidth = 22,
                YToolTipLabelFormatter = point => Format(point.Coordinate.PrimaryValue),
            }
            : new ColumnSeries<double>
            {
                Values = values, Name = name, IgnoresBarPosition = true, Fill = new SolidColorPaint(color),
                MaxBarWidth = 48,
                YToolTipLabelFormatter = point => Format(point.Coordinate.PrimaryValue),
            };
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Plain WPF legend - LiveCharts' own SkiaSharp legend reads blurry at most
    /// Windows display scales, same reason Ventes and Charges build theirs this way.</summary>
    private static void RenderSeriesLegend(Panel container, IReadOnlyList<(string Label, SKColor Color)> entries)
    {
        container.Children.Clear();
        foreach (var (label, color) in entries)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
            item.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0),
                Background = ToBrush(color), VerticalAlignment = VerticalAlignment.Center,
            });
            item.Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            container.Children.Add(item);
        }
    }

    private static void RenderPieLegend(Panel container, IReadOnlyList<(string Label, decimal Value, SKColor Color)> slices,
        IReadOnlyList<MargeCategoryDto> losing)
    {
        container.Children.Clear();
        var total = slices.Sum(s => s.Value);

        foreach (var slice in slices)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var dot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 3, 6, 0), Fill = ToBrush(slice.Color), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock
            {
                Text = $"{slice.Label} ({Percent(total > 0 ? slice.Value / total * 100 : 0)})",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
            });
            container.Children.Add(row);
        }

        if (losing.Count > 0)
        {
            var note = new TextBlock
            {
                Text = "Sans marge positive : " + string.Join(", ", losing.Select(c => c.Categorie)),
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            container.Children.Add(note);
        }
    }

    private SKColor ThemeColor(string key)
    {
        var c = ((SolidColorBrush)FindResource(key)).Color;
        return new SKColor(c.R, c.G, c.B, c.A);
    }

    private static SolidColorBrush ToBrush(SKColor c) => new(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));

    // --- Print ---

    // Explicit brushes rather than DynamicResource: this is paper, always white regardless of
    // dark mode, same reasoning as CaisseReportDialog/VenteReceiptDialog's own printouts.
    private static readonly Brush PrintInk = Brushes.Black;
    private static readonly Brush PrintMuted = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Brush PrintAccent = new SolidColorBrush(Color.FromRgb(0x4C, 0x3B, 0x9E));
    private static readonly Brush PrintLoss = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));

    /// <summary>
    /// Prints a summary built fresh from <see cref="_data"/> - Ctrl+P (wired via the
    /// <c>ApplicationCommands.Print</c> CommandBinding above) and the "Imprimer" button both
    /// land here. Deliberately not a snapshot of the live tab: <see cref="OverviewPanel"/> and
    /// <see cref="CategoriesPanel"/> hold LiveCharts controls, which render through SkiaSharp
    /// outside WPF's own visual tree - capturing them via VisualBrush/PrintVisual produced a
    /// truncated page and, printing to a PDF writer, crashed the app outright. A plain-WPF
    /// report built the same way <c>CaisseReportDialog</c> already prints one has no such risk,
    /// and the whole operation is wrapped in a try/catch besides: a failed printout should
    /// never be able to take the app down with it.
    /// </summary>
    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_data is not { } data) return;

        try
        {
            var paper = BuildPrintReport(data);
            paper.Measure(new Size(paper.Width, double.PositiveInfinity));
            paper.Arrange(new Rect(new Point(0, 0), paper.DesiredSize));
            paper.UpdateLayout();

            // Preview first; the printer is chosen from the preview window.
            PrintPreviewWindow.Show(
                Window.GetWindow(this),
                new VisualPaginator(paper, PrintPreviewWindow.A4, margin: 36),
                $"Analyse des Marges - {_session.Groupe?.Nom}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), $"Impossible d'imprimer : {ex.Message}", "Imprimer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Builds the printable page: header, the KPI tiles as plain rows, the points
    /// d'attention, and - for whichever tab is open - a simple table, so "Imprimer" always
    /// prints something relevant to what is currently on screen without depending on it.</summary>
    private Border BuildPrintReport(MargesResponse d)
    {
        var paper = new StackPanel();

        void Centered(string text, double size, FontWeight weight, Brush brush, double top = 0) =>
            paper.Children.Add(new TextBlock
            {
                Text = text, FontSize = size, FontWeight = weight, Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
            });

        void Section(string title) =>
            paper.Children.Add(new TextBlock
            {
                Text = title, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = PrintInk,
                Margin = new Thickness(0, 14, 0, 6),
            });

        void Row(string label, string value, bool bold = false, Brush? color = null)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = color ?? PrintMuted });
            var valueText = new TextBlock
            {
                Text = value, FontSize = 12, Foreground = color ?? PrintInk,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
            paper.Children.Add(grid);
        }

        void Divider() => paper.Children.Add(new Line
        {
            X1 = 0, Y1 = 0, X2 = 500, Y2 = 0, Stroke = PrintInk, StrokeThickness = 1,
            StrokeDashArray = [4, 2], Margin = new Thickness(0, 10, 0, 0),
        });

        void Table(string[] headers, double[] widths, IEnumerable<string[]> rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var w in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });

            void AddRow(string[] cells, bool header)
            {
                var r = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition());
                for (var c = 0; c < cells.Length; c++)
                {
                    var text = new TextBlock
                    {
                        Text = cells[c], FontSize = 10, Margin = new Thickness(0, 2, 6, 2),
                        FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = header ? PrintInk : PrintMuted, TextWrapping = TextWrapping.Wrap,
                    };
                    Grid.SetRow(text, r);
                    Grid.SetColumn(text, c);
                    grid.Children.Add(text);
                }
            }

            AddRow(headers, header: true);
            foreach (var row in rows) AddRow(row, header: false);
            paper.Children.Add(grid);
        }

        Centered(_session.Groupe?.Nom ?? "Lonnii", 16, FontWeights.Bold, PrintAccent);
        Centered("ANALYSE DES MARGES", 13, FontWeights.Bold, PrintAccent, top: 4);
        Centered(PeriodText.Text, 11, FontWeights.Normal, PrintMuted, top: 4);
        var category = (CategoryFilterCombo.SelectedItem as MargeCategoryOptionDto)?.Nom;
        if (!string.IsNullOrEmpty(category) && category != "Toutes les catégories")
            Centered(category, 11, FontWeights.Normal, PrintMuted, top: 2);
        Divider();

        Section("RÉSUMÉ");
        Row("Chiffre d'affaires", Amount(d.TotalRevenue), bold: true);
        Row("Coût des ventes", Amount(d.TotalCosts));
        Row("Marge brute", Amount(d.GrossProfit), bold: true, color: d.GrossProfit >= 0 ? PrintInk : PrintLoss);
        Row("Taux de marque", Percent(d.GrossMargin));
        Row("Charges (fixes + variables)", Amount(d.TotalCharges));
        Row("Bénéfice net", Amount(d.NetProfit), bold: true, color: d.NetProfit >= 0 ? PrintInk : PrintLoss);
        Row("Marge nette", Percent(d.NetMargin));

        Divider();
        Section("RATIOS");
        var markup = d.TotalCosts > 0 ? d.GrossProfit / d.TotalCosts * 100 : (decimal?)null;
        var coefficient = d.TotalCosts > 0 ? d.TotalRevenue / d.TotalCosts : (decimal?)null;
        Row("Taux de marge (sur coût)", markup is { } m ? Percent(m) : "—");
        Row("Coefficient multiplicateur", coefficient is { } c ? Money.FormatPlain(c, 2) : "—");
        Row("Marge sur coûts variables", Amount(d.MargeCoutsVariables));
        Row("Taux de MCV", Percent(d.TauxMcv));
        if (d.SeuilRentabilite is { } seuil)
        {
            Row("Seuil de rentabilité", Amount(seuil));
            if (d.PointMortDate is { } date) Row("Point mort", date.ToString("dd/MM/yyyy"));
        }

        var negative = d.Products.Count(p => p.Profit < 0);
        var low = d.Products.Count(p => p.Margin < LowMarginThreshold);
        var estimated = d.Products.Count(p => p.CostSource == MargeCostSources.Estime);
        if (negative > 0 || low > 0 || estimated > 0 || (d.TotalCharges > 0 && d.GrossProfit < d.TotalCharges))
        {
            Divider();
            Section("POINTS D'ATTENTION");
            if (negative > 0) Row($"{negative} produit(s) vendu(s) à perte", string.Empty, color: PrintLoss);
            if (low > 0) Row($"{low} produit(s) avec un taux de marque inférieur à {Money.FormatPlain(LowMarginThreshold)} %", string.Empty);
            if (estimated > 0) Row($"{estimated} produit(s) sans prix d'achat connu (marge estimée)", string.Empty);
            if (d.TotalCharges > 0 && d.GrossProfit < d.TotalCharges)
                Row("La marge brute ne couvre pas les charges", $"-{Amount(d.TotalCharges - d.GrossProfit)}", color: PrintLoss);
        }

        if (_activeTab == "products")
        {
            var products = FilteredProducts();
            Divider();
            Section($"PRODUITS ({products.Count})");
            Table(["Produit", "Catégorie", "Qté", "CA", "Coût", "Marge", "Taux"],
                [140, 90, 50, 80, 80, 80, 60],
                products.Select(p => new[]
                {
                    p.Nom, p.Categorie, Money.FormatPlain(p.QuantiteVendue), Amount(p.Revenue),
                    Amount(p.Cost), Amount(p.Profit), Percent(p.Margin),
                }));
        }
        else if (_activeTab == "categories")
        {
            Divider();
            Section($"CATÉGORIES ({d.Categories.Count})");
            Table(["Catégorie", "Qté", "CA", "Coût", "Marge", "Taux"],
                [140, 60, 90, 90, 90, 70],
                d.Categories.Select(c => new[]
                {
                    c.Categorie, Money.FormatPlain(c.QuantiteVendue), Amount(c.Revenue),
                    Amount(c.Cost), Amount(c.Profit), Percent(c.Margin),
                }));
        }

        Divider();
        Centered($"Imprimé le {DateTime.Now:dd/MM/yyyy à HH:mm}", 10, FontWeights.Normal, PrintMuted, top: 6);

        return new Border { Width = 700, Padding = new Thickness(24), Background = Brushes.White, Child = paper };
    }

    // --- Export ---

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_data is not { } data || data.Products.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "Aucune donnée à exporter.", "Exporter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupName = _session.Groupe?.Nom ?? "export";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{groupName.Trim().ToLowerInvariant().Replace(' ', '_')}_marges_{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "Fichier CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            await System.IO.File.WriteAllTextAsync(dialog.FileName, BuildCsv(data), new System.Text.UTF8Encoding(true));
        }
        catch (System.IO.IOException ex)
        {
            ShowMessage($"Erreur lors de l'export : {ex.Message}");
        }
    }

    /// <summary>Same layout as Lonnii Business's export - a header block, one row per
    /// product, then the financial summary - plus the category breakdown, with every amount
    /// written "1 000,25".</summary>
    private string BuildCsv(MargesResponse d)
    {
        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        static string N(decimal value) => Money.FormatPlain(value, 2);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== RAPPORT DES MARGES - {(_session.Groupe?.Nom ?? "Export").ToUpperInvariant()} ===");
        sb.AppendLine($"Date d'export;{DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Période;{Csv(PeriodText.Text)}");
        sb.AppendLine($"Catégorie;{Csv((CategoryFilterCombo.SelectedItem as MargeCategoryOptionDto)?.Nom ?? "Toutes les catégories")}");
        sb.AppendLine($"Nombre de produits;{d.Products.Count}");
        sb.AppendLine();

        sb.AppendLine(string.Join(';', "Produit", "Catégorie", "Quantité vendue", $"CA ({Money.Label})",
            $"Coût ({Money.Label})", $"Marge ({Money.Label})", "Taux de marque (%)", "Coût basé sur"));
        foreach (var p in d.Products)
        {
            sb.AppendLine(string.Join(';', Csv(p.Nom), Csv(p.Categorie), p.QuantiteVendue,
                N(p.Revenue), N(p.Cost), N(p.Profit), N(p.Margin),
                p.CostSource switch { MargeCostSources.Reel => "Prix d'achat", MargeCostSources.Estime => "Estimation", _ => "Sans coût" }));
        }

        sb.AppendLine();
        sb.AppendLine("--- MARGES PAR CATÉGORIE ---");
        sb.AppendLine(string.Join(';', "Catégorie", "Quantité vendue", $"CA ({Money.Label})", $"Coût ({Money.Label})",
            $"Marge ({Money.Label})", "Taux de marque (%)"));
        foreach (var c in d.Categories)
            sb.AppendLine(string.Join(';', Csv(c.Categorie), c.QuantiteVendue, N(c.Revenue), N(c.Cost), N(c.Profit), N(c.Margin)));

        sb.AppendLine();
        sb.AppendLine("--- RÉSUMÉ FINANCIER ---");
        sb.AppendLine($"Chiffre d'affaires;{N(d.TotalRevenue)}");
        sb.AppendLine($"Coût des ventes;{N(d.TotalCosts)}");
        sb.AppendLine($"Marge brute;{N(d.GrossProfit)};{N(d.GrossMargin)} %");
        sb.AppendLine($"Charges;{N(d.TotalCharges)}");
        sb.AppendLine($"Bénéfice net;{N(d.NetProfit)};{N(d.NetMargin)} %");
        sb.AppendLine($"Dont charges fixes;{N(d.ChargesFixes)}");
        sb.AppendLine($"Dont charges variables;{N(d.ChargesVariables)}");
        sb.AppendLine($"Marge sur coûts variables;{N(d.MargeCoutsVariables)};{N(d.TauxMcv)} %");
        if (d.SeuilRentabilite is { } seuil)
        {
            sb.AppendLine($"Seuil de rentabilité (charges fixes ÷ taux de MCV);{N(seuil)}");
            if (d.PointMortDate is { } date) sb.AppendLine($"Point mort;{date:dd/MM/yyyy}");
            sb.AppendLine($"Marge de sécurité;{N(d.TotalRevenue - seuil)}");
        }
        if (d.EstimatedLines > 0)
            sb.AppendLine($"Lignes à coût estimé;{d.EstimatedLines};CA concerné : {N(d.EstimatedRevenue)}");

        return sb.ToString();
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;
}
