using System.Security.Cryptography;
using System.Text;

namespace Lonnii.Api.Features.Backup;

/// <summary>
/// The secret a bound machine proves itself with when it talks to the backup endpoints.
///
/// <para>
/// Derived, not stored: an HMAC of the workspace and device ids under the server's own key.
/// The licence server therefore needs no new column on the live <c>devices</c> table, the same
/// machine gets the same token every time it activates, and revoking a machine needs nothing
/// extra - the endpoints check the device row on every call, so a revoked token is dead the
/// moment <c>revoked_at</c> is set.
/// </para>
/// <para>
/// Why not just accept the workspace id and device id, as licence refresh does? Refresh hands
/// back settings. These endpoints hand back <em>every sale the shop ever made</em>, and the
/// device id is a hardware fingerprint, not a secret.
/// </para>
/// </summary>
public sealed class BackupTokens(string secret)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes("lonnii-backup-token|" + secret);

    public string Create(string groupId, string deviceId)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{groupId}|{deviceId}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    public bool Verify(string groupId, string deviceId, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        var expected = Encoding.ASCII.GetBytes(Create(groupId, deviceId));
        var given = Encoding.ASCII.GetBytes(presented);

        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
