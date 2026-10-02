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

    public WithdrawCaisseDialog(decimal availableCash, decimal availableMobile)
    {
        _availableCash = availableCash;
        _availableMobile = availableMobile;
        InitializeComponent();

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

        SubtitleText.Text = _mode == "cash"
            ? $"Espèces disponibles en caisse : {Money.Format(Available)}"
            : $"Mobile money disponible en caisse : {Money.Format(Available)}";
        ErrorText.Text = string.Empty;
    }

    private void Withdraw_Click(object sender, RoutedEventArgs e)
    {
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
        DialogResult = true;
    }
}
