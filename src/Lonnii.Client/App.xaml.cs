using System.IO;
using System.Text.Json;
using System.Windows;
using Lonnii.Client.Features.Auth;
using Lonnii.Client.Services;

namespace Lonnii.Client;

/// <summary>
/// Application entry point. Shows the sign-in window first; the main shell opens only
/// once a user has signed in and chosen a group, so no window can render without a
/// resolved privilege set behind it.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Every DatePicker shows and parses dates with the machine's short date pattern, so a
    /// Windows set to English (United States) showed 09/28/2026 - read as the 9th of the 28th
    /// month by anyone used to day first. Only the date patterns are overridden: numbers and
    /// everything else keep the machine's culture, so no parsing or number formatting shifts.
    /// </summary>
    private static void UseDayMonthYearDates()
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.CurrentCulture.Clone();
        culture.DateTimeFormat.ShortDatePattern = "dd/MM/yyyy";
        culture.DateTimeFormat.DateSeparator = "/";
        culture.DateTimeFormat.FirstDayOfWeek = DayOfWeek.Monday;

        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    /// <summary>Shared for the lifetime of the process. The app is single-user per machine.</summary>
    public static AppSession Session { get; } = new(new LonniiApiClient());

    /// <summary>Remembered host address and last user, so a till does not retype them daily.</summary>
    public static ClientSettings Settings { get; private set; } = ClientSettings.Load();

    protected override async void OnStartup(StartupEventArgs e)
    {
        UseDayMonthYearDates();
        base.OnStartup(e);

        // Every window and dialog gets the themed icon unless it set its own, so none falls
        // back to the .exe's day-mode icon.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => {
                if (s is not Window w) return;
                if (w.Icon is null) w.Icon = AppIcon.Current;
                AppIcon.ApplyTaskbarIcon(w);
            }));

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

        if (!await TryRestoreSessionAsync())
        {
            // Sign in, then choose an espace. Cancelling the choice goes back to sign-in rather
            // than leaving a signed-in user with nowhere to work.
            while (true)
            {
                var login = new LoginWindow();
                if (login.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }

                if (new GroupPickerWindow().ShowDialog() == true)
                {
                    if (login.RememberMe && Session.AccessToken is { } token)
                        SessionStore.Save(new StoredSession(login.Host, token, Session.AccessTokenExpiresAt));
                    else
                        SessionStore.Clear();
                    break;
                }

                Session.SignOut();
            }
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

    /// <summary>
    /// "Rester connecté": skips the sign-in form entirely by replaying a saved token
    /// against the server and re-opening the last group worked in. Anything short of a
    /// full success - no saved session, an expired or revoked token, the group gone - falls
    /// back to the ordinary sign-in screen rather than leaving the app half signed in.
    /// </summary>
    private async Task<bool> TryRestoreSessionAsync()
    {
        var stored = SessionStore.Load();
        if (stored is null) return false;

        // The saved host is only taken on trust if it is this machine or one we authorised;
        // otherwise it has to answer the search on the shop's network again.
        if (!ServerTrust.IsAllowedWithoutSearch(stored.Host, Settings))
        {
            var found = await HostDiscovery.FindAsync(TimeSpan.FromSeconds(3));
            if (!found.Any(h => string.Equals(h.Address, stored.Host, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        try
        {
            Session.Api.Connect(stored.Host);
            await Session.RestoreAsync(stored.AccessToken, stored.ExpiresAt, Settings.LastGroupId);
            if (Session.HasGroup) return true;

            Session.SignOut();
            return false;
        }
        catch (ApiException)
        {
            Session.SignOut();
            return false;
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

    /// <summary>An address Lonnii authorised for this till by repair code, and the code that
    /// proves it. Checked on every start: editing either one makes it stop working.</summary>
    public string? RepairHost { get; set; }
    public string? RepairCode { get; set; }

    /// <summary>The identity of the host this till belongs to, remembered the first time it
    /// connects. A host presenting a different one is refused, so a till cannot be pointed
    /// at a foreign or fake server. Cleared only by deleting this line from the file.</summary>
    public string? PinnedHostId { get; set; }

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
