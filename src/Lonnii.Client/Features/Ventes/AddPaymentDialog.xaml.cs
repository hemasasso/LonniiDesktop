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

        // One box: what the customer hands over. Less than owed = paiement partiel; more, in cash,
        // = change to give back (or an avoir). Amounts read "1 000" while typed (see MoneyInput).
        MoneyInput.GroupAsYouType(MontantBox);
        SubtitleText.Text = $"Vente {numeroVente} — restant à payer : {Money.Format(montantRestant)}";
        MontantBox.Text = string.Empty;

        MontantBox.LostFocus += (_, _) =>
        {
            if (Money.TryParse(MontantBox.Text, out decimal montant) && montant > 0)
                MontantBox.Text = Money.FormatPlain(montant);
        };
        MontantBox.TextChanged += (_, _) => UpdateHints();

        // One "Mobile Money" choice only, as at checkout (VentesView): Orange Money stays hidden.
        PaymentOrangeButton.Visibility = Visibility.Collapsed;

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

        PaymentCashButton.Style   = _modePaiement == "cash"         ? primary : secondary;
        PaymentMobileButton.Style = _modePaiement == "mobile_money" ? primary : secondary;
        PaymentOrangeButton.Style = _modePaiement == "orange_money" ? primary : secondary;
        PaymentCarteButton.Style  = _modePaiement == "carte"        ? primary : secondary;
        UpdateHints();
    }

    /// <summary>Short: what will still be owed. Over, in cash: the change, or the avoir if kept.
    /// Over by Mobile Money or card: only what is owed is recorded.</summary>
    private void UpdateHints()
    {
        var typed = Money.TryParse(MontantBox.Text, out decimal t) ? t : 0m;

        var insufficient = typed > 0 && typed < _montantRestant;
        InsuffisantText.Visibility = insufficient || (typed > _montantRestant && _modePaiement != "cash")
            ? Visibility.Visible : Visibility.Collapsed;
        InsuffisantText.SetResourceReference(TextBlock.ForegroundProperty, insufficient ? "Warning" : "TextSecondary");
        InsuffisantText.Text = insufficient
            ? $"Paiement partiel - reste à payer : {Money.Format(_montantRestant - typed)}"
            : $"Montant supérieur au restant - {Money.Format(_montantRestant)} sera encaissé.";

        var change = _modePaiement == "cash" ? typed - _montantRestant : 0m;
        MontantRecuPanel.Visibility = change > 0 ? Visibility.Visible : Visibility.Collapsed;
        var canAvoir = change > 0 && _canCreateAvoir;
        MonnaieAvoirCheck.Visibility = canAvoir ? Visibility.Visible : Visibility.Collapsed;
        if (!canAvoir) MonnaieAvoirCheck.IsChecked = false;
        if (change <= 0) return;

        var asAvoir = MonnaieAvoirCheck.IsChecked == true;
        MonnaieARendreText.Foreground = (Brush)FindResource(asAvoir ? "Warning" : "Success");
        MonnaieARendreText.Text = asAvoir ? $"Avoir : {Money.Format(change)}" : $"Monnaie à rendre : {Money.Format(change)}";
    }

    private void MonnaieAvoir_Changed(object sender, RoutedEventArgs e) => UpdateHints();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!Money.TryParse(MontantBox.Text, out decimal montant) || montant <= 0)
        {
            ErrorText.Text = "Indiquez un montant valide.";
            ErrorPanel.Visibility = Visibility.Visible;
            return;
        }

        // Over what is owed: the change goes back, so only the balance is recorded - unless it is
        // kept as an avoir, when the whole amount is (the server makes the excess the avoir).
        if (montant > _montantRestant && !(_modePaiement == "cash" && MonnaieAvoirCheck.IsChecked == true))
            montant = _montantRestant;

        Montant = montant;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
