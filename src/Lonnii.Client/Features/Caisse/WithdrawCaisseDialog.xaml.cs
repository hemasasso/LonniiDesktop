using System.Windows;
using System.Windows.Controls;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Caisse;

/// <summary>Collects the amount, motif and pool (espèces or mobile money) for a manual
/// withdrawal from the caller's open session - buying supplies, paying a delivery, and the
/// like. The available figure shown as a hint is enforced client-side; the server enforces
/// the same cap independently (CaisseEndpoints.RetraitAsync), since figures can move between
/// this dialog opening and the request landing.</summary>
public partial class WithdrawCaisseDialog : Window
{
    private readonly decimal _availableCash;
    private readonly decimal _availableMobile;
    private string _mode = "cash";

    public WithdrawCaisseRequest? Result { get; private set; }

    /// <summary>Every withdrawal to send: one, or two for "Mixte" (espèces then mobile money).</summary>
    public IReadOnlyList<WithdrawCaisseRequest> Results { get; private set; } = [];

    public WithdrawCaisseDialog(decimal availableCash, decimal availableMobile)
    {
        _availableCash = availableCash;
        _availableMobile = availableMobile;
        InitializeComponent();

        // Amounts read "1 000" while they are typed (see MoneyInput).
        MoneyInput.GroupAsYouType(MontantBox);
        MoneyInput.GroupAsYouType(MobileMontantBox);

        UpdateMode();

        MontantBox.LostFocus += (_, _) =>
        {
            if (Money.TryParse(MontantBox.Text, out decimal montant))
                MontantBox.Text = Money.FormatPlain(montant);
        };

        Loaded += (_, _) => MontantBox.Focus();
    }

    private decimal Available => _mode == "cash" ? _availableCash : _availableMobile;

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        _mode = (string)((Button)sender).Tag;
        UpdateMode();
    }

    private void UpdateMode()
    {
        var primary = (Style)FindResource("PrimaryButton");
        var secondary = (Style)FindResource("SecondaryButton");
        CashModeButton.Style = _mode == "cash" ? primary : secondary;
        MobileModeButton.Style = _mode == "mobile_money" ? primary : secondary;
        MixteModeButton.Style = _mode == "mixte" ? primary : secondary;
        MobilePartPanel.Visibility = _mode == "mixte" ? Visibility.Visible : Visibility.Collapsed;
        MontantLabel.Text = _mode == "mixte" ? "Montant en espèces" : "Montant à retirer";

        SubtitleText.Text = _mode switch
        {
            "cash" => $"Espèces disponibles en caisse : {Money.Format(Available)}",
            "mobile_money" => $"Mobile money disponible en caisse : {Money.Format(Available)}",
            _ => $"Disponible : espèces {Money.Format(_availableCash)} · mobile money {Money.Format(_availableMobile)}",
        };
        ErrorText.Text = string.Empty;
    }

    private void Withdraw_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == "mixte")
        {
            WithdrawMixte();
            return;
        }
        if (!Money.TryParse(MontantBox.Text, out decimal montant) || montant <= 0)
        {
            ErrorText.Text = "Indiquez un montant valide.";
            MontantBox.Focus();
            return;
        }

        if (montant > Available)
        {
            ErrorText.Text = _mode == "cash"
                ? $"Le montant dépasse les espèces disponibles ({Money.Format(Available)})."
                : $"Le montant dépasse le mobile money disponible ({Money.Format(Available)}).";
            MontantBox.Focus();
            return;
        }

        var motif = MotifBox.Text.Trim();
        if (motif.Length == 0)
        {
            ErrorText.Text = "Le motif du retrait est requis.";
            MotifBox.Focus();
            return;
        }

        Result = new WithdrawCaisseRequest(montant, motif, _mode);
        Results = [Result];
        DialogResult = true;
    }

    /// <summary>Part in espèces, part in mobile money - each capped by what that pool holds.</summary>
    private void WithdrawMixte()
    {
        static decimal Read(TextBox box) =>
            string.IsNullOrWhiteSpace(box.Text) ? 0 : Money.TryParse(box.Text, out decimal v) && v >= 0 ? v : -1;
        var cash = Read(MontantBox);
        var mobile = Read(MobileMontantBox);
        if (cash < 0 || mobile < 0 || cash + mobile <= 0)
        {
            ErrorText.Text = "Indiquez le montant en espèces et/ou en mobile money.";
            (cash < 0 ? MontantBox : MobileMontantBox).Focus();
            return;
        }
        if (cash > _availableCash)
        {
            ErrorText.Text = $"Le montant dépasse les espèces disponibles ({Money.Format(_availableCash)}).";
            MontantBox.Focus();
            return;
        }
        if (mobile > _availableMobile)
        {
            ErrorText.Text = $"Le montant dépasse le mobile money disponible ({Money.Format(_availableMobile)}).";
            MobileMontantBox.Focus();
            return;
        }
        var motif = MotifBox.Text.Trim();
        if (motif.Length == 0)
        {
            ErrorText.Text = "Le motif du retrait est requis.";
            MotifBox.Focus();
            return;
        }
        var list = new List<WithdrawCaisseRequest>();
        if (cash > 0) list.Add(new WithdrawCaisseRequest(cash, motif, "cash"));
        if (mobile > 0) list.Add(new WithdrawCaisseRequest(mobile, motif, "mobile_money"));
        Results = list;
        Result = list[0];
        DialogResult = true;
    }
}
