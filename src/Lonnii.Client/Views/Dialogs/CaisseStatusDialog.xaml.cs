using System.Windows;
using System.Windows.Controls;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Shows the caller's open session's live figures and, for someone holding
/// <c>can_close_caisse</c>, collects the counted cash to close it with. Read-only for anyone
/// who can only view the status - the "Fermer la caisse" section simply does not render for
/// them, the same way <c>can_add_payment</c> hides "Payer &amp; Valider" in Ventes. The same
/// privilege also gates "Retirer" - see the route mapping comment in
/// CaisseEndpoints.MapCaisseEndpoints for why this reuses can_close_caisse rather than a
/// dedicated one.
/// </summary>
public partial class CaisseStatusDialog : Window
{
    private readonly AppSession _session;
    private readonly bool _canClose;

    /// <summary>Not readonly: a successful withdrawal (<see cref="Withdraw_Click"/>) replaces
    /// this with the server's freshly recomputed figures, without closing the dialog, so the
    /// cashier can withdraw more than once - or withdraw, then close - in one visit.</summary>
    private CaisseDto _caisse;

    /// <summary>Cash the drawer should hold - <see cref="CaisseDto.MontantInitialCash"/> (not
    /// <see cref="CaisseDto.MontantInitial"/>, which also carries the mobile-money float)
    /// plus the session's cash sale payments, same formula as
    /// CaisseEndpoints.LiveStats.ExpectedCash. Reads <see cref="_caisse"/> live, so it stays
    /// correct across a withdrawal without needing its own refresh call.</summary>
    private decimal ExpectedCash => _caisse.MontantInitialCash + _caisse.PaiementCash;

    /// <summary>Set once <see cref="CloseCaisse_Click"/> accepts the input.</summary>
    public CloseCaisseRequest? CloseResult { get; private set; }

    public CaisseStatusDialog(AppSession session, CaisseDto caisse, bool canClose)
    {
        _session = session;
        _caisse = caisse;
        _canClose = canClose;
        InitializeComponent();

        ClosePanel.Visibility = canClose ? Visibility.Visible : Visibility.Collapsed;
        NoCloseNotice.Visibility = canClose ? Visibility.Collapsed : Visibility.Visible;
        CloseCaisseButton.Visibility = canClose ? Visibility.Visible : Visibility.Collapsed;
        WithdrawButton.Visibility = canClose ? Visibility.Visible : Visibility.Collapsed;

        Populate();

        if (canClose)
        {
            MontantFinalBox.Text = Money.FormatPlain(ExpectedCash);
            UpdateExpectedPreview();
        }

        MontantFinalBox.LostFocus += (_, _) =>
        {
            if (Money.TryParse(MontantFinalBox.Text, out decimal montant))
                MontantFinalBox.Text = Money.FormatPlain(montant);
        };
    }

    /// <summary>(Re)paints every read-only figure from <see cref="_caisse"/>. Called once at
    /// construction and again after a withdrawal changes it.</summary>
    private void Populate()
    {
        SubtitleText.Text = $"Ouverte le {_caisse.DateOuverture.ToLocalTime():dd/MM/yyyy à HH:mm} par {_caisse.UserName}";
        VentesCountText.Text = _caisse.TotalVentes.ToString();
        ChiffreAffairesText.Text = Money.Format(_caisse.TotalChiffreAffaires);
        EncaisseText.Text = Money.Format(_caisse.TotalEncaisse);
        MontantInitialText.Text = _caisse.MontantInitialMobile > 0
            ? $"{Money.Format(_caisse.MontantInitialCash)} + {Money.Format(_caisse.MontantInitialMobile)} mobile"
            : Money.Format(_caisse.MontantInitialCash);
        PaiementCashText.Text = Money.Format(_caisse.PaiementCash);
        AutresModesText.Text =
            $"{Money.Format(_caisse.PaiementMobile)} / {Money.Format(_caisse.PaiementCarte)} / {Money.Format(_caisse.PaiementAutres)}";

        var retraits = _caisse.Retraits ?? [];
        RetraitsPanel.Visibility = retraits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetraitsText.Text = $"-{Money.Format(_caisse.TotalRetraits)}";
        RetraitsPanel.ToolTip = retraits.Count == 0 ? null
            : string.Join(Environment.NewLine, retraits.Select(r =>
                $"{r.Date.ToLocalTime():HH:mm}  {Money.Format(r.Montant)}  —  {r.Motif}"));
    }

    private void MontantFinal_Changed(object sender, TextChangedEventArgs e) => UpdateExpectedPreview();

    private void UpdateExpectedPreview()
    {
        if (!Money.TryParse(MontantFinalBox.Text, out decimal counted))
        {
            ExpectedText.Text = $"Attendu : {Money.Format(ExpectedCash)}";
            return;
        }

        var ecart = counted - ExpectedCash;
        ExpectedText.Text = ecart switch
        {
            0 => $"Attendu : {Money.Format(ExpectedCash)}  •  Aucun écart",
            > 0 => $"Attendu : {Money.Format(ExpectedCash)}  •  Excédent de {Money.Format(ecart)}",
            _ => $"Attendu : {Money.Format(ExpectedCash)}  •  Manque de {Money.Format(-ecart)}",
        };
    }

    private async void Withdraw_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WithdrawCaisseDialog(ExpectedCash) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            _caisse = await _session.Api.WithdrawCaisseAsync(request);
            Populate();
            if (_canClose) UpdateExpectedPreview();
            ErrorText.Text = string.Empty;
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private void CloseCaisse_Click(object sender, RoutedEventArgs e)
    {
        if (!Money.TryParse(MontantFinalBox.Text, out decimal montant) || montant < 0)
        {
            ErrorText.Text = "Indiquez le montant compté en caisse.";
            MontantFinalBox.Focus();
            return;
        }

        CloseResult = new CloseCaisseRequest(montant, string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim());
        DialogResult = true;
    }
}
