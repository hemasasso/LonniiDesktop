using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lonnii.Client.Services;

/// <summary>What "stay signed in" remembers between launches.</summary>
public sealed record StoredSession(string Host, string AccessToken, DateTime ExpiresAt);

/// <summary>
/// Persists a bearer token so "Rester connecté" can skip the sign-in form on the next
/// launch. Protected with Windows DPAPI (<see cref="ProtectedData"/>, current-user scope)
/// rather than a passphrase: this file never leaves the machine, so the thing worth
/// protecting against is another Windows account on the same computer reading it, not
/// someone carrying it elsewhere - which is exactly what DPAPI ties itself to.
///
/// Deliberately opt-in and off by default: a shared till remembering whoever last checked
/// the box would hand the next person that account, undermining the per-machine device
/// accounting <see cref="DeviceIdentity"/> already relies on to know who is signed in where.
/// </summary>
public static class SessionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lonnii", "session.bin");

    // Bound as additional entropy so a file copied onto another machine, or decrypted by a
    // generic DPAPI tool, still does not silently work - it must have come from this app.
    private static readonly byte[] Entropy = "Lonnii.Client.SessionStore.v1"u8.ToArray();

    public static void Save(StoredSession session)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(session);
            var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, protectedBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Staying signed in is a convenience; failing to remember is never fatal.
        }
    }

    /// <summary>Null when nothing is stored, it was written by a different Windows account,
    /// or it has expired - never throws, so a corrupt or foreign file just means signing
    /// in again rather than a startup crash.</summary>
    public static StoredSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var protectedBytes = File.ReadAllBytes(FilePath);
            var json = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<StoredSession>(json);

            if (session is null || session.ExpiresAt <= DateTime.UtcNow) return null;
            return session;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
