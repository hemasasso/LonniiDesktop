using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Lets the signed-in user change their own password, proving they know the current one.
///
/// Worth having even though an administrator can reset a password: without it, anyone
/// given a generated password by their manager would be stuck with it, since there is no
/// email reset on a local host.
/// </summary>
public partial class ChangePasswordDialog : Window
{
    private readonly AppSession _session;

    /// <summary>Minimum password length, matching what the API enforces.</summary>
    private const int MinimumPasswordLength = 8;

    public ChangePasswordDialog(AppSession session)
    {
        _session = session;
        InitializeComponent();

        AccountText.Text = $"Compte : {_session.User?.Email}";
        UpdateHint();

        Loaded += (_, _) => CurrentBox.Focus();
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        if (PasswordHint is null) return;

        var password = NewBox.Password;
        var confirm = ConfirmBox.Password;

        if (password.Length == 0)
            PasswordHint.Text = $"Au moins {MinimumPasswordLength} caractères.";
        else if (password.Length < MinimumPasswordLength)
            PasswordHint.Text = $"Encore {MinimumPasswordLength - password.Length} caractère(s) au minimum.";
        else if (confirm.Length > 0 && password != confirm)
            PasswordHint.Text = "Les deux mots de passe ne correspondent pas.";
        else
            PasswordHint.Text = "Mot de passe accepté.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CurrentBox.Password))
        {
            Fail("Indiquez votre mot de passe actuel.", CurrentBox);
            return;
        }

        if (NewBox.Password.Length < MinimumPasswordLength)
        {
            Fail($"Le nouveau mot de passe doit contenir au moins {MinimumPasswordLength} caractères.", NewBox);
            return;
        }

        if (NewBox.Password != ConfirmBox.Password)
        {
            Fail("Les deux mots de passe ne correspondent pas.", ConfirmBox);
            return;
        }

        SaveButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            await _session.ChangeOwnPasswordAsync(CurrentBox.Password, NewBox.Password);
            DialogResult = true;
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
            SaveButton.IsEnabled = true;
        }
        finally
        {
            Cursor = null;
        }
    }

    private void Fail(string message, System.Windows.Controls.Control focus)
    {
        ErrorText.Text = message;
        focus.Focus();
    }
}
