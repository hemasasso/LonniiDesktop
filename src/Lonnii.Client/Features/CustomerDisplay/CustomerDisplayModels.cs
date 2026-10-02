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

/// <summary>The look of the second-screen view.</summary>
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

    /// <summary>One of <see cref="DisplayThemes"/>.</summary>
    public string Theme { get; set; } = DisplayThemes.Light;

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
