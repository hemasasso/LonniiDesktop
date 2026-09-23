using System.Windows;

namespace Lonnii.Client;

/// <summary>
/// Switches the app between <c>Theme/Palette.Light.xaml</c> and <c>Theme/Palette.Dark.xaml</c>
/// at runtime. Every brush a style or a piece of XAML pulls from the palette does so via
/// <c>DynamicResource</c>, not <c>StaticResource</c>, so replacing the dictionary at index 0
/// of <see cref="Application.Resources"/>'s merged dictionaries re-themes every window and
/// control already open, not just ones created afterwards - see App.xaml and
/// Theme/Palette.Light.xaml for the other half of how this works.
/// </summary>
public static class ThemeManager
{
    private const int PaletteIndex = 0;

    public static bool IsDark { get; private set; }

    public static event EventHandler? Changed;

    public static void Toggle() => Apply(!IsDark);

    public static void Apply(bool dark)
    {
        if (dark == IsDark) return;
        IsDark = dark;

        var uri = new Uri($"Theme/Palette.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        Application.Current.Resources.MergedDictionaries[PaletteIndex] = new ResourceDictionary { Source = uri };

        Changed?.Invoke(null, EventArgs.Empty);
    }
}
