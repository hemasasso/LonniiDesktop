using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>Records a payment against an existing sale - settling a facture, or a further
/// instalment on a partial one. Mirrors Lonnii Business's payment modal (the amount defaults
/// to what remains, same as <c>setPaymentAmount(vente.montant_restant)</c> there).</summary>
public partial class AddPaymentDialog : Window
{
    private string _modePaiement = "cash";
    private readonly decimal _montantRestant;

    public decimal Montant { get; private set; }
    public string ModePaiement => _modePaiement;

    public AddPaymentDialog(string numeroVente, decimal montantRestant)
    {
        _montantRestant = montantRestant;
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
        MontantBox.TextChanged += (_, _) => UpdateMonnaieARendre();

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

        MontantRecuPanel.Visibility = _modePaiement == "cash" ? Visibility.Visible : Visibility.Collapsed;
        if (_modePaiement != "cash") MontantRecuBox.Text = string.Empty;
        UpdateMonnaieARendre();
    }

    /// <summary>Drives Montant from what was actually handed over: capped at what remains
    /// owed, since a cash-only overpayment is change to give back, not something to apply to
    /// the sale. Left alone if the box is cleared, so the cashier can still type Montant by
    /// hand for a partial instalment with no "received" figure to type at all.</summary>
    private void MontantRecu_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Money.TryParse(MontantRecuBox.Text, out decimal recu) && recu > 0)
            MontantBox.Text = Money.FormatPlain(Math.Min(recu, _montantRestant), 2);

        UpdateMonnaieARendre();
    }

    private void MontantRecu_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Money.TryParse(MontantRecuBox.Text, out decimal recu) && recu > 0)
            MontantRecuBox.Text = Money.FormatPlain(recu, 2);
    }

    /// <summary>Change is measured against Montant (what is actually being applied to the
    /// sale), not the amount still owed - the cashier may only be collecting part of it.</summary>
    private void UpdateMonnaieARendre()
    {
        if (MontantRecuPanel.Visibility != Visibility.Visible) return;

        if (!Money.TryParse(MontantRecuBox.Text, out decimal recu) || recu <= 0
            || !Money.TryParse(MontantBox.Text, out decimal montant))
        {
            MonnaieARendreText.Text = string.Empty;
            return;
        }

        var difference = recu - montant;
        MonnaieARendreText.Foreground = (Brush)FindResource(difference < 0 ? "Danger" : "Success");
        MonnaieARendreText.Text = difference < 0
            ? $"Il manque {Money.Format(-difference)}"
            : $"Monnaie à rendre : {Money.Format(difference)}";
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
