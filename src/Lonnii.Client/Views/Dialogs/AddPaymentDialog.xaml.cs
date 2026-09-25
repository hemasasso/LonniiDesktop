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
        MontantBox.Text = Money.FormatPlain(montantRestant);
        ApplyPaymentVisuals();
        Loaded += (_, _) => { MontantBox.Focus(); MontantBox.SelectAll(); };
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
