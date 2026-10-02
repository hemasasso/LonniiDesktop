using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// The Ventes client space: every customer ranked by what they have bought, with what they
/// still owe. Customers who only appear on sales are listed as "non enregistré" and can be
/// saved from here. Same no-hard-delete rule as the other managers: a client with sales
/// against them is deactivated, never removed.
/// </summary>
public partial class ClientManagerDialog : Window
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly AppSession _session;
    private readonly bool _canManage;
    private List<Row> _rows = [];
    private List<Row> _filtered = [];
    private int _page = 1;
    private int _pageSize = 10;
    private string _sortField = "Classement";
    private bool _sortDescending;

    /// <summary>True until the constructor finishes restoring the saved "Par page" choice -
    /// see VentesView's identical field for why the XAML default's own SelectionChanged would
    /// otherwise overwrite a previously saved choice before it is even read.</summary>
    private bool _suppressPageSizeSave = true;

    /// <summary>Set when the user asked to see a client's sales; the caller then opens the
    /// sales list filtered on this name.</summary>
    public string? ShowSalesFor { get; private set; }

    private sealed record Row(ClientDto Client, int Rank)
    {
        public string TotalDisplay => Money.Format(Client.TotalAchats);
        public string ResteDisplay => Client.ResteDu > 0
            ? $"{Money.Format(Client.ResteDu)} ({Client.FacturesImpayees})"
            : "—";
        public string DernierDisplay => Client.DernierAchat?.ToLocalTime().ToString("dd/MM/yyyy", French) ?? "—";
        public string FicheDisplay => !Client.IsRegistered ? "non enregistré" : Client.IsActive ? "enregistré" : "désactivé";
    }

    public ClientManagerDialog(AppSession session)
    {
        _session = session;
        _canManage = session.Can(Priv.Gestion.ManageClients);
        InitializeComponent();

        AddButton.IsEnabled = _canManage;
        if (!_canManage)
            AddButton.ToolTip = EditButton.ToolTip = ToggleActiveButton.ToolTip = "Nécessite le privilège « Gérer les clients »";

        if (UiState.For(_session).ClientPageSize is { } savedPageSize
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
            var clients = await _session.Api.GetClientsAsync();
            _rows = clients.Select((c, i) => new Row(c, i + 1)).ToList();
            ApplyFilter();

            var owed = clients.Sum(c => c.ResteDu);
            SummaryText.Text = $"{clients.Count} client(s), classés par total acheté"
                               + (owed > 0 ? $"  •  créances : {Money.Format(owed)}" : string.Empty);
            ErrorText.Text = string.Empty;
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    /// <summary>Re-applies the text and "reste dû" filters, resets to page 1 (a narrower
    /// filter almost never still has the same page count), then renders whichever page that
    /// lands on.</summary>
    private void ApplyFilter()
    {
        var text = SearchBox.Text.Trim();
        var digits = new string(text.Where(char.IsDigit).ToArray());

        IEnumerable<Row> filtered = _rows;
        if (text.Length > 0)
            filtered = filtered.Where(r => r.Client.Nom.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                               || (digits.Length > 0 && r.Client.Telephone is { } phone
                                   && new string(phone.Where(char.IsDigit).ToArray()).Contains(digits)));
        if (ResteDuCheck.IsChecked == true)
            filtered = filtered.Where(r => r.Client.ResteDu > 0);

        _filtered = ApplySort(filtered).ToList();
        _page = 1;
        RenderPage();

        EmptyPanel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Sorts the whole filtered list, not just the visible page - clicking a
    /// DataGrid column header would only reorder the current page's rows, since ClientGrid's
    /// ItemsSource is a fresh slice every time (see RenderPage), which is why ClientGrid has
    /// CanUserSortColumns="False" and this explicit control exists instead.</summary>
    private IEnumerable<Row> ApplySort(IEnumerable<Row> rows)
    {
        IOrderedEnumerable<Row> sorted = _sortField switch
        {
            "Nom" => rows.OrderBy(r => r.Client.Nom, StringComparer.CurrentCultureIgnoreCase),
            "Total acheté" => rows.OrderBy(r => r.Client.TotalAchats),
            "Reste dû" => rows.OrderBy(r => r.Client.ResteDu),
            "Dernier achat" => rows.OrderBy(r => r.Client.DernierAchat),
            _ => rows.OrderBy(r => r.Rank),
        };
        return _sortDescending ? sorted.Reverse() : sorted;
    }

    private void RenderPage()
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)_pageSize));
        _page = Math.Clamp(_page, 1, pageCount);
        Pager.Configure(_page, pageCount);

        ClientGrid.ItemsSource = _filtered.Skip((_page - 1) * _pageSize).Take(_pageSize).ToList();
        Grid_SelectionChanged(this, null!);
    }

    private void Pager_PageChanged(object? sender, EventArgs e)
    {
        _page = Pager.CurrentPage;
        RenderPage();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ResteDuFilter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        _sortField = (SortField.SelectedItem as ComboBoxItem)?.Content as string ?? "Classement";
        ApplyFilter();
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;
        SortDirectionButton.Content = _sortDescending ? "▼" : "▲";
        SortDirectionButton.ToolTip = _sortDescending ? "Ordre décroissant" : "Ordre croissant";
        ApplyFilter();
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
            UiState.For(_session).ClientPageSize = size;
            UiState.Save();
        }

        RenderPage();
    }

    private ClientDto? Selected => (ClientGrid.SelectedItem as Row)?.Client;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var client = Selected;
        EditButton.IsEnabled = _canManage && client is not null;
        EditButton.Content = client?.IsRegistered == false ? "Enregistrer ce client" : "Modifier";
        ToggleActiveButton.IsEnabled = _canManage && client?.IsRegistered == true;
        ToggleActiveButton.Content = client?.IsActive == false ? "Activer" : "Désactiver";
        ShowSalesButton.IsEnabled = client is { NombreAchats: > 0 };
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null && _canManage) Edit_Click(sender, e);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Add_Click(object sender, RoutedEventArgs e) => await SaveAsync(null);

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } client) await SaveAsync(client);
    }

    /// <summary>Edits a saved client, or creates one - blank, or from a customer who so far
    /// only appears on sales.</summary>
    private async Task SaveAsync(ClientDto? client)
    {
        var dialog = new ClientDialog(client) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (client?.Id is { } id) await _session.Api.UpdateClientAsync(id, dialog.Result!);
            else await _session.Api.CreateClientAsync(dialog.Result!);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private async void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Id: { } id } c) return;

        try
        {
            await _session.Api.UpdateClientAsync(id, new SaveClientRequest(
                c.Nom, c.Telephone, c.Email, c.Adresse, c.Ville, c.Notes, IsActive: !c.IsActive));
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void ShowSales_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } client) return;
        ShowSalesFor = client.Nom;
        Close();
    }
}
