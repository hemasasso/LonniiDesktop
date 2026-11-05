using Lonnii.Shared.Security;

namespace Lonnii.Api.Features.Licensing;

/// <summary>
/// The licence server's half of <see cref="LicenceToken"/>: signs each refresh's answer with the
/// licence key, read from <c>Lonnii:LicenceSigningKeyFile</c> (default
/// <c>/etc/lonnii-licence-signing.key</c>, base64 PKCS#8 as <c>lonnii-setup licence-keygen</c> writes it).
///
/// <para>Missing key: refreshes still answer, unsigned, and say so in the log. Shops then run on
/// their 14-day window to fetch a signed licence, so a forgotten key file is noticed long before
/// anyone is locked - rather than locking every shop the day the server is deployed.</para>
/// </summary>
public sealed class LicenceSigner
{
    private readonly byte[]? _key;

    public LicenceSigner(IConfiguration configuration, ILogger<LicenceSigner> logger)
    {
        var path = configuration["Lonnii:LicenceSigningKeyFile"] ?? "/etc/lonnii-licence-signing.key";
        try
        {
            if (File.Exists(path)) _key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            logger.LogError("Clé de signature des licences illisible ({Path}) : {Message}", path, e.Message);
        }
    }

    /// <summary>Only on the licence server: a shop's host has no key, and signs nothing.</summary>
    public bool CanSign => _key is not null;

    public string? Sign(LicenceClaims claims) => _key is null ? null : LicenceToken.Issue(_key, claims);
}
