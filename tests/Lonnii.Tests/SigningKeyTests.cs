using Lonnii.Api.Features.Auth;

namespace Lonnii.Tests;

/// <summary>
/// The key that signs every token and from which every cloud-backup token is derived. Found on the live server
/// readable by every user: whoever can read it can forge a token and download any shop's backup.
/// </summary>
public class SigningKeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string KeyPath => Path.Combine(_directory, "jwt.key");

    [Fact]
    public void A_key_is_created_once_and_reused()
    {
        var first = TokenService.LoadOrCreateSecret(KeyPath);
        var second = TokenService.LoadOrCreateSecret(KeyPath);

        Assert.True(first.Length >= 32);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_new_key_is_readable_by_its_owner_only()
    {
        // File modes are a Unix idea: on Windows there is nothing to assert.
        if (OperatingSystem.IsWindows()) return;

        TokenService.LoadOrCreateSecret(KeyPath);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
    }

    [Fact]
    public void An_existing_key_that_everyone_can_read_is_closed_when_it_is_loaded()
    {
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(_directory);
        File.WriteAllText(KeyPath, new string('k', 64));
        File.SetUnixFileMode(KeyPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var secret = TokenService.LoadOrCreateSecret(KeyPath);

        Assert.Equal(new string('k', 64), secret);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
    }
}
