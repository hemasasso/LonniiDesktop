using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lonnii.Client.Services;
using Lonnii.Client.Views.Dialogs;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

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
        EditButton.IsEnabled = _session.Can(Priv.Gestion.EditProducts);
        AdjustButton.IsEnabled = _session.Can(Priv.Gestion.AdjustStock);
        DeleteButton.IsEnabled = _session.Can(Priv.Gestion.DeleteProducts);
        HistoryButton.IsEnabled = _session.Can(Priv.Gestion.ViewStockHistory);
        CategoriesButton.IsEnabled = _session.Can(Priv.Gestion.ManageCategories);

        // An admin-only privilege never resolves true for a member, so say why it is greyed out.
        if (!DeleteButton.IsEnabled)
            DeleteButton.ToolTip = "Réservé aux administrateurs";
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

            if (resetPage) _page = 0;
            await ApplyPageAsync();

            HideMessage();
            UpdateSummary();
            UpdateEmptyState();
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
        EditButton.IsEnabled = hasSelection && _session.Can(Priv.Gestion.EditProducts);
        AdjustButton.IsEnabled = hasSelection && _session.Can(Priv.Gestion.AdjustStock);
        DeleteButton.IsEnabled = hasSelection && _session.Can(Priv.Gestion.DeleteProducts);
        HistoryButton.IsEnabled = hasSelection && _session.Can(Priv.Gestion.ViewStockHistory);
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
