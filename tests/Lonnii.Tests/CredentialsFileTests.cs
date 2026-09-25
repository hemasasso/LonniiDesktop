using Lonnii.Shared.Security;

namespace Lonnii.Tests;

/// <summary>
/// Covers the credentials file a customer receives. The failure that matters most here is
/// a silent one - a file that decrypts into something subtly wrong, or one an owner can
/// edit to raise their own device limit - so tampering gets as much attention as the
/// happy path.
/// </summary>
public class CredentialsFileTests
{
    private const string Passphrase = "correct horse battery staple";

    private static StoreCredentials Sample(int maxDevices = 5) => new(
        GroupId: "3f2b1c44-8a6e-4d2f-9b77-0c1e5a9d4e21",
        StoreName: "Pharmacie Nord",
        AdminEmail: "admin@pharmacie-nord.bf",
        AdminPassword: "Motdepasse123",
        Mode: "local",
        MaxDevices: maxDevices,
        ServerUrl: null,
        IssuedAt: new DateTime(2026, 9, 23, 10, 30, 0, DateTimeKind.Utc));

    [Fact]
    public void Round_trips_every_field()
    {
        var original = Sample();

        var restored = CredentialsFile.Unprotect(CredentialsFile.Protect(original, Passphrase), Passphrase);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Wrong_passphrase_is_refused()
    {
        var file = CredentialsFile.Protect(Sample(), Passphrase);

        var error = Assert.Throws<CredentialsFileException>(
            () => CredentialsFile.Unprotect(file, "not the passphrase"));

        Assert.Contains("Phrase secrète incorrecte", error.Message);
    }

    [Fact]
    public void Passphrase_is_case_sensitive()
    {
        var file = CredentialsFile.Protect(Sample(), Passphrase);

        Assert.Throws<CredentialsFileException>(
            () => CredentialsFile.Unprotect(file, Passphrase.ToUpperInvariant()));
    }

    /// <summary>
    /// The point of AES-GCM here: a shop owner editing the file to raise their device
    /// limit must be refused, not silently obeyed. Every byte of the body is flipped in
    /// turn so the check cannot pass by only covering a lucky offset.
    /// </summary>
    [Fact]
    public void Any_tampered_byte_is_refused()
    {
        var file = CredentialsFile.Protect(Sample(), Passphrase);

        // Byte 0-7 are the magic header, which has its own message; start past it.
        for (var i = 8; i < file.Length; i++)
        {
            var edited = (byte[])file.Clone();
            edited[i] ^= 0xFF;

            Assert.Throws<CredentialsFileException>(
                () => CredentialsFile.Unprotect(edited, Passphrase));
        }
    }

    [Fact]
    public void A_file_that_is_not_ours_is_named_as_such()
    {
        var notOurs = new byte[128];
        Random.Shared.NextBytes(notOurs);

        var error = Assert.Throws<CredentialsFileException>(
            () => CredentialsFile.Unprotect(notOurs, Passphrase));

        Assert.Contains("n'est pas un fichier d'identifiants Lonnii", error.Message);
    }

    [Fact]
    public void A_truncated_file_is_named_as_such()
    {
        var file = CredentialsFile.Protect(Sample(), Passphrase);

        var error = Assert.Throws<CredentialsFileException>(
            () => CredentialsFile.Unprotect(file[..20], Passphrase));

        Assert.Contains("incomplet", error.Message);
    }

    /// <summary>
    /// Two files for the same shop must not be byte-identical: the salt and nonce are
    /// fresh each time, so identical input cannot leak that two stores share a passphrase.
    /// </summary>
    [Fact]
    public void Encrypting_twice_gives_different_bytes()
    {
        var credentials = Sample();

        var first = CredentialsFile.Protect(credentials, Passphrase);
        var second = CredentialsFile.Protect(credentials, Passphrase);

        Assert.NotEqual(first, second);
        Assert.Equal(
            CredentialsFile.Unprotect(first, Passphrase),
            CredentialsFile.Unprotect(second, Passphrase));
    }

    [Fact]
    public void An_empty_passphrase_is_rejected_on_both_sides()
    {
        Assert.Throws<ArgumentException>(() => CredentialsFile.Protect(Sample(), "   "));

        var file = CredentialsFile.Protect(Sample(), Passphrase);
        Assert.Throws<CredentialsFileException>(() => CredentialsFile.Unprotect(file, ""));
    }

    /// <summary>The plaintext must not be readable in the file, least of all the password.</summary>
    [Fact]
    public void Nothing_sensitive_survives_in_clear_text()
    {
        var credentials = Sample();
        var file = CredentialsFile.Protect(credentials, Passphrase);
        var asText = System.Text.Encoding.UTF8.GetString(file);

        Assert.DoesNotContain(credentials.AdminPassword, asText, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.AdminEmail, asText, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.GroupId, asText, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, asText, StringComparison.Ordinal);
    }
}
