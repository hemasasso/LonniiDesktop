using Microsoft.Win32;

namespace Lonnii.Client.Services;

/// <summary>
/// What kind of machine this client runs on, chosen in the installer: a classic computer
/// (mouse + keyboard) or a touch POS terminal with no keyboard, where the app must offer
/// on-screen typing and finger-sized buttons. Machine-wide, in
/// HKLM\Software\Lonnii\Business\DeviceMode ("pos" or "desktop").
///
/// For testing without reinstalling (e.g. a tablet through Spacedesk), start the client with
/// <c>--pos</c> or <c>--desktop</c>, which wins over the registry for that run.
/// </summary>
public static class DeviceProfile
{
    public static bool IsPos { get; private set; }

    /// <summary>Where the answer came from, for the log.</summary>
    public static string Source { get; private set; } = "défaut";

    /// <param name="chosen">"pos" / "desktop" picked in Configuration on this computer (wins over
    /// the installer, which needs admin rights to change); null to keep the installer's choice.</param>
    public static void Initialize(string[] args, string? chosen = null)
    {
        if (args.Any(a => string.Equals(a, "--pos", StringComparison.OrdinalIgnoreCase)))
        {
            IsPos = true;
            Source = "--pos";
            return;
        }

        if (args.Any(a => string.Equals(a, "--desktop", StringComparison.OrdinalIgnoreCase)))
        {
            IsPos = false;
            Source = "--desktop";
            return;
        }

        if (chosen is "pos" or "desktop")
        {
            IsPos = chosen == "pos";
            Source = "configuration";
            return;
        }

        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"Software\Lonnii\Business");
            if (key?.GetValue("DeviceMode") is string mode)
            {
                IsPos = string.Equals(mode.Trim(), "pos", StringComparison.OrdinalIgnoreCase);
                Source = "installation";
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            // Unreadable: behave as a classic computer.
        }
    }
}
