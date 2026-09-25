using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// Licence refresh: what an already-activated installation calls to pick up changes, and
/// what keeps it running.
///
/// <para>
/// Two jobs in one call. It carries our changes down - a shop raised from five machines to
/// six reconnects briefly and has them - and it resets the offline clock. An installation
/// that has not completed one of these within <see cref="Groupe.MaxOfflineDays"/> stops.
/// </para>
/// <para>
/// That second job is what stops an online-mode shop, bought cheaply against a yearly fee,
/// from simply unplugging the internet and using the software for ever as though it had
/// paid for the far more expensive offline licence. Without it, the cheaper tier would
/// undercut the dearer one and the recurring revenue would be optional.
/// </para>
/// <para>
/// Deliberately unauthenticated, like activation: the workspace id and a bound device id
/// are the credentials. A device that is not bound, or has been revoked, gets nothing.
/// </para>
/// </summary>
public static class LicenceEndpoints
{
    public static void MapLicenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/licence").WithTags("Licence");

        group.MapPost("/refresh", RefreshAsync);
    }

    private static async Task<IResult> RefreshAsync(
        LicenceRefreshRequest request,
        LonniiDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.GroupId) || string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.BadRequest(new ApiError("Requête de licence incomplète."));

        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        if (groupe is null || groupe.IsDeleted)
        {
            return Results.Json(
                new ApiError("Espace introuvable. Contactez le support."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var device = await db.Devices.FirstOrDefaultAsync(
            d => d.GroupId == groupe.Id && d.DeviceId == request.DeviceId, ct);

        // An unbound or revoked machine must not be able to keep itself alive by refreshing.
        // Revoking a stolen laptop has to actually reach it, and this is how.
        if (device is null || device.RevokedAt is not null)
        {
            return Results.Json(
                new ApiError("Ce poste n'est pas autorisé. Réactivez-le depuis le fichier d'identifiants."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var now = DateTime.UtcNow;

        var subscriptionRequired = DeploymentModes.RequiresSubscription(groupe.Mode);
        DashboardSubscription? subscription = null;

        if (subscriptionRequired)
        {
            subscription = await db.DashboardSubscriptions
                .Where(s => s.GroupId == groupe.Id)
                .OrderByDescending(s => s.ContractStartDate)
                .ThenByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (subscription is null || !subscription.IsCurrentAt(now))
            {
                return Results.Json(
                    new ApiError("Abonnement inactif ou expiré. Contactez le support pour réactiver cet espace."),
                    statusCode: StatusCodes.Status402PaymentRequired);
            }
        }

        // The clock only moves on for a workspace that is actually entitled to run, so a
        // blocked or unpaid shop cannot refresh its way to another week.
        device.LastSeenAt = now;
        device.LastIpAddress = http.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(request.AppVersion)) device.AppVersion = request.AppVersion;

        groupe.LastLicenceCheckAt = now;

        await db.SaveChangesAsync(ct);

        var devicesUsed = await db.Devices
            .CountAsync(d => d.GroupId == groupe.Id && d.RevokedAt == null, ct);

        return Results.Ok(new LicenceRefreshResponse(
            GroupId: groupe.Id,
            GroupName: groupe.Nom,
            Mode: groupe.Mode,
            MaxDevices: groupe.MaxDevices,
            DevicesUsed: devicesUsed,
            CurrencyLabel: groupe.CurrencyLabel,
            SubscriptionRequired: subscriptionRequired,
            SubscriptionStatus: subscription?.Statut,
            SubscriptionExpiresAt: subscription?.ContractEndDate,
            // Blocking is reported rather than refused: the installation needs to hear the
            // reason so it can show the shop why, instead of failing silently.
            IsBlocked: groupe.IsBlocked,
            BlockReason: groupe.BlockReason,
            ServerTime: now,
            // Online only. An offline licence is paid in full with no recurring fee, so it
            // has nothing to keep checking in for, and the tier is sold on needing no
            // internet - a deadline there would punish the customer who paid the most.
            // Such a shop still calls this when we change something for it, which is what
            // "only at installation and when the machine allowance changes" means.
            MustReconnectBy: subscriptionRequired ? now.AddDays(groupe.MaxOfflineDays) : null));
    }
}
