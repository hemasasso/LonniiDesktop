using System.IO;

namespace Lonnii.Client.Services;

/// <summary>
/// Removes the sign-in token older builds saved for "Rester connecté". That option is gone: a
/// till is shared, and a remembered session hands the next person whoever signed in last. The
/// file is deleted at every launch and sign-out so no machine keeps one from before the update.
/// </summary>
public static class SessionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Lonnii", "session.bin");

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
