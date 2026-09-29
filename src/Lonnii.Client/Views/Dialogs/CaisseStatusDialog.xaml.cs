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

    /// <summary>Read from <see cref="_caisse"/> live, so both stay correct across a withdrawal
    /// without their own refresh call. Same formulas as CaisseEndpoints.LiveStats.</summary>
    private decimal ExpectedCash => _caisse.ExpectedCash;

    private decimal ExpectedMobile => _caisse.ExpectedMobile;

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

        // Left empty on purpose: pre-filling the expected figures invited the cashier to
        // accept them without counting, which hides exactly the écart the count is for.
        if (canClose) UpdateExpectedPreview();

        foreach (var box in new[] { MontantFinalBox, MontantFinalMobileBox })
        {
            box.LostFocus += (_, _) =>
            {
                if (Money.TryParse(box.Text, out decimal montant))
                    box.Text = Money.FormatPlain(montant);
            };
        }
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
                $"{r.Date.ToLocalTime():HH:mm}  {Money.Format(r.Montant)} " +
                $"({(r.ModePaiement == "mobile_money" ? "mobile" : "espèces")})  —  {r.Motif}"));
    }

    private void MontantFinal_Changed(object sender, TextChangedEventArgs e) => UpdateExpectedPreview();

    /// <summary>Each count shows its own expected figure and difference, and the line below
    /// sums them into the écart the server will record - so a shortfall in cash hidden by an
    /// excess in mobile money (or the reverse) is still visible on its own side.</summary>
    private void UpdateExpectedPreview()
    {
        // Fires from XAML's TextChanged before the constructor has built every control.
        if (ExpectedText is null || ExpectedCashText is null || ExpectedMobileText is null) return;

        var cash = Money.TryParse(MontantFinalBox.Text, out decimal c) ? c : (decimal?)null;
        var mobile = Money.TryParse(MontantFinalMobileBox.Text, out decimal m) ? m : (decimal?)null;

        ExpectedCashText.Text = Line(ExpectedCash, cash);
        ExpectedMobileText.Text = Line(ExpectedMobile, mobile);

        if (cash is null || mobile is null)
        {
            ExpectedText.Text = $"Total attendu : {Money.Format(ExpectedCash + ExpectedMobile)}";
            ExpectedText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
            return;
        }

        var ecart = cash.Value - ExpectedCash + mobile.Value - ExpectedMobile;
        ExpectedText.Text = $"Total attendu : {Money.Format(ExpectedCash + ExpectedMobile)}  •  " + ecart switch
        {
            0 => "Aucun écart",
            > 0 => $"Écart : excédent de {Money.Format(ecart)}",
            _ => $"Écart : manque de {Money.Format(-ecart)}",
        };
        ExpectedText.Foreground = (System.Windows.Media.Brush)FindResource(ecart == 0 ? "Success" : "Danger");

        static string Line(decimal expected, decimal? counted) =>
            counted is not { } value || value == expected
                ? $"Attendu : {Money.Format(expected)}"
                : $"Attendu : {Money.Format(expected)}  ({(value > expected ? "+" : "-")}{Money.Format(Math.Abs(value - expected))})";
    }

    private async void Withdraw_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WithdrawCaisseDialog(ExpectedCash, ExpectedMobile) { Owner = this };
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
            ErrorText.Text = "Indiquez les espèces comptées en caisse.";
            MontantFinalBox.Focus();
            return;
        }

        if (!Money.TryParse(MontantFinalMobileBox.Text, out decimal mobile) || mobile < 0)
        {
            ErrorText.Text = "Indiquez le solde mobile money constaté (0 s'il n'y en a pas).";
            MontantFinalMobileBox.Focus();
            return;
        }

        CloseResult = new CloseCaisseRequest(montant,
            string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(), mobile);
        DialogResult = true;
    }
}
