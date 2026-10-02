using System.Windows;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Ventes;

/// <summary>History of "Paiement Groupé" receipts: reprint one, or pay back the avoir a group
/// payment created. Mirrors the history modal in Lonnii Business's GroupePaymentModals.jsx.</summary>
public partial class GroupeHistoryDialog : Window
{
    private const int PageSize = 10;

    private readonly AppSession _session;
    private readonly bool _canSolderAvoir;
    private int _total;

    /// <summary>True once an avoir was paid back, so the caller can refresh the till figures
    /// (the refund is recorded as cash leaving the open caisse).</summary>
    public bool HasChanges { get; private set; }

    private sealed class Row(GroupePaiementDto payment, bool canSolder)
    {
        public GroupePaiementDto Payment { get; } = payment;
        public string DateDisplay => Payment.Date.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
        public string ClientDisplay => string.IsNullOrWhiteSpace(Payment.ClientName) ? "—" : Payment.ClientName;
        public string CaissierDisplay => Payment.CaissierName ?? "—";
        public string FacturesDisplay => string.Join(", ", Payment.Factures.Select(f => f.NumeroVente));
        public string TotalDisplay => Money.Format(Payment.Total);
        public string AvoirDisplay => Payment.AvoirAmount <= 0 ? "—"
            : Payment.IsAvoirSolded ? $"{Money.Format(Payment.AvoirAmount)} (soldé)"
            : Money.Format(Payment.AvoirAmount);
        public Visibility SolderVisibility =>
            canSolder && Payment.AvoirAmount > 0 && !Payment.IsAvoirSolded ? Visibility.Visible : Visibility.Collapsed;
    }

    public GroupeHistoryDialog(AppSession session)
    {
        _session = session;
        _canSolderAvoir = session.Can(Priv.Gestion.SoldeAvoir);
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync(1);
    }

    private DateOnly? Debut => DebutPicker.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;
    private DateOnly? Fin => FinPicker.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;

    private async Task LoadAsync(int page)
    {
        ErrorText.Text = string.Empty;
        try
        {
            var response = await _session.Api.GetGroupePaymentsAsync(page, PageSize, Debut, Fin);
            _total = response.Total;
            HistoryGrid.ItemsSource = response.Payments.Select(p => new Row(p, _canSolderAvoir)).ToList();
            EmptyPanel.Visibility = response.Payments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SummaryText.Text = _total == 0 ? "Aucun paiement groupé" : $"{_total} paiement(s) groupé(s)";
            Pager.Configure(page, (int)Math.Ceiling(_total / (double)PageSize));
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private async void Pager_PageChanged(object sender, EventArgs e) => await LoadAsync(Pager.CurrentPage);

    private async void Filter_Click(object sender, RoutedEventArgs e) => await LoadAsync(1);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(Pager.CurrentPage);

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        DebutPicker.SelectedDate = null;
        FinPicker.SelectedDate = null;
        await LoadAsync(1);
    }

    private async void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is Row row) await ShowReceiptAsync(row.Payment);
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Row row) await ShowReceiptAsync(row.Payment);
    }

    private async Task ShowReceiptAsync(GroupePaiementDto payment)
    {
        try
        {
            await GroupeReceiptDialog.ShowForAsync(payment, _session, this);
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private async void Solder_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Row row) return;

        var confirm = MessageBox.Show(this,
            $"Voulez-vous vraiment solder l'avoir de {Money.Format(row.Payment.AvoirAmount)} pour ce paiement groupé ?",
            "Solder l'avoir", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.SolderGroupeAvoirAsync(row.Payment.Id);
            HasChanges = true;
            await LoadAsync(Pager.CurrentPage);
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
