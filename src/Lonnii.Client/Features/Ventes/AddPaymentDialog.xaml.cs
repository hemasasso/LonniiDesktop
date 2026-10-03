using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Features.Payments;

namespace Lonnii.Client.Features.Ventes;

/// <summary>Records a payment against an existing sale - settling a facture, or a further
/// instalment on a partial one. Mirrors Lonnii Business's payment modal (the amount defaults
/// to what remains, same as <c>setPaymentAmount(vente.montant_restant)</c> there).</summary>
public partial class AddPaymentDialog : Window
{
    private string _modePaiement = "cash";
    private readonly decimal _montantRestant;
    private readonly bool _canCreateAvoir;

    public decimal Montant { get; private set; }
    public string ModePaiement => _modePaiement;

    public AddPaymentDialog(string numeroVente, decimal montantRestant, bool canCreateAvoir = false)
    {
        _montantRestant = montantRestant;
        _canCreateAvoir = canCreateAvoir;
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
        MontantBox.TextChanged += (_, _) =>
        {
            UpdateInsuffisant();
            UpdateMonnaieARendre();
        };

        PaymentOrangeButton.Visibility = PaymentProviderRegistry.EnabledAccounts(PaymentProviderIds.OrangeMoney).Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        ApplyPaymentVisuals();
        Loaded += (_, _) => MontantBox.Focus();
    }

    /// <summary>Warns, in red, when the amount falls short of what is owed - it is still
    /// accepted, but recorded as a partial payment with the difference left to pay.</summary>
    private void UpdateInsuffisant()
    {
        var insufficient = Money.TryParse(MontantBox.Text, out decimal montant)
            && montant > 0 && montant < _montantRestant;
        InsuffisantText.Visibility = insufficient ? Visibility.Visible : Visibility.Collapsed;
        if (insufficient)
            InsuffisantText.Text = $"Montant insuffisant : il restera {Money.Format(_montantRestant - montant)} à payer (paiement partiel).";
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

        PaymentCashButton.Style   = _modePaiement == "cash"         ? primary : secondary;
        PaymentMobileButton.Style = _modePaiement == "mobile_money" ? primary : secondary;
        PaymentOrangeButton.Style = _modePaiement == "orange_money" ? primary : secondary;
        PaymentCarteButton.Style  = _modePaiement == "carte"        ? primary : secondary;

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
            MonnaieAvoirCheck.Visibility = Visibility.Collapsed;
            MonnaieAvoirCheck.IsChecked = false;
            return;
        }

        var difference = recu - montant;

        // Only what exceeds the whole remaining balance can be an avoir; the rest of the
        // change would still be settling the sale.
        var avoir = recu - _montantRestant;
        var canAvoir = difference > 0 && avoir > 0 && _canCreateAvoir;
        MonnaieAvoirCheck.Visibility = canAvoir ? Visibility.Visible : Visibility.Collapsed;
        if (!canAvoir) MonnaieAvoirCheck.IsChecked = false;

        var asAvoir = canAvoir && MonnaieAvoirCheck.IsChecked == true;
        MonnaieARendreText.Foreground = (Brush)FindResource(difference < 0 ? "Danger" : asAvoir ? "Warning" : "Success");
        MonnaieARendreText.Text = difference < 0
            ? $"Il manque {Money.Format(-difference)}"
            : asAvoir ? $"Avoir : {Money.Format(avoir)}"
            : $"Monnaie à rendre : {Money.Format(difference)}";
    }

    private void MonnaieAvoir_Changed(object sender, RoutedEventArgs e) => UpdateMonnaieARendre();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!Money.TryParse(MontantBox.Text, out decimal montant) || montant <= 0)
        {
            ErrorText.Text = "Indiquez un montant valide.";
            ErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        // Keeping the change as an avoir means recording everything handed over; the server
        // turns whatever exceeds the remaining balance into the avoir.
        if (MonnaieAvoirCheck.IsChecked == true && Money.TryParse(MontantRecuBox.Text, out decimal recu) && recu > montant)
            montant = recu;

        Montant = montant;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
