using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Lonnii.Client.Services;

/// <summary>
/// Typing in any date field puts the slashes in for you: "12012026" becomes "12/01/2026", and
/// "1/" becomes "01/". Registered once for every DatePicker in the app.
/// </summary>
public static class DateTyping
{
    private const int FullLength = 10; // dd/MM/yyyy

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(DatePickerTextBox), UIElement.PreviewTextInputEvent,
            new TextCompositionEventHandler(OnTextInput));

    private static void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not DatePickerTextBox box || e.Text.Length != 1) return;

        var c = e.Text[0];
        if (!char.IsDigit(c) && c != '/')
        {
            e.Handled = true;
            return;
        }

        // Editing in the middle of the text or over a selection: only keep the characters valid.
        if (box.SelectionLength > 0 || box.CaretIndex != box.Text.Length) return;

        var text = box.Text;
        var segment = text.Length - (text.LastIndexOf('/') + 1);
        var slashes = text.Count(ch => ch == '/');

        if (c == '/')
        {
            // A slash after a single digit day or month pads it; anywhere else it is not needed.
            if (slashes < 2 && segment == 1)
                text = text[..^1] + "0" + text[^1] + "/";
            else if (slashes < 2 && segment == 2)
                text += "/";
            Set(box, text);
            e.Handled = true;
            return;
        }

        if (text.Length >= FullLength)
        {
            e.Handled = true;
            return;
        }

        text += c;
        if (slashes < 2 && segment + 1 == 2) text += "/";
        Set(box, text);
        e.Handled = true;
    }

    private static void Set(DatePickerTextBox box, string text)
    {
        box.Text = text;
        box.CaretIndex = text.Length;
    }
}
