using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lonnii.Client.Features.Payments;

/// <summary>
/// The places a card or wallet payment will plug in. Nothing is wired yet: this is the shape
/// a provider has to fit, a registry to put it in, and a settings store - so adding "Orange
/// Money" or "Apple Pay" later means one class implementing <see cref="IPaymentProvider"/> and
/// one <see cref="PaymentProviderRegistry.Register"/> call, with no change to the till or to
/// Paramètres.
/// </summary>
public static class PaymentProviderIds
{
    public const string MobileMoney = "mobile_money";

    /// <summary>Direct Orange Money Burkina Faso, without an aggregator.</summary>
    public const string OrangeMoney = "orange_money";

    /// <summary>Debit and credit cards, whichever network (Visa, Mastercard, ...).</summary>
    public const string Card = "card";

    public const string GooglePay = "google_pay";
    public const string ApplePay = "apple_pay";
    public const string PayPal = "paypal";
}

/// <summary>One setting a provider needs, e.g. a merchant id or an API key.</summary>
public sealed record PaymentField(string Key, string Label, bool Secret = false, string? Hint = null);

/// <summary>
/// Something the cashier collects from the customer at the till for each payment - not a shop
/// setting. For Burkina Faso's Orange Money that is the customer's number and the one-time code
/// they just generated on their phone.
/// </summary>
/// <param name="Secret">Shown masked; the value is used for this one charge, never stored or logged.</param>
public sealed record CustomerInput(string Key, string Label, bool Secret = false, string? Hint = null);

public static class CustomerInputKeys
{
    public const string Phone = "phone";

    /// <summary>The one-time code the customer generates on their own phone (e.g. Orange Money's USSD code).</summary>
    public const string Otp = "otp";
}

/// <summary>What Paramètres shows about a provider, what it asks the shop to fill in, and what the till asks the customer for.</summary>
/// <param name="CustomerInputs">Empty for a provider that needs nothing typed at the till (a card terminal, a wallet on the customer's own device).</param>
public sealed record PaymentProviderInfo(
    string Id, string Name, string Description, IReadOnlyList<PaymentField> Fields,
    IReadOnlyList<CustomerInput>? CustomerInputs = null)
{
    public IReadOnlyList<CustomerInput> Inputs => CustomerInputs ?? [];
}

/// <param name="Amount">In the shop's currency, as the till shows it.</param>
/// <param name="Reference">Ties the charge to the sale (e.g. its V2026-00042 number), so a retry cannot charge twice.</param>
/// <param name="CustomerValues">What the cashier collected for <see cref="PaymentProviderInfo.Inputs"/>, by key (<see cref="CustomerInputKeys"/>).</param>
public sealed record PaymentRequest(
    decimal Amount, string Currency, string Reference, IReadOnlyDictionary<string, string>? CustomerValues = null);

public sealed record PaymentOutcome(bool Success, string? TransactionId = null, string? Error = null);

/// <summary>A way of taking a payment that is not cash. Implement it, register it, switch it on in Paramètres.</summary>
public interface IPaymentProvider
{
    PaymentProviderInfo Info { get; }

    /// <summary>Whether the shop has filled in everything this provider needs for this account.</summary>
    bool IsConfigured(PaymentProviderSettings account);

    /// <summary>Takes the payment through <paramref name="account"/> - one of the shop's accounts of this provider.</summary>
    Task<PaymentOutcome> ChargeAsync(PaymentRequest request, PaymentProviderSettings account, CancellationToken ct = default);
}

/// <summary>The four planned providers and any implementation registered for them.</summary>
public static class PaymentProviderRegistry
{
    /// <summary>
    /// Flip to true once at least one provider is wired: it reveals Paramètres → Moyens de
    /// paiement. Kept off so a shop is not shown switches that do nothing.
    /// </summary>
    public static bool SettingsVisible { get; } = true;

