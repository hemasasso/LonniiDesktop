using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Lonnii.Client.Features.Stock;

/// <summary>
/// Vector drawings for the Emballages tab: a crate (caisse, casier, cageot), a bottle, a carton
/// (carton, pack, paquet), a barrel (fût, bidon), drawn on a 64 x 56 canvas and scaled to any size.
/// Fixed colours on purpose - they read on both the light and the dark theme.
/// </summary>
public static class EmballageArt
{
    public sealed record Palette(Color Body, Color Stroke, Color Bottle, Color Cap);

    public static readonly Palette Amber = new(Rgb("#F59E0B"), Rgb("#B45309"), Rgb("#15803D"), Rgb("#FDE68A"));
    public static readonly Palette Slate = new(Rgb("#94A3B8"), Rgb("#475569"), Rgb("#64748B"), Rgb("#E2E8F0"));
    public static readonly Palette Blue = new(Rgb("#60A5FA"), Rgb("#1D4ED8"), Rgb("#2563EB"), Rgb("#BFDBFE"));
    public static readonly Palette Red = new(Rgb("#F87171"), Rgb("#B91C1C"), Rgb("#DC2626"), Rgb("#FECACA"));
    public static readonly Palette Emerald = new(Rgb("#34D399"), Rgb("#047857"), Rgb("#065F46"), Rgb("#A7F3D0"));

    /// <summary>The one colour of a full crate: a muted steel blue, sober on both themes.</summary>
    public static readonly Palette Steel = new(Rgb("#7896B6"), Rgb("#3B5573"), Rgb("#56708D"), Rgb("#DCE4EC"));

    // The user's code: full = red, empty = grey, with clients = blue, total = green.
    public static readonly Color PleinsColor = Rgb("#DC2626");
    public static readonly Color VidesColor = Rgb("#7C8BA1");
    public static readonly Color ClientsColor = Rgb("#2563EB");
    public static readonly Color TotalColor = Rgb("#16A34A");
    /// <summary>Casse / perte: dark slate, so red keeps meaning "pleins".</summary>
    public static readonly Color CasseColor = Rgb("#475569");

    /// <summary>Every product is drawn in the same colour (kept as a method so a per-product
    /// colour can come back without touching the callers).</summary>
    public static Palette ForKey(string? key) => Steel;

    public enum Kind { Crate, Bottle, Carton, Barrel }

    /// <summary>What to draw for a product's display unit ("caisse" -> crate, "bouteille" -> bottle...).</summary>
    public static Kind KindFor(string? unit)
    {
        var u = (unit ?? string.Empty).ToLower(CultureInfo.CurrentCulture);
        if (u.Contains("bouteille") || u.Contains("btl") || u.Contains("flacon")) return Kind.Bottle;
        if (u.Contains("carton") || u.Contains("pack") || u.Contains("paquet") || u.Contains("boite") || u.Contains("boîte")) return Kind.Carton;
        if (u.Contains("fût") || u.Contains("fut") || u.Contains("bidon") || u.Contains("baril") || u.Contains("tonneau")) return Kind.Barrel;
        return Kind.Crate; // caisse, casier, cageot - and the usual returnable packaging
    }

    public static FrameworkElement For(string? unit, double size, Palette palette, bool full = true) =>
        Wrap(KindFor(unit) switch
        {
            Kind.Bottle => Bottle(palette),
            Kind.Carton => Carton(palette),
            Kind.Barrel => Barrel(palette),
            _ => Crate(palette, full),
        }, size);

    public static FrameworkElement CrateArt(double size, Palette palette, bool full) => Wrap(Crate(palette, full), size);

    /// <summary>A crate handed to a client: the crate and a person beside it.</summary>
    public static FrameworkElement ClientArt(double size)
    {
        var c = new Canvas { Width = 64, Height = 56 };
        c.Children.Add(Place(Crate(Blue, full: true), 0, 14, 0.62));
        var person = Brush(ClientsColor);
        var head = new Ellipse { Width = 13, Height = 13, Fill = person };
        Canvas.SetLeft(head, 44); Canvas.SetTop(head, 12);
        c.Children.Add(head);
        c.Children.Add(new Path
        {
            Fill = person,
            Data = Geometry.Parse("M 38,52 C 38,36 44,28 50.5,28 C 57,28 63,36 63,52 Z"),
        });
        // the arrow from the crate to the client
        c.Children.Add(new Path
        {
            Stroke = Brush(Blue.Stroke), StrokeThickness = 2.2, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round,
            Data = Geometry.Parse("M 30,8 Q 38,2 44,8 M 41,4 L 44,8 L 40,10"),
        });
        return Wrap(c, size);
    }

