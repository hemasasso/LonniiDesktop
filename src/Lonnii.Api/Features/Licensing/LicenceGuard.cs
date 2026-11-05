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
/// <para>
/// On a shop's host, what is enforced comes from the <see cref="LicenceToken"/> the licence server
/// signed - mode, deadline, block, bound machines - not from the <c>groupes</c> columns, which the
/// shop can edit. Without a signed licence (an installation from before tokens, or one whose token
/// was deleted) the host has <see cref="UnsignedDays"/> to fetch one, capped at
/// <see cref="UnsignedBuildCap"/> after this build, then locks until it connects once.
/// </para>
/// <param name="publicKey">The key licences are checked against; tests only. Production always
/// uses the built-in <see cref="LicenceToken.PublicKey"/>.</param>
public class LicenceGuard(LonniiDbContext db, string? publicKey = null)
{
    public const string ExpiredMessage =
        "La licence de cet espace a expiré : l'ordinateur n'a pas pu joindre le serveur Lonnii " +
        "depuis trop longtemps. Connectez-le à Internet puis cliquez sur « Réessayer ».";

    public const string UnsignedMessage =
        "Cet ordinateur doit se connecter une fois à Internet pour confirmer la licence de l'espace. " +
        "Connectez-le puis cliquez sur « Réessayer ».";

    /// <summary>How long a host may run without a signed licence before it must fetch one.</summary>
    public const int UnsignedDays = 14;

    /// <summary>However often that window is restarted by deleting the row that records it, it
    /// never runs past this long after the build was made.</summary>
    public static readonly TimeSpan UnsignedBuildCap = TimeSpan.FromDays(60);

    /// <summary>When this build was made (stamped by the project file); null in a build without it,
    /// where the cap does not apply.</summary>
    public static readonly DateTime? BuildDate =
        typeof(LicenceGuard).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDateUtc")?.Value is { } stamp
            && DateTime.TryParse(stamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var built)
            ? built.ToUniversalTime() : null;

    /// <summary>The verified claims for <paramref name="groupId"/>'s stored token, or null when it
    /// has none, or one that is altered, foreign, or for another espace.</summary>
    public async Task<LicenceClaims?> SignedClaimsAsync(string groupId, CancellationToken ct)
    {
        var token = await db.HostLicences.AsNoTracking()
            .Where(h => h.GroupId == groupId).Select(h => h.Token).FirstOrDefaultAsync(ct);
        return Verified(token, groupId);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, LicenceClaims?> VerifiedTokens = new();

    /// <summary>ECDSA checks are cheap but run on every request; a token's verdict never changes.</summary>
    private LicenceClaims? Verified(string? token, string groupId)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (VerifiedTokens.Count > 256) VerifiedTokens.Clear(); // a new token every refresh: keep it small
        var claims = publicKey is null
            ? VerifiedTokens.GetOrAdd(token, t => LicenceToken.Verify(t))
            : LicenceToken.Verify(token, publicKey);
        return claims?.GroupId == groupId ? claims : null;
    }

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

        // A shop's host answers to its signed licence, whatever its database says.
        if (!string.IsNullOrWhiteSpace(groupe.LicenceServerUrl))
            return await CheckSignedAsync(groupe, ct);

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

    /// <summary>
    /// A host's licence, from its signed token. Signed local: never locked. Signed online: locked
    /// after the token's deadline. Blocked in the token: locked. No valid token: the unsigned
    /// window. "Now" never goes below the latest moment seen nor below the token's own issue time,
    /// so winding the clock back, even with the mark row edited, gains nothing past the signature.
    /// </summary>
    private async Task<LicenceStatusDto> CheckSignedAsync(Groupe groupe, CancellationToken ct)
    {
        var claims = await SignedClaimsAsync(groupe.Id, ct);

        var now = DateTime.UtcNow;
        if (groupe.LicenceClockMark is { } mark && mark > now) now = mark;
        if (claims is not null && claims.IssuedAt > now) now = claims.IssuedAt;
        if (groupe.LicenceClockMark is null || now - groupe.LicenceClockMark > MarkInterval)
        {
            groupe.LicenceClockMark = now;
            await db.SaveChangesAsync(ct);
        }

        if (claims is not null)
        {
            if (claims.IsBlocked)
                return Locked(claims.BlockReason ?? "Cet espace est bloqué. Contactez le support.", claims.MustReconnectBy);
            if (claims.MustReconnectBy is not { } signedDeadline) return NotEnforced();
            return Status(signedDeadline, now, ExpiredMessage);
        }

        // No signed licence yet: a window to fetch one, which deleting the row cannot stretch past
        // the build's own cap.
        var state = await db.HostLicences.FirstOrDefaultAsync(h => h.GroupId == groupe.Id, ct);
        if (state is null) db.HostLicences.Add(state = new HostLicence { GroupId = groupe.Id });
        if (state.UnsignedSince is null || state.UnsignedSince > now)
        {
            state.UnsignedSince = now;
            state.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        var deadline = state.UnsignedSince.Value.AddDays(UnsignedDays);
        if (BuildDate is { } built && built + UnsignedBuildCap < deadline) deadline = built + UnsignedBuildCap;
        return Status(deadline, now, UnsignedMessage);
    }

    private static LicenceStatusDto Status(DateTime deadline, DateTime now, string expiredMessage)
    {
        var expired = now > deadline;
        return new LicenceStatusDto(
            Enforced: true,
            Expired: expired,
            Deadline: deadline,
            DaysLeft: expired ? 0 : (int)Math.Ceiling((deadline - now).TotalDays),
            Message: expired ? expiredMessage : null);
    }

    /// <summary>
    /// Whether <paramref name="deviceId"/> may open the espace according to its signed licence:
    /// true or false when there is one, null when there is none (the caller falls back to the
    /// device table, the unsigned window bounding how long that lasts).
    /// </summary>
    public async Task<bool?> SignedDeviceAllowedAsync(string groupId, string? deviceId, CancellationToken ct)
    {
        var claims = await SignedClaimsAsync(groupId, ct);
        if (claims is null) return null;
        return !string.IsNullOrWhiteSpace(deviceId) && claims.Devices.Contains(deviceId);
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

        // The signed licence is what this host enforces from now on; only one that verifies, for
        // this espace, replaces the stored one (an unsigned answer leaves it as it was).
        if (Verified(response.LicenceToken, groupe.Id) is not null)
        {
            var state = await db.HostLicences.FirstOrDefaultAsync(h => h.GroupId == groupe.Id, ct);
            if (state is null) db.HostLicences.Add(state = new HostLicence { GroupId = groupe.Id });
            state.Token = response.LicenceToken;
            state.UpdatedAt = DateTime.UtcNow;
        }

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
