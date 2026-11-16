using System.Windows;

namespace Lonnii.Client.Common;

/// <summary>A one-field modal prompt, for the places a full dialog would be overkill.</summary>
public partial class PromptDialog : Window
{
    private PromptDialog(string title, string prompt, string? initialValue)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue ?? string.Empty;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    /// <summary>Shows the prompt and returns what was typed, or null if it was cancelled.
    /// <paramref name="money"/> groups the digits as they are typed ("5 000").</summary>
    public static string? Show(Window owner, string title, string prompt, string? initialValue = null, bool money = false)
    {
        var dialog = new PromptDialog(title, prompt, initialValue) { Owner = owner };
        if (money) MoneyInput.GroupAsYouType(dialog.ValueBox);
        return dialog.ShowDialog() == true ? dialog.ValueBox.Text : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
