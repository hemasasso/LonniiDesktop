using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Every caisse session in the group, newest first - "Historique Caisse". A user without
/// admin standing only ever gets their own sessions back (CaisseEndpoints.HistoriqueAsync
/// enforces this server-side), so the vendeur filter is hidden rather than shown empty.
/// </summary>
public partial class CaisseHistoryDialog : Window
{
    private readonly AppSession _session;
    private readonly bool _isAdmin;
    private readonly bool _canResolveEcart;
    private List<CaisseDto> _caisses = [];

    /// <summary>Adapts a <see cref="CaisseDto"/> for the grid's plain-text columns, which bind
    /// to formatted strings rather than reimplementing formatting in XAML converters.</summary>
    private sealed record HistoryRow(CaisseDto Caisse)
    {
        public DateTime DateOuverture => Caisse.DateOuverture.ToLocalTime();
        public DateTime? DateFermeture => Caisse.DateFermeture?.ToLocalTime();
        public string UserName => Caisse.UserName;
        public int TotalVentes => Caisse.TotalVentes;
        public string ChiffreAffairesDisplay => Money.Format(Caisse.TotalChiffreAffaires);
        public string EncaisseDisplay => Money.Format(Caisse.TotalEncaisse);

        public string RetraitsDisplay => Caisse.TotalRetraits == 0 ? "—" : $"-{Money.Format(Caisse.TotalRetraits)}";

        /// <summary>One line per withdrawal, shown on hover. Null rather than empty when there
        /// are none, so a session without withdrawals shows no tooltip at all.</summary>
        public string? RetraitsTooltip => Caisse.Retraits is not { Count: > 0 } retraits ? null
            : string.Join(Environment.NewLine, retraits.Select(r =>
                $"{r.Date.ToLocalTime():HH:mm}  {Money.Format(r.Montant)}  —  {r.Motif}"));

        public string EcartDisplay => Caisse.Status != "closed" ? "—"
            : Caisse.Ecart == 0 ? "Aucun"
            : Caisse.Ecart > 0 ? $"+{Money.Format(Caisse.Ecart)}" : $"-{Money.Format(-Caisse.Ecart)}";

        public string StatusDisplay => Caisse.Status != "closed" ? "Ouverte"
            : Caisse.Ecart == 0 ? "Fermée"
            : Caisse.EcartResolved ? "Fermée — écart résolu" : "Fermée — écart à résoudre";
    }

    public CaisseHistoryDialog(AppSession session, bool isAdmin, bool canResolveEcart)
    {
        _session = session;
        _isAdmin = isAdmin;
        _canResolveEcart = canResolveEcart;
        InitializeComponent();

        VendeurBox.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;

        Loaded += async (_, _) =>
        {
            if (isAdmin) await LoadVendeursAsync();
            await LoadAsync();
        };
    }

    private async Task LoadVendeursAsync()
    {
        try
        {
            var vendeurs = await _session.Api.GetCaisseVendeursAsync();
            VendeurBox.ItemsSource = new[] { new CaisseVendeurDto("", "Tous les vendeurs") }.Concat(vendeurs).ToList();
            VendeurBox.SelectedIndex = 0;
        }
        catch (ApiException)
        {
            // The list is a convenience filter; its absence should not block the history itself.
        }
    }

    private async void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var dateDebut = DateDebutPicker.SelectedDate is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
            var dateFin = DateFinPicker.SelectedDate is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
            var userId = (VendeurBox.SelectedItem as CaisseVendeurDto)?.UserId;

            var response = await _session.Api.GetCaisseHistoryAsync(
                dateDebut, dateFin, string.IsNullOrEmpty(userId) ? null : userId, pageSize: 200);

            _caisses = response.Caisses.ToList();
            HistoryGrid.ItemsSource = _caisses.Select(c => new HistoryRow(c)).ToList();
            EmptyPanel.Visibility = _caisses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SubtitleText.Text = $"{response.Total} session(s)";
        }
        catch (ApiException ex)
        {
            MessageBox.Show(this, ex.Message, "Historique des caisses", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedCaisse();
        ResolveButton.IsEnabled = _canResolveEcart && selected is { Status: "closed", Ecart: not 0, EcartResolved: false };
        ReportButton.IsEnabled = selected is not null;
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCaisse() is not { } caisse) return;
        new CaisseReportDialog(caisse, _session.Groupe?.Nom) { Owner = this }.ShowDialog();
    }

    private void HistoryGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Report_Click(sender, e);

    private CaisseDto? SelectedCaisse() => (HistoryGrid.SelectedItem as HistoryRow)?.Caisse;

    private async void Resolve_Click(object sender, RoutedEventArgs e)
    {
        var caisse = SelectedCaisse();
        if (caisse is null) return;

        var dialog = new ResolveEcartDialog(caisse.Ecart) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.ResolveCaisseEcartAsync(caisse.Id, request);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            MessageBox.Show(this, ex.Message, "Résoudre l'écart", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_caisses.Count == 0)
        {
            MessageBox.Show(this, "Aucune session à exporter.", "Exporter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupName = _session.Groupe?.Nom ?? "espace_groupe";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"caisses_{groupName.Trim().ToLowerInvariant().Replace(' ', '_')}_{DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "Fichier CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildCsv(), new UTF8Encoding(true));
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Erreur lors de l'export : {ex.Message}", "Exporter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildCsv()
    {
        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', "Ouverture", "Fermeture", "Vendeur", "Ventes",
            "Chiffre d'affaires", "Encaissé", "Retraits", "Motifs des retraits",
            "Fonds initial (espèces)", "Fonds initial (mobile)",
            "Montant compté", "Écart", "Statut"));

        foreach (var c in _caisses)
        {
            sb.AppendLine(string.Join(';',
                c.DateOuverture.ToLocalTime().ToString("dd/MM/yyyy HH:mm"),
                c.DateFermeture?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "",
                Csv(c.UserName),
                c.TotalVentes,
                Money.FormatPlain(c.TotalChiffreAffaires, 2),
                Money.FormatPlain(c.TotalEncaisse, 2),
                Money.FormatPlain(c.TotalRetraits, 2),
                Csv(string.Join(" | ", (c.Retraits ?? []).Select(r => $"{Money.FormatPlain(r.Montant, 2)} : {r.Motif}"))),
                Money.FormatPlain(c.MontantInitialCash, 2),
                Money.FormatPlain(c.MontantInitialMobile, 2),
                c.MontantFinal is { } final ? Money.FormatPlain(final, 2) : "",
                Money.FormatPlain(c.Ecart, 2),
                Csv(c.Status)));
        }

        return sb.ToString();
    }
}