    /// <summary>Everything the shop owns: crates stacked two and one.</summary>
    public static FrameworkElement StackArt(double size, Palette? palette = null)
    {
        var p = palette ?? Emerald;
        var c = new Canvas { Width = 64, Height = 56 };
        c.Children.Add(Place(Crate(p, full: false), 0, 27, 0.5));
        c.Children.Add(Place(Crate(p, full: false), 32, 27, 0.5));
        c.Children.Add(Place(Crate(p, full: true), 16, 1, 0.5));
        return Wrap(c, size);
    }

    /// <summary>Money paid back: two banknotes, the front one marked FCFA.</summary>
    public static FrameworkElement MoneyArt(double size)
    {
        var c = new Canvas { Width = 64, Height = 56 };
        var stroke = Brush(Rgb("#047857"));
        // The note behind, tilted.
        var back = Rect(10, 8, 48, 28, 3, Brush(Rgb("#6EE7B7")), stroke, 1.2);
        back.RenderTransform = new RotateTransform(-10, 24, 14);
        c.Children.Add(back);
        // The front note.
        c.Children.Add(Rect(4, 18, 54, 32, 3.5, Brush(Rgb("#34D399")), stroke, 1.5));
        c.Children.Add(Rect(8, 22, 46, 24, 2, Brushes.Transparent, Brush(Rgb("#ECFDF5")), 1));
        var seal = new Ellipse { Width = 14, Height = 14, Fill = Brush(Rgb("#ECFDF5")), Stroke = stroke, StrokeThickness = 1 };
        Canvas.SetLeft(seal, 11); Canvas.SetTop(seal, 27);
        c.Children.Add(seal);
        var text = new TextBlock { Text = "FCFA", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brush(Rgb("#065F46")) };
        Canvas.SetLeft(text, 28); Canvas.SetTop(text, 28);
        c.Children.Add(text);
        return Wrap(c, size);
    }

