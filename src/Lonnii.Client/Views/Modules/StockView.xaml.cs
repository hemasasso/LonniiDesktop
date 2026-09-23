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
using Microsoft.Win32;

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

    /// <summary>Products load one page at a time so a large catalogue never means downloading
    /// thumbnails for, or rendering, hundreds of cards at once.</summary>
    private const int PageSize = 24;
    private int _page;

    /// <summary>Thumbnails already downloaded, keyed by image URL, so switching views or
    /// refreshing the list does not re-download a photo it already has.</summary>
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = [];

    /// <summary>Sentinel for the "all categories" row of the filter.</summary>
    private static readonly CategoryDto AllCategories = new("", "Toutes les catégories", null, null, null, null, true, 0);

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
        InitializeComponent();

        ApplyPrivileges();
        ApplyViewModeVisuals();
        ApplyTabVisuals();
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await LoadAsync();
        };
        Loaded += async (_, _) => await LoadAsync();
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
            UpdateAnalyse();
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

    private void AnalyseTab_Click(object sender, RoutedEventArgs e) => SetActiveTab(analyse: true);

    private void SetActiveTab(bool analyse)
    {
        if (_analyseTab == analyse) return;
        _analyseTab = analyse;
        ApplyTabVisuals();
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
        PaginationPanel.Visibility = _analyseTab ? Visibility.Collapsed : Visibility.Visible;
        StockContentGrid.Visibility = _analyseTab ? Visibility.Collapsed : Visibility.Visible;
        AnalysePanel.Visibility = _analyseTab ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Recomputes the Analyse tab's stat cards from the currently loaded, already
    /// filtered <see cref="_products"/>. Cheap enough to run after every load; no separate
    /// API call of its own.</summary>
    private void UpdateAnalyse()
    {
        AnalyseStatsPanel.Children.Clear();

        var count = _products.Count;
        var lowStock = _products.Count(p => p.IsLowStock);
        var saleValue = _products.Sum(p => p.Price * p.Quantity);
        var costValue = _products.Sum(p => (p.CostPrice ?? 0) * p.Quantity);
        var margin = saleValue - costValue;

        AnalyseStatsPanel.Children.Add(StatCard("Produits", count.ToString(), (Brush)FindResource("Accent")));
        AnalyseStatsPanel.Children.Add(StatCard("Valeur du stock (vente)", Money.Format(saleValue), (Brush)FindResource("Accent")));
        AnalyseStatsPanel.Children.Add(StatCard("Valeur du stock (achat)", Money.Format(costValue), (Brush)FindResource("TextSecondary")));
        AnalyseStatsPanel.Children.Add(StatCard("Marge potentielle", Money.Format(margin), (Brush)FindResource("Success")));
        AnalyseStatsPanel.Children.Add(StatCard("En stock bas", lowStock.ToString(),
            (Brush)FindResource(lowStock > 0 ? "Danger" : "TextSecondary")));
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
