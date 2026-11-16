using System.Security.Cryptography;
using System.Text;

namespace Lonnii.Api.Features.Infrastructure;

/// <summary>
/// A hash of the host's own program files, reported at each licence check-in so our server can tell
/// when they were changed without the version changing - the mark of a program patched to switch the
/// licence off (editing LicenceGuard in the DLL, say), which keeps the same version number so a swap
/// is not noticed as an update.
///
/// <para>
/// A published build's files are identical on every computer, so every honest host on one version
/// reports the same hash; a patched one differs. Our server only compares it, so what goes into it
/// can change without a migration. Detection only - it is never used to block, so a false positive
/// (an antivirus that rewrote a file) at worst shows a line to look into.
/// </para>
/// </summary>
public static class HostBinaries
{
    /// <summary>The files that carry the licence logic, so a change to any of them shows. Other files
    /// (native libraries, UI resources) are left out: they differ by install and would only add noise.</summary>
    private static readonly string[] Watched =
        ["Lonnii.Api.dll", "Lonnii.Data.dll", "Lonnii.Shared.dll"];

    /// <summary>The combined hash, computed once. Null when the files cannot be read (so nothing is
    /// reported rather than a hash that would look like a change).</summary>
    public static string? Hash { get; } = Compute();

    private static string? Compute()
    {
        try
        {
            using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var any = false;
            foreach (var name in Watched.OrderBy(n => n, StringComparer.Ordinal))
            {
                var path = Path.Combine(AppContext.BaseDirectory, name);
                if (!File.Exists(path)) continue;
                combined.AppendData(Encoding.UTF8.GetBytes(name + "|"));
                combined.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
                any = true;
            }
            return any ? Convert.ToHexString(combined.GetHashAndReset()).ToLowerInvariant() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
