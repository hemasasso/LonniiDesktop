using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lonnii.Client.Features.CustomerDisplay;

/// <summary>
/// The customer-facing view: what is being bought, the total due, and (once the cashier types
/// what was handed over) the change. Shown full-screen on a second monitor, or as an ordinary
/// resizable window for the settings screen's preview.
/// </summary>
public partial class CustomerDisplayWindow : Window
{
    /// <param name="OldTotalText">The undiscounted price, shown struck through above the new one; null when the line has no discount.</param>
    private sealed record Row(string Name, string Detail, string TotalText, bool IsFocus, string? OldTotalText)
    {
        public Visibility OldTotalVisibility => OldTotalText is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private MonitorInfo? _target;
    private string _layout = DisplayLayouts.Classique;
    private string _pillStyle = DisplayPillStyles.Auto;

    // Welcome screen
    private CustomerDisplaySettings _idle = new();
    private string? _loadedMedia;
    private GifAnimator? _gif;
    private string _theme = DisplayThemes.Light;
    private string _accent = CustomerDisplaySettings.DefaultAccent;
    private double _scale = 0.8;
    private bool _portrait;

    /// <param name="preview">An ordinary window with a title bar, for trying the layout on a
    /// machine that has no second screen.</param>
    public CustomerDisplayWindow(bool preview = false)
    {
        ApplyStyle(new CustomerDisplaySettings());
        InitializeComponent();

        if (preview)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = true;
            ShowActivated = true;
            Cursor = Cursors.Arrow;
            Width = 960;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Title = "Aperçu — écran client";
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (_, _) => ApplyPlacement();
        }

        SizeChanged += (_, _) => FitCanvasToScreen();
        ItemsCard.SizeChanged += (_, e) =>
            ItemsCard.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
                ItemsCard.CornerRadius.TopLeft, ItemsCard.CornerRadius.TopLeft);
        Loaded += (_, _) => FitCanvasToScreen();

        // "Automatique" follows the till's own light/dark switch.
        ThemeManager.Changed += OnAppThemeChanged;
        Closed += (_, _) =>
        {
            ThemeManager.Changed -= OnAppThemeChanged;
            StopMedia();
        };

