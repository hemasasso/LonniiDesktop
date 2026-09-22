using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Lists every category (active and inactive alike) and opens <see cref="CategoryDialog"/>
/// to create or edit one.
///
/// There is no hard delete: a category with products against it cannot simply vanish
/// without either orphaning them or silently losing history, so deactivating is the only
/// path offered here - the same choice already made for products
/// (<see cref="Modules.StockView"/> soft-deletes rather than removing rows outright).
/// </summary>
public partial class CategoryManagerDialog : Window
{
    private readonly AppSession _session;
    private List<Row> _rows = [];

    /// <summary>A category paired with its downloaded thumbnail, for the grid's photo column.</summary>
    private sealed record Row(CategoryDto Category, BitmapImage? Thumbnail);

    public CategoryManagerDialog(AppSession session)
    {
        _session = session;
        InitializeComponent();

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var categories = await _session.Api.GetCategoriesAsync();
            _rows = await BuildRowsAsync(categories);

            CategoryGrid.ItemsSource = _rows;
            SummaryText.Text = $"{_rows.Count} catégorie(s)";
            EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HideError();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            Grid_SelectionChanged(this, null!);
        }
    }

    /// <summary>
    /// Downloads each category's thumbnail. The list is short enough in practice (a shop
    /// has tens of categories, not thousands) that fetching them all up front is simpler
    /// than lazy per-row loading, and keeps the grid free of flicker as it scrolls.
    /// </summary>
    private async Task<List<Row>> BuildRowsAsync(List<CategoryDto> categories)
    {
        var rows = new List<Row>(categories.Count);

        foreach (var category in categories)
        {
            BitmapImage? thumbnail = null;
            if (category.ImageUrl is { } url)
            {
                try
                {
                    var bytes = await _session.Api.GetImageBytesAsync(url);
                    thumbnail = ImageHelper.FromBytes(bytes);
                }
                catch (ApiException)
                {
                    // Missing thumbnail is not worth failing the whole list over.
                }
            }

            rows.Add(new Row(category, thumbnail));
        }

        return rows;
    }

    private Row? Selected => CategoryGrid.SelectedItem as Row;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = Selected is not null;
        EditButton.IsEnabled = hasSelection;
        ToggleActiveButton.IsEnabled = hasSelection;
        ToggleActiveButton.Content = Selected?.Category.IsActive == false ? "Activer" : "Désactiver";
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null) Edit_Click(sender, e);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CategoryDialog(_session, null) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var created = await _session.Api.CreateCategoryAsync(dialog.Result!);
            await ApplyPendingPhotoAsync(dialog, created.Id);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;

        var dialog = new CategoryDialog(_session, row.Category) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await _session.Api.UpdateCategoryAsync(row.Category.Id, dialog.Result!);
            await ApplyPendingPhotoAsync(dialog, row.Category.Id);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>Uploads or removes a category's photo after the category itself has been saved.</summary>
    private async Task ApplyPendingPhotoAsync(CategoryDialog dialog, string categoryId)
    {
        if (dialog.PendingPhoto is { } bytes)
        {
            await _session.Api.UploadCategoryImageAsync(categoryId, bytes, dialog.PendingPhotoFileName ?? "photo.jpg");
        }
        else if (dialog.PhotoRemoved)
        {
            await _session.Api.DeleteCategoryImageAsync(categoryId);
        }
    }

    /// <summary>Flips a category's active flag, keeping every other field unchanged.</summary>
    private async void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;

        var category = row.Category;
        var request = new SaveCategoryRequest(
            category.Name, category.Description, category.Color, category.Icon,
            IsActive: !category.IsActive);

        try
        {
            await _session.Api.UpdateCategoryAsync(category.Id, request);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message) => ErrorText.Text = message;

    private void HideError() => ErrorText.Text = string.Empty;
}
