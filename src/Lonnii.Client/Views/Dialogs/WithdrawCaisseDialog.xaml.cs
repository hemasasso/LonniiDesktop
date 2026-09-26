using System.Windows;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Collects the amount and motif for a manual cash withdrawal from the caller's open
/// session - buying supplies, paying a delivery, and the like. <see cref="AvailableCash"/> is
/// shown as a hint and enforced client-side; the server enforces the same cap independently
/// (CaisseEndpoints.RetraitAsync), since figures can move between this dialog opening and the
/// request landing.</summary>
public partial class WithdrawCaisseDialog : Window
{
    private readonly decimal _availableCash;

    public WithdrawCaisseRequest? Result { get; private set; }

    public WithdrawCaisseDialog(decimal availableCash)
    {
        _availableCash = availableCash;
        InitializeComponent();

        SubtitleText.Text = $"Espèces disponibles en caisse : {Money.Format(availableCash)}";

        MontantBox.LostFocus += (_, _) =>
        {
            if (Money.TryParse(MontantBox.Text, out decimal montant))
                MontantBox.Text = Money.FormatPlain(montant);
        };

        Loaded += (_, _) => MontantBox.Focus();
    }

    private void Withdraw_Click(object sender, RoutedEventArgs e)
    {
        if (!Money.TryParse(MontantBox.Text, out decimal montant) || montant <= 0)
        {
            ErrorText.Text = "Indiquez un montant valide.";
            MontantBox.Focus();
            return;
        }

        if (montant > _availableCash)
        {
            ErrorText.Text = $"Le montant dépasse les espèces disponibles ({Money.Format(_availableCash)}).";
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

        Result = new WithdrawCaisseRequest(montant, motif);
        DialogResult = true;
    }
}
