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
            ItemsCard.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);
        Loaded += (_, _) => FitCanvasToScreen();

        // "Automatique" follows the till's own light/dark switch.
        ThemeManager.Changed += OnAppThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnAppThemeChanged;
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        if (_theme == DisplayThemes.Auto) ApplyColors(_theme, _accent);
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
    /// simply shows more item rows. On a portrait screen the summary moves under the basket.</summary>
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

        BodyGrid.ColumnDefinitions[1].Width = portrait ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        System.Windows.Controls.Grid.SetRow(SummaryCard, portrait ? 1 : 0);
        System.Windows.Controls.Grid.SetColumn(SummaryCard, portrait ? 0 : 1);
        System.Windows.Controls.Grid.SetColumnSpan(SummaryCard, portrait ? 2 : 1);
        SummaryCard.Margin = portrait ? new Thickness(0, 24, 0, 0) : new Thickness(28, 0, 0, 0);
    }

    // --- Theme ---

    /// <summary>Applies the look chosen in Paramètres: colours, typeface and text size.</summary>
    public void ApplyStyle(CustomerDisplaySettings s)
    {
        FontFamily = new FontFamily(string.IsNullOrWhiteSpace(s.FontFamily) ? CustomerDisplaySettings.DefaultFont : s.FontFamily);
        _scale = Math.Clamp(s.TextScale, 40, 150) / 100d;
        ApplyColors(s.Theme, s.AccentColor);
        FitCanvasToScreen();
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
        IdleCompany.Text = s.Branding.Company;
        IdleLogo.Source = s.Branding.Logo is { } logo ? ImageHelper.FromBytes(logo) : null;
        IdleLogoChip.Visibility = IdleLogo.Source is null ? Visibility.Collapsed : Visibility.Visible;

        IdleQr.Source = s.Branding.Qr is { } qr ? ImageHelper.FromBytes(qr) : null;
        IdleQrBox.Visibility = IdleQr.Source is null && string.IsNullOrWhiteSpace(s.Branding.QrNote)
            ? Visibility.Collapsed : Visibility.Visible;
        IdleQrCard.Visibility = IdleQr.Source is null ? Visibility.Collapsed : Visibility.Visible;
        IdleQrNote.Text = s.Branding.QrNote ?? string.Empty;
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
