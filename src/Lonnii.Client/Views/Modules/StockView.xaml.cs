using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lonnii.Client.Services;
using Lonnii.Client.Views.Dialogs;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Win32;
using SkiaSharp;

namespace Lonnii.Client.Views.Modules;

/// <summary>
/// Gestion de Stock. Every command is enabled only when the user holds the privilege
/// the API will check, so a button never leads to a refusal the user could not predict.
/// </summary>
public partial class StockView : UserControl
{
    private readonly AppSession _session;
    private List<ProductDto> _products = [];
    private List<CategoryDto> _categories = [];
    private List<ProductDto> _pageItems = [];
    private bool _iconView = true;
    private bool _analyseTab;
    private string _sortField = "Nom";
    private bool _sortDescending;

    /// <summary>Gates the Analyse tab's "Marge potentielle" tile, the same way
    /// <c>can_add_payment</c> gates the Caisse button - margin reveals cost price, which a
    /// preparer who can see stock is not automatically meant to see.</summary>
    private readonly bool _canViewMarges;

    /// <summary>Gates the "Mouvements de Stock" table on Analyse - the same privilege that
    /// gates the per-product history dialog.</summary>
    private readonly bool _canViewStockHistory;

    /// <summary>Gates the Analyse tab itself, previously visible to anyone who could open
    /// Stock at all - can_view_analytics and can_view_stock_analytics are documented aliases
    /// of each other (PrivilegeAliases), so either grants it.</summary>
    private readonly bool _canViewStockAnalytics;

    private DateOnly? _movementDateDebut;
    private DateOnly? _movementDateFin;

    /// <summary>French label for each <c>Lonnii.Data.Entities.StockMovementTypes</c> constant,
    /// same wording as <see cref="Dialogs.StockAdjustDialog"/>'s movement dropdown so a type
    /// reads identically wherever it appears.</summary>
    private static readonly Dictionary<string, string> MovementTypeLabels = new(StringComparer.Ordinal)
    {
        ["ajout"] = "Entrée en stock",
        ["vente"] = "Vente",
        ["retour"] = "Retour client",
        ["adjustment"] = "Correction d'inventaire",
        ["transfer"] = "Transfert sortant",
        ["damaged"] = "Casse ou perte",
        ["expired"] = "Périmé",
    };

    /// <summary>One row of the "Quantités par Produit" table.</summary>
    private sealed record ProductRow(ProductDto Product, bool ShowMarge)
    {
        public string Name => Product.Name;
        public string? CategoryName => Product.CategoryName;
        public string QuantityDisplay => Product.QuantityDisplay;

        // Stock indéfini means there is no quantity to multiply a price by at all - Quantity
        // reads 0 in that case, and showing "0 FCFA" would read as "this product is worthless"
        // rather than "this product's stock is not counted". A Vente Libre product that kept a
        // real reference quantity (Stock indéfini unchecked) still gets a real figure here.
        public string SaleValueDisplay => Product.StockIllimite ? "—" : Money.Format(Product.Price * Product.Quantity);
        public string AchatValueDisplay => Product.StockIllimite ? "—" : Money.Format((Product.CostPrice ?? 0) * Product.Quantity);
        public string MargeDisplay => !ShowMarge ? "—"
            : Product.StockIllimite ? "—"
            : Money.FormatPlain((Product.Price - (Product.CostPrice ?? 0)) * Product.Quantity, 2);
    }

    /// <summary>One row of the "Mouvements de Stock" table.</summary>
    private sealed record MovementRow(StockMovementStatDto Stat)
    {
        public string Label => MovementTypeLabels.GetValueOrDefault(Stat.MovementType, Stat.MovementType);
        public int Count => Stat.Count;
        public string QuantityDisplay => Money.FormatPlain(Stat.TotalQuantity);
        public string CostDisplay => Money.Format(Stat.TotalCost);
    }

    /// <summary>
    /// The whole catalogue, unfiltered by the Stock tab - Analyse looks at everything by
    /// default, through its own filter below the charts, not whatever the Stock list
    /// happens to be narrowed to. Null until Analyse is opened for the first time; cleared
    /// whenever the Stock tab's data changes, so the next open re-fetches rather than
    /// showing stale numbers.
    /// </summary>
    private List<ProductDto>? _allProducts;
    private string? _analyseCategoryId;
    private bool _analyseLowStockOnly;

    /// <summary>Products load one page at a time so a large catalogue never means downloading
    /// thumbnails for, or rendering, hundreds of cards at once.</summary>
    private const int PageSize = 24;
    private int _page;

    /// <summary>Thumbnails already downloaded, keyed by image URL, so switching views or
    /// refreshing the list does not re-download a photo it already has.</summary>
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = [];

