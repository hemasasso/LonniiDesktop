using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;

namespace Lonnii.Client.Views;

/// <summary>
/// Sign-in, plus choosing which host to talk to. The host address matters here in a way
/// it does not on the web: this machine may be the host itself or one of the tills.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AppSession _session = App.Session;

    public LoginWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Current;

        HostBox.Text = App.Settings.HostAddress;
        IdentifierBox.Text = App.Settings.LastIdentifier ?? string.Empty;

        Loaded += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(IdentifierBox.Text)) IdentifierBox.Focus();
            else PasswordBox.Focus();

            // Quietly find out whether this host still needs its first account.
            await CheckSetupStateAsync();
        };
    }

    /// <summary>
    /// Asks the host whether it has any account. A brand-new installation shows the
    /// create-the-first-administrator panel instead of a sign-in form nobody can satisfy.
    /// Failures are silent: an unreachable host is the Tester button's job to report.
    /// </summary>
    private async Task CheckSetupStateAsync()
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text)) return;

        try
        {
            _session.Api.Connect(HostBox.Text);
            var state = await _session.Api.GetSetupStateAsync();
            ShowSetupPanel(!state.HasAnyAccount);
        }
        catch (ApiException)
        {
            ShowSetupPanel(false);
        }
    }

    private void ShowSetupPanel(bool needsSetup)
    {
        SetupPanel.Visibility = needsSetup ? Visibility.Visible : Visibility.Collapsed;
        SignInButton.IsEnabled = !needsSetup;
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
    private async void TestHost_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            ShowError("Indiquez l'adresse du serveur.");
            return;
        }

        SetBusy(true, "Test…");
        try
        {
            _session.Api.Connect(HostBox.Text);
            var reachable = await _session.Api.PingAsync();

            if (reachable)
            {
                HideError();
                HostHint.Text = $"Serveur joignable : {_session.Api.BaseAddress}";
                await CheckSetupStateAsync();
            }
            else
            {
                ShowError($"Aucun serveur Lonnii ne répond à « {_session.Api.BaseAddress} ».\n" +
                          "Vérifiez que l'application serveur est démarrée sur l'ordinateur hôte " +
                          "et que les deux machines sont sur le même réseau.");
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

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

    private async Task SignInCoreAsync()
    {
        var host = HostBox.Text.Trim();
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

            // A user with no group cannot do anything, so the picker also offers to create one.
            var picker = new GroupPickerWindow { Owner = this };
            if (picker.ShowDialog() != true)
            {
                _session.SignOut();
                SetBusy(false);
                PasswordBox.Clear();
                return;
            }

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
        SignInButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorPanel.Visibility = Visibility.Collapsed;
}
