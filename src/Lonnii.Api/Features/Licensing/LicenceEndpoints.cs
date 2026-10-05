using Lonnii.Api.Features.Backup;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Licensing;

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

        // The installation's side: the shop's own host asks the licence server for a fresh
        // deadline, and reports where the workspace stands. Neither is group-scoped, because
        // a locked workspace must still be able to renew itself.
        group.MapPost("/sync", SyncAsync);
        group.MapGet("/status/{groupId}", StatusAsync);
    }

    /// <summary>
    /// Reaches the licence server now and stores the answer. Called by every till at start-up
    /// and every few hours, and by the lock screen's "Réessayer".
    /// </summary>
    private static async Task<IResult> SyncAsync(
        LicenceSyncRequest request,
        LonniiDbContext db,
        LicenceGuard guard,
        ILicenceServer licences,
        HttpContext http,
        CancellationToken ct)
    {
        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        // A shop with no licence server to answer to has nothing to check in with: the server itself, or a
        // host that was never activated against ours.
        if (groupe is null || string.IsNullOrWhiteSpace(groupe.LicenceServerUrl))
            return Results.Ok(await guard.CheckAsync(request.GroupId, ct));

        // Every shop activated against our server checks in - local ones too. That is how a local shop
        // learns it was made online, or given more machines, or blocked: the server is the authority for
        // all three and has no way to reach a shop that never asks. What differs is the cost of failing:
        // only an online shop can be locked for not reaching us.
        var enforced = LicenceGuard.IsEnforced(groupe);
        var deviceId = http.Request.Headers["x-device-id"].ToString();

        try
        {
            var response = await licences.RefreshAsync(
                groupe.LicenceServerUrl!, new LicenceRefreshRequest(groupe.Id, deviceId), ct);

            await guard.ApplyRefreshAsync(groupe, response, deviceId, ct);
        }
        catch (ActivationRefusedException) when (!enforced)
        {
            // A local shop works offline for ever: being unreachable, or not (yet) recognised by the server,
            // changes nothing and shows nothing. It tries again at the next sync.
            return Results.Ok(await guard.CheckAsync(groupe, ct));
        }
        catch (ActivationRefusedException e)
        {
            // Unreachable is a 503; a refusal keeps the server's own status (402 unpaid, 403
            // device refused). Either way the deadline is left exactly where it was - a
            // refused shop must not earn more days by asking.
            return Results.Json(new ApiError(e.Message),
                statusCode: (int)(e.Status ?? System.Net.HttpStatusCode.ServiceUnavailable));
        }

        return Results.Ok(await guard.CheckAsync(groupe, ct));
    }

    private static async Task<IResult> StatusAsync(string groupId, LicenceGuard guard, CancellationToken ct) =>
        Results.Ok(await guard.CheckAsync(groupId, ct));

    private static async Task<IResult> RefreshAsync(
        LicenceRefreshRequest request,
        LonniiDbContext db,
        HttpContext http,
        BackupTokens backupTokens,
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

        if (groupe.ApprovalStatus != ApprovalStatuses.Approved)
            return ActivationEndpoints.NotApproved(groupe);

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
            MustReconnectBy: subscriptionRequired ? now.AddDays(groupe.MaxOfflineDays) : null,
            // Also sent here, not only at activation, so an installation activated before
            // backups existed picks up its key on its next refresh.
            BackupToken: subscriptionRequired ? backupTokens.Create(groupe.Id, request.DeviceId) : null));
    }
}
