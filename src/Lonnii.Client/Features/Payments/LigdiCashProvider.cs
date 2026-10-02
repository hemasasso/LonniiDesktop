using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lonnii.Client.Features.Payments;

/// <summary>
/// LigdiCash aggregator: handles Orange Money BF (operator_id = 11) and Moov Money BF
/// (operator_id = 12) through a single POST endpoint. For Orange the customer generates a
/// one-time code on their phone via *144*4*6# then hands it to the cashier. For Moov the
/// customer validates on their own phone — the OTP field is left empty. No OTP value is
/// stored or logged; it lives only in the HTTP request body for that one charge.
/// </summary>
public sealed class LigdiCashProvider : IPaymentProvider
{
    private const string ApiUrl = "https://app.ligdicash.com/pay/v01/straight/checkout-invoice/create";

    // One client, shared: LigdiCash answers immediately (no polling), 30 s is ample.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public PaymentProviderInfo Info =>
        PaymentProviderRegistry.Planned.First(p => p.Id == PaymentProviderIds.MobileMoney);

    public bool IsConfigured(PaymentProviderSettings account) =>
        account.Values.ContainsKey("api_key")
        && account.Values.ContainsKey("api_token")
        && account.Values.ContainsKey("operator_id");

    public async Task<PaymentOutcome> ChargeAsync(
        PaymentRequest request, PaymentProviderSettings account, CancellationToken ct = default)
    {
        var apiKey   = account.Values.GetValueOrDefault("api_key")   ?? string.Empty;
        var rawToken = account.Values.GetValueOrDefault("api_token") ?? string.Empty;
        var apiToken = PaymentSettingsStore.Unprotect(rawToken);
        var storeName   = account.Values.GetValueOrDefault("store_name") ?? "Lonnii";
        var operatorId  = int.TryParse(account.Values.GetValueOrDefault("operator_id"), out var op) ? op : 11;

        var phone = NormalizePhone(request.CustomerValues?.GetValueOrDefault(CustomerInputKeys.Phone) ?? string.Empty);
        var otp   = request.CustomerValues?.GetValueOrDefault(CustomerInputKeys.Otp) ?? string.Empty;
        var amount = (int)Math.Round(request.Amount); // XOF has no decimals

        var body = new
        {
            commande = new
            {
                invoice = new
                {
                    items = new[]
                    {
                        new
                        {
                            name        = storeName,
                            description = request.Reference,
                            quantity    = 1,
                            unit_price  = amount,
                            total_price = amount,
                        }
                    },
                    total_amount = amount,
                    devise       = "XOF",
                    description  = $"{storeName} — {request.Reference}",
                    customer     = phone,
                    otp          = otp,
                    operator_id  = operatorId,
                },
                return_url   = "",
                cancel_url   = "",
                callback_url = "",
            }
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        msg.Headers.TryAddWithoutValidation("Apikey",        apiKey);
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiToken}");
        msg.Headers.TryAddWithoutValidation("Accept",        "application/json");
        msg.Content = JsonContent.Create(body);

        try
        {
            using var resp = await Http.SendAsync(msg, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var code = root.TryGetProperty("response_code", out var rc) ? rc.GetString() : null;
            if (code == "00")
            {
                var token = root.TryGetProperty("token", out var tk) ? tk.GetString() : null;
                return new PaymentOutcome(true, TransactionId: token);
            }

            // LigdiCash puts the human message in response_text or message.
            var error = root.TryGetProperty("response_text", out var rt) ? rt.GetString()
                      : root.TryGetProperty("message",        out var m)  ? m.GetString()
                      : null;
            return new PaymentOutcome(false, Error: error ?? $"Code {code ?? "inconnu"}");
        }
        catch (OperationCanceledException)
        {
            return new PaymentOutcome(false, Error: "Délai dépassé — vérifiez la connexion Internet.");
        }
        catch (HttpRequestException ex)
        {
            return new PaymentOutcome(false, Error: ex.Message);
        }
        catch (JsonException)
        {
            return new PaymentOutcome(false, Error: "Réponse inattendue du serveur LigdiCash.");
        }
    }

    /// <summary>Ensures the number has the BF country prefix (226). Strips a leading zero or plus.</summary>
    private static string NormalizePhone(string raw)
    {
        raw = raw.Trim().TrimStart('+');
        if (raw.StartsWith("226")) return raw;
        // "0XXXXXXXX" → strip the leading 0
        raw = raw.TrimStart('0');
        return "226" + raw;
    }
}
