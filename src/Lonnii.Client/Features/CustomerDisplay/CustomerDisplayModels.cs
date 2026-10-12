using System.IO;
using System.Text.Json;

namespace Lonnii.Client.Features.CustomerDisplay;

/// <summary>How a text-only display (pole display, character LCD) understands our frames.</summary>
public static class DisplayProtocols
{
    /// <summary>Most 2x20 pole displays: <c>ESC Q A</c> / <c>ESC Q B</c> write the upper and lower line.</summary>
    public const string Cd5220 = "cd5220";

    /// <summary>Epson-style ESC/POS pole displays: <c>US $</c> moves the cursor, then the text.</summary>
    public const string EscPos = "escpos";

    /// <summary>Anything home-made: a form feed (0x0C) starts a frame, rows follow separated by
    /// line feeds. Trivial to parse on an Arduino, STM32 or ESP32 - see docs/customer-display.md.</summary>
    public const string Text = "text";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Cd5220, "CD5220 (afficheur pôle standard)"),
        (EscPos, "ESC/POS (Epson)"),
        (Text, "Texte simple (Arduino, STM32, ESP32)"),
    ];
}

/// <summary>
/// The five models of the second-screen view a shop chooses from. Each is an arrangement of the
/// same screen - same basket, same summary, same welcome and thank-you pages - so the colours,
/// font and text size chosen alongside apply to every one of them.
/// </summary>
public static class DisplayLayouts
{
    /// <summary>Header bar, basket on the left, summary card on the right. The default.</summary>
    public const string Classique = "classique";

    /// <summary>The total first: a wide summary with a very large amount, the basket beside it.</summary>
    public const string GrandTotal = "grand_total";

    /// <summary>The basket across the whole width, the summary as a band underneath.</summary>
    public const string Bandeau = "bandeau";

    /// <summary>No coloured bar: the shop's name in brand colour over a tinted background, rounder cards.</summary>
    public const string Elegant = "elegant";

    /// <summary>Flat and plain: no header, no card shadows or rounding, thin separators.</summary>
    public const string Minimal = "minimal";

    public static readonly IReadOnlyList<(string Key, string Label, string Description)> All =
    [
        (Classique, "Classique", "Bandeau de couleur, articles à gauche, total à droite."),
        (GrandTotal, "Grand total", "Le montant à payer en très grand, les articles à côté."),
        (Bandeau, "Liste et bandeau", "Articles sur toute la largeur, total dans un bandeau en bas."),
        (Elegant, "Élégant", "Nom de la boutique en couleur, fond teinté, cartes arrondies."),
        (Minimal, "Minimaliste", "Sobre et plat, sans bandeau : idéal pour pharmacie ou petit écran."),
    ];
}

/// <summary>
/// The five models of the welcome screen shown while nothing is in the basket. The three that
/// feature a picture fall back to <see cref="Classique"/> until one is chosen.
/// </summary>
public static class IdleLayouts
{
    /// <summary>Logo, name, title and QR code centred on the brand colour. The default.</summary>
    public const string Classique = "classique";

    /// <summary>The picture or video fills the screen; name and title in a band at the bottom.</summary>
    public const string PleinEcran = "plein_ecran";

    /// <summary>The picture on the left half, the shop's details on the right.</summary>
    public const string CoteACote = "cote_a_cote";

    /// <summary>The picture across the top, the details in a brand-coloured band underneath.</summary>
    public const string Affiche = "affiche";

    /// <summary>No picture: the title and message very large, for a promotion or an announcement.</summary>
    public const string Message = "message";

    public static readonly IReadOnlyList<(string Key, string Label, string Description)> All =
    [
        (Classique, "Classique", "Logo, nom de la boutique, titre et QR code au centre."),
        (PleinEcran, "Plein écran", "Votre photo, GIF ou vidéo sur tout l'écran, le nom en bas."),
        (CoteACote, "Côte à côte", "Votre photo à gauche, le logo, le texte et le QR code à droite."),
        (Affiche, "Affiche", "Votre photo en haut, le texte dans un bandeau de couleur en bas."),
        (Message, "Grand message", "Votre titre et votre message en très grand : promotion, annonce."),
    ];

    public static bool UsesMedia(string layout) => layout is PleinEcran or CoteACote or Affiche;
}

