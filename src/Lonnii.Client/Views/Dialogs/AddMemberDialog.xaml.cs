using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Adds someone to the current workspace, either by naming an existing account or by
/// creating one on the spot.
///
/// The new-account password is shown in clear text rather than masked. The administrator
/// is choosing a password on someone else's behalf and has to read it out to them, so
/// hiding it would only invite a typo neither of them could diagnose.
/// </summary>
public partial class AddMemberDialog : Window
{
    /// <summary>Minimum password length, matching what the API enforces.</summary>
    private const int MinimumPasswordLength = 8;

    /// <summary>The request to send, once the dialog has been accepted.</summary>
    public AddMemberRequest? Result { get; private set; }

    /// <summary>The password chosen, so the caller can show it once after a successful add.</summary>
    public string? CreatedPassword { get; private set; }

    public AddMemberDialog()
    {
        InitializeComponent();
        ApplyMode();
        Loaded += (_, _) => IdentifierBox.Focus();
    }

    private bool IsNewAccount => NewAccountRadio.IsChecked == true;

    private void Mode_Changed(object sender, RoutedEventArgs e) => ApplyMode();

    private void ApplyMode()
    {
        // Guards the first call, which runs from the constructor before the tree is ready.
        if (NewAccountFields is null) return;

        NewAccountFields.Visibility = IsNewAccount ? Visibility.Visible : Visibility.Collapsed;
        ExistingHint.Visibility = IsNewAccount ? Visibility.Collapsed : Visibility.Visible;

        IdentifierLabel.Text = IsNewAccount ? "Adresse email *" : "Email ou nom d'utilisateur *";

        HeaderHint.Text = IsNewAccount
            ? "Créez le compte et ajoutez la personne à cet espace en une seule étape."
            : "Ajoutez à cet espace une personne qui a déjà un compte sur ce serveur.";

        AddButton.Content = IsNewAccount ? "Créer et ajouter" : "Ajouter";
        UpdatePasswordHint();
    }

    private void Password_Changed(object sender, TextChangedEventArgs e) => UpdatePasswordHint();

    private void UpdatePasswordHint()
    {
        if (PasswordHint is null) return;

        var password = PasswordBox.Text;

        PasswordHint.Text = password.Length switch
        {
            0 => $"Au moins {MinimumPasswordLength} caractères.",
            var n when n < MinimumPasswordLength =>
                $"Encore {MinimumPasswordLength - n} caractère(s) au minimum.",
            _ => "Mot de passe accepté.",
        };
    }

    /// <summary>
    /// Suggests a password that is easy to read aloud: no characters that are ambiguous
    /// in speech or on a receipt printer, such as O against 0 or l against 1.
    /// </summary>
    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

        PasswordBox.Text = new string(chars);
        PasswordBox.SelectAll();
        PasswordBox.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var identifier = IdentifierBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(identifier))
        {
            Fail("Indiquez l'identifiant de la personne.", IdentifierBox);
            return;
        }

        if (!IsNewAccount)
        {
            Result = new AddMemberRequest(identifier);
            DialogResult = true;
            return;
        }

        if (!identifier.Contains('@'))
        {
            Fail("Indiquez une adresse email valide pour le nouveau compte.", IdentifierBox);
            return;
        }

        var password = PasswordBox.Text;
        if (password.Length < MinimumPasswordLength)
        {
            Fail($"Le mot de passe doit contenir au moins {MinimumPasswordLength} caractères.", PasswordBox);
            return;
        }

        Result = new AddMemberRequest(
            Identifier: identifier,
            Password: password,
            Username: Blank(UsernameBox.Text),
            FirstName: Blank(FirstNameBox.Text),
            LastName: Blank(LastNameBox.Text));

        CreatedPassword = password;
        DialogResult = true;
    }

    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        focus.Focus();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
