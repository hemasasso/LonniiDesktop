using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Binds this machine to the workspace, after an administrator proves who they are.
///
/// <para>
/// Shown when opening a workspace is refused because the machine is unknown - a new till,
/// a replaced one, or an installation that was copied from another shop. The last of those
/// is the reason this asks for an administrator rather than accepting whoever is signed in:
/// a cashier who could authorise machines would make the allowance meaningless.
/// </para>
/// </summary>
public partial class RegisterDeviceDialog : Window
{
    private readonly AppSession _session = App.Session;

    private RegisterDeviceDialog(string? prefillEmail)
    {
        InitializeComponent();

        EmailBox.Text = prefillEmail ?? string.Empty;
        DeviceText.Text = $"{DeviceIdentity.FriendlyName} — {DeviceIdentity.Current[..12]}…";

        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(EmailBox.Text)) EmailBox.Focus();
            else PasswordBox.Focus();
        };
    }

    /// <summary>Asks to bind this machine. True when it is now authorised.</summary>
    public static bool Show(Window owner, string? prefillEmail = null)
    {
        var dialog = new RegisterDeviceDialog(prefillEmail) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EmailBox.Text) || PasswordBox.Password.Length == 0)
        {
            ShowError("Saisissez l'email et le mot de passe d'un administrateur.");
            return;
        }

        RegisterButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            await _session.Api.RegisterThisDeviceAsync(EmailBox.Text.Trim(), PasswordBox.Password);
            DialogResult = true;
        }
        catch (ApiException ex)
        {
            // Carries the licence server's wording, which already says whether the allowance
            // is exhausted or the credentials were wrong.
            ShowError(ex.Message);
            RegisterButton.IsEnabled = true;
        }
        finally
        {
            Cursor = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}