/// <summary>The colour of the "3 articles" pill in the header bar.</summary>
public static class DisplayPillStyles
{
    /// <summary>A see-through tint of the bar, text in the bar's own text colour. The default.</summary>
    public const string Auto = "auto";

    /// <summary>A white pill with dark text: readable on any brand colour.</summary>
    public const string WhiteBackground = "white_bg";

    /// <summary>The usual tint, but the text always white.</summary>
    public const string WhiteText = "white_text";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Auto, "Automatique (selon la couleur)"),
        (WhiteBackground, "Fond blanc, texte foncé"),
        (WhiteText, "Texte blanc"),
    ];
}

/// <summary>The colours of the second-screen view.</summary>
public static class DisplayThemes
{
    /// <summary>White cards on light grey, like a big retailer's checkout screen. The default.</summary>
    public const string Light = "light";

    public const string Dark = "dark";

    /// <summary>Follows the till's own light/dark switch.</summary>
    public const string Auto = "auto";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Light, "Clair"),
        (Dark, "Sombre"),
        (Auto, "Automatique (comme l'application)"),
    ];

    /// <summary>Brand colours offered next to the free hex field.</summary>
    public static readonly IReadOnlyList<(string Hex, string Label)> Accents =
    [
        ("#0071DC", "Bleu"),
        ("#16A34A", "Vert"),
        ("#DC2626", "Rouge"),
        ("#EA580C", "Orange"),
        ("#7C3AED", "Violet"),
        ("#0F766E", "Turquoise"),
        ("#FACC15", "Jaune"),
        ("#111827", "Noir"),
    ];
}

/// <summary>A text display and its size. Shared by the serial and network targets.</summary>
public class CharacterDisplaySettings
{
    public bool Enabled { get; set; }
    public string Protocol { get; set; } = DisplayProtocols.Cd5220;
    public int Columns { get; set; } = 20;
    public int Rows { get; set; } = 2;
}

public sealed class SerialDisplaySettings : CharacterDisplaySettings
{
    /// <summary>E.g. <c>COM4</c>. A USB pole display or a USB-serial microcontroller shows up as one.</summary>
    public string? Port { get; set; }

    public int BaudRate { get; set; } = 9600;
}

public sealed class NetworkDisplaySettings : CharacterDisplaySettings
{
    /// <summary>The address of an ESP32 (or anything listening on a TCP port) driving the LCD.</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 9100;

    public NetworkDisplaySettings() => Protocol = DisplayProtocols.Text;
}

