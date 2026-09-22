using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views;

/// <summary>
/// Creates the first administrator account on a fresh host.
///
/// Only reachable while the server has no accounts at all. After that, accounts are
/// created by an administrator from the Options screen, which also puts the new person
/// into a workspace - an account with no group grants nothing.
/// </summary>
public partial class CreateAccountWindow : Window
{
    private readonly AppSession _session;

    /// <summary>Minimum password length, matching what the API enforces.</summary>
    private const int MinimumPasswordLength = 8;

    /// <summary>The email of the account just created, so the caller can pre-fill sign-in.</summary>
    public string? CreatedIdentifier { get; private set; }

    public CreateAccountWindow(AppSession session)
    {
        _session = session;
        InitializeComponent();

        UpdatePasswordHint();
        Loaded += (_, _) => EmailBox.Focus();
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => UpdatePasswordHint();

    /// <summary>
    /// Says what is still wrong while the user types, rather than waiting for a failed
    /// submit to tell them the password was too short.
    /// </summary>
    private void UpdatePasswordHint()
    {
        var password = PasswordBox.Password;
        var confirm = ConfirmBox.Password;

        if (password.Length == 0)
        {
            PasswordHint.Text = $"Au moins {MinimumPasswordLength} caractères.";
        }
        else if (password.Length < MinimumPasswordLength)
        {
            PasswordHint.Text =
                $"Encore {MinimumPasswordLength - password.Length} caractère(s) au minimum.";
        }
        else if (confirm.Length > 0 && password != confirm)
        {
            PasswordHint.Text = "Les deux mots de passe ne correspondent pas.";
        }
        else
        {
            PasswordHint.Text = "Mot de passe accepté.";
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            Fail("Indiquez une adresse email valide.", EmailBox);
            return;
        }

        if (password.Length < MinimumPasswordLength)
        {
            Fail($"Le mot de passe doit contenir au moins {MinimumPasswordLength} caractères.", PasswordBox);
            return;
        }

        if (password != ConfirmBox.Password)
        {
            Fail("Les deux mots de passe ne correspondent pas.", ConfirmBox);
            return;
        }

        CreateButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            await _session.Api.RegisterAsync(new RegisterRequest(
                Email: email,
                Password: password,
                Username: Blank(UsernameBox.Text),
                FirstName: Blank(FirstNameBox.Text),
                LastName: Blank(LastNameBox.Text)));

            CreatedIdentifier = email;
            DialogResult = true;
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
            CreateButton.IsEnabled = true;
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

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
