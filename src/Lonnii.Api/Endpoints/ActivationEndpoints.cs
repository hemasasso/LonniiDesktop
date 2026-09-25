using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// Licence activation. Every new installation calls this once before it will run, in both
/// deployment modes, and this instance of the API is the authority when it is the one on
/// our server.
///
/// <para>
/// This is the anti-piracy mechanism, chosen over signing the credentials file. A signature
/// asks "is this file genuine?" - a question answered on a machine the customer controls.
/// Activation instead withholds something the customer's machine does not have: the server
/// has no record of a workspace we never registered, so an invented credentials file cannot
/// be activated at all. Nothing local to patch around.
/// </para>
/// <para>
/// Honest about its limit: in local mode the customer owns the server that later enforces
/// the answer, so a determined person could patch the binary to skip this. It stops the
/// easy attempt, which is the one that actually happens, and obfuscation widens the gap.
/// In online mode the checks run here, on our hardware, and genuinely hold.
/// </para>
/// </summary>
public static class ActivationEndpoints
{
    public static void MapActivationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/activation").WithTags("Activation");

        // Anonymous by necessity: the installation calling this has no account on the local
        // database yet - creating it is what activation authorises. The admin credentials in
        // the request are what stand in for authentication.
        group.MapPost("/", ActivateAsync);
    }

    private static async Task<IResult> ActivateAsync(
        ActivationRequest request,
        LonniiDbContext db,
        HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.GroupId) || string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.BadRequest(new ApiError("Requête d'activation incomplète."));

        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        // The same message whether the workspace is unknown or the credentials are wrong.
        // Distinguishing them would tell someone holding a forged file whether they had
        // guessed a real workspace id.
        var refusal = Results.Json(
            new ApiError("Activation refusée. Vérifiez le fichier d'identifiants et le mot de passe."),
            statusCode: StatusCodes.Status403Forbidden);

        if (groupe is null || groupe.IsDeleted) return refusal;

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user?.Password is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.Password))
            return refusal;

        // The account has to belong to this workspace, or one shop's admin could activate
        // an installation against another's.
        var belongs = groupe.IdUserAdmin == user.IdUser
            || await db.GroupMembers.AnyAsync(m => m.IdGroupe == groupe.Id && m.IdUser == user.IdUser, ct);

        if (!belongs) return refusal;

        // Blocked and deleted are separate states, and blocked has its own message: this one
        // is a customer we know, so telling them why is useful rather than a leak.
        if (groupe.IsBlocked || user.IsBlocked)
        {
            return Results.Json(
                new ApiError(groupe.BlockReason ?? "Cet espace est bloqué. Contactez le support."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var subscriptionRequired = DeploymentModes.RequiresSubscription(groupe.Mode);
        DashboardSubscription? subscription = null;

        if (subscriptionRequired)
        {
            // The contract that decides access is the most recently started one.
            subscription = await db.DashboardSubscriptions
                .Where(s => s.GroupId == groupe.Id)
                .OrderByDescending(s => s.ContractStartDate)
                .ThenByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (subscription is null || !subscription.IsCurrentAt(DateTime.UtcNow))
            {
                return Results.Json(
                    new ApiError("Abonnement inactif ou expiré. Contactez le support pour réactiver cet espace."),
                    statusCode: StatusCodes.Status402PaymentRequired);
            }
        }

        var device = await db.Devices
            .FirstOrDefaultAsync(d => d.GroupId == groupe.Id && d.DeviceId == request.DeviceId, ct);

        // Revoked rows are kept so a machine that comes back is recognised, and they do not
        // count towards the limit - so reactivating one must not need a free slot.
        if (device is null)
        {
            var inUse = await db.Devices
                .CountAsync(d => d.GroupId == groupe.Id && d.RevokedAt == null, ct);

            if (inUse >= groupe.MaxDevices)
            {
                return Results.Json(
                    new ApiError(
                        $"Limite de postes atteinte ({groupe.MaxDevices}). " +
                        "Retirez un poste inutilisé depuis l'application, ou contactez le support."),
                    statusCode: StatusCodes.Status409Conflict);
            }

            device = new Device { GroupId = groupe.Id, DeviceId = request.DeviceId };
            db.Devices.Add(device);
        }

        device.DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? device.DeviceName : request.DeviceName.Trim();
        device.Platform = request.Platform;
        device.AppVersion = request.AppVersion;
        device.LastIpAddress = http.Connection.RemoteIpAddress?.ToString();
        device.LastSeenAt = DateTime.UtcNow;
        device.RevokedAt = null;
        device.RevokedReason = null;

        await db.SaveChangesAsync(ct);

        var devicesUsed = await db.Devices
            .CountAsync(d => d.GroupId == groupe.Id && d.RevokedAt == null, ct);

        return Results.Ok(new ActivationResponse(
            GroupId: groupe.Id,
            GroupName: groupe.Nom,
            Mode: groupe.Mode,
            MaxDevices: groupe.MaxDevices,
            DevicesUsed: devicesUsed,
            CurrencyLabel: groupe.CurrencyLabel,
            SubscriptionRequired: subscriptionRequired,
            SubscriptionStatus: subscription?.Statut,
            SubscriptionExpiresAt: subscription?.ContractEndDate,
            ActivatedAt: DateTime.UtcNow));
    }
}
