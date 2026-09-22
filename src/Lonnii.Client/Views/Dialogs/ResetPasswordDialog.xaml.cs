using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Sets a new password for another member, for when they have forgotten theirs. There is
/// no email reset on a local host, so an administrator doing this is the only way back in.
///
/// As in the add-member dialog, the password is shown in clear text: the administrator is
/// choosing it on someone else's behalf and has to read it out.
/// </summary>
public partial class ResetPasswordDialog : Window
{
    /// <summary>Minimum password length, matching what the API enforces.</summary>
    private const int MinimumPasswordLength = 8;

    /// <summary>The password chosen, once the dialog has been accepted.</summary>
    public string? NewPassword { get; private set; }

    public ResetPasswordDialog(GroupMemberDto member)
    {
        InitializeComponent();

        MemberText.Text = $"Pour {member.Email}. Cette personne devra se reconnecter.";
        UpdateHint();

        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void Password_Changed(object sender, TextChangedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        if (PasswordHint is null) return;

        PasswordHint.Text = PasswordBox.Text.Length switch
        {
            0 => $"Au moins {MinimumPasswordLength} caractères.",
            var n when n < MinimumPasswordLength =>
                $"Encore {MinimumPasswordLength - n} caractère(s) au minimum.",
            _ => "Mot de passe accepté.",
        };
    }

    /// <summary>
    /// Suggests a password that is easy to read aloud: no characters that are ambiguous
    /// in speech or in print, such as O against 0 or l against 1.
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Text.Length < MinimumPasswordLength)
        {
            ErrorText.Text = $"Le mot de passe doit contenir au moins {MinimumPasswordLength} caractères.";
            PasswordBox.Focus();
            return;
        }

        NewPassword = PasswordBox.Text;
        DialogResult = true;
    }
}
