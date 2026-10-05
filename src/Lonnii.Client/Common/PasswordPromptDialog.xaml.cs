using System.Windows;

namespace Lonnii.Client.Common;

/// <summary>A one-field modal prompt for a password, with the show/hide eye.</summary>
public partial class PasswordPromptDialog : Window
{
    private PasswordPromptDialog(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    /// <summary>Shows the prompt and returns what was typed, or null if it was cancelled or left empty.</summary>
    public static string? Show(Window owner, string title, string prompt)
    {
        var dialog = new PasswordPromptDialog(title, prompt) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.PasswordInput.Password.Length > 0
            ? dialog.PasswordInput.Password
            : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
