using System.Windows;
using System.Windows.Controls;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Records a payment against an existing sale - settling a facture, or a further
/// instalment on a partial one. Mirrors Lonnii Business's payment modal (the amount defaults
/// to what remains, same as <c>setPaymentAmount(vente.montant_restant)</c> there).</summary>
public partial class AddPaymentDialog : Window
{
    private string _modePaiement = "cash";

    public decimal Montant { get; private set; }
    public string ModePaiement => _modePaiement;

    public AddPaymentDialog(string numeroVente, decimal montantRestant)
    {
        InitializeComponent();
        SubtitleText.Text = $"Vente {numeroVente} — restant à payer : {Money.Format(montantRestant)}";

        // Left empty rather than pre-filled with the amount owed: a cashier who is not
        // actually collecting that whole amount today (a partial instalment) would otherwise
        // have to notice and overwrite it, instead of just typing what was actually handed over.
        MontantBox.Text = string.Empty;

        // Grouped as "1 000,25", same convention as every other money field - reformatted only
        // once typing is done, or it would fight the caret over the group spaces mid-entry.
        MontantBox.LostFocus += (_, _) =>
        {
            if (Money.TryParse(MontantBox.Text, out decimal montant))
                MontantBox.Text = Money.FormatPlain(montant, 2);
        };

        ApplyPaymentVisuals();
        Loaded += (_, _) => MontantBox.Focus();
    }

    private void PaymentMode_Click(object sender, RoutedEventArgs e)
    {
        _modePaiement = (string)((Button)sender).Tag;
        ApplyPaymentVisuals();
    }

    private void ApplyPaymentVisuals()
    {
        var primary = (Style)FindResource("PrimaryButton");
        var secondary = (Style)FindResource("SecondaryButton");

        PaymentCashButton.Style = _modePaiement == "cash" ? primary : secondary;
        PaymentMobileButton.Style = _modePaiement == "mobile_money" ? primary : secondary;
        PaymentCarteButton.Style = _modePaiement == "carte" ? primary : secondary;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!Money.TryParse(MontantBox.Text, out decimal montant) || montant <= 0)
        {
            ErrorText.Text = "Indiquez un montant valide.";
            ErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        Montant = montant;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
