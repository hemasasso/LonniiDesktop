using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Common;

/// <summary>Picks the top bar's colour and pattern. <see cref="Result"/> is set on Enregistrer.</summary>
public partial class TopBarStyleDialog : Window
{
    private readonly TopBarPreference _working;

    public TopBarPreference? Result { get; private set; }

    public TopBarStyleDialog(TopBarPreference current)
    {
        InitializeComponent();
        _working = new TopBarPreference { Color = current.Color, Pattern = current.Pattern };
        Refresh();
    }

    private void Refresh()
    {
        // Colours: "theme default" first (the bar's own blue), then the espace palette.
        ColorSwatches.Children.Clear();
        ColorSwatches.Children.Add(Swatch(null));
        foreach (var hex in EspaceColors.Palette) ColorSwatches.Children.Add(Swatch(hex));

        PatternPanel.Children.Clear();
        foreach (var (key, label) in TopBarPatterns.All)
        {
            var current = key == (_working.Pattern ?? TopBarPatterns.Squares);
            var button = new Button
            {
                Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 5, 12, 5),
                Style = (Style)FindResource(current ? "PrimaryButton" : "SecondaryButton"),
            };
            button.Click += (_, _) => { _working.Pattern = key; Refresh(); };
            PatternPanel.Children.Add(button);
        }

        // The preview paints into its own resources, the way the window does.
        var preview = new Border();
        TopBarPreferences.Apply(_working, preview, Resources, ThemeManager.IsDark);
        Preview.Background = preview.Background;
        PreviewPill.SetResourceReference(Border.BackgroundProperty, "HeaderPill");
        PreviewPill.SetResourceReference(Border.BorderBrushProperty, "HeaderPillBorder");
        PreviewPillText.SetResourceReference(TextBlock.ForegroundProperty, "HeaderPillText");
    }

    private Button Swatch(string? hex)
    {
        var isCurrent = EspaceColors.Normalize(_working.Color) == hex;
        var fill = hex is null
            ? new SolidColorBrush(ThemeManager.IsDark ? Color.FromRgb(0x1F, 0x3F, 0x66) : Color.FromRgb(0x4A, 0x90, 0xE2))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        var ring = new FrameworkElementFactory(typeof(Border));
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        ring.SetValue(Border.BackgroundProperty, fill);
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(isCurrent ? 3 : 0));
        ring.SetResourceReference(Border.BorderBrushProperty, "TextPrimary");
        if (isCurrent)
        {
            var check = new FrameworkElementFactory(typeof(TextBlock));
            check.SetValue(TextBlock.TextProperty, "✓");
            check.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            check.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            ring.AppendChild(check);
        }

        var button = new Button
        {
            Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = hex ?? "Couleur du thème (par défaut)",
            Template = new ControlTemplate(typeof(Button)) { VisualTree = ring },
        };
        button.Click += (_, _) => { _working.Color = hex; Refresh(); };
        return button;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _working.Color = null;
        _working.Pattern = TopBarPatterns.Squares;
        Refresh();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = _working;
        DialogResult = true;
    }
}
