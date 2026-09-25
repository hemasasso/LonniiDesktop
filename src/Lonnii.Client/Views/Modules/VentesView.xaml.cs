using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lonnii.Client.Services;
using Lonnii.Client.Views.Dialogs;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Lonnii.Client.Views.Modules;

/// <summary>
/// Gestion des Ventes: the till. "Nouvelle Vente" rings up sales, "Liste des Ventes" lists
/// and manages them (mirrors Lonnii Business's ListeVentes.jsx, including its action
/// buttons), and "Statistiques" still shows a placeholder, until it is built.
/// </summary>
public partial class VentesView : UserControl
{
    private readonly AppSession _session;
    private List<ProductDto> _products = [];
    private List<CategoryDto> _categories = [];
    private string? _selectedCategoryId;
    private readonly List<CartLine> _cart = [];
    private string _modePaiement = "cash";

    private string _activeTab = "nouvelle";

    // --- Liste des Ventes ---
    private List<VenteListItemDto> _ventes = [];
    private string _venteSearchType = "both";
    private string _venteStatus = "all";
    private DateOnly? _venteDateDebut;
    private DateOnly? _venteDateFin;
    private string _venteDateFilterTag = "today";
    private bool _suppressVenteDateFilterEvent;
    private int _ventePage = 1;
    private const int VentePageSize = 10;
    private readonly DispatcherTimer _venteSearchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    // --- Statistiques ---
    private VentesStatsResponse? _stats;
    private List<ProductDto> _statsProducts = [];
    private string? _statsCategoryId;
    private string? _statsProductId;
    private DateOnly? _statsDateDebut;
    private DateOnly? _statsDateFin;
    private string _statsDateFilterTag = "month";
    private bool _suppressStatsDateFilterEvent;

    /// <summary>Fixed 8-colour palette for the category pie chart - same colours, same
    /// order, as Lonnii Business's own <c>Statistiques.jsx</c>.</summary>
    private static readonly SKColor[] CategoryPalette =
    [
        new(0x25, 0x63, 0xEB), new(0x10, 0xB9, 0x81), new(0xF5, 0x9E, 0x0B), new(0xEF, 0x44, 0x44),
        new(0x8B, 0x5C, 0xF6), new(0xEC, 0x48, 0x99), new(0x06, 0xB6, 0xD4), new(0x84, 0xCC, 0x16),
    ];

    /// <summary>Column labels and widths shared by the list's header row and every data row,
    /// so the two can never drift apart - same order as Lonnii Business's ListeVentes.jsx table.</summary>
    private static readonly (string Label, GridLength Width)[] VenteColumns =
    [
        ("N° Vente", new GridLength(85)),
        ("Date", new GridLength(85)),
        ("Client", new GridLength(1, GridUnitType.Star)),
        ("Vendeur", new GridLength(110)),
        ("Montant Total", new GridLength(105)),
        ("Payé", new GridLength(95)),
        ("Restant", new GridLength(95)),
        ("Avoir", new GridLength(85)),
        ("Statut", new GridLength(80)),
        ("Actions", new GridLength(165)),
    ];

    private readonly bool _canViewVenteDetails;
    private readonly bool _canPrintReceipt;
    private readonly bool _canSoldeAvoir;
    private readonly bool _canEditVente;
    private readonly bool _canCancelVente;
    private readonly bool _canExportVentes;

    /// <summary>Which of the three module tabs this user may open at all - a preparer with
    /// only can_create_vente should never see "Liste des Ventes" or "Statistiques" buttons
    /// they'd only get a 403 from clicking.</summary>
    private readonly bool _canCreateVenteTab;
    private readonly bool _canViewVentesListTab;
    private readonly bool _canViewStatistiquesTab;

    /// <summary>True once the catalogue (categories + products) has been fetched at least
    /// once - "Nouvelle Vente" may not be the initial tab any more (see
    /// <see cref="_canCreateVenteTab"/>), so its data is now loaded lazily on first visit
    /// rather than unconditionally at startup.</summary>
    private bool _catalogueLoaded;

    /// <summary>Thumbnails already downloaded, keyed by image URL, shared by the catalogue
    /// cards and the cart rows so a product added to the cart never re-downloads its photo.</summary>
    private readonly Dictionary<string, BitmapImage> _thumbnailCache = [];

    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    /// <summary>Without this, hiding "Payer &amp; Valider" is the only thing standing between
    /// a preparer and the till - and the server enforces the same rule independently, so a
    /// stale client can never write a paid sale it merely couldn't show a button for.</summary>
    private readonly bool _canAddPayment;

    /// <summary>Mirrors Lonnii Business's Panier.jsx: the per-line discount column only
    /// renders for someone holding can_apply_discount.</summary>
    private readonly bool _canApplyDiscount;

    private sealed record CartLine(
        ProductDto Product, int Quantity, decimal UnitPrice,
        decimal Discount = 0, string DiscountType = DiscountTypes.Amount)
    {
        public decimal LineTotal
        {
            get
            {
                var raw = UnitPrice * Quantity;
                var discounted = DiscountType == DiscountTypes.Amount
                    ? raw - Discount
                    : raw * (1 - Discount / 100m);
                return Math.Max(0, discounted);
            }
        }
    }

    /// <summary>A product paired with its downloaded thumbnail, for the catalogue's cards.</summary>
    private sealed record CatalogRow(ProductDto Product, BitmapImage? Thumbnail)
    {
        public bool HasNoThumbnail => Thumbnail is null;
    }

    /// <summary>An entry in a cart line's discount-type dropdown; <see cref="Label"/> is what
    /// the combo box shows, <see cref="Value"/> is what gets sent to the API.</summary>
    private sealed record DiscountOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    public VentesView(AppSession session)
    {
        _session = session;
        _canAddPayment = _session.Can(Priv.Gestion.AddPayment);
        _canApplyDiscount = _session.Can(Priv.Gestion.ApplyDiscount);
        _canViewVenteDetails = _session.Can(Priv.Gestion.ViewVenteDetails);
        _canPrintReceipt = _session.Can(Priv.Gestion.PrintReceipt);
        _canSoldeAvoir = _session.Can(Priv.Gestion.SoldeAvoir);
        _canEditVente = _session.Can(Priv.Gestion.EditVente);
        _canCancelVente = _session.Can(Priv.Gestion.CancelVente);
        _canExportVentes = _session.Can(Priv.Gestion.ExportVentes);
        _canCreateVenteTab = _session.Can(Priv.Gestion.CreateVente);
        _canViewVentesListTab = _session.Can(Priv.Gestion.ViewVentes);
        _canViewStatistiquesTab = _session.Can(Priv.Gestion.ViewVentesAnalytics);
        InitializeComponent();

        SubtitleText.Text = _session.Groupe?.Nom;
        RemiseCurrencyText.Text = _session.Groupe?.CurrencyLabel ?? Money.Label;
        ExportVentesButton.Visibility = _canExportVentes ? Visibility.Visible : Visibility.Collapsed;