        // A video loops for as long as the welcome screen is up.
        IdleMediaVideo.MediaEnded += (_, _) =>
        {
            IdleMediaVideo.Position = TimeSpan.Zero;
            IdleMediaVideo.Play();
        };
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        if (_theme != DisplayThemes.Auto) return;
        ApplyColors(_theme, _accent);
        StyleForLayout();
        StylePill();
    }

    // --- Placement ---

    /// <summary>Puts the window over exactly this monitor, in physical pixels. Done with
    /// SetWindowPos rather than Left/Top/Width/Height because those are DIPs of whichever
    /// DPI the window thinks it is on - wrong the moment the two screens scale differently.</summary>
    public void MoveTo(MonitorInfo monitor)
    {
        _target = monitor;
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero) ApplyPlacement();
    }

    private void ApplyPlacement()
    {
        if (_target is not { } m) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, m.X, m.Y, m.Width, m.Height, SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>Keeps the design canvas the same shape as the screen - 1280 wide on a landscape
    /// one, 720 wide on a portrait one - so the Viewbox never letterboxes and a taller screen
    /// simply shows more item rows. On a portrait screen every model stacks basket and summary.</summary>
    private void FitCanvasToScreen()
    {
        if (Canvas is null || ActualWidth < 1 || ActualHeight < 1) return;

        // A smaller text size is a bigger design canvas: everything shrinks together, and more
        // rows fit, instead of text outgrowing the boxes it sits in.
        var portrait = ActualWidth < ActualHeight;
        var designWidth = (portrait ? 720d : 1280d) / _scale;
        Canvas.Width = designWidth;
        Canvas.Height = Math.Max(designWidth * ActualHeight / ActualWidth, 320);

        if (portrait == _portrait) return;
        _portrait = portrait;
        ArrangeBody();
        if (IdlePanel.Visibility == Visibility.Visible) ArrangeIdle();
    }

    // --- Layout: the five models ---

    /// <summary>Where the basket and the summary sit, for the chosen model and the screen's shape.</summary>
    private void ArrangeBody()
    {
        if (BodyGrid is null) return;

        // Stacked: the summary under (or, for Grand total, above) a full-width basket.
        var stacked = _portrait || _layout == DisplayLayouts.Bandeau;
        var totalFirst = _layout == DisplayLayouts.GrandTotal;

        if (stacked)
        {
            BodyGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            BodyGrid.ColumnDefinitions[1].Width = new GridLength(0);
            BodyGrid.RowDefinitions[0].Height = totalFirst ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            BodyGrid.RowDefinitions[1].Height = totalFirst ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            Place(ItemsCard, row: totalFirst ? 1 : 0, column: 0, span: 2);
            Place(SummaryCard, row: totalFirst ? 0 : 1, column: 0, span: 2);
            SummaryCard.Margin = totalFirst ? new Thickness(0, 0, 0, 24) : new Thickness(0, 24, 0, 0);
            ItemsCard.Margin = new Thickness(0);
        }
        else
        {
            // Side by side: Grand total gives the summary the left half, the others 40 % on the right.
            BodyGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            BodyGrid.RowDefinitions[1].Height = GridLength.Auto;
            BodyGrid.ColumnDefinitions[0].Width = new GridLength(totalFirst ? 1 : 3, GridUnitType.Star);
            BodyGrid.ColumnDefinitions[1].Width = new GridLength(totalFirst ? 1 : 2, GridUnitType.Star);
            Place(SummaryCard, row: 0, column: totalFirst ? 0 : 1, span: 1);
            Place(ItemsCard, row: 0, column: totalFirst ? 1 : 0, span: 1);
            SummaryCard.Margin = totalFirst ? new Thickness(0) : new Thickness(28, 0, 0, 0);
            ItemsCard.Margin = totalFirst ? new Thickness(28, 0, 0, 0) : new Thickness(0);
        }

        // Grand total: the amount is the point. In a band it stays one readable line.
        TotalText.FontSize = totalFirst ? 120 : _layout == DisplayLayouts.Bandeau ? 60 : 68;
        TotalLabel.FontSize = totalFirst ? 28 : 22;
        SummaryTitle.Visibility = _layout == DisplayLayouts.Bandeau ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Place(UIElement element, int row, int column, int span)
    {
        System.Windows.Controls.Grid.SetRow(element, row);
        System.Windows.Controls.Grid.SetColumn(element, column);
        System.Windows.Controls.Grid.SetColumnSpan(element, span);
    }

    /// <summary>The model's own look: header bar, card shape and spacing. Colours stay the
    /// shop's choice; only how much of them each model shows changes.</summary>
    private void StyleForLayout()
    {
        if (HeaderBar is null) return;

        var radius = _layout switch
        {
            DisplayLayouts.Elegant => 28,
            DisplayLayouts.Minimal => 0,
            _ => 16,
        };
        ItemsCard.CornerRadius = SummaryCard.CornerRadius = new CornerRadius(radius);
        ItemsCard.BorderThickness = SummaryCard.BorderThickness =
            _layout == DisplayLayouts.Elegant ? new Thickness(0) : new Thickness(1);
        BodyGrid.Margin = new Thickness(_layout == DisplayLayouts.Minimal ? 0 : 32);
        if (_layout == DisplayLayouts.Minimal)
        {
            // Flat: the two blocks are separated by a line rather than floating cards.
            SummaryCard.BorderThickness = _portrait ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
            ItemsCard.BorderThickness = new Thickness(0);
        }

        switch (_layout)
        {
            case DisplayLayouts.Elegant:
                // No bar: the shop's name in its brand colour, sitting on the tinted page.
                HeaderBar.Visibility = Visibility.Visible;
                HeaderBar.Background = Brushes.Transparent;
                HeaderBar.Padding = new Thickness(48, 28, 48, 0);
                HeaderCompany.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrandText");
                HeaderCount.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrandText");
                HeaderCountPill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BrandSoft");
                SellingPanel.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "BrandPage");
                break;
            case DisplayLayouts.Minimal:
                HeaderBar.Visibility = Visibility.Collapsed;
                SellingPanel.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "Card");
                break;
            default:
                HeaderBar.Visibility = Visibility.Visible;
                HeaderBar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brand");
                HeaderBar.Padding = new Thickness(40, 18, 40, 18);
                HeaderCompany.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "OnBrand");
                HeaderCount.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "OnBrand");
                HeaderCountPill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BrandOnSoft");
                SellingPanel.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "Bg");
                break;
        }

        ItemsCard.Clip = ItemsCard.ActualWidth > 0
            ? new RectangleGeometry(new Rect(0, 0, ItemsCard.ActualWidth, ItemsCard.ActualHeight), radius, radius)
            : null;
    }

    // --- Theme ---

    /// <summary>Applies the look chosen in Configuration: model, colours, typeface and text size.</summary>
    public void ApplyStyle(CustomerDisplaySettings s)
    {
        FontFamily = new FontFamily(string.IsNullOrWhiteSpace(s.FontFamily) ? CustomerDisplaySettings.DefaultFont : s.FontFamily);
        _scale = Math.Clamp(s.TextScale, 20, 150) / 100d;
        _layout = DisplayLayouts.All.Any(l => l.Key == s.Layout) ? s.Layout : DisplayLayouts.Classique;
        _pillStyle = s.PillStyle;
        _idle = s;
        LoadMedia(s.IdleMediaPath);
        ApplyColors(s.Theme, s.AccentColor);
        FitCanvasToScreen();
        ArrangeBody();
        StyleForLayout();
        StylePill();
    }

    /// <summary>The "3 articles" pill: the model's own colours, unless the shop chose white.</summary>
    private void StylePill()
    {
        if (HeaderCountPill is null) return;
        switch (_pillStyle)
        {
            case DisplayPillStyles.WhiteBackground:
                HeaderCountPill.Background = Brushes.White;
                HeaderCount.Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
                break;
            case DisplayPillStyles.WhiteText:
                HeaderCount.Foreground = Brushes.White;
                break;
        }
    }

    /// <summary>Fills in the colour brushes the XAML refers to, from a theme and a brand colour.</summary>
    private void ApplyColors(string theme, string accentHex)
    {
        _theme = theme;
        _accent = accentHex;

        var dark = theme == DisplayThemes.Dark || (theme == DisplayThemes.Auto && ThemeManager.IsDark);
        var brand = ParseColor(accentHex);

        // White on a pale brand colour (yellow, say) would be unreadable.
        var onBrand = Luminance(brand) > 0.6 ? Color.FromRgb(0x11, 0x18, 0x27) : Colors.White;

        var card = dark ? Rgb("#131C2E") : Colors.White;
        Set("Bg", dark ? Rgb("#0B1220") : Rgb("#F3F4F6"));
        Set("Card", card);
        Set("Ink", dark ? Rgb("#F8FAFC") : Rgb("#111827"));
        Set("Muted", dark ? Rgb("#94A3B8") : Rgb("#6B7280"));
        Set("Line", dark ? Rgb("#1F2A3F") : Rgb("#E5E7EB"));
        // Élégant's page: the background faintly tinted with the brand colour.
        Set("BrandPage", Blend(dark ? Rgb("#0B1220") : Rgb("#F3F4F6"), brand, dark ? 0.12 : 0.08));

        Set("Brand", brand);
        Set("BrandSoft", Blend(card, brand, dark ? 0.22 : 0.10));
        Set("BrandText", dark ? Blend(brand, Colors.White, 0.35) : brand);
        Set("OnBrand", onBrand);
        Set("OnBrandSoft", Color.FromArgb(0xD9, onBrand.R, onBrand.G, onBrand.B));
        Set("BrandOnSoft", Color.FromArgb(0x33, onBrand.R, onBrand.G, onBrand.B));
        SetBrush("BrandDeep", new LinearGradientBrush(brand, Blend(brand, Colors.Black, 0.45), 90));

        Set("Good", dark ? Rgb("#4ADE80") : Rgb("#15803D"));
        Set("GoodSoft", dark ? Rgb("#12372A") : Rgb("#DCFCE7"));
        Set("Bad", dark ? Rgb("#F87171") : Rgb("#B91C1C"));
        Set("BadSoft", dark ? Rgb("#4A1D1D") : Rgb("#FEE2E2"));
    }

    private void Set(string key, Color color) => SetBrush(key, new SolidColorBrush(color));

    private void SetBrush(string key, Brush brush)
    {
        brush.Freeze();
        Resources[key] = brush;
    }

    private static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color ParseColor(string hex)
    {
        try { return Rgb(hex); }
        catch (FormatException) { return Rgb(CustomerDisplaySettings.DefaultAccent); }
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    // --- Content ---

    public void Show(CustomerDisplaySnapshot s)
    {
        SellingPanel.Visibility          = s.Mode == DisplayMode.Selling           ? Visibility.Visible : Visibility.Collapsed;
        IdlePanel.Visibility             = s.Mode == DisplayMode.Idle              ? Visibility.Visible : Visibility.Collapsed;
        PaymentConfirmedPanel.Visibility = s.Mode == DisplayMode.PaymentConfirmed  ? Visibility.Visible : Visibility.Collapsed;
        ThanksPanel.Visibility           = s.Mode == DisplayMode.Thanks            ? Visibility.Visible : Visibility.Collapsed;

        switch (s.Mode)
        {
            case DisplayMode.Selling:           ShowSelling(s); break;
            case DisplayMode.PaymentConfirmed:  ShowPaymentConfirmed(s); break;
            case DisplayMode.Thanks:            ShowThanks(s); break;
            default:                            ShowIdle(s); break;
        }
    }

    private void ShowPaymentConfirmed(CustomerDisplaySnapshot s)
    {
        // FocusLineId is repurposed to carry the payment method name (e.g. "Orange Money").
        ConfirmedModeText.Text  = s.FocusLineId ?? string.Empty;
        ConfirmedTotalText.Text = Money.Format(s.Total);
    }

    private void ShowSelling(CustomerDisplaySnapshot s)
    {
        HeaderCompany.Text = s.Branding.Company;
        HeaderLogo.Source = s.Branding.Logo is { } logo ? ImageHelper.FromBytes(logo) : null;
        HeaderLogoChip.Visibility = HeaderLogo.Source is null ? Visibility.Collapsed : Visibility.Visible;

        var articles = s.Lines.Sum(l => l.Quantity);
        HeaderCount.Text = articles == 1 ? "1 article" : $"{articles} articles";

        // Only the line that just changed is highlighted, and only when there is more than one
        // to tell apart.
        ItemsList.ItemsSource = s.Lines.Select(l => new Row(
            l.Unite is null ? l.Name : $"{l.Name} ({l.Unite})",
            $"{l.Quantity} × {Money.Format(l.UnitPrice)}",
            Money.Format(l.Total),
            IsFocus: s.Lines.Count > 1 && l.Id == s.FocusLineId,
            OldTotalText: l.IsDiscounted ? Money.Format(l.FullTotal!.Value) : null)).ToList();

        // The newest line is at the bottom; keep it in view as the list grows.
        Dispatcher.BeginInvoke(() => ItemsScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);

        SubtotalRow.Visibility = s.Discount > 0 || s.Tva > 0 ? Visibility.Visible : Visibility.Collapsed;
        SubtotalText.Text = Money.Format(s.Subtotal);
        DiscountRow.Visibility = s.Discount > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscountText.Text = $"- {Money.Format(s.Discount)}";
        TvaRow.Visibility = s.Tva > 0 ? Visibility.Visible : Visibility.Collapsed;
        TvaText.Text = $"+ {Money.Format(s.Tva)}";
        TotalText.Text = Money.Format(s.Total);

        if (s.Recu is { } recu && s.Difference is { } diff)
        {
            RecuBox.Visibility = Visibility.Visible;
            RecuText.Text = Money.Format(recu);

            var good = diff >= 0;
            DifferenceCard.Background = (Brush)Resources[good ? "GoodSoft" : "BadSoft"];
            DifferenceLabel.Foreground = DifferenceText.Foreground = (Brush)Resources[good ? "Good" : "Bad"];
            DifferenceLabel.Text = good ? "MONNAIE À RENDRE" : "IL MANQUE";
            DifferenceText.Text = Money.Format(Math.Abs(diff));
        }
        else
        {
            RecuBox.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowIdle(CustomerDisplaySnapshot s)
    {
        var o = _idle;

        IdleCompany.Text = s.Branding.Company;
        IdleCompany.Visibility = o.IdleShowCompany && !string.IsNullOrWhiteSpace(s.Branding.Company) ? Visibility.Visible : Visibility.Collapsed;
        IdleTitle.Text = o.IdleTitle ?? string.Empty;
        IdleTitle.Visibility = string.IsNullOrWhiteSpace(o.IdleTitle) ? Visibility.Collapsed : Visibility.Visible;
        IdleMessage.Text = o.IdleMessage ?? string.Empty;
        IdleMessage.Visibility = string.IsNullOrWhiteSpace(o.IdleMessage) ? Visibility.Collapsed : Visibility.Visible;

        IdleLogo.Source = o.IdleShowLogo && s.Branding.Logo is { } logo ? ImageHelper.FromBytes(logo) : null;
        IdleLogoChip.Visibility = IdleLogo.Source is null ? Visibility.Collapsed : Visibility.Visible;

        IdleQr.Source = o.IdleShowQr && s.Branding.Qr is { } qr ? ImageHelper.FromBytes(qr) : null;
        var qrNote = o.IdleShowQr ? s.Branding.QrNote : null;
        IdleQrBox.Visibility = IdleQr.Source is null && string.IsNullOrWhiteSpace(qrNote) ? Visibility.Collapsed : Visibility.Visible;
        IdleQrCard.Visibility = IdleQr.Source is null ? Visibility.Collapsed : Visibility.Visible;
        IdleQrNote.Text = qrNote ?? string.Empty;

        ArrangeIdle();
        if (IdleMediaVideo.Visibility == Visibility.Visible && IdleMediaHost.Visibility == Visibility.Visible) IdleMediaVideo.Play();
    }

    // --- Welcome screen: the five models ---

    /// <summary>Loads the chosen picture, GIF or video once; the same file is not reloaded on
    /// every snapshot.</summary>
    private void LoadMedia(string? path)
    {
        if (path == _loadedMedia) return;
        StopMedia();
        _loadedMedia = path;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;

        if (CustomerDisplaySettings.IsVideo(path))
        {
            IdleMediaVideo.Source = new Uri(path);
            IdleMediaVideo.Visibility = Visibility.Visible;
            IdleMediaImage.Visibility = Visibility.Collapsed;
        }
        else if (System.IO.Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            _gif = GifAnimator.Play(IdleMediaImage, path);
        }
        else
        {
            try { IdleMediaImage.Source = ImageHelper.FromBytes(System.IO.File.ReadAllBytes(path)); }
            catch (Exception e) when (e is System.IO.IOException or NotSupportedException or UnauthorizedAccessException) { }
        }
    }

    private void StopMedia()
    {
        _gif?.Dispose();
        _gif = null;
        IdleMediaVideo.Stop();
        IdleMediaVideo.Source = null;
        IdleMediaVideo.Visibility = Visibility.Collapsed;
        IdleMediaImage.Visibility = Visibility.Visible;
        IdleMediaImage.Source = null;
    }

    private bool HasMedia => IdleMediaImage.Source is not null || IdleMediaVideo.Source is not null;

    /// <summary>Places the picture and the text block for the chosen model. A picture model with
    /// no picture chosen shows as Classique; on a portrait screen Côte à côte stacks like Affiche.</summary>
    private void ArrangeIdle()
    {
        var layout = IdleLayouts.All.Any(l => l.Key == _idle.IdleLayout) ? _idle.IdleLayout : IdleLayouts.Classique;
        if (IdleLayouts.UsesMedia(layout) && !HasMedia) layout = IdleLayouts.Classique;
        if (layout == IdleLayouts.CoteACote && _portrait) layout = IdleLayouts.Affiche;

        var cols = IdlePanel.ColumnDefinitions;
        var rows = IdlePanel.RowDefinitions;
        cols[0].Width = new GridLength(1, GridUnitType.Star);
        cols[1].Width = new GridLength(0);
        rows[0].Height = new GridLength(1, GridUnitType.Star);
        rows[1].Height = new GridLength(0);
        IdleMediaHost.Visibility = Visibility.Collapsed;
        IdleTextHost.Background = Brushes.Transparent;
        IdleTextBox.Margin = new Thickness(40);
        IdleTextBox.MaxHeight = double.PositiveInfinity;
        IdleTitle.FontSize = 42;
        IdleMessage.FontSize = 30;
        IdleCompany.FontSize = 68;
        IdleCompany.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "OnBrand");
        foreach (var soft in new[] { IdleTitle, IdleMessage, IdleQrNote })
            soft.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "OnBrandSoft");
        Place(IdleTextHost, row: 0, column: 0, span: 2);
        System.Windows.Controls.Grid.SetRowSpan(IdleTextHost, 1);
        System.Windows.Controls.Grid.SetRowSpan(IdleMediaHost, 1);

        switch (layout)
        {
            case IdleLayouts.PleinEcran:
                // The picture under everything; the text in a dark band at the bottom.
                IdleMediaHost.Visibility = Visibility.Visible;
                Place(IdleMediaHost, row: 0, column: 0, span: 2);
                System.Windows.Controls.Grid.SetRowSpan(IdleMediaHost, 2);
                rows[1].Height = GridLength.Auto;
                Place(IdleTextHost, row: 1, column: 0, span: 2);
                IdleTextHost.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0));
                IdleTextBox.Margin = new Thickness(40, 24, 40, 24);
                IdleTextBox.MaxHeight = 300;
                // The band is dark whatever the brand colour, so its text is always white.
                foreach (var text in new[] { IdleCompany, IdleTitle, IdleMessage, IdleQrNote })
                    text.Foreground = Brushes.White;
                break;

            case IdleLayouts.CoteACote:
                cols[1].Width = new GridLength(1, GridUnitType.Star);
                IdleMediaHost.Visibility = Visibility.Visible;
                Place(IdleMediaHost, row: 0, column: _idle.IdleMediaRight ? 1 : 0, span: 1);
                Place(IdleTextHost, row: 0, column: _idle.IdleMediaRight ? 0 : 1, span: 1);
                break;

            case IdleLayouts.Affiche:
                rows[1].Height = GridLength.Auto;
                IdleMediaHost.Visibility = Visibility.Visible;
                Place(IdleMediaHost, row: 0, column: 0, span: 2);
                Place(IdleTextHost, row: 1, column: 0, span: 2);
                IdleTextHost.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brand");
                IdleTextBox.Margin = new Thickness(40, 24, 40, 24);
                IdleTextBox.MaxHeight = 320;
                break;

            case IdleLayouts.Message:
                // The message is the point: title and message very large, the name smaller.
                IdleCompany.FontSize = 44;
                IdleTitle.FontSize = 96;
                IdleMessage.FontSize = 54;
                break;
        }
    }

    private void ShowThanks(CustomerDisplaySnapshot s)
    {
        ThanksTotal.Text = s.Facture ? $"Facture : {Money.Format(s.Total)}" : $"Total payé : {Money.Format(s.Total)}";

        var change = s.Difference is > 0 ? s.Difference : null;
        ThanksChangeCard.Visibility = change is null ? Visibility.Collapsed : Visibility.Visible;
        ThanksChange.Text = change is { } c ? $"Monnaie : {Money.Format(c)}" : string.Empty;

        ThanksNote.Text = s.Facture ? "À régler en caisse" : "À bientôt !";
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
