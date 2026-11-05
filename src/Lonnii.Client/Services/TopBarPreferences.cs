using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Lonnii.Client.Services;

/// <summary>The top bar's background pattern. <see cref="Squares"/> is the original grid.</summary>
public static class TopBarPatterns
{
    public const string Squares = "squares";
    public const string Plain = "plain";
    public const string Gradient = "gradient";
    public const string Lines = "lines";
    public const string Dots = "dots";

    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Squares, "Carreaux"), (Plain, "Uni"), (Gradient, "Dégradé"), (Lines, "Lignes"), (Dots, "Points"),
    ];
}

/// <summary>One user's top bar look. Null colour = the theme's own blue.</summary>
public sealed class TopBarPreference
{
    public string? Color { get; set; }
    public string Pattern { get; set; } = TopBarPatterns.Squares;
}

/// <summary>
/// The top bar's colour and pattern, a personal choice: kept per user on this computer (every
/// espace they open), beside ui-state.json. Losing the file only brings back the default bar.
/// </summary>
public static class TopBarPreferences
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lonnii", "top-bar.json");

    private static Dictionary<string, TopBarPreference>? _all;

    public static TopBarPreference For(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return new TopBarPreference();
        _all ??= Load();
        return _all.TryGetValue(userId, out var p) ? p : new TopBarPreference();
    }

    public static void Save(string? userId, TopBarPreference preference)
    {
        if (string.IsNullOrEmpty(userId)) return;
        _all ??= Load();
        _all[userId] = preference;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_all));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, TopBarPreference> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, TopBarPreference>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    // --- Painting -------------------------------------------------------------------------

    /// <summary>The theme's own bar colours (Palette.Light/Dark HeaderBackground).</summary>
    private static Color DefaultBase(bool dark) => dark ? Rgb(0x1F, 0x3F, 0x66) : Rgb(0x4A, 0x90, 0xE2);

    /// <summary>
    /// Paints <paramref name="bar"/> and, for a chosen colour, overrides the pill brushes in
    /// <paramref name="scope"/> (the window's resources, which win over the palette) so pills,
    /// borders and text follow the bar. The default colour removes the overrides again.
    /// </summary>
    public static void Apply(TopBarPreference p, System.Windows.Controls.Border bar, ResourceDictionary scope, bool dark)
    {
        var chosen = TryParse(p.Color);
        // A chosen colour is darkened in dark mode so the bar sits with the dark surfaces.
        var baseColor = chosen is { } c ? (dark ? Mix(c, Colors.Black, 0.5) : c) : DefaultBase(dark);
        bar.Background = Pattern(p.Pattern, baseColor);

        string[] keys = ["HeaderPill", "HeaderPillBorder", "HeaderPillText"];
        if (chosen is not { } col)
        {
            foreach (var k in keys) scope.Remove(k);
            return;
        }

        if (dark)
        {
            scope["HeaderPill"] = Frozen(Mix(col, Colors.Black, 0.25));
            scope["HeaderPillBorder"] = Frozen(Mix(col, Colors.White, 0.2));
            scope["HeaderPillText"] = Frozen(Colors.White);
        }
        else
        {
            scope["HeaderPill"] = Frozen(Colors.White);
            scope["HeaderPillBorder"] = Frozen(Mix(col, Colors.Black, 0.2));
            scope["HeaderPillText"] = Frozen(Mix(col, Colors.Black, 0.45));
        }
    }

    /// <summary>The bar background for a pattern over <paramref name="c"/>.</summary>
    public static Brush Pattern(string? pattern, Color c)
    {
        var accent = Mix(c, Colors.White, 0.08);
        Brush brush = pattern switch
        {
            TopBarPatterns.Plain => new SolidColorBrush(c),
            TopBarPatterns.Gradient => new LinearGradientBrush(
                Mix(c, Colors.White, 0.15), Mix(c, Colors.Black, 0.2), new Point(0, 0), new Point(1, 1)),
            TopBarPatterns.Lines => Tile(c, new GeometryDrawing(new SolidColorBrush(accent), null,
                new RectangleGeometry(new Rect(0, 0, 22, 2))), 22),
            TopBarPatterns.Dots => Tile(c, new GeometryDrawing(new SolidColorBrush(Mix(c, Colors.White, 0.14)), null,
                new EllipseGeometry(new Point(7, 7), 1.6, 1.6)), 14),
            _ => Tile(c, new GeometryDrawing(new SolidColorBrush(accent), null, new GeometryGroup
            {
                Children = { new RectangleGeometry(new Rect(0, 0, 22, 1)), new RectangleGeometry(new Rect(0, 0, 1, 22)) },
            }), 22),
        };
        brush.Freeze();
        return brush;
    }

    private static DrawingBrush Tile(Color c, GeometryDrawing overlay, double size)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(c), null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(overlay);
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, size, size), ViewboxUnits = BrushMappingMode.Absolute,
        };
    }

    public static Color? TryParse(string? hex)
    {
        if (Lonnii.Shared.Contracts.EspaceColors.Normalize(hex) is not { } h) return null;
        return (Color)ColorConverter.ConvertFromString(h);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
