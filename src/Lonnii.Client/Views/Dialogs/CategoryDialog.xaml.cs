using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Microsoft.Win32;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Creates or edits a category, including its photo.</summary>
public partial class CategoryDialog : Window
{
    private readonly AppSession _session;
    private readonly CategoryDto? _existing;
    private string? _selectedColor;

    /// <summary>The accent colours already used across the app's own navigation, offered as swatches.</summary>
    private static readonly string[] Palette =
    [
        "#4361ee", "#10b981", "#f59e0b", "#8b5cf6", "#e11d48",
        "#4895ef", "#6366f1", "#06b6d4", "#7c3aed", "#64748b",
    ];

    /// <summary>The request to send, once the dialog has been accepted.</summary>
    public SaveCategoryRequest? Result { get; private set; }

    /// <summary>A newly picked photo's bytes, staged until the category itself has been saved.</summary>
    public byte[]? PendingPhoto { get; private set; }

    public string? PendingPhotoFileName { get; private set; }

    /// <summary>True when the user removed an existing photo and picked no replacement.</summary>
    public bool PhotoRemoved { get; private set; }

    public CategoryDialog(AppSession session, CategoryDto? existing)
    {
        _session = session;
        _existing = existing;
        InitializeComponent();

        BuildColorSwatches();

        if (existing is null)
        {
            Title = "Nouvelle catégorie";
            HeaderText.Text = "Nouvelle catégorie";
            HeaderHint.Text = "Créez une catégorie pour organiser votre inventaire.";
            SelectColor(Palette[0]);
        }
        else
        {
            Title = "Modifier la catégorie";
            HeaderText.Text = existing.Name;
            HeaderHint.Text = "Modifiez les informations de la catégorie.";

            NameBox.Text = existing.Name;
            DescriptionBox.Text = existing.Description ?? string.Empty;
            IconBox.Text = existing.Icon ?? string.Empty;
            ActiveCheck.IsChecked = existing.IsActive;
            SelectColor(existing.Color ?? Palette[0]);

            RemovePhotoButton.Visibility = existing.ImageUrl is null ? Visibility.Collapsed : Visibility.Visible;
        }

        Loaded += async (_, _) => { NameBox.Focus(); NameBox.SelectAll(); await LoadExistingPhotoAsync(); };
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
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = hex,
            };

            swatch.MouseLeftButtonUp += (_, _) => SelectColor(hex);
            ColorPanel.Children.Add(swatch);
        }
    }

    /// <summary>Highlights the chosen swatch with a border, so the current colour is obvious at a glance.</summary>
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

    private async Task LoadExistingPhotoAsync()
    {
        if (_existing?.ImageUrl is not { } url) return;

        try
        {
            var bytes = await _session.Api.GetImageBytesAsync(url);
            ShowPhoto(bytes);
        }
        catch (ApiException)
        {
            // The placeholder keeps showing "Aucune photo"; not worth blocking the dialog for.
        }
    }

    private void ShowPhoto(byte[] bytes)
    {
        PhotoImage.Source = ImageHelper.FromBytes(bytes);
        PhotoPlaceholder.Visibility = Visibility.Collapsed;
        RemovePhotoButton.Visibility = Visibility.Visible;
    }

    private void ClearPhoto()
    {
        PhotoImage.Source = null;
        PhotoPlaceholder.Visibility = Visibility.Visible;
        RemovePhotoButton.Visibility = Visibility.Collapsed;
    }

    private void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir une photo",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            PendingPhoto = bytes;
            PendingPhotoFileName = Path.GetFileName(dialog.FileName);
            PhotoRemoved = false;
            ShowPhoto(bytes);
        }
        catch (IOException ex)
        {
            ErrorText.Text = $"Impossible de lire ce fichier : {ex.Message}";
        }
    }

    private void RemovePhoto_Click(object sender, RoutedEventArgs e)
    {
        PendingPhoto = null;
        PendingPhotoFileName = null;
        PhotoRemoved = true;
        ClearPhoto();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = "Le nom de la catégorie est requis.";
            NameBox.Focus();
            return;
        }

        Result = new SaveCategoryRequest(
            Name: name,
            Description: Blank(DescriptionBox.Text),
            Color: _selectedColor,
            Icon: Blank(IconBox.Text),
            IsActive: ActiveCheck.IsChecked != false);

        DialogResult = true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
