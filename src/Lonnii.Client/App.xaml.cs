using System.IO;
using System.Text.Json;
using System.Windows;
using Lonnii.Client.Services;
using Lonnii.Client.Views;

namespace Lonnii.Client;

/// <summary>
/// Application entry point. Shows the sign-in window first; the main shell opens only
/// once a user has signed in and chosen a group, so no window can render without a
/// resolved privilege set behind it.
/// </summary>
public partial class App : Application
{
    /// <summary>Shared for the lifetime of the process. The app is single-user per machine.</summary>
    public static AppSession Session { get; } = new(new LonniiApiClient());

    /// <summary>Remembered host address and last user, so a till does not retype them daily.</summary>
    public static ClientSettings Settings { get; private set; } = ClientSettings.Load();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A crash dialog is friendlier than a silent disappearance on a shop counter,
        // and the log gives something to read afterwards when nobody saw the dialog.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            MessageBox.Show(
                $"Une erreur inattendue s'est produite.\n\n{args.Exception.Message}",
                "Lonnii", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Catches what the dispatcher handler cannot, including failures during start-up.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };

        // The sign-in window closes before the shell opens. Under the default
        // OnLastWindowClose the app would shut down in that gap, so stay alive
        // explicitly until the shell is up.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var login = new LoginWindow();
        if (login.ShowDialog() != true)
        {
            Shutdown();
            return;
        }

        try
        {
            MainWindow = new MainWindow();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            // The shell failing to build is the one crash the user cannot work around,
            // so say so plainly rather than vanishing.
            LogCrash("Startup", ex);
            MessageBox.Show(
                $"Impossible d'ouvrir l'espace de travail.\n\n{ex.Message}\n\n" +
                $"Détails enregistrés dans :\n{CrashLogPath}",
                "Lonnii", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>Where crash details are written, beside the client's own settings.</summary>
    public static string CrashLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lonnii", "client-errors.log");

    private static void LogCrash(string source, Exception? exception)
    {
        if (exception is null) return;
        Log($"{source}: {exception}");
    }

    /// <summary>
    /// Appends a line to the client log. Deliberately plain: on a shop counter the useful
    /// question is "what was the app doing when it stopped", and a file that can be read
    /// in Notepad answers it without any tooling.
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never let logging cause a failure of its own.
        }
    }
}

/// <summary>
/// The few things worth remembering between runs. Stored per Windows user in AppData,
/// never including a password.
/// </summary>
public class ClientSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lonnii", "client-settings.json");

    /// <summary>The host to connect to. Defaults to this machine, which is right on the host laptop.</summary>
    public string HostAddress { get; set; } = "localhost:5280";

    /// <summary>The identifier last used to sign in, pre-filled on the next start.</summary>
    public string? LastIdentifier { get; set; }

    /// <summary>The group last worked in, so the picker can preselect it.</summary>
    public string? LastGroupId { get; set; }

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<ClientSettings>(json) ?? new ClientSettings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file should never stop the app starting.
        }

        return new ClientSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Remembering the host is a convenience, not a requirement.
        }
    }
}
