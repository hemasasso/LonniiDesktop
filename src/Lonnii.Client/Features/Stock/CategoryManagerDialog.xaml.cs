using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Stock;

/// <summary>
/// Lists every category (active and inactive alike) and opens <see cref="CategoryDialog"/>
/// to create or edit one.
///
/// A category can be deleted only while no product in stock uses it (shops remove the
/// seeded defaults they do not sell); one still in use can only be deactivated.
/// </summary>
public partial class CategoryManagerDialog : Window
{
    private const int PageSize = 20;

    private readonly AppSession _session;
    private List<Row> _rows = [];
    private int _page = 1;

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

            SummaryText.Text = $"{_rows.Count} catégorie(s)";
            EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderPage();
            HideError();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void RenderPage()
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_rows.Count / (double)PageSize));
        _page = Math.Clamp(_page, 1, pageCount);
        Pager.Configure(_page, pageCount);

        CategoryGrid.ItemsSource = _rows.Skip((_page - 1) * PageSize).Take(PageSize).ToList();
        Grid_SelectionChanged(this, null!);
    }

    private void Pager_PageChanged(object? sender, EventArgs e)
    {
        _page = Pager.CurrentPage;
        RenderPage();
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
                    var bytes = await _session.Api.GetThumbnailBytesAsync(url);
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
        DeleteButton.IsEnabled = Selected?.Category.ProductCount == 0;
    }

    /// <summary>Deletes a category no product in stock uses; the server re-checks.</summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;

        if (MessageBox.Show(this, $"Supprimer la catégorie « {row.Category.Name} » ?", "Supprimer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.DeleteCategoryAsync(row.Category.Id);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
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
        await SetActiveAsync([row.Category], !row.Category.IsActive);
    }

    /// <summary>The row's Active box: ticking activates, unticking deactivates.</summary>
    private async void Active_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: Row row } box) return;
        await SetActiveAsync([row.Category], box.IsChecked == true);
    }

    private async void ActivateAll_Click(object sender, RoutedEventArgs e) =>
        await SetActiveAsync(_rows.Select(r => r.Category).ToList(), true);

    private async void DeactivateAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Désactiver toutes les catégories ?", "Tout désactiver",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await SetActiveAsync(_rows.Select(r => r.Category).ToList(), false);
    }

    /// <summary>Saves the active flag on each category that differs, keeping every other field,
    /// then reloads. Stops at the first refusal and shows it.</summary>
    private async Task SetActiveAsync(List<CategoryDto> categories, bool active)
    {
        try
        {
            foreach (var category in categories.Where(c => c.IsActive != active))
            {
                await _session.Api.UpdateCategoryAsync(category.Id, new SaveCategoryRequest(
                    category.Name, category.Description, category.Color, category.Icon, IsActive: active));
            }
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        await LoadAsync();
    }

    private void ShowError(string message) => ErrorText.Text = message;

    private void HideError() => ErrorText.Text = string.Empty;
}