        NouvelleVenteTabButton.Visibility = _canCreateVenteTab ? Visibility.Visible : Visibility.Collapsed;
        ListeVentesTabButton.Visibility = _canViewVentesListTab ? Visibility.Visible : Visibility.Collapsed;
        StatistiquesTabButton.Visibility = _canViewStatistiquesTab ? Visibility.Visible : Visibility.Collapsed;

        // Defaults the list to "Aujourd'hui", matching VenteDateFilterCombo's own
        // IsSelected="True" item - a cashier should not be confused by older sales
        // (lonnii-preparer-cashier-flow memory, point 6).
        var today = DateOnly.FromDateTime(DateTime.Now);
        _venteDateDebut = today;
        _venteDateFin = today;

        // "Statistiques" defaults to the current month, matching Lonnii Business's own
        // Statistiques.jsx default and its StatsPeriodCombo's own IsSelected="True" item.
        _statsDateDebut = new DateOnly(today.Year, today.Month, 1);
        _statsDateFin = today;

        _venteSearchDebounce.Tick += async (_, _) =>
        {
            _venteSearchDebounce.Stop();
            await LoadVentesAsync();
        };

        if (!_canAddPayment)
        {
            // Force facture-only: no "Vente Rapide", and "Facture" cannot be unchecked, so
            // Validate_Click's own IsChecked check always takes this path.
            VenteRapideCheck.IsChecked = false;
            VenteRapideCheck.Visibility = Visibility.Collapsed;
            AvecFactureCheck.IsChecked = true;
            AvecFactureCheck.IsEnabled = false;
            FactureOnlyNotice.Visibility = Visibility.Visible;
        }

        ClientNomBox.TextChanged += (_, _) =>
            ClientNomPlaceholder.Visibility = ClientNomBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClientTelephoneBox.TextChanged += (_, _) =>
            ClientTelephonePlaceholder.Visibility = ClientTelephoneBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClientEmailBox.TextChanged += (_, _) =>
            ClientEmailPlaceholder.Visibility = ClientEmailBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Wired here rather than in XAML: a XAML-declared handler is connected as soon as
        // this element is built, so the "0" in Text="0" above would fire it immediately -
        // while later elements like SubtotalText do not exist yet, crashing UpdateTotals().
        RemiseGlobaleBox.TextChanged += RemiseGlobale_Changed;

        // Wired programmatically for the same reason as RemiseGlobaleBox above, in case
        // something later sets IsChecked before AvecFactureCheck exists.
        VenteRapideCheck.Checked += VenteRapide_Checked;
        VenteRapideCheck.Unchecked += VenteRapide_Unchecked;

        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await LoadAsync();
        };

        // LiveCharts paints are plain SkiaSharp colours snapshotted at render time, not
        // DynamicResource-aware like the rest of this view - without this, toggling dark
        // mode while Statistiques is open would leave every chart's axis/gridline colours
        // stuck on whichever theme was active when it last loaded.
        ThemeManager.Changed += (_, _) =>
        {
            if (_stats is not null) RenderStats();
        };

        ApplyTabVisuals();
        ApplyPaymentVisuals();
        UpdateCheckoutMode();
        RenderCart();
        Loaded += async (_, _) =>
        {
            // The initial tab is the first one this user actually has a privilege for -
            // "Nouvelle Vente" is the default only when they can create a sale at all.
            var initialTab = _canCreateVenteTab ? "nouvelle"
                : _canViewVentesListTab ? "liste"
                : _canViewStatistiquesTab ? "statistiques"
                : "nouvelle";
            await SetActiveTabAsync(initialTab);
        };
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
            _catalogueLoaded = true;
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
            // A TextBlock rather than the bare string: the implicit TextBlock style sets a
            // dark Foreground, and a style setter outranks the inherited value, so a plain
            // string label stays dark even on the selected pill's Accent background.
            // ButtonContentText binds back to this button's own Foreground instead.
            Content = new TextBlock { Text = label, Style = (Style)FindResource("ButtonContentText") },
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

    private async void NouvelleVenteTab_Click(object sender, RoutedEventArgs e) => await SetActiveTabAsync("nouvelle");

    private async void OtherTab_Click(object sender, RoutedEventArgs e) =>
        await SetActiveTabAsync(((Button)sender).Tag as string ?? "nouvelle");

