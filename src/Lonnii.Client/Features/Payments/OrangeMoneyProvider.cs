using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Lonnii.Client.Features.Payments;

/// <summary>
/// Direct Orange Money Burkina Faso integration — no aggregator. The customer dials
/// *144*4*6# on their phone to get a one-time code (OTP), hands the code and their
/// number to the cashier, who submits both here. Orange answers immediately.
///
/// Credentials needed: api_username, api_password (secret), merchant_msisdn, and a
/// prod flag ("1" = production, anything else = test/sandbox).
///
/// API endpoint (test):  https://testom.orange.bf:9008/payment
/// API endpoint (prod):  https://apiom.orange.bf
/// Protocol: HTTP POST, Content-Type text/xml; charset=utf-8 — credentials live in the
/// XML body, not in headers.
/// </summary>
public sealed class OrangeMoneyProvider : IPaymentProvider
{
    private const string TestUrl = "https://testom.orange.bf:9008/payment";
    private const string ProdUrl = "https://apiom.orange.bf";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public PaymentProviderInfo Info =>
        PaymentProviderRegistry.Planned.First(p => p.Id == PaymentProviderIds.OrangeMoney);

    public bool IsConfigured(PaymentProviderSettings account) =>
        account.Values.ContainsKey("api_username")
        && account.Values.ContainsKey("api_password")
        && account.Values.ContainsKey("merchant_msisdn");

    public async Task<PaymentOutcome> ChargeAsync(
        PaymentRequest request, PaymentProviderSettings account, CancellationToken ct = default)
    {
        var username       = account.Values.GetValueOrDefault("api_username")   ?? string.Empty;
        var rawPassword    = account.Values.GetValueOrDefault("api_password")   ?? string.Empty;
        var password       = PaymentSettingsStore.Unprotect(rawPassword);
        var merchantMsisdn = account.Values.GetValueOrDefault("merchant_msisdn") ?? string.Empty;
        var isProd         = account.Values.GetValueOrDefault("prod") == "1";

        var phone  = NormalizePhone(request.CustomerValues?.GetValueOrDefault(CustomerInputKeys.Phone) ?? string.Empty);
        var otp    = request.CustomerValues?.GetValueOrDefault(CustomerInputKeys.Otp) ?? string.Empty;
        var amount = (int)Math.Round(request.Amount);
        var extId  = Guid.NewGuid().ToString("N")[..12]; // short unique id per transaction

        var xml = BuildXml(username, password, merchantMsisdn, phone, amount, request.Reference, otp, extId);
        var url = isProd ? ProdUrl : TestUrl;

        using var msg = new HttpRequestMessage(HttpMethod.Post, url);
        msg.Content = new StringContent(xml, Encoding.UTF8, "text/xml");
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };

        try
        {
            using var resp = await Http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            // Orange Money wraps the XML in a root element if it isn't already present.
            var wrapped = body.TrimStart().StartsWith('<') ? body : $"<response>{body}</response>";
            var doc = XDocument.Parse(wrapped);
            var root = doc.Root ?? doc.Element("response") ?? doc.Element("COMMAND");

            var status  = (string?)root?.Element("status") ?? "";
            var message = (string?)root?.Element("message") ?? "";
            var transId = (string?)root?.Element("transID") ?? "";

            // Orange Money returns "200" for success.
            if (status == "200")
                return new PaymentOutcome(true, TransactionId: transId.Length > 0 ? transId : null);

            return new PaymentOutcome(false, Error: message.Length > 0 ? message : $"Statut {status}");
        }
        catch (OperationCanceledException)
        {
            return new PaymentOutcome(false, Error: "Délai dépassé — vérifiez la connexion Internet.");
        }
        catch (HttpRequestException ex)
        {
            var netMsg = ex.InnerException?.Message ?? ex.Message;
            return netMsg.Contains("connect", StringComparison.OrdinalIgnoreCase)
                   || netMsg.Contains("refused", StringComparison.OrdinalIgnoreCase)
                   || netMsg.Contains("respond", StringComparison.OrdinalIgnoreCase)
                ? new PaymentOutcome(false, Error: "Impossible de joindre le serveur Orange Money — vérifiez la connexion Internet.")
                : new PaymentOutcome(false, Error: $"Erreur réseau : {ex.StatusCode?.ToString() ?? "connexion échouée"}.");
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return new PaymentOutcome(false, Error: "Réponse inattendue du serveur Orange Money.");
        }
    }

    private static string BuildXml(
        string username, string password, string merchantMsisdn,
        string customerMsisdn, int amount, string reference, string otp, string extId)
    {
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("COMMAND",
                new XElement("api_username",    username),
                new XElement("api_password",    password),
                new XElement("merchant_msisdn", merchantMsisdn),
                new XElement("customer_msisdn", customerMsisdn),
                new XElement("amount",          amount),
                new XElement("reference_number", reference),
                new XElement("otp",             otp),
                new XElement("ext_txn_id",      extId),
                new XElement("PROVIDER",        "101"),
                new XElement("PROVIDER2",       "101"),
                new XElement("PAYID",           "12"),
                new XElement("PAYID2",          "12"),
                new XElement("TYPE",            "OMPREQ")
            )
        ).ToString(SaveOptions.DisableFormatting);
    }

    private static string NormalizePhone(string raw)
    {
        raw = raw.Trim().TrimStart('+');
        if (raw.StartsWith("226")) return raw;
        raw = raw.TrimStart('0');
        return "226" + raw;
    }
}
