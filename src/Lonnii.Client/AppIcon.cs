using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Lonnii.Client;

/// <summary>
/// Picks the black-ink or white-ink Lonnii icon to match Windows' current app theme, so the
/// window icon (title bar, Alt-Tab, taskbar button) stays visible whichever mode the user
/// is in. The .exe's own baked-in icon (<c>ApplicationIcon</c> in the csproj) cannot switch
/// like this - it is fixed at build time - so this only affects <see cref="Window.Icon"/>.
/// </summary>
public static class AppIcon
{
    private static ImageSource? _current;

    /// <summary>The icon matching the current theme. Resolved once and cached: the app
    /// does not react to a theme change while running, only picks correctly at each start.</summary>
    public static ImageSource Current => _current ??= Load(IsLightTheme() ? "icon-light.ico" : "icon-dark.ico");

    /// <summary>Hands WPF the .ico's largest frame. A bare BitmapImage takes the first frame,
    /// which can be the 16px one, and the taskbar then shows a small blurry icon.</summary>
    private static ImageSource Load(string fileName)
    {
        var decoder = BitmapDecoder.Create(
            new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// Reads the same registry value Windows itself uses to decide whether apps get a
    /// light or dark chrome. Defaults to light on any failure - the more common theme and
    /// the one the .exe's own baked-in icon already matches.
    /// </summary>
    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is not int intValue || intValue != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or System.IO.IOException)
        {
            return true;
        }
    }
}