    private async Task SetActiveTabAsync(string tab)
    {
        _activeTab = tab;
        ApplyTabVisuals();

        NouvelleVentePanel.Visibility = tab == "nouvelle" ? Visibility.Visible : Visibility.Collapsed;
        ListeVentesPanel.Visibility = tab == "liste" ? Visibility.Visible : Visibility.Collapsed;
        StatistiquesPanel.Visibility = tab == "statistiques" ? Visibility.Visible : Visibility.Collapsed;

        if (tab == "nouvelle" && !_catalogueLoaded) await LoadAsync();
        if (tab == "liste") await LoadVentesAsync();
        if (tab == "statistiques") await OpenStatistiquesAsync();
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

        Apply(NouvelleVenteTabButton, _activeTab == "nouvelle");
        Apply(ListeVentesTabButton, _activeTab == "liste");
        Apply(StatistiquesTabButton, _activeTab == "statistiques");
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

    /// <summary>Recomputes SOUS-TOTAL / REMISE ARTICLES / REMISE GLOBALE / Total from the
    /// cart and the discount box, without touching the cart's row list. SOUS-TOTAL is the
    /// raw pre-discount sum, so the two discounts each show as their own line rather than
    /// being silently folded into it.</summary>
    private void UpdateTotals()
    {
        var rawSubtotal = _cart.Sum(c => c.UnitPrice * c.Quantity);
        var afterItemDiscounts = _cart.Sum(c => c.LineTotal);
        var itemDiscountTotal = rawSubtotal - afterItemDiscounts;

        Money.TryParse(RemiseGlobaleBox.Text, out decimal remise);
        remise = Math.Clamp(remise, 0, afterItemDiscounts);

        var total = afterItemDiscounts - remise;

        SubtotalText.Text = Money.Format(rawSubtotal);
        ItemDiscountRow.Visibility = itemDiscountTotal > 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemDiscountText.Text = $"- {Money.Format(itemDiscountTotal)}";
        RemiseSummaryRow.Visibility = remise > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemiseSummaryText.Text = $"- {Money.Format(remise)}";
        TotalText.Text = Money.Format(total);

        // Neither box checked means neither a payment nor a facture would be recorded -
        // nothing Validate_Click could actually do, so it must not be clickable.
        var hasCheckoutMode = VenteRapideCheck.IsChecked == true || AvecFactureCheck.IsChecked == true;
        ValidateButton.IsEnabled = _cart.Count > 0 && hasCheckoutMode && _session.Can(Priv.Gestion.CreateVente);
    }

    /// <summary>One compact line per cart row: thumbnail, name/price, quantity stepper,
    /// per-line discount (when the user may apply one), line total, remove. Same column
    /// order as Lonnii Business's <c>.cart-item</c> grid (Panier.jsx), just narrower.</summary>
    private Border BuildCartRow(CartLine line)
    {
        var thumb = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(5),
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
                Text = "🛒", FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var lineTotalText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Accent"), Text = Money.Format(line.LineTotal),
            Margin = new Thickness(8, 0, 8, 0),
        };

        void RefreshLineTotal()
        {
            var index = _cart.FindIndex(c => c.Product.Id == line.Product.Id);
            if (index < 0) return;
            lineTotalText.Text = Money.Format(_cart[index].LineTotal);
            UpdateTotals();
        }

        FrameworkElement priceElement;
        if (line.Product.PrixFixe)
        {
            priceElement = new TextBlock
            {
                Text = Money.Format(line.UnitPrice), FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondary"),
            };
        }
        else
        {
            var priceBox = new TextBox
            {
                Text = line.UnitPrice > 0 ? Money.FormatPlain(line.UnitPrice) : string.Empty,
                Width = 46, FontSize = 11, Padding = new Thickness(4, 2, 4, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Prix pour cette vente",
            };
            priceBox.TextChanged += (_, _) =>
            {
                Money.TryParse(priceBox.Text, out decimal price);
                var index = _cart.FindIndex(c => c.Product.Id == line.Product.Id);
                if (index < 0) return;

                _cart[index] = _cart[index] with { UnitPrice = price };
                RefreshLineTotal();
            };
            // Grouped ("1 000") only once typing is done - reformatting every keystroke
            // would fight the caret position and the space the user is trying to type past.
            priceBox.LostFocus += (_, _) =>
            {
                if (Money.TryParse(priceBox.Text, out decimal price) && price > 0)
                    priceBox.Text = Money.FormatPlain(price);
            };
            priceElement = priceBox;
        }

        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        details.Children.Add(new TextBlock
        {
            Text = line.Product.Name, FontWeight = FontWeights.SemiBold, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        details.Children.Add(priceElement);

        // Quantity stepper - small, matching Lonnii Business's qty-control rather than the
        // large buttons a POS keypad would use, since a mouse-driven till does not need them.
        // MinWidth 88 comes from ButtonBase (SecondaryButton's base style) and overrides an
        // explicit Width smaller than it, so it must be cleared here too, or these render at
        // the same size as a full-text button no matter what Width says.
        var minus = new Button
        {
            Content = "−", Width = 20, Height = 20, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), FontSize = 11,
            Style = (Style)FindResource("SecondaryButton"),
        };
        minus.Click += (_, _) => ChangeQuantity(line.Product.Id, -1);

        var plus = new Button
        {
            Content = "+", Width = 20, Height = 20, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), FontSize = 11,
            Style = (Style)FindResource("SecondaryButton"),
        };
        plus.Click += (_, _) => ChangeQuantity(line.Product.Id, +1);

        // Typed directly rather than only stepped, since a product sold 100 at a time
        // should not need 100 clicks. TextChanged updates the total live without rebuilding
        // the row (which would drop focus mid-keystroke, same reason priceBox behaves this
        // way); LostFocus is what clamps to stock and normalises the display.
        var qtyBox = new TextBox
        {
            Text = line.Quantity.ToString(), Width = 34, TextAlignment = TextAlignment.Center,
            FontSize = 12, Padding = new Thickness(2, 1, 2, 1), VerticalContentAlignment = VerticalAlignment.Center,
        };
        qtyBox.TextChanged += (_, _) =>
        {
            var index = _cart.FindIndex(c => c.Product.Id == line.Product.Id);
            if (index < 0 || !int.TryParse(qtyBox.Text, out var typed) || typed <= 0) return;

            var tracked = !(line.Product.VenteLibre || line.Product.StockIllimite);
            var quantity = tracked ? Math.Min(typed, line.Product.Quantity) : typed;

            _cart[index] = _cart[index] with { Quantity = quantity };
            RefreshLineTotal();
        };
        qtyBox.LostFocus += (_, _) =>
        {
            var index = _cart.FindIndex(c => c.Product.Id == line.Product.Id);
            if (index < 0) return;

            if (!int.TryParse(qtyBox.Text, out var typed) || typed <= 0) typed = 1;

            var tracked = !(line.Product.VenteLibre || line.Product.StockIllimite);
            if (tracked && typed > line.Product.Quantity)
            {
                ShowMessage($"Stock insuffisant pour « {line.Product.Name} ».");
                typed = line.Product.Quantity;
            }

            _cart[index] = _cart[index] with { Quantity = typed };
            qtyBox.Text = typed.ToString();
            RefreshLineTotal();
        };

        var qty = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        qty.Children.Add(minus);
        qty.Children.Add(qtyBox);
        qty.Children.Add(plus);

        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // thumb
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // details
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // qty
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // discount
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // total
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // remove

        row.Children.Add(thumb);
        Grid.SetColumn(details, 1);
        row.Children.Add(details);
        Grid.SetColumn(qty, 2);
        row.Children.Add(qty);

        if (_canApplyDiscount)
        {
            var discountBox = new TextBox
            {
                Text = line.Discount > 0 ? Money.FormatPlain(line.Discount) : string.Empty,
                Width = 46, Height = 22, FontSize = 11, Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(8, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Remise sur cette ligne",
            };
            var discountOptions = new[]
            {
                new DiscountOption(DiscountTypes.Amount, Money.Label),
                new DiscountOption(DiscountTypes.Percentage, "%"),
            };
            var discountType = new ComboBox
            {
                Width = 60, Height = 22, FontSize = 11, Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ItemsSource = discountOptions,
                SelectedItem = discountOptions.First(o => o.Value == line.DiscountType),
            };
            discountType.SelectionChanged += (_, _) =>
            {
                var index = _cart.FindIndex(c => c.Product.Id == line.Product.Id);
                if (index < 0 || discountType.SelectedItem is not DiscountOption option) return;

                _cart[index] = _cart[index] with { DiscountType = option.Value };
                RefreshLineTotal();
            };
            discountBox.TextChanged += (_, _) =>
            {
                Money.TryParse(discountBox.Text, out decimal discount);
                var index = _cart.FindIndex(c => c.Product.Id == line.Product.Id);
                if (index < 0) return;

                var isPercentage = (discountType.SelectedItem as DiscountOption)?.Value == DiscountTypes.Percentage;
                discount = Math.Clamp(discount, 0, isPercentage ? 100 : decimal.MaxValue);

                _cart[index] = _cart[index] with { Discount = discount };
                RefreshLineTotal();
            };
            discountBox.LostFocus += (_, _) =>
            {
                if (Money.TryParse(discountBox.Text, out decimal discount) && discount > 0)
                    discountBox.Text = Money.FormatPlain(discount);
            };

            var discountPanel = new StackPanel { Orientation = Orientation.Horizontal };
            discountPanel.Children.Add(discountBox);
            discountPanel.Children.Add(discountType);
            Grid.SetColumn(discountPanel, 3);
            row.Children.Add(discountPanel);
        }

        Grid.SetColumn(lineTotalText, 4);
        row.Children.Add(lineTotalText);

        var remove = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "✕", Width = 20, Height = 20, FontSize = 10,
        };
        SetBrush(remove, Control.ForegroundProperty, "TextMuted");
        remove.Click += (_, _) => RemoveFromCart(line.Product.Id);
        Grid.SetColumn(remove, 5);
        row.Children.Add(remove);

        return new Border
        {
            Background = (Brush)FindResource("Surface"),
            BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Child = row,
        };
    }

    // --- Checkout options ---

    private void VenteRapide_Checked(object sender, RoutedEventArgs e)
    {
        AvecFactureCheck.IsChecked = false;
        UpdateCheckoutMode();
    }

    private void VenteRapide_Unchecked(object sender, RoutedEventArgs e) => UpdateCheckoutMode();

    private void AvecFacture_Checked(object sender, RoutedEventArgs e)
    {
        VenteRapideCheck.IsChecked = false;
        UpdateCheckoutMode();
    }

    private void AvecFacture_Unchecked(object sender, RoutedEventArgs e) => UpdateCheckoutMode();

    /// <summary>
    /// Keeps the validate button's label, the payment-method row and the client-info fields
    /// in step with the two checkboxes. Mirrors Lonnii Business's Panier.jsx for the label:
    /// it names the outcome - an unpaid facture, a fully-paid quick sale, or a sale recorded
    /// with whatever amount was typed - so a cashier never validates the wrong one by habit.
    /// The payment-method row only makes sense for Paiement (a facture takes no payment), and
    /// the client fields only make sense once a mode is picked at all - with neither checked
    /// there is nothing that would use them, same as Validate_Click being disabled then.
    /// </summary>
    private void UpdateCheckoutMode()
    {
        ValidateButtonLabel.Text = AvecFactureCheck.IsChecked == true ? "Créer Facture"
            : VenteRapideCheck.IsChecked == true ? "Payer & Valider"
            : "Valider la Vente";

        PaymentModePanel.Visibility = VenteRapideCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;

        var hasCheckoutMode = VenteRapideCheck.IsChecked == true || AvecFactureCheck.IsChecked == true;
        ClientFieldsPanel.Visibility = hasCheckoutMode ? Visibility.Visible : Visibility.Collapsed;

        UpdateTotals();
    }

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

        var items = _cart.Select(c =>
            new CartItemRequest(c.Product.Id, c.Quantity, c.UnitPrice, c.Discount, c.DiscountType)).ToList();
        var clientNom = string.IsNullOrWhiteSpace(ClientNomBox.Text) ? null : ClientNomBox.Text.Trim();

        // A facture is exactly a sale recorded with nothing paid: it is what leaves it an
        // en_attente amount the cashier settles later. Sending the total here, as before,
        // silently charged the client instead of invoicing them.
        var isFacture = AvecFactureCheck.IsChecked == true;
        var montantPaye = isFacture ? 0 : total;

        var clientTelephone = string.IsNullOrWhiteSpace(ClientTelephoneBox.Text) ? null : ClientTelephoneBox.Text.Trim();
        var clientEmail = string.IsNullOrWhiteSpace(ClientEmailBox.Text) ? null : ClientEmailBox.Text.Trim();

        var request = new CreateVenteRequest(
            items, _modePaiement, montantPaye, remise, clientNom, clientTelephone, clientEmail);

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

            await VenteReceiptDialog.ShowForAsync(vente, _session, Window.GetWindow(this));

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

    // --- Liste des Ventes ---

    private async void RefreshVentes_Click(object sender, RoutedEventArgs e) => await LoadVentesAsync();

    private void VenteSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        VenteSearchPlaceholder.Visibility = VenteSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!IsLoaded) return;
        _venteSearchDebounce.Stop();
        _venteSearchDebounce.Start();
    }

    private async void VenteSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _venteSearchDebounce.Stop();
        await LoadVentesAsync();
    }

    private async void VenteFilters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _venteSearchType = (string)((ComboBoxItem)VenteSearchTypeCombo.SelectedItem).Tag;
        _venteStatus = (string)((ComboBoxItem)VenteStatusCombo.SelectedItem).Tag;
        await LoadVentesAsync();
    }

    private async void VenteDateFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressVenteDateFilterEvent) return;

        var tag = (string)((ComboBoxItem)VenteDateFilterCombo.SelectedItem).Tag;
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (tag == "custom")
        {
            var dialog = new CustomDateRangeDialog(_venteDateDebut ?? today, _venteDateFin ?? today)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() != true)
            {
                // Same as Lonnii Business's own date modal cancel: put the dropdown back on
                // whatever filter was active before, without re-firing this handler.
                _suppressVenteDateFilterEvent = true;
                foreach (ComboBoxItem item in VenteDateFilterCombo.Items)
                {
                    if ((string)item.Tag != _venteDateFilterTag) continue;
                    VenteDateFilterCombo.SelectedItem = item;
                    break;
                }
                _suppressVenteDateFilterEvent = false;
                return;
            }

            _venteDateDebut = dialog.DateDebut;
            _venteDateFin = dialog.DateFin;
            _venteDateFilterTag = "custom";
            await LoadVentesAsync();
            return;
        }

        (_venteDateDebut, _venteDateFin) = tag switch
        {
            "today" => (today, today),
            "week" => (today.AddDays(-6), today),
            "month" => (new DateOnly(today.Year, today.Month, 1), today),
            "year" => (new DateOnly(today.Year, 1, 1), today),
            _ => (_venteDateDebut, _venteDateFin),
        };
        _venteDateFilterTag = tag;

        await LoadVentesAsync();
    }

    private async void ExportVentes_Click(object sender, RoutedEventArgs e)
    {
        if (_ventes.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "Aucune vente à exporter.", "Exporter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupName = _session.Groupe?.Nom ?? "espace_groupe";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{SlugifyFileName(groupName)}_ventes_{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "Fichier CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, BuildVentesCsv(groupName), new UTF8Encoding(true));
        }
        catch (IOException ex)
        {
            ShowMessage($"Erreur lors de l'export : {ex.Message}");
        }
    }

    private static string SlugifyFileName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name.Trim().ToLowerInvariant(), @"\s+", "_");

    /// <summary>Semicolon-delimited CSV, UTF-8 BOM, matching Lonnii Business's
    /// <c>exportToExcel</c> in Ventes.jsx byte for byte - the header block, the column
    /// order, and the totals summary (cancelled sales excluded from it) are all copied from
    /// there so a file exported from either side looks the same.</summary>
    private string BuildVentesCsv(string groupName)
    {
        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

        var sb = new System.Text.StringBuilder();
        sb.Append($"=== GESTION DES VENTES - {groupName.ToUpperInvariant()} ===\n");
        sb.Append($"Date d'export: {DateTime.Now:dd/MM/yyyy} - {DateTime.Now:HH:mm:ss}\n");
        sb.Append($"Nombre total de ventes: {_ventes.Count}\n\n");

        sb.Append(string.Join(';',
            "N° Vente", "Date", "Client", "Vendeur", "Montant Total", "Montant Payé",
            "Montant Restant", "Statut", "Mode de Paiement", "Motif Annulation"));
        sb.Append('\n');

        foreach (var v in _ventes)
        {
            var statut = v.StatutPaiement switch
            {
                "paye" => "Payé",
                "partiel" => "Partiel",
                "annule" => "Annulé",
                _ => "En attente",
            };

            sb.Append(string.Join(';',
                Csv(v.NumeroVente),
                Csv(v.DateVente.ToLocalTime().ToString("dd/MM/yyyy")),
                Csv(v.ClientNom ?? string.Empty),
                Csv(v.VendeurNom ?? "N/A"),
                Money.FormatPlain(v.MontantTotal),
                Money.FormatPlain(v.MontantPaye),
                Money.FormatPlain(Math.Max(0, v.MontantRestant)),
                Csv(statut),
                Csv(v.ModePaiement ?? "N/A"),
                Csv(v.CancellationReason ?? string.Empty)));
            sb.Append('\n');
        }

        var active = _ventes.Where(v => v.StatutPaiement != "annule").ToList();
        var cancelledCount = _ventes.Count - active.Count;
        sb.Append("\n--- RESUME ---\n");
        sb.Append($"Total des ventes (hors annulées): {Money.Format(active.Sum(v => v.MontantTotal))}\n");
        sb.Append($"Total payé: {Money.Format(active.Sum(v => v.MontantPaye))}\n");
        sb.Append($"Total restant: {Money.Format(active.Sum(v => v.MontantRestant))}\n");
        if (cancelledCount > 0)
            sb.Append($"Ventes annulées: {cancelledCount} (non incluses dans les totaux)\n");

        return sb.ToString();
    }

    private async Task LoadVentesAsync()
    {
        VenteListBusyPanel.Visibility = Visibility.Visible;
        try
        {
            var response = await _session.Api.GetVentesAsync(
                _venteStatus, VenteSearchBox.Text, _venteSearchType, _venteDateDebut, _venteDateFin);
            _ventes = response.Ventes.ToList();
            _ventePage = 1;
            RenderVenteList();
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            VenteListBusyPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void VentePrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_ventePage <= 1) return;
        _ventePage--;
        RenderVenteList();
    }

    private void VenteNextPage_Click(object sender, RoutedEventArgs e)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(_ventes.Count / (double)VentePageSize));
        if (_ventePage >= totalPages) return;
        _ventePage++;
        RenderVenteList();
    }

    /// <summary>Rebuilds the visible page of the list. Pagination is client-side over
    /// whatever <see cref="LoadVentesAsync"/> last fetched - same as ListeVentes.jsx's own
    /// <c>.slice((currentPage - 1) * itemsPerPage, ...)</c>.</summary>
    private void RenderVenteList()
    {
        VenteListRows.Items.Clear();

        if (_ventes.Count == 0)
        {
            VenteListEmptyPanel.Visibility = Visibility.Visible;
            VenteListScroll.Visibility = Visibility.Collapsed;
            VentePaginationPanel.Visibility = Visibility.Collapsed;
            return;
        }

        VenteListEmptyPanel.Visibility = Visibility.Collapsed;
        VenteListScroll.Visibility = Visibility.Visible;

        if (VenteListHeaderRow.Children.Count == 0) BuildVenteListHeader();

        var totalPages = Math.Max(1, (int)Math.Ceiling(_ventes.Count / (double)VentePageSize));
        _ventePage = Math.Clamp(_ventePage, 1, totalPages);

        foreach (var vente in _ventes.Skip((_ventePage - 1) * VentePageSize).Take(VentePageSize))
            VenteListRows.Items.Add(BuildVenteRow(vente));

        VentePaginationPanel.Visibility = _ventes.Count > VentePageSize ? Visibility.Visible : Visibility.Collapsed;
        VentePaginationText.Text = $"Page {_ventePage} sur {totalPages} ({_ventes.Count} ventes)";
        VentePrevPageButton.IsEnabled = _ventePage > 1;
        VenteNextPageButton.IsEnabled = _ventePage < totalPages;
    }

    /// <summary>Assigns a brush by resource key the same way <c>DynamicResource</c> does in
    /// XAML - a live reference that keeps following the key, not a one-time snapshot.
    ///
    /// <para>
    /// <c>ThemeManager</c> re-themes the app by replacing the whole palette
    /// ResourceDictionary object, not by mutating brush colours in place - by design, so
    /// every DynamicResource-bound piece of XAML picks up the new dictionary. But
    /// <c>(Brush)FindResource(key)</c> in code does a one-time lookup and assigns the brush
    /// *instance* it finds; once the dictionary is swapped, that instance is orphaned and
    /// keeps painting the old theme's colour until the element is rebuilt. Two symptoms of
    /// this: a row built in one theme staying stale after a toggle (until the list reloads),
    /// and - worse - a cell with no explicit Foreground taking the *new* theme's colour via
    /// the implicit TextBlock style's own DynamicResource while its row's Background (set
    /// with the old FindResource pattern) stays on the *old* theme, so the two can land on
    /// the same colour and the text vanishes. SetResourceReference avoids both.
    /// </para>
    /// </summary>
    private static void SetBrush(FrameworkElement element, DependencyProperty property, string key) =>
        element.SetResourceReference(property, key);

    private void BuildVenteListHeader()
    {
        VenteListHeaderRow.ColumnDefinitions.Clear();
        foreach (var (label, width) in VenteColumns)
        {
            VenteListHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var text = new TextBlock
            {
                Text = label, FontWeight = FontWeights.SemiBold, FontSize = 11,
                Margin = new Thickness(4, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            SetBrush(text, TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(text, VenteListHeaderRow.ColumnDefinitions.Count - 1);
            VenteListHeaderRow.Children.Add(text);
        }
    }

    /// <summary>One row of the sales list. Column order and the action buttons' conditions
    /// mirror Lonnii Business's ListeVentes.jsx line by line.</summary>
    private Border BuildVenteRow(VenteListItemDto vente)
    {
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (_, width) in VenteColumns)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var isCancelled = vente.StatutPaiement == "annule";
        var shortNumber = vente.NumeroVente.Contains('-')
            ? vente.NumeroVente[(vente.NumeroVente.LastIndexOf('-') + 1)..]
            : vente.NumeroVente;

        void AddCell(int column, FrameworkElement element)
        {
            Grid.SetColumn(element, column);
            row.Children.Add(element);
        }

        var numero = new TextBlock
        {
            Text = $"#{shortNumber}", FontFamily = new FontFamily("Consolas"), FontSize = 12,
            ToolTip = vente.NumeroVente, Margin = new Thickness(4, 0, 4, 0),
        };
        SetBrush(numero, TextBlock.ForegroundProperty, "TextPrimary");
        AddCell(0, numero);

        var date = new TextBlock
        {
            Text = vente.DateVente.ToLocalTime().ToString("dd/MM/yyyy"), FontSize = 12,
            Margin = new Thickness(4, 0, 4, 0),
        };
        SetBrush(date, TextBlock.ForegroundProperty, "TextPrimary");
        AddCell(1, date);

        var client = new TextBlock
        {
            Text = vente.ClientNom ?? "-", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 0),
        };
        SetBrush(client, TextBlock.ForegroundProperty, "TextPrimary");
        AddCell(2, client);

        var vendeur = new TextBlock
        {
            Text = PersonName.Abbreviate(vente.VendeurNom) is { Length: > 0 } abbrev ? abbrev : "N/A",
            ToolTip = vente.VendeurNom, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 0),
        };
        SetBrush(vendeur, TextBlock.ForegroundProperty, "TextMuted");
        AddCell(3, vendeur);

        var montantTotal = new TextBlock
        {
            Text = Money.Format(vente.MontantTotal), FontSize = 12, Margin = new Thickness(4, 0, 4, 0),
        };
        SetBrush(montantTotal, TextBlock.ForegroundProperty, "TextPrimary");
        AddCell(4, montantTotal);

        var montantPaye = new TextBlock
        {
            Text = Money.Format(vente.MontantPaye), FontSize = 12, Margin = new Thickness(4, 0, 4, 0),
        };
        SetBrush(montantPaye, TextBlock.ForegroundProperty, "Success");
        AddCell(5, montantPaye);

        var restantVisible = !isCancelled;
        var restantOwed = restantVisible && vente.MontantRestant > 0;
        var restant = new TextBlock
        {
            Text = restantVisible ? Money.Format(Math.Max(0, vente.MontantRestant)) : "-",
            FontSize = 12, Margin = new Thickness(4, 0, 4, 0),
            FontWeight = restantOwed ? FontWeights.Bold : FontWeights.Normal,
        };
        SetBrush(restant, TextBlock.ForegroundProperty, restantOwed ? "Danger" : "TextPrimary");
        AddCell(6, restant);

        var avoirVisible = !isCancelled && vente.AvoirAmount > 0;
        var avoir = new TextBlock
        {
            Text = avoirVisible ? Money.Format(vente.AvoirAmount) + (vente.IsAvoirSolded ? " ✓" : "") : "-",
            FontSize = 12, Margin = new Thickness(4, 0, 4, 0),
            FontWeight = avoirVisible ? FontWeights.Bold : FontWeights.Normal,
        };
        SetBrush(avoir, TextBlock.ForegroundProperty,
            avoirVisible ? (vente.IsAvoirSolded ? "Success" : "Warning") : "TextMuted");
        AddCell(7, avoir);

        AddCell(8, BuildStatusBadge(vente));

        var actions = new StackPanel { Orientation = Orientation.Horizontal };

        if (_canPrintReceipt && vente.StatutPaiement == "paye")
            actions.Children.Add(BuildActionButton("🖨", "Imprimer reçu", null,
                async () => await OpenReceiptAsync(vente.Id)));
        if (_canPrintReceipt && (vente.StatutPaiement == "en_attente" || vente.StatutPaiement == "partiel"))
            actions.Children.Add(BuildActionButton("🖨", "Imprimer facture", "Warning",
                async () => await OpenReceiptAsync(vente.Id)));
        if (_canViewVenteDetails)
            actions.Children.Add(BuildActionButton("🔍", "Détails", null,
                async () => await OpenDetailAsync(vente.Id)));
        if (_canSoldeAvoir && vente.AvoirAmount > 0 && !vente.IsAvoirSolded && !isCancelled)
            actions.Children.Add(BuildActionButton("✓", "Solder l'avoir", "Accent",
                async () => await SolderAvoirAsync(vente)));
        if (_canAddPayment && vente.StatutPaiement != "paye" && !isCancelled)
            actions.Children.Add(BuildActionButton("", "Ajouter paiement", "Success",
                async () => await AddPaiementAsync(vente)));
        if (_canEditVente && !isCancelled)
            actions.Children.Add(BuildActionButton("✎", "Modifier", null,
                async () => await EditVenteAsync(vente)));
        if (_canCancelVente && !isCancelled)
            actions.Children.Add(BuildActionButton("✕", "Annuler", "Danger",
                async () => await CancelVenteAsync(vente)));

        AddCell(9, actions);

        var border = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 8, 0, 8),
            Child = row,
        };
        SetBrush(border, Border.BackgroundProperty, "Surface");
        SetBrush(border, Border.BorderBrushProperty, "Border");
        return border;
    }

    private static readonly Dictionary<string, (string Label, string ColorKey)> StatusBadges = new()
    {
        ["paye"] = ("Payé", "Success"),
        ["partiel"] = ("Partiel", "Warning"),
        ["en_attente"] = ("ATT.", "TextMuted"),
        ["annule"] = ("Annulé", "Danger"),
    };

    private FrameworkElement BuildStatusBadge(VenteListItemDto vente)
    {
        var (label, colorKey) = StatusBadges.TryGetValue(vente.StatutPaiement, out var badge)
            ? badge : ("?", "TextMuted");

        var text = new TextBlock
        {
            Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 0),
        };
        SetBrush(text, TextBlock.ForegroundProperty, colorKey);

        if (vente.StatutPaiement == "annule" && !string.IsNullOrWhiteSpace(vente.CancellationReason))
            text.ToolTip = $"Motif : {vente.CancellationReason}";

        return text;
    }

    private static readonly FontFamily SegoeMdl2 = new("Segoe MDL2 Assets");

    private Button BuildActionButton(string glyph, string tooltip, string? colorKey, Func<Task> onClick)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        // Only the Segoe MDL2 Assets private-use-area glyphs (U+E000-U+F8FF) need that font
        // explicitly - it has no ASCII or standard Unicode glyphs at all, so forcing it on a
        // plain symbol like a checkmark or an emoji renders tofu instead. Everything else
        // keeps the default font, which already resolves those through normal font fallback
        // (proven elsewhere in this view: the placeholder icons, the cart row's remove mark).
        if (glyph.Length == 1 && glyph[0] is >= '' and <= '')
            icon.FontFamily = SegoeMdl2;

        // IconButton (see Styles.xaml) keeps the WPF default Button template out of this -
        // that template bakes in its own solid-colour hover/pressed states (a light system
        // highlight) that a plain Background=Transparent cannot override, which is why a
        // hover here used to flash white even in dark mode. ButtonBase-derived styles fade
        // opacity instead, so Foreground stays whatever colour this button was given.
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 26, Height = 26, Margin = new Thickness(2, 0, 2, 0),
            ToolTip = tooltip, Content = icon,
        };
        SetBrush(button, Control.ForegroundProperty, colorKey ?? "TextSecondary");
        button.Click += async (_, _) => await onClick();
        return button;
    }

    /// <summary>Both print actions - the printable receipt/facture.</summary>
    private async Task OpenReceiptAsync(string venteId)
    {
        SetBusy(true);
        try
        {
            var vente = await _session.Api.GetVenteAsync(venteId);
            await VenteReceiptDialog.ShowForAsync(vente, _session, Window.GetWindow(this));
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

    /// <summary>"Détails" - the read-only information panel (client info, payment history,
    /// line items), distinct from the printable receipt above. Mirrors Lonnii Business's own
    /// separate detail modal.</summary>
    private async Task OpenDetailAsync(string venteId)
    {
        SetBusy(true);
        try
        {
            var vente = await _session.Api.GetVenteAsync(venteId);
            new VenteDetailDialog(vente) { Owner = Window.GetWindow(this) }.ShowDialog();
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

    private async Task AddPaiementAsync(VenteListItemDto vente)
    {
        var dialog = new AddPaymentDialog(vente.NumeroVente, Math.Max(0, vente.MontantRestant))
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true) return;

        SetBusy(true);
        try
        {
            await _session.Api.AddPaiementAsync(vente.Id, new AddPaiementRequest(dialog.Montant, dialog.ModePaiement));
            await LoadVentesAsync();
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

    private async Task EditVenteAsync(VenteListItemDto vente)
    {
        var dialog = new EditVenteDialog(vente.NumeroVente, vente.ClientNom, vente.DateVente)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true) return;

        SetBusy(true);
        try
        {
            await _session.Api.EditVenteAsync(vente.Id, new EditVenteRequest(dialog.ClientNom, dialog.DateVente));
            await LoadVentesAsync();
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

    private async Task CancelVenteAsync(VenteListItemDto vente)
    {
        var dialog = new CancelVenteDialog(vente.NumeroVente) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        SetBusy(true);
        try
        {
            await _session.Api.CancelVenteAsync(vente.Id, new CancelVenteRequest(dialog.Motif));
            await LoadVentesAsync();
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

    private async Task SolderAvoirAsync(VenteListItemDto vente)
    {
        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"Voulez-vous vraiment solder l'avoir de {Money.Format(vente.AvoirAmount)} pour cette vente ?",
            "Solder l'avoir", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            await _session.Api.SolderAvoirAsync(vente.Id);
            await LoadVentesAsync();
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

    // --- Statistiques ---

    /// <summary>First visit to the tab: makes sure categories/products are loaded and the
    /// filter combos built before the first stats fetch.</summary>
    private async Task OpenStatistiquesAsync()
    {
        try
        {
            if (_categories.Count == 0) _categories = await _session.Api.GetCategoriesAsync();
            if (_statsProducts.Count == 0) _statsProducts = await _session.Api.GetProductsAsync();
        }
        catch (ApiException ex)
        {
            // The tab itself is only shown to someone holding ViewVentesAnalytics (see the
            // constructor), but the category/product filters still need the broader
            // ViewStock/CreateVente-gated catalogue endpoints - this is a second line of
            // defence, not the expected path.
            ShowMessage(ex.Message);
            return;
        }

        if (StatsCategoryCombo.Items.Count == 0) BuildStatsFilterCombos();

        await LoadStatsAsync();
    }

    private void BuildStatsFilterCombos()
    {
        StatsCategoryCombo.Items.Clear();
        StatsCategoryCombo.Items.Add(new ComboBoxItem { Content = "Toutes catégories", Tag = null, IsSelected = true });
        foreach (var category in _categories.Where(c => c.IsActive))
            StatsCategoryCombo.Items.Add(new ComboBoxItem { Content = category.Name, Tag = category.Id });

        StatsProductCombo.Items.Clear();
        StatsProductCombo.Items.Add(new ComboBoxItem { Content = "Tous les produits", Tag = null, IsSelected = true });
        foreach (var product in _statsProducts.OrderBy(p => p.Name))
            StatsProductCombo.Items.Add(new ComboBoxItem { Content = product.Name, Tag = product.Id });
    }

    private async void RefreshStats_Click(object sender, RoutedEventArgs e) => await LoadStatsAsync();

    private async void StatsFilters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _statsCategoryId = (StatsCategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        _statsProductId = (StatsProductCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        await LoadStatsAsync();
    }

    /// <summary>Mirrors <see cref="VenteDateFilter_Changed"/> - same presets, same "custom"
    /// dialog and revert-on-cancel behaviour, just against the Statistiques tab's own
    /// combo and date fields.</summary>
    private async void StatsPeriod_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressStatsDateFilterEvent) return;

        var tag = (string)((ComboBoxItem)StatsPeriodCombo.SelectedItem).Tag;
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (tag == "custom")
        {
            var dialog = new CustomDateRangeDialog(_statsDateDebut ?? today, _statsDateFin ?? today)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() != true)
            {
                _suppressStatsDateFilterEvent = true;
                foreach (ComboBoxItem item in StatsPeriodCombo.Items)
                {
                    if ((string)item.Tag != _statsDateFilterTag) continue;
                    StatsPeriodCombo.SelectedItem = item;
                    break;
                }
                _suppressStatsDateFilterEvent = false;
                return;
            }

            _statsDateDebut = dialog.DateDebut;
            _statsDateFin = dialog.DateFin;
            _statsDateFilterTag = "custom";
            await LoadStatsAsync();
            return;
        }

        (_statsDateDebut, _statsDateFin) = tag switch
        {
            "today" => (today, today),
            "week" => (today.AddDays(-6), today),
            "month" => (new DateOnly(today.Year, today.Month, 1), today),
            "year" => (new DateOnly(today.Year, 1, 1), today),
            _ => (_statsDateDebut, _statsDateFin),
        };
        _statsDateFilterTag = tag;

        await LoadStatsAsync();
    }

    private async Task LoadStatsAsync()
    {
        StatsBusyPanel.Visibility = Visibility.Visible;
        try
        {
            _stats = await _session.Api.GetVentesStatsAsync(
                _statsDateDebut, _statsDateFin, _statsCategoryId, _statsProductId);
            RenderStats();
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            StatsBusyPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Rebuilds every KPI tile and chart from <see cref="_stats"/>. Tile order and
    /// labels mirror Lonnii Business's own stat-grid, plus two extra tiles (Moyenne
    /// Quotidienne, Croissance) this port surfaces as tiles rather than a separate "Analyse
    /// Temporelle" text block.</summary>
    private void RenderStats()
    {
        if (_stats is not { } stats) return;

        StatsTilesPanel.Children.Clear();
        StatsTilesPanel.Children.Add(BuildStatTile("Nombre de Ventes", stats.TotalVentes.ToString(), "TextPrimary"));
        StatsTilesPanel.Children.Add(BuildStatTile("Chiffre d'Affaires", Money.Format(stats.ChiffreAffaires), "Success"));
        StatsTilesPanel.Children.Add(BuildStatTile("Montant Encaissé", Money.Format(stats.MontantEncaisse), "Accent"));
        StatsTilesPanel.Children.Add(BuildStatTile("Montant Restant", Money.Format(stats.MontantRestant), "Danger"));
        StatsTilesPanel.Children.Add(BuildStatTile("Total Avoir Client", Money.Format(stats.TotalAvoir), "Warning"));
        StatsTilesPanel.Children.Add(BuildStatTile("Vente Moyenne", Money.Format(stats.VenteMoyenne), "TextPrimary"));
        StatsTilesPanel.Children.Add(BuildStatTile("Ventes Payées", stats.VentesPayees.ToString(), "Success"));
        StatsTilesPanel.Children.Add(BuildStatTile("Paiements Partiels", stats.VentesPartielles.ToString(), "Warning"));
        StatsTilesPanel.Children.Add(BuildStatTile("En Attente", stats.VentesEnAttente.ToString(), "TextMuted"));
        StatsTilesPanel.Children.Add(BuildStatTile("Ventes Annulées", stats.VentesAnnulees.ToString(), "Danger"));
        StatsTilesPanel.Children.Add(BuildStatTile("Moyenne Quotidienne", Money.Format(stats.MoyenneQuotidienne), "TextPrimary"));
        StatsTilesPanel.Children.Add(BuildStatTile(
            "Croissance", $"{(stats.Croissance >= 0 ? "+" : string.Empty)}{stats.Croissance:0.#}%",
            stats.Croissance >= 0 ? "Success" : "Danger"));

        StatsEmptyPanel.Visibility = stats.TotalVentes == 0 ? Visibility.Visible : Visibility.Collapsed;

        // LiveCharts paints are plain SkiaSharp colours, not DynamicResource-aware (unlike
        // the legend, which is plain WPF), so both are snapshotted fresh on every render - see
        // the ThemeManager.Changed subscription in the constructor, which re-renders this
        // whenever the theme toggles while Statistiques is open.
        var axisPaint = new SolidColorPaint(CurrentTextColor());
        var separatorPaint = new SolidColorPaint(CurrentBorderColor()) { StrokeThickness = 1 };

        // Ventes par Catégorie
        var categorySlices = stats.CategorySales
            .Select((c, i) => (Label: c.Categorie, Value: c.MontantTotal, Color: CategoryPalette[i % CategoryPalette.Length]))
            .ToList();
        CategoryEmptyText.Visibility = categorySlices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CategoryPieChart.Series = categorySlices.Select(c => (ISeries)new PieSeries<double>
        {
            Values = [(double)c.Value], Name = c.Label, Fill = new SolidColorPaint(c.Color),
        }).ToArray();
        RenderPieLegend(CategoryLegendPanel, categorySlices);

        // Répartition des Paiements
        var paymentSlices = new (string Label, decimal Value, SKColor Color)[]
        {
            ("Espèces", stats.PaiementCash, CategoryPalette[0]),
            ("Mobile Money", stats.PaiementMobile, CategoryPalette[1]),
            ("Carte Bancaire", stats.PaiementCarte, CategoryPalette[2]),
            ("Autres", stats.PaiementAutres, CategoryPalette[4]),
        }.Where(p => p.Value > 0).ToList();

        PaymentEmptyText.Visibility = paymentSlices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaymentPieChart.Series = paymentSlices.Select(p => (ISeries)new PieSeries<double>
        {
            Values = [(double)p.Value], Name = p.Label, Fill = new SolidColorPaint(p.Color),
        }).ToArray();
        RenderPieLegend(PaymentLegendPanel, paymentSlices);

        // Évolution du Chiffre d'Affaires
        RevenueEmptyText.Visibility = stats.Serie.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RevenueChart.Series =
        [
            new ColumnSeries<double>
            {
                Values = stats.Serie.Select(p => (double)p.MontantTotal).ToArray(),
                Fill = new SolidColorPaint(CategoryPalette[0]),
                Name = "Chiffre d'Affaires",
                // Otherwise LiveCharts prints the raw double with its own culture-invariant
                // "." decimal point and no currency - Money.Format is the one place this app
                // decides both (French "," and the group's own currency label).
                YToolTipLabelFormatter = point => Money.Format((decimal)point.Coordinate.PrimaryValue),
            },
        ];
        RevenueChart.XAxes =
        [
            new Axis
            {
                Labels = stats.Serie.Select(p => p.Date.ToString("dd/MM")).ToArray(),
                LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint,
                TextSize = 10,
            },
        ];
        RevenueChart.YAxes =
        [
            new Axis
            {
                LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint,
                Labeler = v => Money.Format((decimal)v),
            },
        ];

        // Produits les Plus Vendus (horizontal bars, highest first at the top)
        var topProducts = stats.TopProducts.AsEnumerable().Reverse().ToList();
        TopProductsEmptyText.Visibility = topProducts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TopProductsChart.Series =
        [
            new RowSeries<double>
            {
                Values = topProducts.Select(p => (double)p.MontantTotal).ToArray(),
                Fill = new SolidColorPaint(CategoryPalette[1]),
                Name = "Montant vendu",
                YToolTipLabelFormatter = point => Money.Format((decimal)point.Coordinate.PrimaryValue),
            },
        ];
        TopProductsChart.YAxes =
        [
            new Axis
            {
                Labels = topProducts.Select(p => p.Nom).ToArray(),
                LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint,
                TextSize = 10,
            },
        ];
        // The value axis for a row series is horizontal (X), unlike a column series.
        TopProductsChart.XAxes =
        [
            new Axis
            {
                LabelsPaint = axisPaint, SeparatorsPaint = separatorPaint,
                Labeler = v => Money.Format((decimal)v),
            },
        ];
    }

    /// <summary>One KPI tile: a muted label over a bold coloured value, same visual weight
    /// throughout the grid - <paramref name="colorKey"/> is a theme brush key (e.g. "Success",
    /// "Danger"), not a literal colour, so tiles stay correct across the light/dark toggle.</summary>
    private Border BuildStatTile(string label, string value, string colorKey)
    {
        var labelText = new TextBlock
        {
            Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap,
        };
        SetBrush(labelText, TextBlock.ForegroundProperty, "TextMuted");

        var valueText = new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeights.Bold };
        SetBrush(valueText, TextBlock.ForegroundProperty, colorKey);

        var stack = new StackPanel();
        stack.Children.Add(labelText);
        stack.Children.Add(valueText);

        var border = new Border
        {
            Width = 190, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Child = stack,
        };
        SetBrush(border, Border.BackgroundProperty, "Surface");
        SetBrush(border, Border.BorderBrushProperty, "Border");
        return border;
    }

    /// <summary>
    /// A plain WPF legend for a pie chart - colour dot, label, and its share of the total in
    /// parentheses - built natively rather than through LiveCharts' own SkiaSharp-rendered
    /// legend, which reads blurry at most Windows display scales.
    /// </summary>
    private static void RenderPieLegend(Panel container, IReadOnlyList<(string Label, decimal Value, SKColor Color)> slices)
    {
        container.Children.Clear();

        var total = slices.Sum(s => s.Value);
        foreach (var slice in slices)
        {
            var dot = new Ellipse
            {
                Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0),
                Fill = new SolidColorBrush(Color.FromArgb(slice.Color.Alpha, slice.Color.Red, slice.Color.Green, slice.Color.Blue)),
            };

            var percent = total > 0 ? slice.Value / total * 100 : 0;
            // "45,23%", not "45.23%" - French decimal comma, same as every other number in
            // this app (Money.FormatPlain), computed with InvariantCulture so the "." it
            // starts from is predictable regardless of the machine's own locale.
            var percentText = percent.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');
            var text = new TextBlock
            {
                Text = $"{slice.Label} ({percentText}%)", FontSize = 12, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            SetBrush(text, TextBlock.ForegroundProperty, "TextPrimary");

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(dot);
            row.Children.Add(text);
            container.Children.Add(row);
        }
    }

    /// <summary>Snapshot of the current theme's secondary text colour, for chart axis labels -
    /// LiveCharts paints are plain SkiaSharp colours, not DynamicResource-aware, so this is
    /// read fresh every time <see cref="RenderStats"/> runs rather than bound once.</summary>
    private SKColor CurrentTextColor()
    {
        if (FindResource("TextSecondary") is SolidColorBrush brush)
            return new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
        return SKColors.Gray;
    }

    /// <summary>Snapshot of the current theme's border colour, for chart gridlines - dim
    /// enough not to compete with the bars/lines themselves, in either theme.</summary>
    private SKColor CurrentBorderColor()
    {
        if (FindResource("Border") is SolidColorBrush brush)
            return new SKColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A);
        return SKColors.Gray;
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
