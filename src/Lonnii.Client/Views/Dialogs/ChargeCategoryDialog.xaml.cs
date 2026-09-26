using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Creates or edits a charge category. Same swatch-picker mechanic as
/// <see cref="CategoryDialog"/> (Stock's own category dialog), with the source app's own
/// twelve default category colours offered as swatches instead of the app's nav accents.</summary>
public partial class ChargeCategoryDialog : Window
{
    private readonly ChargeCategoryDto? _existing;
    private string _selectedColor = Palette[0];

    private static readonly string[] Palette =
    [
        "#3b82f6", "#facc15", "#06b6d4", "#8b5cf6", "#10b981", "#f59e0b",
        "#ec4899", "#f97316", "#ef4444", "#6366f1", "#dc2626", "#64748b",
    ];

    public SaveChargeCategoryRequest? Result { get; private set; }

    public ChargeCategoryDialog(ChargeCategoryDto? existing)
    {
        _existing = existing;
        InitializeComponent();

        BuildColorSwatches();

        if (existing is null)
        {
            HeaderText.Text = "Nouvelle catégorie";
            HeaderHint.Text = "Créez une catégorie de charge.";
            SelectColor(Palette[0]);
        }
        else
        {
            HeaderText.Text = "Modifier la catégorie";
            HeaderHint.Text = existing.Nom;
            NomBox.Text = existing.Nom;
            DescriptionBox.Text = existing.Description ?? string.Empty;
            SelectColor(existing.Color);
        }

        Loaded += (_, _) => { NomBox.Focus(); NomBox.SelectAll(); };
    }

    private void BuildColorSwatches()
    {
        ColorPanel.Children.Clear();

        foreach (var hex in Palette)
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;

            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(4),
                Background = brush,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = hex,
            };

            swatch.MouseLeftButtonUp += (_, _) => SelectColor(hex);
            ColorPanel.Children.Add(swatch);
        }
    }

    private void SelectColor(string hex)
    {
        _selectedColor = hex;

        foreach (var child in ColorPanel.Children)
        {
            if (child is Border swatch)
            {
                swatch.BorderBrush = string.Equals((string)swatch.Tag, hex, StringComparison.OrdinalIgnoreCase)
                    ? (Brush)Application.Current.Resources["TextPrimary"]
                    : Brushes.Transparent;
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var nom = NomBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(nom))
        {
            ErrorText.Text = "Le nom de la catégorie est requis.";
            NomBox.Focus();
            return;
        }

        Result = new SaveChargeCategoryRequest(
            nom,
            string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
            _selectedColor);

        DialogResult = true;
    }
}
