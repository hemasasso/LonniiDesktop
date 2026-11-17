using Lonnii.Api.Features.Backup;
using Lonnii.Api.Features.Infrastructure;
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
        // On a shipped host every espace has ours, whatever its row says (see ShippedHost).
        if (groupe is null || string.IsNullOrWhiteSpace(ShippedHost.LicenceServerUrlFor(groupe)))
            return Results.Ok(await guard.CheckAsync(request.GroupId, ct));

        var deviceId = http.Request.Headers["x-device-id"].ToString();
        // The calling till's own hardware signature, so our server stores and compares it against the
        // right machine (not the host's). Null for an older client; the host's own is used then.
        var hardware = http.Request.Headers["x-device-hw"].ToString();
        return await RenewAsync(groupe, deviceId, db, guard, licences, ct,
            string.IsNullOrWhiteSpace(hardware) ? null : hardware) ?? Results.Ok(await guard.CheckAsync(groupe, ct));
    }

    /// <summary>
    /// Asks the licence server for a fresh licence as <paramref name="deviceId"/> and stores the
    /// answer. Shared by the tills' <c>/sync</c> and the host's own <see cref="LicenceRenewalService"/>,
    /// so both follow the same rules. Returns the answer to give when the renewal ended the matter
    /// (a removed machine, a refusal), or null when the stored state now speaks for itself.
    /// </summary>
    /// <summary>
    /// The installed version, stamped by the installer build on the API and the client alike. Sent
    /// with every check-in so the dashboard follows updates; without it the server kept the version
    /// recorded at activation forever.
    /// </summary>
    internal static readonly string HostVersion =
        typeof(LicenceEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>One check-in at a time per espace. The host's own renewal and the app's check-in often
    /// start together (after an update, at start-up): run side by side, the one finishing last saved its
    /// slightly older licence over the newer one, and the next check reported "ancienne copie de la base".</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> RenewGates = new();

    internal static async Task<IResult?> RenewAsync(
        Groupe groupe, string deviceId, LonniiDbContext db, LicenceGuard guard, ILicenceServer licences, CancellationToken ct,
        string? hardwareSignature = null)
    {
        var gate = RenewGates.GetOrAdd(groupe.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await RenewOnceAsync(groupe, deviceId, db, guard, licences, ct, hardwareSignature);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<IResult?> RenewOnceAsync(
        Groupe groupe, string deviceId, LonniiDbContext db, LicenceGuard guard, ILicenceServer licences, CancellationToken ct,
        string? hardwareSignature)
    {
        // Every shop activated against our server checks in - local ones too. That is how a local shop
        // learns it was made online, or given more machines, or blocked: the server is the authority for
        // all three and has no way to reach a shop that never asks. What differs is the cost of failing:
        // only an online shop can be locked for not reaching us.
        var enforced = LicenceGuard.IsEnforced(groupe);
        var serverUrl = ShippedHost.LicenceServerUrlFor(groupe)!;

        // On a shop's computer the address is always ours: a row that says otherwise was edited.
        if (ShippedHost.IsShipped
            && !string.Equals(groupe.LicenceServerUrl?.TrimEnd('/'), serverUrl, StringComparison.OrdinalIgnoreCase))
            guard.Security?.Record(groupe.Id, SecurityEventKinds.ServerUrlChanged,
                string.IsNullOrWhiteSpace(groupe.LicenceServerUrl) ? "Adresse effacée" : $"Adresse : {groupe.LicenceServerUrl}");

        // What this computer noticed since its last check-in rides along, and is forgotten once our
        // server has answered - a refusal included, since it read them before refusing.
        var events = guard.Security?.Pending(groupe.Id) ?? [];
        var answered = false;
        bool NoteAnswer(ActivationRefusedException e)
        {
            answered = e.Status is not null;
            return false;
        }

        // The shop's other devices as this host last saw them. A phone on the shop's Wi-Fi signs in here,
        // never with our server, so without this the dashboard showed it "seen" on the day it was added
        // and never again. The calling device is left out: our server stamps it itself.
        var seen = await db.Devices.AsNoTracking()
            .Where(d => d.GroupId == groupe.Id && d.RevokedAt == null && d.DeviceId != deviceId)
            .OrderByDescending(d => d.LastSeenAt)
            .Take(50)
            .Select(d => new DeviceSeenDto(d.DeviceId, d.LastSeenAt, d.AppVersion, d.DeviceName, d.Platform))
            .ToListAsync(ct);

        try
        {
            try
            {
                var response = await licences.RefreshAsync(
                    serverUrl, new LicenceRefreshRequest(groupe.Id, deviceId, HostVersion, events.Count > 0 ? events : null,
                        hardwareSignature ?? Infrastructure.HostMachineIdentity.Hardware,
                        Infrastructure.HostBinaries.Hash, seen.Count > 0 ? seen : null), ct);
                answered = true;

                // The server that just answered is the espace's: puts back a licence_server_url someone
                // blanked or changed, so backups and the rest of the host see an activated espace again.
                groupe.LicenceServerUrl = serverUrl;
                await guard.ApplyRefreshAsync(groupe, response, deviceId, ct);
            }
            catch (ActivationRefusedException e) when (NoteAnswer(e))
            {
                // Never runs: only notes whether our server answered.
            }
            catch (ActivationRefusedException e) when (e.Status == System.Net.HttpStatusCode.Gone)
            {
                // The server's final word: this machine was removed from the shop. Only this machine
                // locks - the shop's other computers check in through here too and carry on. Both modes,
                // local included. Lifted by the next refresh that succeeds, after re-authorising.
                await guard.MarkDeviceRemovedAsync(groupe.Id, deviceId, e.Message, ct);
                return Results.Ok(LicenceGuard.Locked(e.Message, groupe.LicenceDeadline));
            }
            catch (ActivationRefusedException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
            {
                // The shop itself was deleted on our side: every computer stops.
                groupe.IsBlocked = true;
                groupe.BlockReason = e.Message;
                await db.SaveChangesAsync(ct);
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
        }
        finally
        {
            if (answered && events.Count > 0) guard.Security?.Sent(groupe.Id, events);
        }

        return null;
    }

    private static async Task<IResult> StatusAsync(string groupId, LicenceGuard guard, CancellationToken ct) =>
        Results.Ok(await guard.CheckAsync(groupId, ct));

    private static async Task<IResult> RefreshAsync(
        LicenceRefreshRequest request,
        LonniiDbContext db,
        HttpContext http,
        BackupTokens backupTokens,
        LicenceSigner signer,
        SecurityEventStore securityEvents,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.GroupId) || string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.BadRequest(new ApiError("Requête de licence incomplète."));

        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == request.GroupId, ct);

        if (groupe is null)
        {
            return Results.Json(
                new ApiError("Espace introuvable. Contactez le support."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        // 404, not 403: a deleted shop is a final answer the installation acts on (it locks the
        // whole shop), unlike a refusal it could be wrong about - see SyncAsync.
        if (groupe.IsDeleted)
        {
            return Results.Json(
                new ApiError("Cet espace a été supprimé. Contactez le support."),
                statusCode: StatusCodes.Status404NotFound);
        }

        if (groupe.ApprovalStatus != ApprovalStatuses.Approved)
            return ActivationEndpoints.NotApproved(groupe);

        // What the shop's computer noticed (someone editing its database, its clock...), kept whatever
        // the answer below: a refused machine's report is the most telling one.
        var ip = http.Connection.RemoteIpAddress?.ToString();
        securityEvents.AddReported(groupe.Id, request.Events, request.DeviceId, ip, request.AppVersion);

        var device = await db.Devices.FirstOrDefaultAsync(
            d => d.GroupId == groupe.Id && d.DeviceId == request.DeviceId, ct);

        // An unbound or revoked machine must not be able to keep itself alive by refreshing.
        // Revoking a stolen laptop has to actually reach it, and this is how: a revoked one is told
        // with 410, which makes it lock itself. An unknown one keeps 403 - it may simply predate
        // the device list, and locking a shop for our gap would be wrong.
        if (device is { RevokedAt: not null })
        {
            securityEvents.AddFromServer(groupe.Id, SecurityEventKinds.RevokedDevice, request.DeviceId, ip, request.AppVersion,
                device.DeviceName is { Length: > 0 } name ? $"Poste : {name}" : null);
            return Results.Json(
                new ApiError("Cet ordinateur a été retiré de l'espace. Demandez à l'administrateur de l'autoriser à nouveau."),
                statusCode: StatusCodes.Status410Gone);
        }

        if (device is null)
        {
            securityEvents.AddFromServer(groupe.Id, SecurityEventKinds.UnboundDevice, request.DeviceId, ip, request.AppVersion);
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

        // Captured before they are overwritten below: a program-file change only means tampering when
        // the version did not change too (a version change is an ordinary update, with new files).
        var previousVersion = device.AppVersion;
        var previousBuildHash = device.BuildHash;

        // The clock only moves on for a workspace that is actually entitled to run, so a
        // blocked or unpaid shop cannot refresh its way to another week.
        device.LastSeenAt = now;
        device.LastIpAddress = http.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(request.AppVersion)) device.AppVersion = request.AppVersion;

        // The host's program files changed but its version did not: a build patched to switch the
        // licence off keeps the same version so the swap passes for the one we published. Recorded for
        // the dashboard; never blocks (an antivirus rewriting a file would read the same). Same version
        // means the reported version equals the one stored, or the client sent none.
        if (!string.IsNullOrWhiteSpace(request.BuildHash))
        {
            var sameVersion = string.IsNullOrWhiteSpace(request.AppVersion)
                || string.Equals(previousVersion, request.AppVersion, StringComparison.OrdinalIgnoreCase);
            if (previousBuildHash is { Length: > 0 } knownBuild && knownBuild != request.BuildHash && sameVersion)
                securityEvents.AddFromServer(groupe.Id, SecurityEventKinds.BinariesModified, request.DeviceId, ip, request.AppVersion,
                    $"Version {request.AppVersion ?? previousVersion ?? "?"} inchangée");
            device.BuildHash = request.BuildHash;
        }

        // The device id is this machine's MachineGuid alone, so copying that value onto another computer
        // reuses this binding. The hardware signature carries more of the machine, so the copy's differs:
        // a signature that changes from the one first stored is that copy, or a disk or motherboard
        // swapped on the real one. Recorded for the dashboard either way; never blocks - the owner knows
        // which it was. An older client sends none, and absence is not a change.
        if (!string.IsNullOrWhiteSpace(request.HardwareSignature))
        {
            if (device.HardwareSignature is { Length: > 0 } known && known != request.HardwareSignature)
                securityEvents.AddFromServer(groupe.Id, SecurityEventKinds.HardwareChanged, request.DeviceId, ip, request.AppVersion,
                    device.DeviceName is { Length: > 0 } dn ? $"Poste : {dn}" : null);
            device.HardwareSignature = request.HardwareSignature;
        }

        groupe.LastLicenceCheckAt = now;

        // The shop's other devices as its host saw them (phones on its Wi-Fi never reach us themselves).
        // Only moves a bound device's clock forward, never past now; the host's say-so adds nothing else.
        if (request.Devices is { Count: > 0 } reported)
        {
            var reportedIds = reported.Select(r => r.DeviceId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(50).ToList();
            var known = await db.Devices
                .Where(d => d.GroupId == groupe.Id && d.RevokedAt == null && reportedIds.Contains(d.DeviceId))
                .ToListAsync(ct);
            foreach (var r in reported)
            {
                var d = known.FirstOrDefault(k => k.DeviceId == r.DeviceId);
                if (d is null || r.LastSeenAt <= d.LastSeenAt || r.LastSeenAt > now.AddHours(1)) continue;
                d.LastSeenAt = r.LastSeenAt;
                if (!string.IsNullOrWhiteSpace(r.AppVersion)) d.AppVersion = r.AppVersion[..Math.Min(r.AppVersion.Length, 20)];
                if (!string.IsNullOrWhiteSpace(r.DeviceName)) d.DeviceName = r.DeviceName[..Math.Min(r.DeviceName.Length, 80)];
            }
        }

        await db.SaveChangesAsync(ct);

        var bound = await db.Devices
            .Where(d => d.GroupId == groupe.Id && d.RevokedAt == null)
            .Select(d => d.DeviceId)
            .ToListAsync(ct);
        var devicesUsed = bound.Count;
        var mustReconnectBy = subscriptionRequired ? now.AddDays(groupe.MaxOfflineDays) : (DateTime?)null;

        // What the shop's host will enforce, signed so its own database cannot overrule it.
        var licenceToken = signer.Sign(new LicenceClaims(
            groupe.Id, groupe.Mode, mustReconnectBy, now, bound, groupe.IsBlocked, groupe.BlockReason,
            groupe.DesktopGestionAccess));

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
            MustReconnectBy: mustReconnectBy,
            // Also sent here, not only at activation, so an installation activated before
            // backups existed picks up its key on its next refresh.
            BackupToken: subscriptionRequired ? backupTokens.Create(groupe.Id, request.DeviceId) : null,
            LicenceToken: licenceToken));
    }
}
