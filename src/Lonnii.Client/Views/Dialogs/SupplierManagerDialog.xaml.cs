using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Lists every supplier (active and inactive alike) and opens <see cref="SupplierDialog"/>
/// to create or edit one. Same no-hard-delete choice as <see cref="CategoryManagerDialog"/>:
/// a supplier already referenced by products cannot simply vanish, so deactivating is the
/// only path offered here.
/// </summary>
public partial class SupplierManagerDialog : Window
{
    private readonly AppSession _session;
    private List<Row> _rows = [];
    private List<Row> _filtered = [];
    private int _page = 1;
    private int _pageSize = 10;
    private string _sortField = "Nom";
    private bool _sortDescending;

    /// <summary>True until the constructor finishes restoring the saved "Par page" choice -
    /// see VentesView's identical field for why.</summary>
    private bool _suppressPageSizeSave = true;

    private sealed record Row(SupplierDto Supplier)
    {
        public string RatingDisplay => Supplier.Rating is { } r ? $"{r} / 5" : string.Empty;
        public string MontantDuDisplay => Supplier.MontantDu > 0 ? Money.Format(Supplier.MontantDu) : "—";
    }

    public SupplierManagerDialog(AppSession session)
    {
        _session = session;
        InitializeComponent();

        if (UiState.For(_session).SupplierPageSize is { } savedPageSize
            && PageSizeCombo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Content as string == savedPageSize.ToString()) is { } savedItem)
        {
            _pageSize = savedPageSize;
            PageSizeCombo.SelectedItem = savedItem;
        }
        _suppressPageSizeSave = false;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var suppliers = await _session.Api.GetSuppliersAsync();
            _rows = suppliers.Select(s => new Row(s)).ToList();

            var owed = suppliers.Sum(s => s.MontantDu);
            SummaryText.Text = $"{_rows.Count} fournisseur(s)"
                               + (owed > 0 ? $"  •  dû aux fournisseurs : {Money.Format(owed)}" : string.Empty);
            EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter();
            HideError();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ApplyFilter()
    {
        var text = SearchBox.Text.Trim();
        IEnumerable<Row> filtered = _rows;
        if (text.Length > 0)
            filtered = filtered.Where(r => r.Supplier.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        if (MontantDuCheck.IsChecked == true)
            filtered = filtered.Where(r => r.Supplier.MontantDu > 0);

        _filtered = ApplySort(filtered).ToList();
        _page = 1;
        RenderPage();
    }

    /// <summary>Sorts the whole filtered list, not just the visible page - see
    /// ClientManagerDialog's identical method for why SupplierGrid has
    /// CanUserSortColumns="False" instead of relying on column-header clicks.</summary>
    private IEnumerable<Row> ApplySort(IEnumerable<Row> rows)
    {
        IOrderedEnumerable<Row> sorted = _sortField switch
        {
            "Montant dû" => rows.OrderBy(r => r.Supplier.MontantDu),
            _ => rows.OrderBy(r => r.Supplier.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        return _sortDescending ? sorted.Reverse() : sorted;
    }

    private void MontantDuFilter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        _sortField = (SortField.SelectedItem as ComboBoxItem)?.Content as string ?? "Nom";
        ApplyFilter();
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;
        SortDirectionButton.Content = _sortDescending ? "▼" : "▲";
        SortDirectionButton.ToolTip = _sortDescending ? "Ordre décroissant" : "Ordre croissant";
        ApplyFilter();
    }

    private void RenderPage()
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)_pageSize));
        _page = Math.Clamp(_page, 1, pageCount);
        Pager.Configure(_page, pageCount);

        SupplierGrid.ItemsSource = _filtered.Skip((_page - 1) * _pageSize).Take(_pageSize).ToList();
        Grid_SelectionChanged(this, null!);
    }

    private void Pager_PageChanged(object? sender, EventArgs e)
    {
        _page = Pager.CurrentPage;
        RenderPage();
    }

    private void PageSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        if ((PageSizeCombo.SelectedItem as ComboBoxItem)?.Content as string is not { } text
            || !int.TryParse(text, out var size))
            return;

        _pageSize = size;
        _page = 1;

        if (!_suppressPageSizeSave)
        {
            UiState.For(_session).SupplierPageSize = size;
            UiState.Save();
        }

        RenderPage();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private Row? Selected => SupplierGrid.SelectedItem as Row;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = Selected is not null;
        EditButton.IsEnabled = hasSelection;
        ToggleActiveButton.IsEnabled = hasSelection;
        ToggleActiveButton.Content = Selected?.Supplier.IsActive == false ? "Activer" : "Désactiver";
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null) Edit_Click(sender, e);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SupplierDialog(null) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await _session.Api.CreateSupplierAsync(dialog.Result!);
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

        var dialog = new SupplierDialog(row.Supplier) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await _session.Api.UpdateSupplierAsync(row.Supplier.Id, dialog.Result!);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>Flips a supplier's active flag, keeping every other field unchanged.</summary>
    private async void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;

        var supplier = row.Supplier;
        var request = new SaveSupplierRequest(
            supplier.Name, supplier.ContactPerson, supplier.Email, supplier.Phone,
            supplier.Address, supplier.City, supplier.Country, supplier.PaymentTerms,
            supplier.Notes, supplier.Rating, IsActive: !supplier.IsActive, MontantDu: supplier.MontantDu);

        try
        {
            await _session.Api.UpdateSupplierAsync(supplier.Id, request);
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
