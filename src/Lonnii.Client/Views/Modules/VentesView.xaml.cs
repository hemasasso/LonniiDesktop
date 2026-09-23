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

namespace Lonnii.Client.Views.Modules;

/// <summary>
/// Gestion des Ventes: the till. "Nouvelle Vente" is the only tab actually implemented -
/// "Liste des Ventes" and "Statistiques" show a placeholder, and "Ouvrir Caisse" a notice,
/// until those are built.
/// </summary>
public partial class VentesView : UserControl
{
    private readonly AppSession _session;
    private List<ProductDto> _products = [];
    private List<CategoryDto> _categories = [];
    private string? _selectedCategoryId;
    private readonly List<CartLine> _cart = [];
    private string _modePaiement = "cash";

    /// <summary>Thumbnails already downloaded, keyed by image URL, shared by the catalogue
    /// cards and the cart rows so a product added to the cart never re-downloads its photo.</summary>
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = [];

    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private sealed record CartLine(ProductDto Product, int Quantity, decimal UnitPrice)
    {
        public decimal LineTotal => UnitPrice * Quantity;
    }

    /// <summary>A product paired with its downloaded thumbnail, for the catalogue's cards.</summary>
    private sealed record CatalogRow(ProductDto Product, BitmapImage? Thumbnail)
    {
        public bool HasNoThumbnail => Thumbnail is null;
    }

    public VentesView(AppSession session)
    {
        _session = session;
        InitializeComponent();

        SubtitleText.Text = _session.Groupe?.Nom;
        RemiseCurrencyText.Text = _session.Groupe?.CurrencyLabel ?? Money.Label;

        ClientNomBox.TextChanged += (_, _) =>
            ClientNomPlaceholder.Visibility = ClientNomBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Wired here rather than in XAML: a XAML-declared handler is connected as soon as
        // this element is built, so the "0" in Text="0" above would fire it immediately -
        // while later elements like SubtotalText do not exist yet, crashing UpdateTotals().
        RemiseGlobaleBox.TextChanged += RemiseGlobale_Changed;

        // Same reason: IsChecked="True" would fire Checked before AvecFactureCheck, its
        // next sibling, has been constructed.
        VenteRapideCheck.Checked += VenteRapide_Checked;

        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await LoadAsync();
        };

