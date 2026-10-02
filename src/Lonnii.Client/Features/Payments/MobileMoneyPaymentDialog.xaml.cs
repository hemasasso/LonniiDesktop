using System.Windows;
using Lonnii.Client.Features.CustomerDisplay;

namespace Lonnii.Client.Features.Payments;

/// <summary>
/// Shown at the till when the cashier chooses Mobile Money. Collects the customer's phone
/// number and (for Orange Money) the OTP they just generated on their phone, calls the
/// LigdiCash provider, and returns <c>true</c> only when the charge was accepted.
/// </summary>
public partial class MobileMoneyPaymentDialog : Window
{
    private readonly IReadOnlyList<PaymentProviderSettings> _accounts;
    private readonly IPaymentProvider _provider;
    private readonly decimal _total;
    private readonly string _reference;

    /// <param name="reference">Sale reference tied to this charge (prevents double-charging on retry).</param>
    public MobileMoneyPaymentDialog(
        decimal total, IReadOnlyList<PaymentProviderSettings> accounts,
        IPaymentProvider provider, string reference, Window owner)
    {
        _total     = total;
        _accounts  = accounts;
        _provider  = provider;
        _reference = reference;
        Owner      = owner;
        InitializeComponent();
        Build();
    }

    private void Build()
    {
        AmountLabel.Text = Money.Format(_total);

        if (_accounts.Count > 1)
        {
            AccountPanel.Visibility = Visibility.Visible;
            foreach (var a in _accounts)
                AccountCombo.Items.Add(a.Label);
            AccountCombo.SelectedIndex = 0;
        }

        UpdateOtpSection();
        AccountCombo.SelectionChanged += (_, _) => UpdateOtpSection();
        PhoneBox.Focus();
    }

    private PaymentProviderSettings SelectedAccount =>
        _accounts.Count == 1 ? _accounts[0] : _accounts[AccountCombo.SelectedIndex];

    private bool IsOrange =>
        SelectedAccount.Values.TryGetValue("operator_id", out var id) && id == "11";

    private void UpdateOtpSection()
    {
        if (IsOrange)
        {
            OtpPanel.Visibility   = Visibility.Visible;
            OtpHintRun.Text       = "— Orange Money";
            OtpInstruction.Text   = "Le client compose *144*4*6# sur son téléphone pour obtenir le code.";
        }
        else
        {
            OtpPanel.Visibility   = Visibility.Visible;
            OtpHintRun.Text       = "— Moov Money (laisser vide)";
            OtpInstruction.Text   = "Le client valide directement sur son téléphone. Laissez ce champ vide.";
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text         = message;
        ErrorBorder.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBorder.Visibility = Visibility.Collapsed;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void Pay_Click(object sender, RoutedEventArgs e)
    {
        HideError();

        var phone = PhoneBox.Text.Trim().Replace(" ", "");
        if (phone.Length == 0)
        {
            ShowError("Entrez le numéro de téléphone du client.");
            PhoneBox.Focus();
            return;
        }

        if (IsOrange && OtpBox.Password.Trim().Length == 0)
        {
            ShowError("Entrez le code OTP que le client a reçu sur son téléphone.");
            OtpBox.Focus();
            return;
        }

        PayButton.IsEnabled    = false;
        CancelButton.IsEnabled = false;
        PayButton.Content      = "Traitement…";

        var values = new Dictionary<string, string>
        {
            [CustomerInputKeys.Phone] = phone,
            [CustomerInputKeys.Otp]   = OtpBox.Password.Trim(),
        };

        var request = new PaymentRequest(_total, "XOF", _reference, values);
        var account = SelectedAccount;

        PaymentOutcome outcome;
        try
        {
            outcome = await _provider.ChargeAsync(request, account);
        }
        catch (Exception ex)
        {
            outcome = new PaymentOutcome(false, Error: ex.Message);
        }

        if (outcome.Success)
        {
            // Show green confirmation on the customer screen while the sale is being recorded.
            CustomerDisplayService.Instance.ShowPaymentConfirmed(_total, account.Label);
            DialogResult = true;
        }
        else
        {
            ShowError(outcome.Error ?? "Paiement refusé.");
            PayButton.IsEnabled    = true;
            CancelButton.IsEnabled = true;
            PayButton.Content      = "Réessayer";
        }
    }
}
