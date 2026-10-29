using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Licensing;

/// <summary>
/// The offline deadline's enforcement: day <see cref="Groupe.MaxOfflineDays"/> is a hard stop.
///
/// <para>
/// Applies only to a workspace that has a licence server to answer to
/// (<see cref="Groupe.LicenceServerUrl"/>, set at setup) and whose mode needs a subscription.
/// The OCI server itself has no <c>LicenceServerUrl</c> - it <em>is</em> the licence server -
/// so it is never locked by this, and local-mode shops, who paid in full, are never locked
/// at all.
/// </para>
/// <para>
/// The deadline arrives from the server (<see cref="LicenceRefreshResponse.MustReconnectBy"/>)
/// and is only ever replaced by one from the server. "Now" is never earlier than the latest
/// instant this installation has seen, so winding the computer's clock back does not extend it.
/// </para>
/// </summary>
public class LicenceGuard(LonniiDbContext db)
{
    public const string ExpiredMessage =
        "La licence de cet espace a expiré : l'ordinateur n'a pas pu joindre le serveur Lonnii " +
        "depuis trop longtemps. Connectez-le à Internet puis cliquez sur « Réessayer ».";

    /// <summary>How often the clock high-water mark is written, so a busy till does not
    /// write to the database on every request.</summary>
    private static readonly TimeSpan MarkInterval = TimeSpan.FromMinutes(5);

    public static bool IsEnforced(Groupe groupe) =>
        !string.IsNullOrWhiteSpace(groupe.LicenceServerUrl) &&
        DeploymentModes.RequiresSubscription(groupe.Mode);

    /// <summary>Evaluates the deadline, starting it the first time it is needed.</summary>
    public async Task<LicenceStatusDto> CheckAsync(string groupId, CancellationToken ct)
    {
        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        return groupe is null ? NotEnforced() : await CheckAsync(groupe, ct);
    }

    public async Task<LicenceStatusDto> CheckAsync(Groupe groupe, CancellationToken ct)
    {
        // Blocked from the dashboard, or this machine removed: stops the workspace on the next
        // request, in both modes. Only for a shop's own host - on our server a block is applied
        // where sessions open, and this guard never runs for it.
        if (groupe.IsBlocked && !string.IsNullOrWhiteSpace(groupe.LicenceServerUrl))
            return Locked(groupe.BlockReason ?? "Cet espace est bloqué. Contactez le support.", groupe.LicenceDeadline);

        if (!IsEnforced(groupe)) return NotEnforced();

        var now = DateTime.UtcNow;
        var dirty = false;

        // A clock wound back reads as the latest moment ever seen, not as earlier.
        if (groupe.LicenceClockMark is { } mark && mark > now)
        {
            now = mark;
        }
        else if (groupe.LicenceClockMark is null || now - groupe.LicenceClockMark > MarkInterval)
        {
            groupe.LicenceClockMark = now;
            dirty = true;
        }

        // First sight of this workspace under enforcement (a fresh setup, or an installation
        // from before the deadline existed): the clock starts now, so it gets a full period
        // rather than being locked out by an update - but cannot run for ever unchecked.
        if (groupe.LicenceDeadline is null)
        {
            groupe.LicenceDeadline = now.AddDays(groupe.MaxOfflineDays);
            dirty = true;
        }

        if (dirty) await db.SaveChangesAsync(ct);

        var deadline = groupe.LicenceDeadline.Value;
        var expired = now > deadline;
        var daysLeft = expired ? 0 : (int)Math.Ceiling((deadline - now).TotalDays);

        return new LicenceStatusDto(
            Enforced: true,
            Expired: expired,
            Deadline: deadline,
            DaysLeft: daysLeft,
            Message: expired ? ExpiredMessage : null);
    }

    /// <summary>The status that puts the lock screen up with <paramref name="message"/>.</summary>
    public static LicenceStatusDto Locked(string message, DateTime? deadline) =>
        new(Enforced: true, Expired: true, Deadline: deadline, DaysLeft: 0, Message: message);

    /// <summary>
    /// Why this machine may not use the workspace - it was removed from the shop on our server -
    /// or null when it may. Per machine: the shop's other computers are not affected.
    /// </summary>
    public async Task<string?> DeviceRemovedAsync(string groupId, string? deviceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        var device = await db.Devices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.GroupId == groupId && d.DeviceId == deviceId && d.RevokedAt != null, ct);

        return device is null ? null : device.RevokedReason ?? "Cet ordinateur a été retiré de l'espace.";
    }

    /// <summary>Records on this host that our server removed a machine, so it is refused here too.</summary>
    public async Task MarkDeviceRemovedAsync(string groupId, string deviceId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;

        var device = await db.Devices.FirstOrDefaultAsync(d => d.GroupId == groupId && d.DeviceId == deviceId, ct);
        if (device is null) db.Devices.Add(device = new Device { GroupId = groupId, DeviceId = deviceId });

        device.RevokedAt ??= DateTime.UtcNow;
        device.RevokedReason = reason;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Applies the server's answer: a fresh deadline, and the server's view of the workspace.</summary>
    public async Task ApplyRefreshAsync(
        Groupe groupe, LicenceRefreshResponse response, string deviceId, CancellationToken ct)
    {
        StoreBackupToken(db, groupe.Id, deviceId, response.BackupToken);

        // The server accepted this machine, so it is authorised again (re-added after a removal).
        var removed = await db.Devices.FirstOrDefaultAsync(
            d => d.GroupId == groupe.Id && d.DeviceId == deviceId && d.RevokedAt != null, ct);
        if (removed is not null)
        {
            removed.RevokedAt = null;
            removed.RevokedReason = null;
        }

        // The server is the authority: it overrides whatever this installation believed.
        if (DeploymentModes.All.Contains(response.Mode)) groupe.Mode = response.Mode;
        groupe.MaxDevices = response.MaxDevices;
        groupe.IsBlocked = response.IsBlocked;
        groupe.BlockReason = response.BlockReason;
        groupe.LastLicenceCheckAt = response.ServerTime;

        // Null from a local-mode answer: nothing to enforce.
        groupe.LicenceDeadline = response.MustReconnectBy;

        groupe.LicenceClockMark = new[]
        {
            DateTime.UtcNow, response.ServerTime, groupe.LicenceClockMark ?? DateTime.MinValue,
        }.Max();

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Keeps the key to the cloud backup the server just issued, with the machine id it
    /// belongs to. A null token (local mode) leaves whatever is stored alone.</summary>
    public static void StoreBackupToken(LonniiDbContext db, string groupId, string deviceId, string? token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(deviceId)) return;

        var state = db.CloudBackupStates.Local.FirstOrDefault(s => s.GroupId == groupId)
            ?? db.CloudBackupStates.FirstOrDefault(s => s.GroupId == groupId);

        if (state is null)
            db.CloudBackupStates.Add(state = new CloudBackupState { GroupId = groupId });

        state.DeviceToken = token;
        state.DeviceId = deviceId;
    }

    private static LicenceStatusDto NotEnforced() =>
        new(Enforced: false, Expired: false, Deadline: null, DaysLeft: int.MaxValue, Message: null);
}
