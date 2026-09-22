using System.Windows;

namespace Lonnii.Client.Views;

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

    /// <summary>Shows the prompt and returns what was typed, or null if it was cancelled.</summary>
    public static string? Show(Window owner, string title, string prompt, string? initialValue = null)
    {
        var dialog = new PromptDialog(title, prompt, initialValue) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.ValueBox.Text : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
