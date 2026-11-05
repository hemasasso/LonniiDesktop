using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lonnii.Shared.Security;

/// <summary>What the licence server vouches for about one espace.</summary>
/// <param name="Mode">local or online, as <see cref="DeploymentModes"/>.</param>
/// <param name="MustReconnectBy">The offline deadline; null for a local shop, which has none.</param>
/// <param name="Devices">The machines bound to the espace (device fingerprints), unrevoked.</param>
public sealed record LicenceClaims(
    string GroupId,
    string Mode,
    DateTime? MustReconnectBy,
    DateTime IssuedAt,
    IReadOnlyList<string> Devices,
    bool IsBlocked,
    string? BlockReason);

/// <summary>
/// The licence as the licence server signs it, so a shop's own host can enforce it without
/// trusting its own database. Everything in <c>lonnii.db</c> can be changed with a free SQLite
/// editor - the mode set to "local", the deadline pushed out, the device rows deleted - but a
/// changed licence no longer carries a valid signature, and the host only believes a signed one.
///
/// <para>Signed with a key of its own, held only by the licence server: not the repair/update
/// key, so a server break-in can at worst hand out licences, never ship code to every shop.</para>
/// <para>Format: base64url(json) "." base64url(ECDSA P-256 / SHA-256 signature).</para>
/// </summary>
public static class LicenceToken
{
    /// <summary>The public half of the licence key (SubjectPublicKeyInfo, base64). Made with
    /// <c>lonnii-setup licence-keygen</c>; the private half lives on the licence server only.</summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEep+AaENKdaAWq3jP61Ahf5Uq6pGPgRAOq0tZwTZhPE2CkOqO9yAgmHNp/x2arr3e3C4Kp3qwbPgpCFmvnp471Q==";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Signs <paramref name="claims"/>. Runs on the licence server only.</summary>
    public static string Issue(byte[] privateKeyPkcs8, LicenceClaims claims)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims, Json);
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{Base64Url(payload)}.{Base64Url(signature)}";
    }

    /// <summary>The claims when <paramref name="token"/> was signed by <paramref name="publicKey"/>
    /// (by default <see cref="PublicKey"/>), otherwise null - malformed, altered or foreign alike.</summary>
    public static LicenceClaims? Verify(string? token, string? publicKey = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return null;

        try
        {
            var payload = FromBase64Url(token[..dot]);
            var signature = FromBase64Url(token[(dot + 1)..]);

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey ?? PublicKey), out _);
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return null;

            return JsonSerializer.Deserialize<LicenceClaims>(payload, Json);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