    /// <summary>Sentinel for the "all categories" row of the filter.</summary>
    private static readonly CategoryDto AllCategories = new("", "Toutes les catégories", null, null, null, null, true, 0);

    /// <summary>Same 8-colour palette as Ventes' Statistiques charts, so a pie chart reads
    /// the same way regardless of which module it is in.</summary>
    private static readonly SKColor[] CategoryPalette =
    [
        new(0x25, 0x63, 0xEB), new(0x10, 0xB9, 0x81), new(0xF5, 0x9E, 0x0B), new(0xEF, 0x44, 0x44),
        new(0x8B, 0x5C, 0xF6), new(0xEC, 0x48, 0x99), new(0x06, 0xB6, 0xD4), new(0x84, 0xCC, 0x16),
    ];

    /// <summary>
    /// Waits for a short pause in typing before searching, so every keystroke does not
    /// fire its own request against the API.
    /// </summary>
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    /// <summary>A product paired with its downloaded thumbnail, for the icon view's cards.</summary>
    private sealed record IconRow(ProductDto Product, BitmapImage? Thumbnail)
    {
        public bool HasNoThumbnail => Thumbnail is null;
    }

    public StockView(AppSession session)
    {
        _session = session;
        _canViewMarges = _session.Can(Priv.Gestion.ViewMarges);
        _canViewStockHistory = _session.Can(Priv.Gestion.ViewStockHistory);
        _canViewStockAnalytics = _session.Can(Priv.Gestion.ViewAnalytics);
        InitializeComponent();

        MovementFilterPanel.Visibility = _canViewStockHistory ? Visibility.Visible : Visibility.Collapsed;
        MovementStatsPanel.Visibility = _canViewStockHistory ? Visibility.Visible : Visibility.Collapsed;
        ProductMargeColumn.Visibility = _canViewMarges ? Visibility.Visible : Visibility.Collapsed;
        AnalyseTabButton.Visibility = _canViewStockAnalytics ? Visibility.Visible : Visibility.Collapsed;

        ApplyPrivileges();
        ApplyViewModeVisuals();
        ApplyTabVisuals();
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await LoadAsync();
        };
        Loaded += async (_, _) =>
        {
            // Reopen on Analyse if that is where the user was before an "Actualiser" or a
            // restart, and they still hold the privilege; LoadAsync then loads it too.
            if (_canViewStockAnalytics && UiState.For(_session).Tabs.GetValueOrDefault(ModuleKey) == "analyse")
                SetActiveTab(analyse: true);
            await LoadAsync();
        };