    private static Canvas Crate(Palette p, bool full)
    {
        var c = new Canvas { Width = 64, Height = 56 };
        var stroke = Brush(p.Stroke);
        if (full)
        {
            // Bottle necks standing out of the crate.
            for (var i = 0; i < 4; i++)
            {
                var x = 9 + i * 12.5;
                c.Children.Add(Rect(x, 12, 9, 16, 2.5, Brush(p.Bottle), null));
                c.Children.Add(Rect(x + 2.75, 4, 3.5, 9, 1, Brush(p.Bottle), null));
                c.Children.Add(Rect(x + 2.25, 2, 4.5, 3, 1, Brush(p.Cap), null));
                c.Children.Add(Rect(x + 1.5, 15, 1.6, 9, 0.8, new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null));
            }
        }
        else
        {
            // Empty: the crate's back wall and the dividers inside show above the rim.
            c.Children.Add(Rect(6, 11, 52, 12, 2, Brush(Darken(p.Body, 0.7)), stroke, 1.2));
            for (var i = 1; i < 4; i++)
                c.Children.Add(Rect(5 + i * 13, 11, 2.2, 12, 0.8, Brush(Lighten(p.Body, 0.2)), null));
            c.Children.Add(new Line { X1 = 7, X2 = 57, Y1 = 16.5, Y2 = 16.5, Stroke = Brush(Lighten(p.Body, 0.2)), StrokeThickness = 1.6 });
        }
        var body = new LinearGradientBrush(Lighten(p.Body, 0.3), p.Body, 90);
        body.Freeze();
        c.Children.Add(Rect(4, 22, 56, 31, 3, body, stroke, 1.5));
        c.Children.Add(Rect(2, 19, 60, 6, 2, Brush(Darken(p.Body, 0.85)), stroke, 1.5));
        c.Children.Add(Rect(4.75, 25, 4.5, 27.25, 1, Brush(Darken(p.Body, 0.88)), null));
        c.Children.Add(Rect(54.75, 25, 4.5, 27.25, 1, Brush(Darken(p.Body, 0.88)), null));
        foreach (var y in new[] { 36.5, 45.0 })
            c.Children.Add(new Line { X1 = 10, X2 = 54, Y1 = y, Y2 = y, Stroke = stroke, StrokeThickness = 1, Opacity = 0.45 });
        c.Children.Add(Rect(24, 28, 16, 5.5, 2.75, Brush(p.Stroke), null, opacity: 0.6));
        // Glass shine along the top of the crate.
        c.Children.Add(Rect(10, 24.5, 12, 2.2, 1.1, new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), null));
        c.Children.Add(Rect(42, 24.5, 12, 2.2, 1.1, new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), null));
        return c;
    }

    private static Canvas Bottle(Palette p)
    {
        var c = new Canvas { Width = 64, Height = 56 };
        c.Children.Add(new Path
        {
            Fill = Brush(p.Bottle), Stroke = Brush(Darken(p.Bottle, 0.7)), StrokeThickness = 1.2,
            Data = Geometry.Parse("M 28,6 H 36 V 16 C 36,19 42,21 42,27 V 51 C 42,53 41,54 39,54 H 25 C 23,54 22,53 22,51 V 27 C 22,21 28,19 28,16 Z"),
        });
        c.Children.Add(Rect(27, 2, 10, 5, 1.5, Brush(p.Cap), null));
        c.Children.Add(Rect(22, 32, 20, 12, 1, Brush(p.Body), null));
        c.Children.Add(Rect(25, 24, 2.2, 26, 1, new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null));
        return c;
    }

    private static Canvas Carton(Palette p)
    {
        var c = new Canvas { Width = 64, Height = 56 };
        var stroke = Brush(p.Stroke);
        c.Children.Add(Poly("8,22 48,22 48,53 8,53", Brush(p.Body), stroke));
        c.Children.Add(Poly("48,22 58,13 58,44 48,53", Brush(Darken(p.Body, 0.8)), stroke));
        c.Children.Add(Poly("8,22 18,13 58,13 48,22", Brush(Lighten(p.Body, 0.25)), stroke));
        c.Children.Add(Poly("25,22 35,13 41,13 31,22", Brush(p.Cap), null));
        c.Children.Add(Rect(25, 22, 6, 12, 0, Brush(p.Cap), null));
        return c;
    }

    private static Canvas Barrel(Palette p)
    {
        var c = new Canvas { Width = 64, Height = 56 };
        var stroke = Brush(p.Stroke);
        c.Children.Add(new Path
        {
            Fill = Brush(p.Body), Stroke = stroke, StrokeThickness = 1.5,
            Data = Geometry.Parse("M 18,6 H 46 C 52,16 52,40 46,52 H 18 C 12,40 12,16 18,6 Z"),
        });
        foreach (var y in new[] { 13.0, 28.0, 43.0 })
            c.Children.Add(Rect(13, y, 38, 3.5, 1.5, Brush(p.Stroke), null, opacity: 0.75));
        c.Children.Add(Rect(22, 8, 3, 42, 1.5, new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null));
        return c;
    }

    private static Canvas Place(Canvas art, double x, double y, double scale)
    {
        art.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(scale, scale), new TranslateTransform(x, y) },
        };
        return art;
    }

    private static Viewbox Wrap(UIElement art, double size) =>
        new() { Width = size, Height = size * 56 / 64, Child = art, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };

    private static Rectangle Rect(double x, double y, double w, double h, double r, Brush fill, Brush? stroke,
        double thickness = 1, double opacity = 1)
    {
        var rect = new Rectangle
        {
            Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = fill, Opacity = opacity,
            Stroke = stroke, StrokeThickness = stroke is null ? 0 : thickness,
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        return rect;
    }

    private static Polygon Poly(string points, Brush fill, Brush? stroke) => new()
    {
        Points = PointCollection.Parse(points), Fill = fill, Stroke = stroke, StrokeThickness = stroke is null ? 0 : 1.5,
        StrokeLineJoin = PenLineJoin.Round,
    };

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Color Rgb(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Darken(Color c, double f) =>
        Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private static Color Lighten(Color c, double f) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));
}
