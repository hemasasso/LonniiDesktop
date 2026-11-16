using System.Security.Cryptography;
using System.Text;

namespace Lonnii.Shared.Security;

/// <summary>
/// Turns several pieces of a computer's identity into one opaque fingerprint. Each computer that
/// reports it (the desktop client's DeviceIdentity, the host API's HostMachineIdentity) gathers the
/// Windows values itself and hashes them here, so the hashing is the same everywhere while the
/// gathering stays next to the registry code that already exists on each side.
///
/// <para>
/// The ordinary device id is built from Windows' MachineGuid alone, so copying that one value makes
/// another computer look like the same machine. This mixes in things that value does not carry -
/// the Windows product id, the install date, the system drive's serial - so a copy on different
/// hardware produces a different signature, which our server can see. It is a fingerprint, not a
/// secret: it only has to differ between machines, and reveal nothing about the customer's computer,
/// which the hash ensures.
/// </para>
/// </summary>
public static class HardwareSignature
{
    private const string Version = "lonnii-hw-v1";

    /// <summary>
    /// The signature for the non-empty <paramref name="parts"/>, or null when none are known (so a
    /// machine that could read nothing reports nothing rather than a signature every such machine shares).
    /// </summary>
    public static string? Of(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).ToArray();
        if (kept.Length == 0) return null;

        var payload = Version + "|" + string.Join("|", kept);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
