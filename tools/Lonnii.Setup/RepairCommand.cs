using System.Security.Cryptography;
using Lonnii.Shared.Security;

namespace Lonnii.Setup;

/// <summary>
/// Issues the repair code that lets one till be pointed at one server address it could not
/// find on its own. The private key stays on our machine; see <see cref="RepairCode"/> for why.
/// </summary>
public static class RepairCommand
{
    private static readonly string DefaultKeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lonnii", "repair-signing.key");

    public static int Keygen(string[] args)
    {
        var path = Option(args, "key") ?? DefaultKeyPath;
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"La clé existe déjà : {path}\nL'écraser rendrait tous les postes déjà livrés incapables de vérifier vos codes.");
            return 1;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));

        Console.WriteLine($"""

            Clé privée enregistrée : {path}
            Sauvegardez-la hors de ce disque et ne la partagez jamais.

            Clé publique, à coller dans RepairCode.PublicKey :
            {Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}

            """);
        return 0;
    }

    public static int Issue(string[] args)
    {
        var device = Option(args, "device");
        var address = Option(args, "address");
        if (device is null || address is null)
        {
            Console.Error.WriteLine("""

                lonnii-setup repair-code --device <identifiant du poste> --address <hôte:port>
                                         [--hours 24] [--key <fichier de clé privée>]

                  --device    L'identifiant affiché sur l'écran de réparation du poste.
                  --address   L'adresse du serveur à autoriser pour ce poste.
                  --hours     Durée pour saisir le code (défaut 24).

                """);
            return 1;
        }

        var keyPath = Option(args, "key") ?? DefaultKeyPath;
        if (!File.Exists(keyPath))
        {
            Console.Error.WriteLine($"Clé privée introuvable : {keyPath}. Créez-la avec : lonnii-setup repair-keygen");
            return 1;
        }

        if (!int.TryParse(Option(args, "hours") ?? "24", out var hours) || hours < 1)
        {
            Console.Error.WriteLine("Durée invalide.");
            return 1;
        }

        try
        {
            var expires = DateTimeOffset.UtcNow.AddHours(hours);
            var code = RepairCode.Issue(Convert.FromBase64String(File.ReadAllText(keyPath).Trim()), device, address, expires);
            Console.WriteLine($"""

                Code de réparation (valable jusqu'au {expires.ToLocalTime():dd/MM/yyyy HH:mm}) :

                {code}

                Poste   : {device}
                Adresse : {RepairCode.NormalizeAddress(address)}

                """);
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or CryptographicException)
        {
            Console.Error.WriteLine($"Impossible d'émettre le code : {e.Message}");
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
