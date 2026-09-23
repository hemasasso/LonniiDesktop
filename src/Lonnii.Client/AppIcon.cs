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
    private static BitmapImage? _current;

    /// <summary>The icon matching the current theme. Resolved once and cached: the app
    /// does not react to a theme change while running, only picks correctly at each start.</summary>
    public static BitmapImage Current => _current ??= Load(IsLightTheme() ? "icon-light.ico" : "icon-dark.ico");

    private static BitmapImage Load(string fileName) =>
        new(new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute));

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
