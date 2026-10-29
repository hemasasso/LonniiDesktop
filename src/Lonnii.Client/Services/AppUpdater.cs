using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Services;

/// <summary>
/// Installing a new version of the app. The shop's main computer holds it (see the API's
/// UpdateDownloader); this computer asks it, then either:
/// <list type="bullet">
/// <item>is that main computer: its Lonnii service installs it silently, no Windows prompt;</item>
/// <item>is a till: downloads it, checks our signature itself - the main computer is just another
/// machine on the network - and runs it, Windows asking for an administrator's permission.</item>
/// </list>
/// Either way the app closes and starts again by itself once the installer has finished.
/// </summary>
public static class AppUpdater
{
    /// <summary>This build's version, as the installer numbers it.</summary>
    public static string CurrentVersion { get; } =
        typeof(AppUpdater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>True when the server this app talks to runs on this very computer.</summary>
    public static bool ServerIsThisComputer(string? baseAddress)
    {
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback) return true;
        if (!IPAddress.TryParse(uri.Host, out var address)) return false;

        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName()).Contains(address);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the installation. Returns null when it is under way and the app must now close, or
    /// the message to show when it could not start.
    /// </summary>
    public static async Task<string?> InstallAsync(LonniiApiClient api, UpdateManifest manifest)
    {
        if (ServerIsThisComputer(api.BaseAddress))
        {
            try
            {
                await api.InstallServerUpdateAsync();
                StartAgainAfterInstall();
                return null;
            }
            catch (ApiException e) when (e.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                // The API is not running as the installed service (started by hand, say): it cannot
                // install without a prompt, so do it the till's way.
            }
        }

        return await InstallWithPromptAsync(api, manifest);
    }

    private static async Task<string?> InstallWithPromptAsync(LonniiApiClient api, UpdateManifest manifest)
    {
        if (!manifest.IsGenuine())
            return "Cette mise à jour n'est pas signée par Lonnii. Elle n'a pas été installée.";

        var directory = Path.Combine(Path.GetTempPath(), "Lonnii", "update");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, manifest.FileName);

        try
        {
            await api.DownloadUpdateAsync(manifest.FileName, path);
        }
        catch (ApiException e)
        {
            return "Téléchargement impossible : " + e.Message;
        }

        if (new FileInfo(path).Length != manifest.Size
            || !string.Equals(await UpdateManifest.HashFileAsync(path), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            return "Le fichier reçu ne correspond pas à la version signée par Lonnii. Il n'a pas été installé.";
        }

        try
        {
            // /SILENT: a progress bar but no questions. The installer keeps this computer's
            // choice from its first install (till or main computer).
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "Installation annulée : Windows n'a pas reçu l'autorisation.";
        }
        catch (Win32Exception e)
        {
            return "Impossible de lancer l'installation : " + e.Message;
        }

        StartAgainAfterInstall();
        return null;
    }

    /// <summary>
    /// A small hidden helper that waits for the installer to appear and finish, then opens the new
    /// version - the installer itself does not, as it runs without questions.
    /// </summary>
    private static void StartAgainAfterInstall()
    {
        if (Environment.ProcessPath is not { } exe) return;

        var script =
            "$deadline=(Get-Date).AddMinutes(2);" +
            "while(-not (Get-Process -Name 'LonniiBusiness-Setup*' -EA 0) -and (Get-Date) -lt $deadline){Start-Sleep 1};" +
            "$deadline=(Get-Date).AddMinutes(30);" +
            "while((Get-Process -Name 'LonniiBusiness-Setup*' -EA 0) -and (Get-Date) -lt $deadline){Start-Sleep 2};" +
            $"Start-Sleep 3; Start-Process -FilePath '{exe.Replace("'", "''")}'";

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-WindowStyle", "Hidden", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