        ApplyTabVisuals();
        ApplyPaymentVisuals();
        RenderCart();
        Loaded += async (_, _) => await LoadAsync();
    }

    // --- Loading ---

    private async Task LoadAsync()
    {
        SetBusy(true);
        try
        {
            if (_categories.Count == 0)
            {
                _categories = await _session.Api.GetCategoriesAsync();
                BuildCategoryPills();
            }

            _products = await _session.Api.GetProductsAsync(search: SearchBox.Text, categoryId: _selectedCategoryId);
            await LoadCatalogueAsync();

            EmptyPanel.Visibility = _products.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Downloads each visible product's thumbnail, reusing whatever is already
    /// cached, and binds the catalogue grid.</summary>
    private async Task LoadCatalogueAsync()
    {
        var rows = new List<CatalogRow>(_products.Count);

        foreach (var product in _products)
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
                        // Missing thumbnail is not worth failing the catalogue over.
                    }
                }
            }

            rows.Add(new CatalogRow(product, thumbnail));
        }

        ProductGrid.ItemsSource = rows;
    }

    private void BuildCategoryPills()
    {
        CategoryPillPanel.Children.Clear();
        AddPill("Tous", null);
        foreach (var category in _categories.Where(c => c.IsActive))
            AddPill(category.Name, category.Id);

        ApplyPillVisuals();
    }

    private void AddPill(string label, string? categoryId)
    {
        var button = new Button
        {
            Content = label,
            Tag = categoryId,
            Margin = new Thickness(0, 0, 6, 0),
            Style = (Style)FindResource("ModuleTabButton"),
        };
        button.Click += async (_, _) =>
        {
            _selectedCategoryId = categoryId;
            ApplyPillVisuals();
            await LoadAsync();
        };
        CategoryPillPanel.Children.Add(button);
    }

    private void ApplyPillVisuals()
    {
        var accent = (Brush)FindResource("Accent");
        var onAccent = (Brush)FindResource("TextOnAccent");
        var muted = (Brush)FindResource("TextMuted");

        foreach (var child in CategoryPillPanel.Children)
        {
            if (child is not Button button) continue;
            var isActive = (button.Tag as string) == _selectedCategoryId;
            button.Background = isActive ? accent : Brushes.Transparent;
            button.Foreground = isActive ? onAccent : muted;
            button.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }
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

    // --- Tabs ---

    private void NouvelleVenteTab_Click(object sender, RoutedEventArgs e) => SetActiveTab(null);

    private void OtherTab_Click(object sender, RoutedEventArgs e) => SetActiveTab(((Button)sender).Content as string);

    private string? _activePlaceholder;

    private void SetActiveTab(string? placeholderTitle)
    {
        _activePlaceholder = placeholderTitle;
        ApplyTabVisuals();

        if (placeholderTitle is null)
        {
            NouvelleVentePanel.Visibility = Visibility.Visible;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            PlaceholderTitle.Text = placeholderTitle;
            NouvelleVentePanel.Visibility = Visibility.Collapsed;
            PlaceholderPanel.Visibility = Visibility.Visible;
        }
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

        Apply(NouvelleVenteTabButton, _activePlaceholder is null);
        Apply(ListeVentesTabButton, _activePlaceholder == (string)ListeVentesTabButton.Content);
        Apply(StatistiquesTabButton, _activePlaceholder == (string)StatistiquesTabButton.Content);
    }

    private void OpenCaisse_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(Window.GetWindow(this), "La gestion de caisse arrive bientôt.",
            "Ouvrir Caisse", MessageBoxButton.OK, MessageBoxImage.Information);

    // --- Cart ---

    private void ProductGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductGrid.SelectedItem is not CatalogRow row) return;
        ProductGrid.SelectedItem = null;
        AddToCart(row.Product);
    }

    private void AddToCart(ProductDto product)
    {
        var tracked = !(product.VenteLibre || product.StockIllimite);
        var index = _cart.FindIndex(c => c.Product.Id == product.Id);

        if (index >= 0)
        {
            var existing = _cart[index];
            if (tracked && existing.Quantity + 1 > product.Quantity)
            {
                ShowMessage($"Stock insuffisant pour « {product.Name} ».");
                return;
            }

            _cart[index] = existing with { Quantity = existing.Quantity + 1 };
            RenderCart();
            return;
        }

        if (tracked && product.Quantity < 1)
        {
            ShowMessage($"« {product.Name} » n'est plus en stock.");
            return;
        }

        // A fixed-price product takes its catalogue price; anything else is priced right in
        // the cart row, so it starts at zero rather than opening a popup to ask.
        _cart.Add(new CartLine(product, 1, product.PrixFixe ? product.Price : 0));
        HideMessage();
        RenderCart();
    }

    private void ChangeQuantity(string productId, int delta)
    {
        var index = _cart.FindIndex(c => c.Product.Id == productId);
        if (index < 0) return;

        var line = _cart[index];
        var newQuantity = line.Quantity + delta;

        if (newQuantity <= 0)
        {
            _cart.RemoveAt(index);
            RenderCart();
            return;
        }

        var tracked = !(line.Product.VenteLibre || line.Product.StockIllimite);
        if (tracked && newQuantity > line.Product.Quantity)
        {
            ShowMessage($"Stock insuffisant pour « {line.Product.Name} ».");
            return;
        }

        _cart[index] = line with { Quantity = newQuantity };
        RenderCart();
    }

    private void RemoveFromCart(string productId)
    {
        _cart.RemoveAll(c => c.Product.Id == productId);
        RenderCart();
    }

    private void ClearCart_Click(object sender, RoutedEventArgs e)
    {
        if (_cart.Count == 0) return;

        var confirm = MessageBox.Show(Window.GetWindow(this), "Vider le panier ?", "Vider",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _cart.Clear();
        RenderCart();
    }

    /// <summary>Rebuilds the cart's row list. Called whenever a line is added, removed, or
    /// its quantity changes - not on every keystroke in a price box, which only needs
    /// <see cref="UpdateTotals"/> so it does not lose focus while the user is typing.</summary>
    private void RenderCart()
    {
        CartList.Items.Clear();
        foreach (var line in _cart) CartList.Items.Add(BuildCartRow(line));

        var isEmpty = _cart.Count == 0;
        CartEmptyPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        CartScroll.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

        UpdateTotals();
    }

    private void RemiseGlobale_Changed(object sender, TextChangedEventArgs e) => UpdateTotals();

    /// <summary>Recomputes SOUS-TOTAL / REMISE GLOBALE / Total from the cart and the discount
    /// box, without touching the cart's row list.</summary>
    private void UpdateTotals()
    {
        var subtotal = _cart.Sum(c => c.LineTotal);

        Money.TryParse(RemiseGlobaleBox.Text, out decimal remise);
        remise = Math.Clamp(remise, 0, subtotal);

        var total = subtotal - remise;

        SubtotalText.Text = Money.Format(subtotal);
        RemiseSummaryRow.Visibility = remise > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemiseSummaryText.Text = $"- {Money.Format(remise)}";
        TotalText.Text = Money.Format(total);

        ValidateButton.IsEnabled = _cart.Count > 0 && _session.Can(Priv.Gestion.CreateVente);
    }

    private Border BuildCartRow(CartLine line)
    {
        var thumb = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("SurfaceAlt"), ClipToBounds = true,
        };
        if (line.Product.ImageUrl is { } url && _thumbnailCache.TryGetValue(url, out var bitmap))
        {
            thumb.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }
        else
        {
            thumb.Child = new TextBlock
            {
                Text = "🛒", FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var remove = new Button
        {
            Content = "✕", Width = 22, Height = 22, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("TextMuted"), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
        };
        remove.Click += (_, _) => RemoveFromCart(line.Product.Id);

        var lineTotalText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Accent"), Text = Money.Format(line.LineTotal),
        };

        FrameworkElement priceElement;
        if (line.Product.PrixFixe)
        {
            priceElement = new TextBlock
            {
                Text = Money.Format(line.UnitPrice), FontSize = 12,
                Foreground = (Brush)FindResource("TextSecondary"),
            };
        }
        else
        {
            var priceBox = new TextBox
            {
                Text = line.UnitPrice > 0 ? Money.FormatPlain(line.UnitPrice) : string.Empty,
                Width = 100, ToolTip = "Prix pour cette vente",
            };
            priceBox.TextChanged += (_, _) =>
            {
                Money.TryParse(priceBox.Text, out decimal price);
                var index = _cart.FindIndex(c => c.Product.Id == line.Product.Id);
                if (index < 0) return;

                _cart[index] = _cart[index] with { UnitPrice = price };
                lineTotalText.Text = Money.Format(_cart[index].LineTotal);
                UpdateTotals();
            };
            priceElement = priceBox;
        }

        var minus = new Button
        {
            Content = "−", Width = 24, Height = 24, Padding = new Thickness(0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        minus.Click += (_, _) => ChangeQuantity(line.Product.Id, -1);

        var plus = new Button
        {
            Content = "+", Width = 24, Height = 24, Padding = new Thickness(0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        plus.Click += (_, _) => ChangeQuantity(line.Product.Id, +1);

        var body = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        body.Children.Add(new TextBlock
        {
            Text = line.Product.Name, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(priceElement);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(thumb);
        Grid.SetColumn(body, 1);
        top.Children.Add(body);
        Grid.SetColumn(remove, 2);
        top.Children.Add(remove);

        var qtyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(50, 8, 0, 0),
        };
        qtyRow.Children.Add(minus);
        qtyRow.Children.Add(new TextBlock
        {
            Text = line.Quantity.ToString(), Width = 30, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        qtyRow.Children.Add(plus);
        qtyRow.Children.Add(new Grid { Width = 1 }); // spacer
        qtyRow.Children.Add(lineTotalText);
        lineTotalText.Margin = new Thickness(12, 0, 0, 0);

        return new Border
        {
            Background = (Brush)FindResource("Surface"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new StackPanel { Children = { top, qtyRow } },
        };
    }

    // --- Checkout options ---

    private void VenteRapide_Checked(object sender, RoutedEventArgs e)
    {
        AvecFactureCheck.IsChecked = false;
    }

    private void AvecFacture_Checked(object sender, RoutedEventArgs e)
    {
        VenteRapideCheck.IsChecked = false;
        FactureFieldsPanel.Visibility = Visibility.Visible;
    }

    private void AvecFacture_Unchecked(object sender, RoutedEventArgs e) =>
        FactureFieldsPanel.Visibility = Visibility.Collapsed;

    private void PaymentMode_Click(object sender, RoutedEventArgs e)
    {
        _modePaiement = (string)((Button)sender).Tag;
        ApplyPaymentVisuals();
    }

    private void ApplyPaymentVisuals()
    {
        var primary = (Style)FindResource("PrimaryButton");
        var secondary = (Style)FindResource("SecondaryButton");

        PaymentCashButton.Style = _modePaiement == "cash" ? primary : secondary;
        PaymentMobileButton.Style = _modePaiement == "mobile_money" ? primary : secondary;
        PaymentCarteButton.Style = _modePaiement == "carte" ? primary : secondary;
    }

    // --- Checkout ---

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (_cart.Count == 0) return;

        var missingPrice = _cart.FirstOrDefault(c => !c.Product.PrixFixe && c.UnitPrice <= 0);
        if (missingPrice is not null)
        {
            ShowMessage($"Indiquez un prix pour « {missingPrice.Product.Name} ».");
            return;
        }

        var subtotal = _cart.Sum(c => c.LineTotal);
        Money.TryParse(RemiseGlobaleBox.Text, out decimal remise);
        remise = Math.Clamp(remise, 0, subtotal);
        var total = subtotal - remise;

        var items = _cart.Select(c => new CartItemRequest(c.Product.Id, c.Quantity, c.UnitPrice)).ToList();
        var clientNom = string.IsNullOrWhiteSpace(ClientNomBox.Text) ? null : ClientNomBox.Text.Trim();

        string? clientTelephone = null;
        string? clientEmail = null;
        if (AvecFactureCheck.IsChecked == true)
        {
            clientTelephone = string.IsNullOrWhiteSpace(ClientTelephoneBox.Text) ? null : ClientTelephoneBox.Text.Trim();
            clientEmail = string.IsNullOrWhiteSpace(ClientEmailBox.Text) ? null : ClientEmailBox.Text.Trim();
        }

        var request = new CreateVenteRequest(
            items, _modePaiement, total, remise, clientNom, clientTelephone, clientEmail);

        SetBusy(true);
        try
        {
            var vente = await _session.Api.CreateVenteAsync(request);

            _cart.Clear();
            RenderCart();
            RemiseGlobaleBox.Text = "0";
            ClientNomBox.Text = string.Empty;
            ClientTelephoneBox.Text = string.Empty;
            ClientEmailBox.Text = string.Empty;

            new VenteReceiptDialog(vente, _session) { Owner = Window.GetWindow(this) }.ShowDialog();

            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // --- Status ---

    private void SetBusy(bool busy) => BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;
}
