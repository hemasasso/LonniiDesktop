using System.Security.Cryptography;
using System.Text;

namespace Lonnii.Shared.Security;

/// <summary>
/// The notice of a new version that computers fetch from our server: which installer, its size and
/// its SHA-256. Signed with the same private key as <see cref="RepairCode"/>, which never leaves our
/// machine, so a computer installs a file only if we published it - not merely because the server
/// that handed it over says so. The installer runs with administrator rights, so this matters more
/// than anywhere else.
/// </summary>
public sealed record UpdateManifest(
    string Version,
    string FileName,
    long Size,
    string Sha256,
    DateTime PublishedAt,
    string? Notes,
    string Signature)
{
    /// <summary>The installers' own name, so nothing else in the folder can ever be served as one.</summary>
    public const string FilePrefix = "LonniiBusiness-Setup-";

    public static UpdateManifest Sign(
        byte[] privateKeyPkcs8, string version, string fileName, long size, string sha256,
        DateTime publishedAt, string? notes)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
        var signature = key.SignData(
            Payload(version, fileName, size, sha256), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new UpdateManifest(version, fileName, size, sha256.ToLowerInvariant(), publishedAt, notes,
            Convert.ToBase64String(signature));
    }

    /// <summary>True when we signed exactly this version, file, size and hash.</summary>
    public bool IsGenuine(string publicKey = RepairCode.PublicKey)
    {
        if (!System.Version.TryParse(Version, out _) || !IsInstallerName(FileName)) return false;

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(
                Payload(Version, FileName, Size, Sha256), Convert.FromBase64String(Signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True when this notice is for a version newer than <paramref name="installed"/>.</summary>
    public bool IsNewerThan(string? installed) =>
        System.Version.TryParse(Version, out var offered)
        && (!System.Version.TryParse(installed, out var current) || offered > current);

    /// <summary>A plain installer filename: no folder, nothing that could reach outside the updates folder.</summary>
    public static bool IsInstallerName(string? name) =>
        name is not null
        && name.StartsWith(FilePrefix, StringComparison.Ordinal)
        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')
        && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>The hash a downloaded file must have, as lower-case hex.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    // "update|" keeps a signed notice from ever being read as a repair or reset code, and back.
    private static byte[] Payload(string version, string fileName, long size, string sha256) =>
        Encoding.UTF8.GetBytes($"lonnii-update|1|{version}|{fileName}|{size}|{sha256.ToLowerInvariant()}");
}