    public static readonly IReadOnlyList<PaymentProviderInfo> Planned =
    [
        new(PaymentProviderIds.MobileMoney, "Mobile Money (LigdiCash)",
            "Orange Money BF et Moov Money BF via l'agrégateur LigdiCash.",
            [
                new("operator_id",  "Opérateur",         Hint: "11 = Orange Money BF · 12 = Moov Money BF"),
                new("api_key",      "Apikey LigdiCash"),
                new("api_token",    "Token API LigdiCash", Secret: true),
                new("store_name",   "Nom du compte",      Hint: "ex. Orange Money Caisse 1"),
            ],
            // Burkina Faso: the customer dials *144*4*6# on their phone to get a one-time code,
            // the cashier enters the phone number and that code, and LigdiCash / Orange answer
            // immediately. For Moov Money the customer validates on their phone — the OTP stays
            // empty. Neither the phone number nor the OTP is stored or logged.
            CustomerInputs:
            [
                new(CustomerInputKeys.Phone, "Numéro du client", Hint: "ex. 70 00 00 00"),
                new(CustomerInputKeys.Otp, "Code OTP", Secret: true,
                    Hint: "Orange : composez *144*4*6# · Moov : laissez vide"),
            ]),
        new(PaymentProviderIds.OrangeMoney, "Orange Money (direct)",
            "Orange Money Burkina Faso directement, sans agrégateur — évite la commission LigdiCash.",
            [
                new("api_username",    "Identifiant API"),
                new("api_password",    "Mot de passe API",  Secret: true),
                new("merchant_msisdn", "Numéro marchand",   Hint: "ex. 22670000000"),
                new("prod",            "Environnement",     Hint: "1 = production · 0 = test"),
            ],
            CustomerInputs:
            [
                new(CustomerInputKeys.Phone, "Numéro du client", Hint: "ex. 70 00 00 00"),
                new(CustomerInputKeys.Otp, "Code OTP", Secret: true,
                    Hint: "Le client compose *144*4*6# sur son téléphone"),
            ]),
        new(PaymentProviderIds.Card, "Carte bancaire (débit et crédit)",
            "Visa, Mastercard et autres réseaux, via un prestataire de paiement ou un terminal.",
            [
                new("processor", "Prestataire", Hint: "ex. Stripe, CinetPay"),
                new("public_key", "Clé publique"),
                new("secret_key", "Clé secrète", Secret: true),
            ]),
        new(PaymentProviderIds.GooglePay, "Google Pay",
            "Paiement par portefeuille Google.",
            [
                new("merchant_id", "Identifiant marchand Google"),
                new("gateway", "Passerelle de paiement"),
                new("gateway_merchant_id", "Identifiant marchand de la passerelle"),
            ]),
        new(PaymentProviderIds.ApplePay, "Apple Pay",
            "Paiement par portefeuille Apple.",
            [
                new("merchant_id", "Identifiant marchand Apple"),
                new("display_name", "Nom affiché au client"),
            ]),
        new(PaymentProviderIds.PayPal, "PayPal",
            "Paiement par compte PayPal ou carte via PayPal.",
            [
                new("environment", "Environnement", Hint: "sandbox (test) ou live (réel)"),
                new("client_id", "Client ID"),
                new("client_secret", "Client Secret", Secret: true),
            ]),
    ];

    private static readonly Dictionary<string, IPaymentProvider> Implemented = [];

    public static void Register(IPaymentProvider provider) => Implemented[provider.Info.Id] = provider;

    public static IPaymentProvider? Get(string id) => Implemented.GetValueOrDefault(id);

    public static bool IsImplemented(string id) => Implemented.ContainsKey(id);

    /// <summary>The shop's switched-on accounts of one provider - what the till will offer.</summary>
    public static IReadOnlyList<PaymentProviderSettings> EnabledAccounts(string providerId) =>
        PaymentSettingsStore.Load().GetValueOrDefault(providerId)?.Where(a => a.Enabled).ToList() ?? [];
}

/// <summary>
/// One account of a provider: whether it is on, and the values it asked for. A shop can hold
/// several per provider - an Orange Money and an MTN MoMo number, two bank terminals - each a
/// separate account with its own label, so nothing is copied or overwritten.
/// </summary>
public sealed class PaymentProviderSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>What the cashier will see to tell accounts of the same kind apart, e.g. "Orange Money caisse 1".</summary>
    public string Label { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>Field key → value, per account. Secret fields hold a DPAPI-protected string (see <see cref="PaymentSettingsStore"/>), never the clear text.</summary>
    public Dictionary<string, string> Values { get; set; } = [];
}

/// <summary>
/// Per-machine store for the shop's <see cref="PaymentProviderSettings"/> accounts, in
/// <c>%AppData%\Lonnii\payment-providers.json</c>. Per machine on purpose: these are merchant
/// keys, and the shared database is readable by every till and by the web app. Secret values
/// are encrypted for the current Windows user, so the file is useless if copied elsewhere.
/// </summary>
public static class PaymentSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lonnii", "payment-providers.json");

    /// <summary>Provider id → its accounts.</summary>
    public static Dictionary<string, List<PaymentProviderSettings>> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, List<PaymentProviderSettings>>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    public static void Save(Dictionary<string, List<PaymentProviderSettings>> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string Protect(string clear) => clear.Length == 0
        ? string.Empty
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(clear), null, DataProtectionScope.CurrentUser));

    /// <summary>The clear text of a protected value, or empty if it cannot be read (another user's file).</summary>
    public static string Unprotect(string protectedValue)
    {
        if (protectedValue.Length == 0) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}