/// <summary>
/// What the customer-facing display does on THIS machine. Deliberately not a shop setting:
/// the display is plugged into one till laptop, so storing it in the shared database would
/// make every other till try to drive a screen it does not have.
/// </summary>
public sealed class CustomerDisplaySettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lonnii", "customer-display.json");

    /// <summary>Master switch: off, nothing is shown anywhere.</summary>
    public bool Enabled { get; set; } = true;

    public const string DefaultAccent = "#0071DC";

    /// <summary>One of <see cref="DisplayLayouts"/>.</summary>
    public string Layout { get; set; } = DisplayLayouts.Classique;

    /// <summary>One of <see cref="DisplayThemes"/>.</summary>
    public string Theme { get; set; } = DisplayThemes.Light;

    /// <summary>One of <see cref="DisplayPillStyles"/>.</summary>
    public string PillStyle { get; set; } = DisplayPillStyles.Auto;

    public const string DefaultFont = "Segoe UI";

    /// <summary>The typeface of every text on the display.</summary>
    public string FontFamily { get; set; } = DefaultFont;

    /// <summary>Text size in percent: 100 is the layout's natural size. Scales the whole view, so
    /// a smaller value also fits more item rows on the screen.</summary>
    public int TextScale { get; set; } = 80;

    /// <summary>The brand colour of the header bar and welcome screen, as <c>#RRGGBB</c>.</summary>
    public string AccentColor { get; set; } = DefaultAccent;

    /// <summary>Show the full-screen customer view on a second monitor (another laptop screen,
    /// a small HDMI or USB-C panel).</summary>
    public bool ScreenEnabled { get; set; } = true;

    /// <summary>Use whichever monitor is not the main one, as soon as it is plugged in.</summary>
    public bool AutoDetectScreen { get; set; } = true;

    /// <summary>The monitor to use when <see cref="AutoDetectScreen"/> is off, e.g. <c>\\.\DISPLAY2</c>.</summary>
    public string? ScreenDevice { get; set; }

    // --- Welcome screen (nothing in the basket) ---

    /// <summary>One of <see cref="IdleLayouts"/>.</summary>
    public string IdleLayout { get; set; } = IdleLayouts.Classique;

    public bool IdleShowLogo { get; set; } = true;
    public bool IdleShowQr { get; set; } = true;
    public bool IdleShowCompany { get; set; } = true;

    /// <summary>Côte à côte: the picture on the right half instead of the left.</summary>
    public bool IdleMediaRight { get; set; }

    /// <summary>The line under the shop's name. Empty hides it.</summary>
    public string IdleTitle { get; set; } = "Bienvenue";

    /// <summary>Free text under the title: opening hours, a promotion, a slogan.</summary>
    public string? IdleMessage { get; set; }

    /// <summary>A picture, animated GIF or video, copied into <see cref="MediaFolder"/> so it
    /// survives the original being moved or deleted.</summary>
    public string? IdleMediaPath { get; set; }

    public static readonly string MediaFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lonnii", "customer-display-media");

    public static readonly string[] VideoExtensions = [".mp4", ".wmv", ".avi", ".mov", ".m4v"];

    public static bool IsVideo(string? path) =>
        path is not null && VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Copies a chosen file into <see cref="MediaFolder"/> under a fresh name and returns
    /// the copy's path.</summary>
    public static string ImportMedia(string source)
    {
        Directory.CreateDirectory(MediaFolder);
        var target = Path.Combine(MediaFolder, $"accueil-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(source).ToLowerInvariant()}");
        File.Copy(source, target, overwrite: true);
        return target;
    }

    public SerialDisplaySettings Serial { get; set; } = new();
    public NetworkDisplaySettings Network { get; set; } = new();

    public static CustomerDisplaySettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CustomerDisplaySettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable settings fall back to the defaults rather than blocking the till.
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public CustomerDisplaySettings Clone() =>
        JsonSerializer.Deserialize<CustomerDisplaySettings>(JsonSerializer.Serialize(this))!;
}

/// <summary>Who the shop is, shown on the idle screen.</summary>
public sealed record DisplayBranding(string Company, byte[]? Logo = null, byte[]? Qr = null, string? QrNote = null);

public enum DisplayMode
{
    /// <summary>Nothing in the cart: welcome screen.</summary>
    Idle,

    /// <summary>A sale is being rung up.</summary>
    Selling,

    /// <summary>A non-cash payment was just confirmed by the provider, before the sale is recorded.</summary>
    PaymentConfirmed,

    /// <summary>The sale was just recorded.</summary>
    Thanks,
}

/// <param name="Total">What the line costs after its own discount.</param>
/// <param name="FullTotal">What it would cost with no discount; above <paramref name="Total"/> when one applies.</param>
public sealed record DisplayLine(
    string Id, string Name, int Quantity, decimal UnitPrice, decimal Total, string? Unite = null, decimal? FullTotal = null)
{
    public bool IsDiscounted => FullTotal is { } full && full > Total;
}

/// <summary>Everything a display needs, in numbers: each target formats it for itself.</summary>
/// <param name="Discount">Item discounts plus the global one, as a positive amount.</param>
/// <param name="Recu">Cash handed over so far, when the cashier typed it.</param>
/// <param name="Facture">For <see cref="DisplayMode.Thanks"/>: the sale was left to settle at the till.</param>
public sealed record CustomerDisplaySnapshot(
    DisplayMode Mode,
    DisplayBranding Branding,
    IReadOnlyList<DisplayLine> Lines,
    decimal Subtotal,
    decimal Discount,
    decimal Tva,
    decimal Total,
    decimal? Recu = null,
    string? FocusLineId = null,
    bool Facture = false)
{
    /// <summary>Change to give back when positive, what is still missing when negative.</summary>
    public decimal? Difference => Recu is { } recu ? recu - Total : null;
}

/// <summary>A monitor Windows reports, in physical pixels.</summary>
public sealed record MonitorInfo(string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public string Label => $"{DeviceName.TrimStart('\\', '.')} — {Width}×{Height}{(IsPrimary ? " (principal)" : string.Empty)}";
}
