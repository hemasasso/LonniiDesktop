using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Lonnii.Shared.Security;

/// <summary>What checking a repair code found.</summary>
public enum RepairCodeResult
{
    Valid,
    Malformed,
    BadSignature,
    WrongDevice,
    WrongAddress,
    Expired,
}

/// <summary>
/// A code we issue so one till may be pointed at one server address it could not find on its
/// own. Signed with a private key that stays on our machine; the till only holds the public
/// half, so it can check a code but never make one - which is the whole point, as the
/// alternative (a free address field) lets a shop route its tills over a VPN and skip the
/// online plan.
///
/// <para>
/// The code names the till (<see cref="DeviceTag"/>), the address and a last day to enter it.
/// The till stores the code beside the address and re-checks it on every start, so editing
/// the settings file to another address, or copying the file to another till, fails the same
/// check. The expiry only applies when the code is first entered.
/// </para>
/// </summary>
public static class RepairCode
{
    /// <summary>
    /// The public half of our signing key, as base64 SubjectPublicKeyInfo. Made with
    /// <c>lonnii-setup repair-keygen</c>; the private half never enters the repository.
    /// </summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEkrj97cqUIvGj4lwR+JqMYVFvseU7TuMQ3e/H7V7SudrCxrD2+LUPGYpG831QXJF206SikUP7SLuRfPE/ZRyLDQ==";

    private const byte Version = 1;
    private const int TagLength = 8;
    private const int PayloadLength = 1 + TagLength + TagLength + 4;
    private const int SignatureLength = 64;

    /// <summary>
    /// The short id a shop reads out over the phone so we can issue a code for this till:
    /// sixteen hex digits, a hash of the device fingerprint so the fingerprint itself is
    /// never spoken or typed.
    /// </summary>
    public static string DeviceTag(string deviceId)
    {
        var hex = Convert.ToHexString(Hash("dev|" + deviceId)[..TagLength]);
        return string.Join('-', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>Issues a code. Runs on our machine only, with the private key.</summary>
    public static string Issue(byte[] privateKeyPkcs8, string deviceTag, string address, DateTimeOffset expiresAt)
    {
        var payload = new byte[PayloadLength];
        payload[0] = Version;
        ParseTag(deviceTag).CopyTo(payload, 1);
        Hash("addr|" + NormalizeAddress(address))[..TagLength].CopyTo(payload, 1 + TagLength);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1 + 2 * TagLength), (uint)expiresAt.ToUnixTimeSeconds());

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return Format(Base32Encode([.. payload, .. signature]));
    }

    /// <summary>
    /// Checks a code for this till and address. <paramref name="checkExpiry"/> is true when a
    /// person has just typed it and false when re-checking one already accepted.
    /// </summary>
    public static RepairCodeResult Check(
        string? code, string deviceId, string address, DateTimeOffset now, bool checkExpiry,
        string publicKey = PublicKey)
    {
        if (string.IsNullOrWhiteSpace(code)) return RepairCodeResult.Malformed;

        byte[] raw;
        try { raw = Base32Decode(code); }
        catch (FormatException) { return RepairCodeResult.Malformed; }

        if (raw.Length != PayloadLength + SignatureLength || raw[0] != Version) return RepairCodeResult.Malformed;

        var payload = raw[..PayloadLength];
        var signature = raw[PayloadLength..];

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return RepairCodeResult.BadSignature;
        }
        catch (CryptographicException)
        {
            return RepairCodeResult.BadSignature;
        }

        if (!payload.AsSpan(1, TagLength).SequenceEqual(Hash("dev|" + deviceId)[..TagLength]))
            return RepairCodeResult.WrongDevice;

        if (!payload.AsSpan(1 + TagLength, TagLength).SequenceEqual(Hash("addr|" + NormalizeAddress(address))[..TagLength]))
            return RepairCodeResult.WrongAddress;

        var expiry = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(1 + 2 * TagLength));
        if (checkExpiry && now.ToUnixTimeSeconds() > expiry) return RepairCodeResult.Expired;

        return RepairCodeResult.Valid;
    }

    /// <summary>One spelling per address, so "HTTP://Host:5280/" and "host:5280" are the same one.</summary>
    public static string NormalizeAddress(string address)
    {
        var text = address.Trim().ToLowerInvariant();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        return text.TrimEnd('/');
    }

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static byte[] ParseTag(string tag)
    {
        var hex = new string(tag.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != TagLength * 2) throw new ArgumentException("Identifiant du poste invalide.", nameof(tag));
        return Convert.FromHexString(hex);
    }

    // --- Base32 (RFC 4648, no padding): letters and digits only, readable over the phone ---

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string text)
    {
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException();
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
            buffer &= (1 << bits) - 1;
        }
        return [.. bytes];
    }

    private static string Format(string code) =>
        string.Join('-', Enumerable.Range(0, (code.Length + 5) / 6).Select(i => code.Substring(i * 6, Math.Min(6, code.Length - i * 6))));
}
