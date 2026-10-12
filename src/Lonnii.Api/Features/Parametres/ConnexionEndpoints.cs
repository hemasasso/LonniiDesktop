using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Parametres;

/// <summary>
/// Configuration &amp; Serveurs → Connexion: every server this espace depends on and where each stands - the
/// shop's own server (this API), and for an espace activated against ours, the central Lonnii
/// server (licence, synchronisation) and the online backup.
///
/// The central server is named, never addressed: its URL is ours, not something a shop needs
/// to read, copy or point anything else at.
/// </summary>
public static class ConnexionEndpoints
{
    /// <summary>The central server counts as reachable when it answered this recently - the
    /// tills sync every few hours, so a day without contact means something is wrong.</summary>
    private static readonly TimeSpan RecentContact = TimeSpan.FromHours(24);

    public static void MapConnexionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/parametres/connexion", GetAsync)
            .WithTags("Paramètres")
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewConnexion);
    }

    private static async Task<IResult> GetAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == scope.GroupId, ct);
        if (groupe is null) return Results.NotFound();

        var online = DeploymentModes.RequiresSubscription(groupe.Mode);
        var activated = !string.IsNullOrWhiteSpace(groupe.LicenceServerUrl);
        var now = DateTime.UtcNow;

        var serveurs = new List<ConnexionServeurDto>
        {
            new(ConnexionRoles.Boutique, "Serveur de la boutique", Environment.MachineName, ConnexionEtats.Connecte, now,
                online
                    ? "Données de l'espace, synchronisées avec le serveur central Lonnii."
                    : "Données de l'espace, conservées sur cet ordinateur (mode local)."),
        };

        if (activated)
        {
            var reachable = groupe.LastLicenceCheckAt is { } last && now - last < RecentContact;
            var detail = online
                ? "Licence et synchronisation"
                  + (groupe.LicenceDeadline is { } deadline ? $" - à joindre avant le {deadline.ToLocalTime():dd/MM/yyyy à HH:mm}" : string.Empty)
                : "Licence et mises à jour";
            serveurs.Add(new(ConnexionRoles.Central, "Serveur central Lonnii", null,
                reachable ? ConnexionEtats.Connecte : ConnexionEtats.HorsLigne, groupe.LastLicenceCheckAt, detail));

            if (online)
            {
                var backup = await db.CloudBackupStates.AsNoTracking().FirstOrDefaultAsync(s => s.GroupId == scope.GroupId, ct);
                var failed = backup?.LastError is not null
                    && (backup.LastSuccessAt is null || backup.LastAttemptAt > backup.LastSuccessAt);
                serveurs.Add(new(ConnexionRoles.Sauvegarde, "Sauvegarde en ligne", null,
                    backup?.LastSuccessAt is null ? ConnexionEtats.Inactif : failed ? ConnexionEtats.HorsLigne : ConnexionEtats.Connecte,
                    backup?.LastSuccessAt,
                    failed ? $"Dernière tentative échouée : {backup!.LastError}"
                    : backup?.LastSuccessAt is null ? "Aucune sauvegarde encore effectuée."
                    : $"{backup.LastRecordCount} enregistrement(s), {backup.LastImageCount} photo(s) sauvegardés."));
            }
        }

        return Results.Ok(new ConnexionResponse(online ? DeploymentModes.Online : DeploymentModes.Local, activated, serveurs));
    }
}
