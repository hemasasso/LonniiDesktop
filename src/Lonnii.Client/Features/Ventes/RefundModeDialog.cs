using System.Windows;
using System.Windows.Controls;
using Lonnii.Client.Services;

namespace Lonnii.Client.Features.Ventes;

/// <summary>
/// Asks where an avoir is paid back from - espèces, mobile money or carte - before "Solder
/// l'avoir". The server refuses when that pool of the till holds less than the refund.
/// </summary>
public sealed class RefundModeDialog : Window
{
    private readonly RadioButton _cash = new() { Content = "Espèces", IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
    private readonly RadioButton _mobile = new() { Content = "Mobile money", Margin = new Thickness(0, 0, 0, 8) };
    private readonly RadioButton _carte = new() { Content = "Banque/Carte" };

    /// <summary>The chosen mode ("cash", "mobile_money", "carte"), or null when cancelled.</summary>
    public static string? Ask(Window? owner, decimal amount, string what)
    {
        var dialog = new RefundModeDialog(amount, what) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Mode : null;
    }

    private string Mode => _mobile.IsChecked == true ? "mobile_money" : _carte.IsChecked == true ? "carte" : "cash";

    private RefundModeDialog(decimal amount, string what)
    {
        Title = "Solder l'avoir";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");

        var ok = new Button { Content = "Rembourser", IsDefault = true, MinWidth = 110 };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");

        var heading = new TextBlock
        {
            Text = $"Rembourser {Money.Format(amount)} {what}",
            FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
        };
        var hint = new TextBlock
        {
            Text = "Sortir l'argent de la caisse en :", Margin = new Thickness(0, 10, 0, 8),
        };
        hint.SetResourceReference(StyleProperty, "PageSubtitle");

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18, 20, 16),
            Children =
            {
                heading, hint, _cash, _mobile, _carte,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 18, 0, 0), Children = { cancel, ok },
                },
            },
        };
    }
}
