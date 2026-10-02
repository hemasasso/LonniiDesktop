using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Ventes;

/// <summary>
/// "Paiement Groupé": settles the remaining balance of several unpaid factures at once and
/// hands back the combined receipt. Mirrors Lonnii Business's GroupePaymentModals.jsx - same
/// fields, same change/avoir rules - except that the factures to pick from are every unpaid
/// one in the shop, not just the ones the list's date filter happens to be showing.
/// </summary>
public partial class GroupePaymentDialog : Window
{
    private const int MaxSuggestions = 50;

    private readonly AppSession _session;
    private List<VenteListItemDto> _unpaid = [];
    private readonly ObservableCollection<FactureRow> _selected = [];
    private bool _paying;

    /// <summary>The payment that was made - set when the dialog closes with <c>DialogResult = true</c>.</summary>
    public GroupePaiementDto? Result { get; private set; }

    private sealed class FactureRow(VenteListItemDto vente)
    {
        public VenteListItemDto Vente { get; } = vente;
        public string ClientDisplay => string.IsNullOrWhiteSpace(Vente.ClientNom) ? "—" : Vente.ClientNom;
        public decimal Restant => Math.Max(0, Vente.MontantRestant);
        public string RestantDisplay => Money.Format(Restant);
    }

    public GroupePaymentDialog(AppSession session)
    {
        _session = session;
        InitializeComponent();
        SelectedGrid.ItemsSource = _selected;
        _selected.CollectionChanged += (_, _) => RefreshTotals();
        RefreshTotals();
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await LoadUnpaidAsync();
        };
    }

    private async Task LoadUnpaidAsync()
    {
        try
        {
            // "partial" and "pending" are separate filters server-side; no date range, so an
            // old facture a customer comes back to settle is found too.
            var pending = await _session.Api.GetVentesAsync(statut: "pending");
            var partial = await _session.Api.GetVentesAsync(statut: "partial");
            _unpaid = pending.Ventes.Concat(partial.Ventes)
                .Where(v => v.MontantRestant > 0)
                .OrderBy(v => v.DateVente)
                .ToList();
            RefreshPicker();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    // --- Picking factures ---

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPicker();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        // Enter takes the only match, or the first one when several remain.
        if (PickerGrid.Items.Count > 0)
        {
            Add(((FactureRow)PickerGrid.Items[0]).Vente);
            SearchBox.Clear();
        }
    }

    private void RefreshPicker()
    {
        var term = SearchBox.Text.Trim();
        var chosen = _selected.Select(r => r.Vente.Id).ToHashSet();

        var matches = _unpaid
            .Where(v => !chosen.Contains(v.Id))
            .Where(v => term.Length == 0
                || v.NumeroVente.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (v.ClientNom?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        PickerGrid.ItemsSource = matches.Take(MaxSuggestions).Select(v => new FactureRow(v)).ToList();
        PickerHint.Text = matches.Count > MaxSuggestions
            ? $"{matches.Count} factures — affinez la recherche"
            : $"{matches.Count} facture(s) impayée(s)";
    }

    private void PickerGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PickerGrid.SelectedItem is FactureRow row) Add(row.Vente);
    }

    private void AddSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in PickerGrid.SelectedItems.Cast<FactureRow>().ToList())
            Add(row.Vente);
    }

    private void Add(VenteListItemDto vente)
    {
        if (_selected.Any(r => r.Vente.Id == vente.Id)) return;
        _selected.Add(new FactureRow(vente));

        // Most of the time one customer is settling all of these - save typing their name.
        if (ClientNameBox.Text.Length == 0 && !string.IsNullOrWhiteSpace(vente.ClientNom))
            ClientNameBox.Text = vente.ClientNom;

        RefreshPicker();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FactureRow row) _selected.Remove(row);
        RefreshPicker();
    }

    // --- Money ---

    private decimal Total => _selected.Sum(r => r.Restant);

    private string Mode => (string)((ComboBoxItem)ModeCombo.SelectedItem).Tag;

    private void RefreshTotals()
    {
        if (!IsInitialized) return;

        SelectedCountText.Text = _selected.Count.ToString();
        EmptySelectedText.Visibility = _selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TotalText.Text = Money.Format(Total);
        TotalDueText.Text = $"Total dû : {Money.Format(Total)}";
        PayButton.Content = _selected.Count == 0 ? "Payer" : $"Payer {Money.Format(Total)}";
        RefreshChange();
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;

        CashPanel.Visibility = Mode == "cash" ? Visibility.Visible : Visibility.Collapsed;
        if (Mode != "cash")
        {
            MontantRecuBox.Clear();
            AvoirCheck.IsChecked = false;
        }
        RefreshChange();
    }

    private void MontantRecu_TextChanged(object sender, TextChangedEventArgs e) => RefreshChange();

    private void MontantRecu_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Money.TryParse(MontantRecuBox.Text, out decimal recu) && recu > 0)
            MontantRecuBox.Text = Money.FormatPlain(recu, 2);
    }

    private void Avoir_Changed(object sender, RoutedEventArgs e) => RefreshChange();

    private void MontantRemis_TextChanged(object sender, TextChangedEventArgs e) => RefreshChange();

    private void MontantRemis_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Money.TryParse(MontantRemisBox.Text, out decimal remis) && remis > 0)
            MontantRemisBox.Text = Money.FormatPlain(remis, 2);
    }

    private decimal Recu => Money.TryParse(MontantRecuBox.Text, out decimal recu) && recu > 0 ? recu : 0;

    private decimal Remis => Money.TryParse(MontantRemisBox.Text, out decimal remis) && remis > 0 ? remis : 0;

    /// <summary>Change to give back and, when the till is short of it, the avoir that covers the rest.</summary>
    private void RefreshChange()
    {
        if (!IsInitialized) return;

        var total = Total;
        var recu = Recu;
        var change = Math.Max(0, recu - total);

        var hasChange = Mode == "cash" && recu > total && total > 0 && _session.Can(Priv.Gestion.CreateAvoir);
        AvoirCheck.Visibility = hasChange ? Visibility.Visible : Visibility.Collapsed;
        if (!hasChange) AvoirCheck.IsChecked = false;

        var asAvoir = hasChange && AvoirCheck.IsChecked == true;
        RemisPanel.Visibility = asAvoir ? Visibility.Visible : Visibility.Collapsed;
        if (!asAvoir) MontantRemisBox.Clear();

        if (recu <= 0 || total <= 0)
        {
            MonnaieText.Text = string.Empty;
        }
        else if (recu < total)
        {
            MonnaieText.Foreground = (System.Windows.Media.Brush)FindResource("Danger");
            MonnaieText.Text = $"Il manque {Money.Format(total - recu)}";
        }
        else
        {
            MonnaieText.Foreground = (System.Windows.Media.Brush)FindResource("Success");
            MonnaieText.Text = $"Monnaie à rendre : {Money.Format(change)}";
        }

        if (asAvoir)
            AvoirText.Text = $"Avoir à remettre : {Money.Format(Math.Max(0, change - Math.Min(Remis, change)))}";
    }

    // --- Paying ---

    private async void Pay_Click(object sender, RoutedEventArgs e)
    {
        if (_paying) return;
        ErrorText.Text = string.Empty;

        if (_selected.Count == 0)
        {
            ShowError("Ajoutez au moins une facture.");
            return;
        }

        var recu = Mode == "cash" ? Recu : 0;
        if (recu > 0 && recu < Total)
        {
            ShowError("Le montant reçu doit être supérieur ou égal au montant total.");
            return;
        }

        var request = new GroupePaiementRequest(
            _selected.Select(r => r.Vente.Id).ToList(),
            Mode,
            string.IsNullOrWhiteSpace(ClientNameBox.Text) ? null : ClientNameBox.Text.Trim(),
            recu > 0 ? recu : null,
            AvoirCheck.IsChecked == true ? Remis : 0,
            AvoirCheck.IsChecked == true);

        _paying = true;
        PayButton.IsEnabled = false;
        try
        {
            Result = await _session.Api.PayGroupeAsync(request);
            DialogResult = true;
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);

            // A conflict means somebody else settled one of them meanwhile - refresh so the
            // list stops offering it.
            if (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                await LoadUnpaidAsync();
                var stillUnpaid = _unpaid.Select(v => v.Id).ToHashSet();
                foreach (var gone in _selected.Where(r => !stillUnpaid.Contains(r.Vente.Id)).ToList())
                    _selected.Remove(gone);
                RefreshPicker();
            }
        }
        finally
        {
            _paying = false;
            PayButton.IsEnabled = true;
        }
    }

    private void ShowError(string message) => ErrorText.Text = message;
}
