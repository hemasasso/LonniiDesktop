using System.Security.Cryptography;
using System.Text.Json;
using Lonnii.Shared.Security;

namespace Lonnii.Setup;

/// <summary>
/// Signs a built installer for publication: writes <c>latest.json</c> beside it, which together
/// with the installer is what goes into the server's updates folder. Signed with the repair key,
/// so only this machine can publish a version the shops will install (see <see cref="UpdateManifest"/>).
/// </summary>
public static class ReleaseCommand
{
    private static readonly string DefaultKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lonnii", "repair-signing.key");

    public static int Run(string[] args)
    {
        var installer = Option(args, "installer");
        if (installer is null)
        {
            Console.Error.WriteLine("""

                lonnii-setup release --installer <LonniiBusiness-Setup-x.y.z.exe>
                                     [--notes "Ce qui change"] [--key <fichier de clé privée>]

                  Écrit latest.json à côté de l'installateur. Copiez les deux fichiers dans
                  /var/lib/lonnii-api/updates sur le serveur.

                """);
            return 1;
        }

        var file = new FileInfo(installer);
        if (!file.Exists)
        {
            Console.Error.WriteLine($"Installateur introuvable : {installer}");
            return 1;
        }

        if (!UpdateManifest.IsInstallerName(file.Name))
        {
            Console.Error.WriteLine($"Nom inattendu : {file.Name}. Attendu : {UpdateManifest.FilePrefix}<version>.exe");
            return 1;
        }

        var version = Path.GetFileNameWithoutExtension(file.Name)[UpdateManifest.FilePrefix.Length..];
        if (!Version.TryParse(version, out _))
        {
            Console.Error.WriteLine($"Version illisible dans le nom : {version}");
            return 1;
        }

        var keyPath = Option(args, "key") ?? DefaultKeyPath;
        if (!File.Exists(keyPath))
        {
            Console.Error.WriteLine($"Clé privée introuvable : {keyPath}.");
            return 1;
        }

        try
        {
            var sha256 = UpdateManifest.HashFileAsync(file.FullName).GetAwaiter().GetResult();
            var manifest = UpdateManifest.Sign(
                Convert.FromBase64String(File.ReadAllText(keyPath).Trim()),
                version, file.Name, file.Length, sha256, DateTime.UtcNow, Option(args, "notes"));

            if (!manifest.IsGenuine())
            {
                Console.Error.WriteLine("Cette clé ne correspond pas à la clé publique livrée dans l'application.");
                return 1;
            }

            var output = Path.Combine(file.DirectoryName!, "latest.json");
            File.WriteAllText(output, JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));

            Console.WriteLine($"""

                Version {version} signée : {output}
                SHA-256 {sha256}

                """);
            return 0;
        }
        catch (Exception e) when (e is FormatException or CryptographicException or IOException)
        {
            Console.Error.WriteLine($"Signature impossible : {e.Message}");
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