        // LiveCharts paints are plain SkiaSharp colours snapshotted at render time, not
        // DynamicResource-aware - same reasoning as Ventes' Statistiques charts - so a
        // theme toggle while Analyse is open would otherwise leave the charts' axis and
        // gridline colours stuck on whichever theme was active when they last rendered.
        ThemeManager.Changed += (_, _) =>
        {
            if (_analyseTab) UpdateAnalyse();
        };
    }

    /// <summary>Matches the toolbar to what the signed-in user may actually do.</summary>
    private void ApplyPrivileges()
    {
        AddButton.IsEnabled = _session.Can(Priv.Gestion.AddProducts);
        EditMenuItem.IsEnabled = _session.Can(Priv.Gestion.EditProducts);
        AdjustMenuItem.IsEnabled = _session.Can(Priv.Gestion.AdjustStock);
        DeleteMenuItem.IsEnabled = _session.Can(Priv.Gestion.DeleteProducts);
        HistoryMenuItem.IsEnabled = _session.Can(Priv.Gestion.ViewStockHistory);
        CategoriesMenuItem.IsEnabled = _session.Can(Priv.Gestion.ManageCategories);
        ExportButton.IsEnabled = _session.Can(Priv.Gestion.ExportStockData);

        // An admin-only privilege never resolves true for a member, so say why it is greyed out.
        if (!DeleteMenuItem.IsEnabled)
            DeleteMenuItem.ToolTip = "Réservé aux administrateurs";
        if (!ExportButton.IsEnabled)
            ExportButton.ToolTip = "Réservé aux administrateurs";
    }

    private async Task LoadAsync(bool resetPage = true)
    {
        SetBusy(true);
        try
        {
            if (_categories.Count == 0)
            {
                _categories = await _session.Api.GetCategoriesAsync();
                CategoryFilter.ItemsSource = new[] { AllCategories }.Concat(_categories).ToList();
                CategoryFilter.SelectedIndex = 0;
            }

            var categoryId = (CategoryFilter.SelectedItem as CategoryDto)?.Id;
            if (string.IsNullOrEmpty(categoryId)) categoryId = null;

            _products = await _session.Api.GetProductsAsync(
                search: SearchBox.Text,
                categoryId: categoryId,
                lowStockOnly: LowStockCheck.IsChecked == true);

            ApplySort();

            if (resetPage) _page = 0;
            await ApplyPageAsync();

            HideMessage();
            UpdateSummary();
            UpdateEmptyState();

            // The catalogue may have changed (a save, a delete, a stock adjustment) - drop
            // the cached copy Analyse uses so it re-fetches next time it needs one, instead
            // of quietly showing numbers from before the change.
            _allProducts = null;
            if (_analyseTab) await LoadAnalyseAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            SetBusy(false);
            Grid_SelectionChanged(this, null!);
        }
    }

    /// <summary>Slices the already-fetched, already-filtered <see cref="_products"/> down to
    /// the current page and binds it to whichever view is active. Does not touch the API.</summary>
    private async Task ApplyPageAsync()
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_products.Count / (double)PageSize));
        _page = Math.Clamp(_page, 0, totalPages - 1);

        _pageItems = _products.Skip(_page * PageSize).Take(PageSize).ToList();
        ProductGrid.ItemsSource = _pageItems;
        if (_iconView) await LoadIconViewAsync();

        PageText.Text = $"Page {_page + 1} / {totalPages}";
        PrevPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = _page < totalPages - 1;
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        _page--;
        await ApplyPageAsync();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        await ApplyPageAsync();
    }

    private void UpdateSummary()
    {
        var lowStock = _products.Count(p => p.IsLowStock);
        var stockValue = _products.Sum(p => p.Price * p.Quantity);

        var summary = $"{_products.Count} produit(s)  •  valeur du stock : {Money.Format(stockValue)}";
        if (lowStock > 0) summary += $"  •  {lowStock} en stock bas";

        SummaryText.Text = summary;
    }

    private void UpdateEmptyState()
    {
        var isEmpty = _products.Count == 0;
        EmptyPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

        if (!isEmpty) return;

        var isFiltered = !string.IsNullOrWhiteSpace(SearchBox.Text)
                         || LowStockCheck.IsChecked == true
                         || !string.IsNullOrEmpty((CategoryFilter.SelectedItem as CategoryDto)?.Id);

        if (isFiltered)
        {
            EmptyTitle.Text = "Aucun résultat";
            EmptyHint.Text = "Aucun produit ne correspond à votre recherche. Modifiez les filtres pour élargir la liste.";
        }
        else
        {
            EmptyTitle.Text = "Aucun produit";
            EmptyHint.Text = AddButton.IsEnabled
                ? "Votre inventaire est vide. Créez votre premier produit pour commencer."
                : "Votre inventaire est vide. Un administrateur doit y ajouter des produits.";
        }
    }

    private ProductDto? Selected => ProductGrid.SelectedItem as ProductDto;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = Selected is not null;
        var tracksStock = Selected is { VenteLibre: false, StockIllimite: false };
        EditMenuItem.IsEnabled = hasSelection && _session.Can(Priv.Gestion.EditProducts);
        AdjustMenuItem.IsEnabled = hasSelection && tracksStock && _session.Can(Priv.Gestion.AdjustStock);
        DeleteMenuItem.IsEnabled = hasSelection && _session.Can(Priv.Gestion.DeleteProducts);
        HistoryMenuItem.IsEnabled = hasSelection && _session.Can(Priv.Gestion.ViewStockHistory);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void ListView_Click(object sender, RoutedEventArgs e) => await SetIconViewAsync(false);

    private async void IconView_Click(object sender, RoutedEventArgs e) => await SetIconViewAsync(true);

    private async Task SetIconViewAsync(bool iconView)
    {
        if (_iconView == iconView) return;
        _iconView = iconView;
        ApplyViewModeVisuals();

        if (iconView) await LoadIconViewAsync();
    }

    private void ApplyViewModeVisuals()
    {
        ListViewButton.Style = (Style)FindResource(_iconView ? "SecondaryButton" : "PrimaryButton");
        IconViewButton.Style = (Style)FindResource(_iconView ? "PrimaryButton" : "SecondaryButton");
        ProductGrid.Visibility = _iconView ? Visibility.Collapsed : Visibility.Visible;
        IconScrollViewer.Visibility = _iconView ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Re-sorts the already-fetched <see cref="_products"/> in place by whichever
    /// field and direction the toolbar's sort controls hold. No API call - the whole
    /// filtered set is already in memory.</summary>
    private void ApplySort()
    {
        IOrderedEnumerable<ProductDto> sorted = _sortField switch
        {
            "Quantité" => _products.OrderBy(p => p.Quantity),
            "Prix d'achat" => _products.OrderBy(p => p.CostPrice ?? 0),
            "Prix de vente" => _products.OrderBy(p => p.Price),
            _ => _products.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        _products = (_sortDescending ? sorted.Reverse() : sorted).ToList();
    }

    private async void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _sortField = (SortField.SelectedItem as ComboBoxItem)?.Content as string ?? "Nom";
        ApplySort();
        await ApplyPageAsync();
    }

    private async void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;
        SortDirectionButton.Content = _sortDescending ? "▼" : "▲";
        SortDirectionButton.ToolTip = _sortDescending ? "Ordre décroissant" : "Ordre croissant";
        ApplySort();
        await ApplyPageAsync();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.IsOpen = true;
    }

    /// <summary>
    /// Right-clicking a row opens the same "⋯ Plus" menu as the toolbar button, on the
    /// product under the cursor. A DataGrid only selects on a left click by default, so
    /// the row is selected here first - otherwise the menu would act on whatever was
    /// selected before, not the row the user actually right-clicked.
    /// </summary>
    private void ProductGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row) return;

        row.IsSelected = true;
        MoreMenu.PlacementTarget = row;
        MoreMenu.IsOpen = true;
    }

    /// <summary>Same as <see cref="ProductGrid_PreviewMouseRightButtonDown"/>, for a card in
    /// the icon view.</summary>
    private void IconGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } item) return;

        item.IsSelected = true;
        MoreMenu.PlacementTarget = item;
        MoreMenu.IsOpen = true;
    }

    /// <summary>
    /// Clears the selection on a click that lands on neither a row nor a card - the
    /// grid's own background, the space past the last item, a column header. A click that
    /// actually lands on a row or a card is left alone; selecting it is that click's job.
    /// </summary>
    private void StockContentGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<DataGridRow>(source) is not null) return;
        if (FindAncestor<ListBoxItem>(source) is not null) return;

        ProductGrid.SelectedItem = null;
        IconGrid.SelectedItem = null;
    }

    /// <summary>Walks up the visual tree from <paramref name="current"/> for the nearest
    /// ancestor of type <typeparamref name="T"/>, or null if there is none.</summary>
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // Setting SelectedDate fires SelectedDateChanged (wired to Filter_Changed), which
    // reloads - no separate reload needed here.
    /// <summary>Writes the currently filtered and sorted products to a CSV file the user
    /// picks. Runs entirely client-side against data already loaded - there is no export
    /// endpoint on the API to call.</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var espaceName = _session.Groupe?.Nom;
        var fileNamePart = string.IsNullOrWhiteSpace(espaceName)
            ? "stock"
            : $"stock-{SanitizeFileName(espaceName)}";

        var dialog = new SaveFileDialog
        {
            FileName = $"{fileNamePart}-{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "Fichier CSV (*.csv)|*.csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, System.Text.Encoding.UTF8);
            writer.WriteLine(CsvField(string.IsNullOrWhiteSpace(espaceName)
                ? "Gestion de Stock"
                : $"Gestion de Stock — {espaceName}"));
            writer.WriteLine(CsvField($"Exporté le {DateTime.Now:dd/MM/yyyy HH:mm}"));
            writer.WriteLine();
            writer.WriteLine("Produit;SKU;Catégorie;Quantité;Seuil;Prix d'achat;Prix de vente;Emplacement");
            foreach (var p in _products)
            {
                writer.WriteLine(string.Join(';',
                    CsvField(p.Name), CsvField(p.Sku), CsvField(p.CategoryName),
                    p.Quantity, p.MinimumThreshold, p.CostPrice ?? 0, p.Price, CsvField(p.StorageLocation)));
            }

            MessageBox.Show(Window.GetWindow(this),
                $"{_products.Count} produit(s) exporté(s) vers\n{dialog.FileName}",
                "Export terminé", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (IOException ex)
        {
            ShowMessage($"Échec de l'export : {ex.Message}");
        }
    }

    private static string CsvField(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return cleaned.Trim();
    }

    private void StockTab_Click(object sender, RoutedEventArgs e) => SetActiveTab(analyse: false);

    private async void AnalyseTab_Click(object sender, RoutedEventArgs e)
    {
        SetActiveTab(analyse: true);
        await LoadAnalyseAsync();
    }

    private const string ModuleKey = "gestion-de-stock";

    private void SetActiveTab(bool analyse)
    {
        if (_analyseTab == analyse) return;
        _analyseTab = analyse;
        ApplyTabVisuals();

        UiState.For(_session).Tabs[ModuleKey] = analyse ? "analyse" : "stock";
        UiState.Save();
    }

    /// <summary>
    /// Fetches the whole catalogue the first time Analyse is opened (or again after the
    /// Stock tab invalidated it), fills the category filter the first time, and renders.
    /// A no-op past the first open in a session where nothing has changed since.
    /// </summary>
    private async Task LoadAnalyseAsync()
    {
        try
        {
            _allProducts ??= await _session.Api.GetProductsAsync(search: null, categoryId: null, lowStockOnly: false);

            if (AnalyseCategoryFilter.ItemsSource is null)
            {
                AnalyseCategoryFilter.ItemsSource = new[] { AllCategories }.Concat(_categories).ToList();
                AnalyseCategoryFilter.SelectedIndex = 0;
            }

            UpdateAnalyse();
            if (_canViewStockHistory) await LoadMovementStatsAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    /// <summary>Analyse's own filter - separate from <see cref="Filter_Changed"/>, which
    /// belongs to the Stock tab's list.</summary>
    private async void AnalyseFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _allProducts is null) return;

        _analyseCategoryId = (AnalyseCategoryFilter.SelectedItem as CategoryDto)?.Id;
        if (string.IsNullOrEmpty(_analyseCategoryId)) _analyseCategoryId = null;
        _analyseLowStockOnly = AnalyseLowStockCheck.IsChecked == true;

        UpdateAnalyse();

        // The category filter narrows the movement table too (it has no low-stock
        // equivalent of its own - a movement is not "low stock" or not), so only a category
        // change, not the low-stock checkbox, needs to re-fetch it.
        if (_canViewStockHistory && sender == AnalyseCategoryFilter) await LoadMovementStatsAsync();
    }

    private async void MovementFilter_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded) return;

        _movementDateDebut = MovementDateDebutPicker.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;
        _movementDateFin = MovementDateFinPicker.SelectedDate is { } f ? DateOnly.FromDateTime(f) : null;

        await LoadMovementStatsAsync();
    }

    /// <summary>Guards <see cref="LoadMovementStatsAsync"/> against an older request (e.g. the
    /// unfiltered load that fires when Analyse first opens) resolving after a newer one (a
    /// date just picked) and silently overwriting it - without this, whichever of two
    /// in-flight requests happens to complete last wins, regardless of which was sent last,
    /// which looks exactly like "picking a date did nothing".</summary>
    private int _movementRequestId;

    /// <summary>Fetches and renders the "Mouvements de Stock" table for the current category
    /// and date-range filter. A no-op unless the caller holds can_view_stock_history - the
    /// table stays empty and hidden for anyone without it.</summary>
    private async Task LoadMovementStatsAsync()
    {
        var requestId = ++_movementRequestId;
        try
        {
            var response = await _session.Api.GetStockMovementStatsAsync(
                _movementDateDebut, _movementDateFin, _analyseCategoryId);
            if (requestId != _movementRequestId) return;

            var rows = response.Movements.OrderByDescending(m => m.TotalCost)
                .Select(m => new MovementRow(m)).ToList();
            MovementStatsGrid.ItemsSource = rows;
            MovementStatsEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (ApiException ex)
        {
            if (requestId == _movementRequestId) ShowMessage(ex.Message);
        }
    }

    /// <summary>
    /// Lets the mouse wheel keep scrolling the Analyse page's outer ScrollViewer even when
    /// the cursor is over one of its DataGrids. A DataGrid owns its own internal ScrollViewer
    /// and marks every wheel tick as handled regardless of whether it actually has anything
    /// left to scroll, so without this the user has to move off the table entirely (e.g. onto
    /// a chart) just to keep scrolling the page.
    /// </summary>
    private void AnalyseGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindAncestorScrollViewer((DependencyObject)sender) is { } scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
    {
        var parent = VisualTreeHelper.GetParent(element);
        while (parent is not null and not ScrollViewer)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as ScrollViewer;
    }

    private void ApplyTabVisuals()
    {
        var mutedBrush = (Brush)FindResource("TextMuted");
        var accentBrush = (Brush)FindResource("Accent");
        var onAccentBrush = (Brush)FindResource("TextOnAccent");
        var transparent = Brushes.Transparent;

        StockTabButton.Background = _analyseTab ? transparent : accentBrush;
        StockTabButton.Foreground = _analyseTab ? mutedBrush : onAccentBrush;
        StockTabButton.FontWeight = _analyseTab ? FontWeights.Normal : FontWeights.SemiBold;
        AnalyseTabButton.Background = _analyseTab ? accentBrush : transparent;
        AnalyseTabButton.Foreground = _analyseTab ? onAccentBrush : mutedBrush;
        AnalyseTabButton.FontWeight = _analyseTab ? FontWeights.SemiBold : FontWeights.Normal;

        StockToolsPanel.Visibility = _analyseTab ? Visibility.Collapsed : Visibility.Visible;
        AnalyseToolsPanel.Visibility = _analyseTab ? Visibility.Visible : Visibility.Collapsed;
        PaginationPanel.Visibility = _analyseTab ? Visibility.Collapsed : Visibility.Visible;
        StockContentGrid.Visibility = _analyseTab ? Visibility.Collapsed : Visibility.Visible;
        AnalysePanel.Visibility = _analyseTab ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Recomputes the Analyse tab's stat cards from <see cref="_allProducts"/>,
    /// narrowed by Analyse's own filter row - not <see cref="_products"/>, which belongs to
    /// the Stock tab. A no-op until <see cref="LoadAnalyseAsync"/> has fetched the catalogue
    /// at least once.</summary>
    private void UpdateAnalyse()
    {
        if (_allProducts is null) return;

        var products = _allProducts
            .Where(p => _analyseCategoryId is null || p.CategoryId == _analyseCategoryId)
            .Where(p => !_analyseLowStockOnly || p.IsLowStock)
            .ToList();

        AnalyseStatsPanel.Children.Clear();

        var count = products.Count;
        var lowStock = products.Count(p => p.IsLowStock);
        var saleValue = products.Sum(p => p.Price * p.Quantity);
        var costValue = products.Sum(p => (p.CostPrice ?? 0) * p.Quantity);
        var margin = saleValue - costValue;

        AnalyseStatsPanel.Children.Add(StatCard("Produits", count.ToString(), (Brush)FindResource("Accent")));
        AnalyseStatsPanel.Children.Add(StatCard("Valeur du stock (vente)", Money.Format(saleValue), (Brush)FindResource("Accent")));
        AnalyseStatsPanel.Children.Add(StatCard("Valeur du stock (achat)", Money.Format(costValue), (Brush)FindResource("TextSecondary")));
        if (_canViewMarges)
            AnalyseStatsPanel.Children.Add(StatCard("Marge potentielle", Money.FormatPlain(margin, 2), (Brush)FindResource("Success")));
        AnalyseStatsPanel.Children.Add(StatCard("En stock bas", lowStock.ToString(),
            (Brush)FindResource(lowStock > 0 ? "Danger" : "TextSecondary")));

        RenderAnalyseCharts(products);

        var rows = products.OrderByDescending(p => p.Price * p.Quantity)
            .Select(p => new ProductRow(p, _canViewMarges)).ToList();
        ProductQuantityGrid.ItemsSource = rows;
        ProductQuantityEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Rebuilds the Analyse tab's three charts from <paramref name="products"/>:
    /// where the stock's value sits by category, how many products are running low, and
    /// which individual products tie up the most of it.</summary>
    private void RenderAnalyseCharts(List<ProductDto> products)
    {
        var axisPaint = new SolidColorPaint(CurrentTextColor());
        var separatorPaint = new SolidColorPaint(CurrentBorderColor()) { StrokeThickness = 1 };

        // Valeur du Stock par Catégorie
        var categorySlices = products
            .GroupBy(p => p.CategoryName ?? "Sans catégorie")
            .Select(g => (Label: g.Key, Value: g.Sum(p => p.Price * p.Quantity)))
            .Where(c => c.Value > 0)
            .OrderByDescending(c => c.Value)
            .Select((c, i) => (c.Label, c.Value, Color: CategoryPalette[i % CategoryPalette.Length]))
            .ToList();
        CategoryValueEmptyText.Visibility = categorySlices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CategoryValuePieChart.Series = categorySlices.Select(c => (ISeries)new PieSeries<double>
        {
            Values = [(double)c.Value], Name = c.Label, Fill = new SolidColorPaint(c.Color),
        }).ToArray();
        RenderPieLegend(CategoryValueLegendPanel, categorySlices);

        // Category -> colour, so a product's bar below reads as the same category the pie
        // above shows it as. A category with no stock value has no slice above and falls
        // back to grey rather than going uncoloured.
        var categoryColors = categorySlices.ToDictionary(c => c.Label, c => c.Color, StringComparer.Ordinal);

        // État du Stock: low vs healthy, counting only products that actually track stock -
        // one sold without tracking or with unlimited stock is neither.
        var tracked = products.Where(p => !p.VenteLibre && !p.StockIllimite).ToList();
        var lowCount = tracked.Count(p => p.IsLowStock);
        var healthSlices = new (string Label, decimal Value, SKColor Color)[]
        {
            ("Stock normal", tracked.Count - lowCount, CategoryPalette[1]),
            ("Stock bas", lowCount, CategoryPalette[3]),
        }.Where(s => s.Value > 0).ToList();
        StockHealthEmptyText.Visibility = healthSlices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StockHealthPieChart.Series = healthSlices.Select(s => (ISeries)new PieSeries<double>
        {
            Values = [(double)s.Value], Name = s.Label, Fill = new SolidColorPaint(s.Color),
        }).ToArray();
        RenderPieLegend(StockHealthLegendPanel, healthSlices);

        // Produits Immobilisant le Plus de Valeur (top 8, highest first at the top). The
        // currency lives in the title, not repeated at every gridline - "20 000", "40 000"
        // and so on already crowd each other at this width once "F CFA" is tacked onto each.
        TopStockValueTitle.Text = $"Produits Immobilisant le Plus de Valeur ({Money.Label})";

        var topByValue = products
            .Select(p => (p.Name, Value: p.Price * p.Quantity, Category: p.CategoryName ?? "Sans catégorie"))
            .Where(p => p.Value > 0)
            .OrderByDescending(p => p.Value)
            .Take(8)
            .Reverse()
            .ToList();
        TopStockValueEmptyText.Visibility = topByValue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // One series per bar rather than one series for all of them, each confined to its
        // own row via IgnoresBarPosition - the only way in LiveCharts to give each bar its
        // own colour. Every other index is NaN, not 0: a real 0 would still draw a
        // (invisible) point and add a "0" line to every other bar's tooltip; NaN is treated
        // as no point there at all.
        TopStockValueChart.Series = topByValue.Select((p, i) =>
        {
            var values = new double[topByValue.Count];
            Array.Fill(values, double.NaN);
            values[i] = (double)p.Value;

            return (ISeries)new RowSeries<double>
            {
                Values = values,
                IgnoresBarPosition = true,
                Fill = new SolidColorPaint(categoryColors.GetValueOrDefault(p.Category, SKColors.Gray)),
                Name = p.Name,
                // PrimaryValue is NaN for every slot but this bar's own (see the Array.Fill
                // comment above) - LiveCharts evaluates this formatter for all of them, not
                // only the one actually drawn, and (decimal)double.NaN throws OverflowException
                // rather than returning something sensible.
                YToolTipLabelFormatter = point => double.IsFinite(point.Coordinate.PrimaryValue)
                    ? Money.Format((decimal)point.Coordinate.PrimaryValue)
                    : string.Empty,
            };
        }).ToArray();
        TopStockValueChart.YAxes =
        [
            new Axis
            {
                Labels = topByValue.Select(p => p.Name).ToArray(),
                LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint,
                TextSize = 10,
            },
        ];
        // The value axis for a row series is horizontal (X), unlike a column series.
        TopStockValueChart.XAxes =
        [
            new Axis
            {
                LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint,
                // Same guard as YToolTipLabelFormatter above - an axis whose only series is
                // all-NaN placeholders (e.g. Analyse filtered down to zero products) can hand
                // this a non-finite tick value.
                Labeler = v => double.IsFinite(v) ? Money.FormatPlain((decimal)v) : string.Empty,
            },
        ];
    }

    /// <summary>
    /// A plain WPF legend for a pie chart - colour dot, label, and its share of the total in
    /// parentheses - built natively rather than through LiveCharts' own SkiaSharp-rendered
    /// legend, which reads blurry at most Windows display scales. Same as Ventes' Statistiques.
    /// </summary>
    private static void RenderPieLegend(Panel container, IReadOnlyList<(string Label, decimal Value, SKColor Color)> slices)
    {
        container.Children.Clear();

        var total = slices.Sum(s => s.Value);
        foreach (var slice in slices)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0),
                Fill = new SolidColorBrush(Color.FromArgb(slice.Color.Alpha, slice.Color.Red, slice.Color.Green, slice.Color.Blue)),
            };

            var percent = total > 0 ? slice.Value / total * 100 : 0;
            // "45,23%", not "45.23%" - French decimal comma, same as every other number in
            // this app, computed with InvariantCulture so the "." it starts from is
            // predictable regardless of the machine's own locale.
            var percentText = percent.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
            var text = new TextBlock
            {
                Text = $"{slice.Label} ({percentText}%)", FontSize = 12, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextPrimary"],
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(dot);
            row.Children.Add(text);
            container.Children.Add(row);
        }
    }

    /// <summary>Snapshot of the current theme's secondary text colour, for chart axis labels -
    /// LiveCharts paints are plain SkiaSharp colours, not DynamicResource-aware, so this is
    /// read fresh every time <see cref="RenderAnalyseCharts"/> runs rather than bound once.</summary>
    private SKColor CurrentTextColor()
    {
        if (FindResource("TextSecondary") is SolidColorBrush brush)
            return new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
        return SKColors.Gray;
    }

    /// <summary>Snapshot of the current theme's border colour, for chart gridlines - dim
    /// enough not to compete with the bars/slices themselves, in either theme.</summary>
    private SKColor CurrentBorderColor()
    {
        if (FindResource("Border") is SolidColorBrush brush)
            return new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
        return SKColors.Gray;
    }

    private static Border StatCard(string label, string value, Brush accent) => new()
    {
        Width = 190,
        Margin = new Thickness(0, 0, 12, 12),
        Padding = new Thickness(16, 14, 16, 14),
        CornerRadius = new CornerRadius(10),
        Background = (Brush)Application.Current.Resources["SurfaceAlt"],
        BorderBrush = (Brush)Application.Current.Resources["Border"],
        BorderThickness = new Thickness(1),
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = value, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = accent,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock
                {
                    Text = label, Margin = new Thickness(0, 4, 0, 0),
                    Foreground = (Brush)Application.Current.Resources["TextSecondary"],
                    TextWrapping = TextWrapping.Wrap, FontSize = 12,
                },
            },
        },
    };

    /// <summary>
    /// Downloads the thumbnail for every product on the current page, reusing whatever is
    /// already cached. Paging keeps this bounded to at most <see cref="PageSize"/> downloads
    /// at a time, even on a large catalogue.
    /// </summary>
    private async Task LoadIconViewAsync()
    {
        var rows = new List<IconRow>(_pageItems.Count);

        foreach (var product in _pageItems)
        {
            BitmapImage? thumbnail = null;
            if (product.ImageUrl is { } url)
            {
                if (!_thumbnailCache.TryGetValue(url, out thumbnail))
                {
                    try
                    {
                        var bytes = await _session.Api.GetImageBytesAsync(url);
                        thumbnail = ImageHelper.FromBytes(bytes);
                        _thumbnailCache[url] = thumbnail;
                    }
                    catch (ApiException)
                    {
                        // Missing thumbnail is not worth failing the whole view over.
                    }
                }
            }

            rows.Add(new IconRow(product, thumbnail));
        }

        IconGrid.ItemsSource = rows;
    }

    /// <summary>Keeps the hidden <see cref="ProductGrid"/>'s selection in step with the icon
    /// view's, so every button in the toolbar keeps working off one selection regardless of
    /// which view is showing.</summary>
    private void IconGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var product = (IconGrid.SelectedItem as IconRow)?.Product;
        var index = product is null ? -1 : _pageItems.FindIndex(p => p.Id == product.Id);
        ProductGrid.SelectedIndex = index;
    }

    private async void IconGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IconGrid.SelectedItem is null) return;
        if (_session.Can(Priv.Gestion.EditProducts)) await EditAsync();
        else if (_session.Can(Priv.Gestion.ViewStockHistory)) ShowHistory();
    }

    private async void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadAsync();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!IsLoaded) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _searchDebounce.Stop();
        await LoadAsync();
    }

    private async void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is null) return;
        if (_session.Can(Priv.Gestion.EditProducts)) await EditAsync();
        else if (_session.Can(Priv.Gestion.ViewStockHistory)) ShowHistory();
        await Task.CompletedTask;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProductDialog(_categories, null, _session) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var created = await _session.Api.CreateProductAsync(dialog.Result!);
            await ApplyPendingPhotoAsync(dialog, created.Id);
            await LoadAsync(resetPage: false);
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditAsync();

    private async Task EditAsync()
    {
        if (Selected is not { } product) return;

        var dialog = new ProductDialog(_categories, product, _session) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await _session.Api.UpdateProductAsync(product.Id, dialog.Result!);
            await ApplyPendingPhotoAsync(dialog, product.Id);
            await LoadAsync(resetPage: false);
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    /// <summary>
    /// Uploads or removes a product's photo after the product itself has been saved.
    /// Run after the save, not before, because a brand new product has no id to attach a
    /// photo to until the create call returns one.
    /// </summary>
    private async Task ApplyPendingPhotoAsync(ProductDialog dialog, string productId)
    {
        if (dialog.PendingPhoto is { } bytes)
        {
            await _session.Api.UploadProductImageAsync(productId, bytes, dialog.PendingPhotoFileName ?? "photo.jpg");
        }
        else if (dialog.PhotoRemoved)
        {
            await _session.Api.DeleteProductImageAsync(productId);
        }
    }

    private async void Adjust_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } product) return;

        var dialog = new StockAdjustDialog(product) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await _session.Api.AdjustStockAsync(product.Id, dialog.Result!);
            await LoadAsync(resetPage: false);
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    /// <summary>Opens the category manager, then refreshes the filter and grid in case anything changed.</summary>
    private async void Categories_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CategoryManagerDialog(_session) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();

        _categories.Clear();
        await LoadAsync();
    }

    private void History_Click(object sender, RoutedEventArgs e) => ShowHistory();

    private void ShowHistory()
    {
        if (Selected is not { } product) return;
        new StockHistoryDialog(_session, product) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } product) return;

        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"Supprimer « {product.Name} » ?\n\n" +
            "Le produit sera retiré de l'inventaire. Les ventes déjà enregistrées le conservent.",
            "Supprimer le produit", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.DeleteProductAsync(product.Id);
            await LoadAsync(resetPage: false);
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private void SetBusy(bool busy) => BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;
}
