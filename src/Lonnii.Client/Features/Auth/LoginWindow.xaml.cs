using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;

namespace Lonnii.Client.Features.Auth;

/// <summary>
/// Sign-in, plus choosing which host to talk to. The host address matters here in a way
/// it does not on the web: this machine may be the host itself or one of the tills.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AppSession _session = App.Session;

    /// <summary>The server in use. Never typed: found by the search, this machine, or a repair code.</summary>
    private string _host = string.Empty;

    public LoginWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Current;

        _host = App.Settings.HostAddress;
        IdentifierBox.Text = App.Settings.LastIdentifier ?? string.Empty;

        Loaded += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(IdentifierBox.Text)) IdentifierBox.Focus();
            else PasswordBox.Focus();

            await FindHostAsync();

            // Quietly find out whether this host still needs its first account.
            await CheckSetupStateAsync();
        };
    }

    /// <summary>
    /// Settles which host to talk to without asking: the address used last time if it still
    /// answers, otherwise whatever answers on the local network. It keeps looking for
    /// <see cref="SearchSeconds"/> before giving up. A found server is shown without its
    /// address and cannot be changed from here, so a worker cannot repoint the till by
    /// accident or learn the port; only when nothing is found does the address field appear.
    /// </summary>
    private const int SearchSeconds = 30;

    private async Task FindHostAsync()
    {
        RepairButton.Visibility = Visibility.Collapsed;
        _serverFound = false;
        _blocked = false;
        UpdateSignIn();
        SetDiscovery("Recherche du serveur… Veuillez patienter jusqu'à la connexion au serveur.", "TextSecondary", "Success", blink: true);

        var deadline = DateTime.UtcNow.AddSeconds(SearchSeconds);
        do
        {
            // Only an address that proves itself is tried without searching: this machine, or
            // one we authorised. Whatever else the settings file says is ignored.
            var known = ServerTrust.RepairedAddress(App.Settings) ?? _host;
            if (ServerTrust.IsAllowedWithoutSearch(known, App.Settings) && await ReachableAsync(known))
            {
                _host = known;
                ShowFound();
                return;
            }

            var hosts = await HostDiscovery.FindAsync(TimeSpan.FromSeconds(2));
            if (hosts.Count > 0)
            {
                _host = hosts[0].Address;
                _session.Api.Connect(_host);
                ShowFound();
                return;
            }
        }
        while (IsLoaded && DateTime.UtcNow < deadline);

        SetDiscovery("Serveur introuvable.", "Danger", "Danger", blink: false);
        RepairButton.Visibility = Visibility.Visible;
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RepairWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _host = App.Settings.HostAddress;
        await FindHostAsync();
        await CheckSetupStateAsync();
    }

    /// <summary>The server line's text, in the theme colour that says what it means: green when found.</summary>
    private void SetDiscovery(string text, string textBrush, string dotBrush, bool blink)
    {
        DiscoveryText.Text = text;
        DiscoveryText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, textBrush);
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotBrush);

        // One full fade out and back per second while searching (1 Hz); steady otherwise.
        StatusDot.BeginAnimation(OpacityProperty, blink
            ? new System.Windows.Media.Animation.DoubleAnimation(1, 0.1, TimeSpan.FromSeconds(0.5))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            }
            : null);
        if (!blink) StatusDot.Opacity = 1;
    }

    private bool _serverFound, _blocked, _needsSetup, _busy;

    /// <summary>Sign-in is possible only with a server found, nothing blocking it, and no work in progress.</summary>
    private void UpdateSignIn() =>
        SignInButton.IsEnabled = _serverFound && !_blocked && !_needsSetup && !_busy;

    private async Task<bool> ReachableAsync(string address)
    {
        _session.Api.Connect(address);
        using var quickly = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        return await _session.Api.PingAsync(quickly.Token);
    }

    private void ShowFound()
    {
        _serverFound = true;
        UpdateSignIn();
        SetDiscovery("Serveur trouvé. Vous pouvez vous connecter maintenant.", "Success", "Success", blink: false);
        RepairButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Asks the host whether it has any account. A brand-new installation shows the
    /// create-the-first-administrator panel instead of a sign-in form nobody can satisfy.
    /// Failures are silent: an unreachable host is the Tester button's job to report.
    /// </summary>
    private async Task CheckSetupStateAsync()
    {
        if (string.IsNullOrWhiteSpace(_host)) return;

        try
        {
            _session.Api.Connect(_host);
            var state = await _session.Api.GetSetupStateAsync();

            // Trust on first use: the first host this till signs in against is its own. After
            // that, any other host is refused - found by search or typed by hand.
            var pinned = App.Settings.PinnedHostId;
            if (state.HostId is not null && pinned is null)
            {
                App.Settings.PinnedHostId = state.HostId;
                App.Settings.Save();
            }
            else if (pinned is not null && state.HostId != pinned)
            {
                WrongHost();
                return;
            }

            // Hand-made accounts exist for development; a shipped host registers with Lonnii.
            ManualAccountButton.Visibility = state.ManualSetupAllowed ? Visibility.Visible : Visibility.Collapsed;
            ShowSetupPanel(!state.HasAnyAccount);
        }
        catch (ApiException)
        {
            ShowSetupPanel(false);
        }
    }

    /// <summary>This host is not the one the till was set up with: no sign-in, no first account.</summary>
    private void WrongHost()
    {
        ShowSetupPanel(false);
        _blocked = true;
        UpdateSignIn();
        SetDiscovery("Ce serveur n'est pas celui de ce magasin.", "Danger", "Danger", blink: false);
        ShowError("Ce poste est lié à un autre serveur Lonnii. Connexion refusée." + "\n" +
                  "Si le serveur du magasin a été réinstallé, contactez Lonnii.");
    }

    private void ShowSetupPanel(bool needsSetup)
    {
        SetupPanel.Visibility = needsSetup ? Visibility.Visible : Visibility.Collapsed;
        _needsSetup = needsSetup;
        UpdateSignIn();
    }

    /// <summary>
    /// The supplied path: the credentials file creates the workspace and its administrator,
    /// after our licence server confirms the shop is one we registered.
    /// </summary>
    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FirstLaunchWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        PasswordBox.Clear();
        HideError();

        await CheckSetupStateAsync();

        IdentifierBox.Focus();
        HostHint.Text = "Espace activé. Connectez-vous avec le compte administrateur.";
    }

    /// <summary>
    /// A new shop registers with Lonnii, and is activated here once we have approved it.
    /// </summary>
    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RegistrationWindow(_session) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        PasswordBox.Clear();
        HideError();

        await CheckSetupStateAsync();

        IdentifierBox.Focus();
        HostHint.Text = "Espace activé. Connectez-vous avec le compte administrateur.";
    }

    /// <summary>Creates the first administrator, then pre-fills sign-in with it.</summary>
    private async void CreateAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CreateAccountWindow(_session) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        IdentifierBox.Text = dialog.CreatedIdentifier ?? string.Empty;
        PasswordBox.Clear();
        HideError();

        await CheckSetupStateAsync();

        PasswordBox.Focus();
        HostHint.Text = "Compte créé. Connectez-vous avec votre mot de passe.";
    }

    /// <summary>Confirms a Lonnii host is answering before the user types a password.</summary>
    private async void SignIn_Click(object sender, RoutedEventArgs e) => await SignInAsync();

    private async Task SignInAsync()
    {
        // Belt and braces alongside removing the duplicate Enter handler: a second
        // sign-in starting while the group picker is open would try to re-parent a
        // dialog onto a window that is already closing.
        if (_signingIn) return;
        _signingIn = true;
        try
        {
            await SignInCoreAsync();
        }
        finally
        {
            _signingIn = false;
        }
    }

    private bool _signingIn;

    /// <summary>The server the user signed in against, once <see cref="Window.DialogResult"/> is true.</summary>
    public string Host { get; private set; } = string.Empty;

    /// <summary>Whether "Rester connecté" was ticked at sign-in.</summary>
    public bool RememberMe { get; private set; }

    private async Task SignInCoreAsync()
    {
        var host = _host.Trim();
        var identifier = IdentifierBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(host)) { ShowError("Indiquez l'adresse du serveur."); return; }
        if (string.IsNullOrWhiteSpace(identifier)) { ShowError("Indiquez votre email ou nom d'utilisateur."); IdentifierBox.Focus(); return; }
        if (string.IsNullOrEmpty(password)) { ShowError("Indiquez votre mot de passe."); PasswordBox.Focus(); return; }

        SetBusy(true, "Connexion…");
        try
        {
            _session.Api.Connect(host);
            await _session.SignInAsync(identifier, password);

            App.Settings.HostAddress = host;
            App.Settings.LastIdentifier = identifier;
            App.Settings.Save();

            // Signed in. The window closes and the caller moves on to choosing an espace; if that is
            // cancelled it opens a fresh sign-in window. (An earlier version showed the picker from
            // here and tried to keep this window out of the way: hiding ends a dialog, and
            // minimising showed as a visible shrink to the taskbar.)
            Host = host;
            RememberMe = RememberMeCheck.IsChecked == true;
            DialogResult = true;
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
            PasswordBox.Clear();
            PasswordBox.Focus();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetBusy(bool busy, string? text = null)
    {
        BusyText.Text = text ?? string.Empty;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _busy = busy;
        UpdateSignIn();
        Cursor = busy ? Cursors.Wait : null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorPanel.Visibility = Visibility.Collapsed;
}
