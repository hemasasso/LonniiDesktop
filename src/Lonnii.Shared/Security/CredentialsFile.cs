using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lonnii.Shared.Security;

/// <summary>
/// What a customer receives to bring a new installation to life: the workspace it belongs
/// to and the first account that can sign in to it.
///
/// <para>
/// The group id is generated once, when the file is issued, and every row the install ever
/// writes hangs off it. That is why it travels in the file rather than being made on the
/// customer's machine - two shops must never mint the same id, and an install that
/// invented its own could not later sync into the right workspace.
/// </para>
/// </summary>
/// <param name="GroupId">The workspace id. Called group_id throughout, for the web app's schema.</param>
/// <param name="StoreName">The shop's name, used for the workspace it creates.</param>
/// <param name="AdminEmail">Login for the first account, which becomes Admin Général.</param>
/// <param name="AdminPassword">That account's initial password, in plaintext inside the encrypted file.</param>
/// <param name="Mode">local or online - see DeploymentModes.</param>
/// <param name="MaxDevices">How many machines this shop may bind.</param>
/// <param name="ServerUrl">Where an online install syncs to. Null for local mode.</param>
/// <param name="IssuedAt">When the file was generated, for support questions.</param>
public sealed record StoreCredentials(
    string GroupId,
    string StoreName,
    string AdminEmail,
    string AdminPassword,
    string Mode,
    int MaxDevices,
    string? ServerUrl,
    DateTime IssuedAt);

/// <summary>Raised when a credentials file cannot be read, with a message fit to show a shopkeeper.</summary>
public sealed class CredentialsFileException(string message) : Exception(message);

/// <summary>
/// Reads and writes the encrypted credentials file handed to a customer.
///
/// <para>
/// Encrypted with AES-GCM under a key derived from a passphrase, which is sent to the
/// customer separately from the file. A passphrase rather than a key built into the
/// application: a built-in key ships to every customer at once, so anyone who pulls it out
/// of the binary can forge credentials for any shop, and obfuscation only delays that.
/// With a passphrase, the binary decrypts nothing on its own and a leaked file is useless
/// without the accompanying message.
/// </para>
/// <para>
/// GCM is chosen over plain AES so a tampered file fails loudly instead of decrypting into
/// rubbish - a customer editing MaxDevices in a hex editor gets a clear refusal. The magic
/// header is bound in as associated data, so the version marker cannot be swapped either.
/// </para>
/// </summary>
public static class CredentialsFile
{
    /// <summary>File marker and format version. Bound into the ciphertext as associated data.</summary>
    private static readonly byte[] Magic = "LNCRED01"u8.ToArray();

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;   // AesGcm.NonceByteSizes.MaxSize
    private const int TagBytes = 16;     // AesGcm.TagByteSizes.MaxSize
    private const int KeyBytes = 32;     // AES-256

    /// <summary>
    /// PBKDF2 rounds. High on purpose: the cost is paid once when a shop is set up, but it
    /// is also the only thing standing between a stolen file and a guessed passphrase.
    /// </summary>
    private const int Iterations = 600_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Encrypts credentials for delivery to a customer.</summary>
    /// <exception cref="ArgumentException">The passphrase is empty.</exception>
    public static byte[] Protect(StoreCredentials credentials, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("La phrase secrète est requise.", nameof(passphrase));

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(passphrase, salt);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);

        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);

        // Layout: magic | salt | nonce | tag | ciphertext
        var output = new byte[Magic.Length + SaltBytes + NonceBytes + TagBytes + ciphertext.Length];
        var at = 0;
        Magic.CopyTo(output, at); at += Magic.Length;
        salt.CopyTo(output, at); at += SaltBytes;
        nonce.CopyTo(output, at); at += NonceBytes;
        tag.CopyTo(output, at); at += TagBytes;
        ciphertext.CopyTo(output, at);

        return output;
    }

    /// <summary>
    /// Decrypts a credentials file. Every failure - truncated, not ours, tampered with, or
    /// simply the wrong passphrase - surfaces as <see cref="CredentialsFileException"/>.
    /// </summary>
    public static StoreCredentials Unprotect(byte[] fileContent, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(fileContent);
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new CredentialsFileException("La phrase secrète est requise.");

        var header = Magic.Length + SaltBytes + NonceBytes + TagBytes;
        if (fileContent.Length < header)
            throw new CredentialsFileException("Ce fichier d'identifiants est incomplet ou endommagé.");

        if (!fileContent.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CredentialsFileException("Ce fichier n'est pas un fichier d'identifiants Lonnii.");

        var at = Magic.Length;
        var salt = fileContent.AsSpan(at, SaltBytes).ToArray(); at += SaltBytes;
        var nonce = fileContent.AsSpan(at, NonceBytes); at += NonceBytes;
        var tag = fileContent.AsSpan(at, TagBytes); at += TagBytes;
        var ciphertext = fileContent.AsSpan(at);

        var key = DeriveKey(passphrase, salt);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
        }
        catch (CryptographicException)
        {
            // GCM cannot tell a wrong passphrase from an edited file, and saying so would
            // only help someone guessing. One message covers both.
            throw new CredentialsFileException(
                "Phrase secrète incorrecte, ou fichier modifié. Vérifiez la phrase secrète qui vous a été communiquée.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        try
        {
            return JsonSerializer.Deserialize<StoreCredentials>(plaintext, JsonOptions)
                   ?? throw new CredentialsFileException("Ce fichier d'identifiants est vide.");
        }
        catch (JsonException)
        {
            throw new CredentialsFileException("Ce fichier d'identifiants est illisible.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
}
