using Lonnii.Shared.Security;

namespace Lonnii.Setup;

/// <summary>
/// Generates the encrypted credentials file issued to a new customer.
///
/// <para>
/// Ours, and never shipped. It used to be a verb on the API, which was a hole: local-mode
/// customers receive that program, so a shop could have run it to issue itself a file
/// granting any number of machines. It lives in tools/ now, referenced by nothing that
/// reaches a customer.
/// </para>
/// <para>
/// Moving it out is only the cheap half of the fix. What actually stops a forged file is
/// that an installation must activate against our server before it will run, and the
/// server has never heard of a workspace we did not create. That is why
/// <c>--group-id</c> exists: the id comes from the dashboard, where the workspace is
/// registered, rather than being invented here where nothing would recognise it.
/// </para>
/// <para>
/// The passphrase is never written into the file or printed with the path - it is sent to
/// the customer separately, or the split is pointless.
/// </para>
/// </summary>
public static class SetupCommand
{
    /// <summary>Runs the setup command. Returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        var options = Parse(args);

        if (options is null)
        {
            PrintUsage();
            return 1;
        }

        var (credentials, passphrase, outputPath) = options.Value;

        try
        {
            var encrypted = CredentialsFile.Protect(credentials, passphrase);

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllBytes(outputPath, encrypted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Impossible d'écrire le fichier : {e.Message}");
            return 1;
        }

        Console.WriteLine($"""

            Fichier d'identifiants créé.

              Fichier      : {Path.GetFullPath(outputPath)}
              Espace       : {credentials.StoreName}
              Group ID     : {credentials.GroupId}
              Admin        : {credentials.AdminEmail}
              Mode         : {credentials.Mode}
              Postes max   : {credentials.MaxDevices}

            Transmettez la phrase secrète au client séparément du fichier.

            """);

        return 0;
    }

    private static (StoreCredentials Credentials, string Passphrase, string OutputPath)? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) return null;
            if (i + 1 >= args.Length) return null;

            values[args[i][2..]] = args[i + 1];
            i++;
        }

        if (!values.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)) return null;
        if (!values.TryGetValue("email", out var email) || string.IsNullOrWhiteSpace(email)) return null;
        if (!values.TryGetValue("password", out var password) || string.IsNullOrWhiteSpace(password)) return null;
        if (!values.TryGetValue("passphrase", out var passphrase) || string.IsNullOrWhiteSpace(passphrase)) return null;

        var mode = values.GetValueOrDefault("mode", DeploymentModes.Local).ToLowerInvariant();
        if (!DeploymentModes.All.Contains(mode))
        {
            Console.Error.WriteLine($"Mode inconnu : {mode}. Utilisez 'local' ou 'online'.");
            return null;
        }

        var maxDevicesText = values.GetValueOrDefault("max-devices", "3");
        if (!int.TryParse(maxDevicesText, out var maxDevices) || maxDevices < 1)
        {
            Console.Error.WriteLine($"Nombre de postes invalide : {maxDevicesText}");
            return null;
        }

        // Required in both modes, not just online: every installation has to activate
        // against our server once before it will run, and this is the address it calls.
        // A local-mode shop works offline afterwards, but it still has to be recognised
        // once - that single call is what makes a self-issued file worthless.
        var serverUrl = values.GetValueOrDefault("server");
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            Console.Error.WriteLine(
                "--server est obligatoire : toute installation s'active auprès du serveur Lonnii, " +
                "même en mode local.");
            return null;
        }

        // The workspace id comes from the dashboard, where the store is registered, so that
        // activation can recognise it. Minting one here is offered only for a store that is
        // being registered at the same time - the printed id then has to be entered there.
        if (!values.TryGetValue("group-id", out var groupId) || string.IsNullOrWhiteSpace(groupId))
        {
            groupId = Guid.NewGuid().ToString();
            Console.Error.WriteLine(
                "Aucun --group-id fourni : un nouvel identifiant a été généré. " +
                "Enregistrez-le dans le tableau de bord, sinon l'activation échouera.");
        }
        else if (!Guid.TryParse(groupId, out _))
        {
            Console.Error.WriteLine($"Identifiant d'espace invalide : {groupId}");
            return null;
        }

        var credentials = new StoreCredentials(
            GroupId: groupId.Trim(),
            StoreName: name.Trim(),
            AdminEmail: email.Trim().ToLowerInvariant(),
            AdminPassword: password,
            Mode: mode,
            MaxDevices: maxDevices,
            ServerUrl: string.IsNullOrWhiteSpace(serverUrl) ? null : serverUrl.Trim(),
            IssuedAt: DateTime.UtcNow);

        var output = values.GetValueOrDefault("out", $"{Slug(credentials.StoreName)}.lonnii");

        return (credentials, passphrase, output);
    }

    /// <summary>A filename-safe version of the shop name, for the default output path.</summary>
    private static string Slug(string name)
    {
        var cleaned = new string([.. name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]);
        return string.Join('-', cleaned.Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        """

        Crée le fichier d'identifiants chiffré remis à un client.
        Outil interne : ne jamais le distribuer avec l'application.

          lonnii-setup --name <nom> --email <email> --password <mdp>
                       --passphrase <phrase> [options]

        Obligatoire
          --name           Nom de la boutique
          --email          Email du compte administrateur
          --password       Mot de passe initial de ce compte
          --passphrase     Phrase secrète protégeant le fichier (à transmettre à part)
          --server         Adresse du serveur d'activation. Obligatoire dans les deux
                           modes : toute installation s'active une fois avant de démarrer.

        Options
          --group-id       Identifiant de l'espace, repris du tableau de bord.
                           Sans lui, un identifiant est généré et doit y être enregistré.
          --mode           local (défaut) ou online
          --max-devices    Nombre de postes autorisés (défaut 3)
          --out            Chemin du fichier (défaut <nom>.lonnii)

        """);
}
