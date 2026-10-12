using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Lonnii.Shared.Security;

/// <summary>What checking a password reset code found.</summary>
public enum ResetCodeResult
{
    Valid,
    Malformed,
    BadSignature,
    WrongAccount,
    Expired,
}

/// <summary>
/// A code we issue so a shop owner who has forgotten their password can set a new one without
/// the internet - the emailed code needs the licence server, and a shop that runs offline, or was
/// never activated, cannot reach it. Signed with the same private key as <see cref="RepairCode"/>;
/// the host only holds the public half, so it can check a code but never make one.
///
/// <para>
/// The owner reads out an <see cref="AccountTag"/> shown on the forgotten-password screen. It is a
/// hash of the server's id, the account and the account's <em>current</em> password hash, so a code
/// works for that owner on that server only, and only once: the moment the password changes, the tag
/// changes and the code is worth nothing.
/// </para>
/// <para>
/// Layout, which the dashboard's <c>backend/services/resetCode.js</c> must match:
/// <c>version 0x52 (1) | account tag (8) | expiry unix seconds, uint32 BE (4)</c>, then the
/// ECDSA P-256 SHA-256 signature (64, IEEE P1363), base32 and grouped by six. The version byte and
/// length differ from a repair code's, so neither can stand in for the other.
/// </para>
/// </summary>
public static class ResetCode
{
    private const byte Version = 0x52;
    private const int TagLength = 8;
    private const int PayloadLength = 1 + TagLength + 4;
    private const int SignatureLength = 64;

    /// <summary>The account's tag on this server, as sixteen hex digits in groups of four.</summary>
    public static string AccountTag(string hostId, string userId, string? passwordHash)
    {
        var hex = Convert.ToHexString(RepairCode.Hash($"reset|{hostId}|{userId}|{passwordHash}")[..TagLength]);
        return string.Join('-', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>Issues a code. Runs on our side only, with the private key.</summary>
    public static string Issue(byte[] privateKeyPkcs8, string accountTag, DateTimeOffset expiresAt)
    {
        var payload = new byte[PayloadLength];
        payload[0] = Version;
        ParseTag(accountTag).CopyTo(payload, 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1 + TagLength), (uint)expiresAt.ToUnixTimeSeconds());

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return RepairCode.Format(RepairCode.Base32Encode([.. payload, .. signature]));
    }

    /// <summary>Checks a code against the tag this server computes for the account right now.</summary>
    public static ResetCodeResult Check(
        string? code, string accountTag, DateTimeOffset now, string publicKey = RepairCode.PublicKey)
    {
        if (string.IsNullOrWhiteSpace(code)) return ResetCodeResult.Malformed;

        byte[] raw;
        try { raw = RepairCode.Base32Decode(code); }
        catch (FormatException) { return ResetCodeResult.Malformed; }

        if (raw.Length != PayloadLength + SignatureLength || raw[0] != Version) return ResetCodeResult.Malformed;

        var payload = raw[..PayloadLength];
        var signature = raw[PayloadLength..];

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return ResetCodeResult.BadSignature;
        }
        catch (CryptographicException)
        {
            return ResetCodeResult.BadSignature;
        }

        if (!payload.AsSpan(1, TagLength).SequenceEqual(ParseTag(accountTag)))
            return ResetCodeResult.WrongAccount;

        var expiry = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(1 + TagLength));
        return now.ToUnixTimeSeconds() > expiry ? ResetCodeResult.Expired : ResetCodeResult.Valid;
    }

    private static byte[] ParseTag(string tag)
    {
        var hex = new string(tag.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != TagLength * 2) throw new ArgumentException("Identifiant du compte invalide.", nameof(tag));
        return Convert.FromHexString(hex);
    }
}
