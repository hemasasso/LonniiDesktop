using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Lonnii.Client.Services;

/// <summary>
/// This machine's fingerprint, sent as <c>x-device-id</c> so the server can tell whether
/// the workspace is running on hardware it bound.
///
/// <para>
/// The two properties that matter: it must survive reinstalling Lonnii - otherwise a shop
/// would burn a slot every time it repaired an installation - and it must differ on another
/// shop's computer, which is what makes a copied installation visible.
/// </para>
/// <para>
/// Windows' MachineGuid gives both. It is written when Windows is installed and left alone
/// afterwards, so it outlives our installer while being unique to the machine. It is hashed
/// with a product-specific salt before it leaves here, so nothing identifying the customer's
/// computer is sent, and the value cannot be reused to correlate with anything else on it.
/// </para>
/// <para>
/// This is a fingerprint, not a secret. Someone who controls the machine can read and replay
/// it; cloning a machine bit for bit would clone this too. It raises copying from "copy the
/// folder" to "clone the computer", and obfuscation is what widens that gap - see the
/// licensing notes.
/// </para>
/// </summary>
public static class DeviceIdentity
{
    private const string Salt = "lonnii-device-v1";

    private static readonly string FallbackPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lonnii", "device-id");

    private static string? _cached;

    /// <summary>
    /// The fingerprint for this machine. Computed once per run: it cannot change while the
    /// application is open, and re-reading the registry on every request would be waste.
    /// </summary>
    public static string Current => _cached ??= Compute();

    /// <summary>A name for the device list, so a shop can tell its tills apart.</summary>
    public static string FriendlyName => Environment.MachineName;

    private static string Compute()
    {
        var source = MachineGuid() ?? StoredFallback();
        return Hash(source);
    }

    /// <summary>
    /// Windows' own machine identifier. Survives our installer; changes only when Windows
    /// itself is reinstalled, which is a new machine as far as a licence is concerned.
    /// </summary>
    private static string? MachineGuid()
    {
        try
        {
            using var key = RegistryKey
                .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");

            var value = key?.GetValue("MachineGuid") as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A locked-down machine can refuse this read. Better a weaker fingerprint than
            // a till that cannot open at all.
            return null;
        }
    }

    /// <summary>
    /// Used only when the registry cannot be read: a value generated once and kept beside
    /// the client's settings. Weaker, because wiping the profile changes it and costs the
    /// shop a slot - which is why it is the fallback rather than the primary.
    /// </summary>
    private static string StoredFallback()
    {
        try
        {
            if (File.Exists(FallbackPath))
            {
                var existing = File.ReadAllText(FallbackPath).Trim();
                if (!string.IsNullOrWhiteSpace(existing)) return existing;
            }

            var created = Guid.NewGuid().ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
            File.WriteAllText(FallbackPath, created);
            return created;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Last resort: stable for this session, so the app still runs even though the
            // machine will look new next time.
            return Environment.MachineName + "|" + Environment.UserName;
        }
    }

    private static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Salt + "|" + source)))
            .ToLowerInvariant();
}
