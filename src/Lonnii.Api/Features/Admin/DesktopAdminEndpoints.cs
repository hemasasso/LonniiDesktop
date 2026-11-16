using System.Security.Claims;
using Lonnii.Api.Features.Backup;
using Lonnii.Api.Features.Licensing;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Admin;

/// <summary>
/// The admin dashboard's view of Lonnii Desktop shops: the "Lonnii Desktop" side of the dashboard.
/// Licence-server only (PostgreSQL on OCI); on a shop's own host no dashboard key is configured, so
/// every request here is refused.
///
/// <para>
/// The database is shared with Lonnii Business, and every web workspace also has
/// <c>mode = 'local'</c> by default, so mode alone cannot tell the two apart. A desktop shop is one
/// that has bound a machine, reached the licence server, or registered itself from the desktop app.
/// </para>
/// </summary>
public static class DesktopAdminEndpoints
{
    public const string ViewPermission = "desktop.view";
    public const string ManageDevicesPermission = "desktop.devices.manage";

    /// <summary>A shop is "active today" when it was heard from within this window.</summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromHours(24);

    /// <summary>An online shop silent this long gets a warning. Its computer checks in every hour, so a
    /// week of silence means it is switched off - or running without asking us any more (an old version
    /// with the internet cut, to dodge the subscription), which never locks by itself.</summary>
    internal const int SilentWarningDays = 7;

    /// <summary>An attempt to get round the licence keeps a shop on the attention list this long.</summary>
    private const int SecurityAlertDays = 7;

    /// <summary>How far back a shop's page lists the attempts.</summary>
    private const int SecurityHistoryDays = 30;

    /// <summary>Warn when an online shop's contract ends within this many days.</summary>
    private const int ExpiryWarningDays = 7;

    /// <summary>An online shop whose last cloud backup is older than this needs a look.</summary>
    private const int BackupStaleDays = 3;

    public const int MaxDevicesCeiling = 200;

    public static void MapDesktopAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/desktop")
            .WithTags("Admin")
            .RequireCors(DashboardAdminAuth.CorsPolicy)
            .RequireAuthorization(DashboardAdminAuth.Policy);

        group.MapGet("/overview", OverviewAsync).RequireDashboardPermission(ViewPermission);
        group.MapGet("/shops", ListShopsAsync).RequireDashboardPermission(ViewPermission);
        group.MapGet("/shops/{id}", ShopDetailAsync).RequireDashboardPermission(ViewPermission);

        group.MapDelete("/shops/{id}/devices/{deviceId}", RevokeDeviceAsync)
            .RequireDashboardPermission(ManageDevicesPermission);
        // Undoes a removal, within the shop's limit: whoever may remove a machine may bring it back.
        group.MapPost("/shops/{id}/devices/{deviceId}/restore", RestoreDeviceAsync)
            .RequireDashboardPermission(ManageDevicesPermission);

        // Raising the limit is what customers pay for, so it stays with owner and super admins.
        group.MapPut("/shops/{id}/max-devices", SetMaxDevicesAsync).RequireDashboardSuperAdmin();

        // Stopping a shop from working is as weighty as what it pays for: owner and super admins.
        group.MapPut("/shops/{id}/block", SetBlockedAsync).RequireDashboardSuperAdmin();

        // Moves where the shop's data lives (its computer or our server): owner and super admins.
        group.MapPut("/shops/{id}/phone-only", SetPhoneOnlyAsync).RequireDashboardSuperAdmin();

        // Gestion is what turns Programme-only into the full product: owner and super admins.
        group.MapPut("/shops/{id}/gestion", SetGestionAccessAsync).RequireDashboardSuperAdmin();

        // The whole shop - sales, customers, account password hashes: owner and super admins only.
        group.MapGet("/shops/{id}/database", DownloadDatabaseAsync).RequireDashboardSuperAdmin();
    }

    // --- Reading -------------------------------------------------------------------------

    /// <summary>Everything the overview and list need about one shop, worked out once.</summary>
    private sealed record ShopFacts(
        Groupe Groupe,
        IReadOnlyList<Device> ActiveDevices,
        DashboardSubscription? CurrentSubscription,
        BackupInfoDto? Backup,
        DateTime Now,
        IReadOnlyList<StoredSecurityEvent> Security)
    {
        public bool IsOnline => Groupe.Mode == DeploymentModes.Online;

        /// <summary>Attempts to get round the licence in the last week: what puts a shop on the
        /// attention list. The detail page shows the last month.</summary>
        public IReadOnlyList<StoredSecurityEvent> RecentSecurity =>
            Security.Where(e => e.ReceivedAt >= Now.AddDays(-SecurityAlertDays)).ToList();

        public DateTime? LastActivity =>
            new[] { Groupe.LastLicenceCheckAt, ActiveDevices.Max(d => (DateTime?)d.LastSeenAt) }.Max();

        public int? DaysSinceCheck => Groupe.LastLicenceCheckAt is { } at ? (int)(Now - at).TotalDays : null;

        /// <summary>Whether this shop is expected to check in: online, approved, with a computer. A
        /// phone-only shop has none - it lives on our server - and one awaiting approval has not started.</summary>
        private bool ChecksIn => IsOnline && !Groupe.PhoneOnly && !IsPending;

        /// <summary>Silent past its offline days. A current version has locked itself by now; an old
        /// one with the internet cut keeps selling - either way someone should call the shop.</summary>
        public bool GraceExceeded => ChecksIn
            && (Groupe.LastLicenceCheckAt is null || Now - Groupe.LastLicenceCheckAt > TimeSpan.FromDays(Groupe.MaxOfflineDays));

        /// <summary>Silent for a week or more, not yet past its offline days.</summary>
        public bool GraceEndingSoon => ChecksIn && !GraceExceeded
            && DaysSinceCheck >= SilentWarningDays;

        public bool NoSubscription => IsOnline && CurrentSubscription is null;

        public bool SubscriptionEndingSoon => CurrentSubscription?.ContractEndDate is { } end
            && end - Now <= TimeSpan.FromDays(ExpiryWarningDays);

        public bool AtDeviceLimit => ActiveDevices.Count >= Groupe.MaxDevices;

        public bool BackupStale => IsOnline
            && (Backup?.SnapshotAt is not { } at || Now - at > TimeSpan.FromDays(BackupStaleDays));

        public bool IsPending => Groupe.ApprovalStatus == ApprovalStatuses.Pending;

        public string? LatestAppVersion => ActiveDevices
            .Select(d => d.AppVersion).Where(v => !string.IsNullOrEmpty(v))
            .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
            .FirstOrDefault();

        public IEnumerable<AttentionItemDto> Attention()
        {
            // A shop its owner deleted is waiting for permanent removal, not for a fix.
            if (Groupe.IsDeleted) yield break;

            var id = Groupe.Id;
            var name = Groupe.Nom;

            if (IsPending)
                yield return new(id, name, "pending", "info", "Inscription en attente d'approbation");

            // One line per shop however many attempts: the worst one, and how many others.
            var attempts = RecentSecurity;
            if (attempts.Count > 0)
            {
                var worst = attempts
                    .Select(e => (Event: e, Info: SecurityEventKinds.Describe(e.Kind)))
                    .OrderByDescending(x => SeverityRank(x.Info.Severity))
                    .ThenByDescending(x => x.Event.ReceivedAt)
                    .First();
                var others = attempts.Count - 1;
                yield return new(id, name, "security", worst.Info.Severity,
                    $"Tentative de contournement : {worst.Info.Label} ({worst.Event.ReceivedAt:dd/MM HH:mm})" +
                    (others > 0 ? $" et {others} autre(s)" : ""));
            }
            if (GraceExceeded)
                yield return new(id, name, "offline", "critical",
                    DaysSinceCheck is { } d
                        ? $"Silencieuse depuis {d} jours : bloquée, ou utilisée sans se connecter (ancienne version ?). Appelez la boutique."
                        : "Ne s'est jamais connectée au serveur");
            else if (GraceEndingSoon)
                yield return new(id, name, "offline", "warning",
                    $"Silencieuse depuis {DaysSinceCheck} jours (blocage à {Groupe.MaxOfflineDays} jours pour une version à jour)");
            if (NoSubscription)
                yield return new(id, name, "subscription", "critical", "Mode en ligne sans abonnement actif");
            else if (SubscriptionEndingSoon)
                yield return new(id, name, "subscription", "warning",
                    $"Abonnement jusqu'au {CurrentSubscription!.ContractEndDate:dd/MM/yyyy}");
            if (AtDeviceLimit)
                yield return new(id, name, "devices", "info",
                    $"{ActiveDevices.Count}/{Groupe.MaxDevices} appareils : limite atteinte");
            if (BackupStale)
                yield return new(id, name, "backup", "warning",
                    Backup?.SnapshotAt is { } at ? $"Dernière sauvegarde le {at:dd/MM/yyyy}" : "Aucune sauvegarde cloud");
        }

        public ShopSummaryDto ToSummary() => new(
            Groupe.Id,
            Groupe.Nom,
            Groupe.Mode,
            Groupe.ApprovalStatus,
            Groupe.IsBlocked,
            Groupe.MaxDevices,
            ActiveDevices.Count,
            Groupe.MaxOfflineDays,
            Groupe.LastLicenceCheckAt,
            LastActivity,
            LatestAppVersion,
            CurrentSubscription?.ContractEndDate,
            Backup?.SnapshotAt,
            Groupe.CreatedAt,
            Attention().Select(a => SeverityRank(a.Severity)).DefaultIfEmpty(0).Max(),
            Groupe.IsDeleted,
            Groupe.PhoneOnly,
            Groupe.GestionAccess,
            Security.Count,
            Security.Count > 0 ? Security.Max(e => e.ReceivedAt) : null);
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0,
    };

    /// <summary>Loads every desktop shop with its devices, contract and backup state. Shops deleted
    /// by their owner only when asked for: they are listed apart, awaiting permanent removal.</summary>
    private static async Task<List<ShopFacts>> LoadShopsAsync(
        LonniiDbContext db, BackupStore backups, SecurityEventStore security, string? onlyGroupId, CancellationToken ct,
        bool includeDeleted = false)
    {
        var now = DateTime.UtcNow;

        var groupes = await db.Groupes.AsNoTracking()
            .Where(g => includeDeleted || !g.IsDeleted)
            .Where(g => onlyGroupId == null || g.Id == onlyGroupId)
            // A phone-only shop has no devices row and never checks in, so it used to be missing here
            // once approved: its flag alone makes it one of ours.
            .Where(g => db.Devices.Any(d => d.GroupId == g.Id)
                        || g.LastLicenceCheckAt != null
                        || g.PhoneOnly
                        || g.ApprovalStatus != ApprovalStatuses.Approved)
            .ToListAsync(ct);

        var ids = groupes.Select(g => g.Id).ToList();

        var devices = await db.Devices.AsNoTracking()
            .Where(d => ids.Contains(d.GroupId) && d.RevokedAt == null)
            .ToListAsync(ct);

        var subscriptions = await db.DashboardSubscriptions.AsNoTracking()
            .Where(s => ids.Contains(s.GroupId))
            .ToListAsync(ct);

        return groupes.Select(g => new ShopFacts(
                g,
                devices.Where(d => d.GroupId == g.Id).ToList(),
                subscriptions.Where(s => s.GroupId == g.Id && s.IsCurrentAt(now))
                    .OrderByDescending(s => s.ContractEndDate ?? DateTime.MaxValue)
                    .FirstOrDefault(),
                g.Mode == DeploymentModes.Online ? BackupInfo(backups, g.Id) : null,
                now,
                security.Read(g.Id, now.AddDays(-SecurityHistoryDays))))
            .ToList();
    }

    /// <summary>The backup store only accepts GUID-shaped ids; one odd row in the shared table
    /// must not take the whole overview down, so it simply shows no backup.</summary>
    private static BackupInfoDto? BackupInfo(BackupStore backups, string groupId)
    {
        try { return backups.Info(groupId); }
        catch (ArgumentException) { return null; }
    }

    private static async Task<IResult> OverviewAsync(
        LonniiDbContext db, BackupStore backups, SecurityEventStore security, CancellationToken ct)
    {
        var shops = await LoadShopsAsync(db, backups, security, null, ct);
        var now = DateTime.UtcNow;

        var attention = shops
            .SelectMany(s => s.Attention())
            .OrderByDescending(a => SeverityRank(a.Severity))
            .ThenBy(a => a.ShopName)
            .ToList();

        return Results.Ok(new DesktopOverviewDto(
            TotalShops: shops.Count,
            OnlineShops: shops.Count(s => s.IsOnline),
            LocalShops: shops.Count(s => !s.IsOnline),
            ActiveToday: shops.Count(s => s.LastActivity >= now - ActiveWindow),
            PendingRegistrations: shops.Count(s => s.IsPending),
            OfflineBeyondGrace: shops.Count(s => s.GraceExceeded),
            SubscriptionsEndingSoon: shops.Count(s => s.SubscriptionEndingSoon),
            ShopsAtDeviceLimit: shops.Count(s => s.AtDeviceLimit),
            ActiveDevices: shops.Sum(s => s.ActiveDevices.Count),
            StaleBackups: shops.Count(s => s.BackupStale),
            Attention: attention,
            GeneratedAt: now,
            SecurityAlerts: shops.Count(s => s.RecentSecurity.Count > 0)));
    }

    private static async Task<IResult> ListShopsAsync(
        string? search, string? filter, LonniiDbContext db, BackupStore backups, SecurityEventStore security,
        CancellationToken ct)
    {
        var shops = await LoadShopsAsync(db, backups, security, null, ct, includeDeleted: filter == "deleted");

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            shops = shops.Where(s => s.Groupe.Nom.Contains(term, StringComparison.OrdinalIgnoreCase)
                                     || s.Groupe.Id.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        shops = (filter ?? "all") switch
        {
            "online" => shops.Where(s => s.IsOnline).ToList(),
            "local" => shops.Where(s => !s.IsOnline).ToList(),
            "pending" => shops.Where(s => s.IsPending).ToList(),
            "blocked" => shops.Where(s => s.Groupe.IsBlocked).ToList(),
            "deleted" => shops.Where(s => s.Groupe.IsDeleted).ToList(),
            "attention" => shops.Where(s => s.Attention().Any(a => a.Severity != "info")).ToList(),
            "security" => shops.Where(s => s.Security.Count > 0).ToList(),
            _ => shops,
        };

        return Results.Ok(shops
            .Select(s => s.ToSummary())
            .OrderByDescending(s => s.AttentionLevel)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<IResult> ShopDetailAsync(
        string id, LonniiDbContext db, BackupStore backups, SecurityEventStore security, CancellationToken ct)
    {
        var shop = (await LoadShopsAsync(db, backups, security, id, ct, includeDeleted: true)).SingleOrDefault();
        if (shop is null) return Results.NotFound(new ApiError("Boutique introuvable"));

        var owner = await db.Users.AsNoTracking()
            .Where(u => u.IdUser == shop.Groupe.IdUserAdmin)
            .Select(u => new { u.Email, u.FirstName, u.LastName, u.Phone })
            .FirstOrDefaultAsync(ct);

        var memberCount = await db.GroupMembers.CountAsync(m => m.IdGroupe == id, ct);

        var allDevices = await db.Devices.AsNoTracking()
            .Where(d => d.GroupId == id)
            .ToListAsync(ct);

        var latestSubscription = shop.CurrentSubscription
            ?? await db.DashboardSubscriptions.AsNoTracking()
                .Where(s => s.GroupId == id)
                .OrderByDescending(s => s.ContractEndDate)
                .FirstOrDefaultAsync(ct);

        return Results.Ok(new ShopDetailDto(
            shop.ToSummary(),
            owner is null ? null : new ShopOwnerDto(
                owner.Email, $"{owner.FirstName} {owner.LastName}".Trim(), owner.Phone),
            memberCount,
            shop.Groupe.IsBlocked ? shop.Groupe.BlockReason : null,
            [.. allDevices
                .OrderBy(d => d.RevokedAt is null ? 0 : 1)
                .ThenByDescending(d => d.LastSeenAt)
                .Select(d => new AdminDeviceDto(
                    d.Id, d.DeviceName, d.Platform, d.AppVersion, d.LastIpAddress,
                    d.RegisteredAt, d.LastSeenAt, d.RevokedAt, d.RevokedReason))],
            latestSubscription is null ? null : new AdminSubscriptionDto(
                latestSubscription.Statut,
                latestSubscription.ContractStartDate,
                latestSubscription.ContractEndDate,
                latestSubscription.Montant,
                IsCurrent: shop.CurrentSubscription is not null),
            shop.Backup,
            [.. shop.Attention()],
            [.. shop.Security.Select(e =>
            {
                var (label, severity) = SecurityEventKinds.Describe(e.Kind);
                return new AdminSecurityEventDto(
                    e.Kind, label, severity, e.At, e.ReceivedAt, e.Detail, e.DeviceId, e.IpAddress, e.AppVersion, e.FromServer);
            })]));
    }

    // --- Changing ------------------------------------------------------------------------

    /// <summary>
    /// Unbinds a machine, freeing its slot - a broken or replaced till. The row is kept, so the same
    /// machine coming back is still recognised. The shop sees it at its next licence check.
    /// </summary>
    private static async Task<IResult> RevokeDeviceAsync(
        string id,
        string deviceId,
        string? reason,
        ClaimsPrincipal admin,
        LonniiDbContext db,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.GroupId == id, ct);
        if (device is null) return Results.NotFound(new ApiError("Appareil introuvable"));
        if (device.RevokedAt is not null) return Results.NoContent();

        device.RevokedAt = DateTime.UtcNow;
        device.RevokedReason = string.IsNullOrWhiteSpace(reason)
            ? "Retiré depuis le tableau de bord"
            : reason.Trim()[..Math.Min(reason.Trim().Length, 200)];

        await db.SaveChangesAsync(ct);

        logs.CreateLogger("Lonnii.Admin").LogInformation(
            "Dashboard admin {Admin} revoked device {Device} ({Name}) of shop {Shop}",
            DashboardAdminAuth.AdminId(admin), device.Id, device.DeviceName, id);

        return Results.NoContent();
    }

    /// <summary>Why ticking "Téléphone uniquement" removed a machine - also how unticking it finds them.</summary>
    internal const string PhoneOnlyRevokedReason = "Espace passé en téléphone uniquement";

    /// <summary>
    /// Brings a removed machine back. It gets in at its next check-in ("Réessayer" on its lock screen),
    /// which also clears the removal it recorded locally. Refused while the shop is phone-only (it would
    /// have nothing to run against) or when the shop has no free slot - the limit is what it pays for.
    /// </summary>
    private static async Task<IResult> RestoreDeviceAsync(
        string id,
        string deviceId,
        ClaimsPrincipal admin,
        LonniiDbContext db,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == id && !g.IsDeleted, ct);
        if (groupe is null) return Results.NotFound(new ApiError("Boutique introuvable"));

        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.GroupId == id, ct);
        if (device is null) return Results.NotFound(new ApiError("Appareil introuvable"));
        if (device.RevokedAt is null) return Results.NoContent();

        if (groupe.PhoneOnly)
            return Results.Conflict(new ApiError(
                "Cette boutique est en « Téléphone uniquement » : décochez-le d'abord, sinon l'appareil serait refusé."));

        var inUse = await db.Devices.CountAsync(d => d.GroupId == id && d.RevokedAt == null, ct);
        if (inUse >= groupe.MaxDevices)
            return Results.Conflict(new ApiError(
                $"Limite d'appareils atteinte ({inUse}/{groupe.MaxDevices}) : retirez-en un ou augmentez la limite."));

        var previousReason = device.RevokedReason;
        device.RevokedAt = null;
        device.RevokedReason = null;
        await db.SaveChangesAsync(ct);

        logs.CreateLogger("Lonnii.Admin").LogInformation(
            "Dashboard admin {Admin} restored device {Device} ({Name}) of shop {Shop}, removed for: {Reason}",
            DashboardAdminAuth.AdminId(admin), device.Id, device.DeviceName, id, previousReason);

        return Results.NoContent();
    }

    /// <summary>
    /// Sets how many machines a shop may bind. The shop picks it up at its next licence check;
    /// lowering it below the machines already bound blocks new ones but does not unbind any.
    /// </summary>
    private static async Task<IResult> SetMaxDevicesAsync(
        string id,
        SetMaxDevicesRequest request,
        ClaimsPrincipal admin,
        LonniiDbContext db,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        if (request.MaxDevices is < 1 or > MaxDevicesCeiling)
            return Results.BadRequest(new ApiError($"Le nombre d'appareils doit être entre 1 et {MaxDevicesCeiling}."));

        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == id && !g.IsDeleted, ct);
        if (groupe is null) return Results.NotFound(new ApiError("Boutique introuvable"));

        var previous = groupe.MaxDevices;
        groupe.MaxDevices = request.MaxDevices;
        await db.SaveChangesAsync(ct);

        logs.CreateLogger("Lonnii.Admin").LogInformation(
            "Dashboard admin {Admin} changed max devices of shop {Shop} from {Previous} to {New}",
            DashboardAdminAuth.AdminId(admin), id, previous, request.MaxDevices);

        return Results.Ok(new { maxDevices = groupe.MaxDevices });
    }

    /// <summary>
    /// The shop's database as of its latest cloud backup, unpacked into a plain SQLite file that
    /// opens in Lonnii's restore or any SQLite tool. Online shops only: a local shop sends us none.
    /// Product photos are not inside; they are kept beside the snapshot.
    /// </summary>
    private static async Task<IResult> DownloadDatabaseAsync(
        string id,
        ClaimsPrincipal admin,
        LonniiDbContext db,
        BackupStore backups,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        if (groupe is null) return Results.NotFound(new ApiError("Boutique introuvable"));

        (string Path, DateTime At)? latest;
        try { latest = backups.LatestSnapshotFile(id); }
        catch (ArgumentException) { latest = null; }
        if (latest is not { } file)
            return Results.NotFound(new ApiError("Aucune sauvegarde cloud pour cette boutique."));

        logs.CreateLogger("Lonnii.Admin").LogInformation(
            "Dashboard admin {Admin} downloaded the database of shop {Shop} (snapshot {At:O})",
            DashboardAdminAuth.AdminId(admin), id, file.At);

        var gzip = new System.IO.Compression.GZipStream(
            File.OpenRead(file.Path), System.IO.Compression.CompressionMode.Decompress);
        return Results.Stream(gzip, "application/vnd.sqlite3", $"lonnii-{id}-{file.At:yyyyMMdd-HHmm}.db");
    }

    /// <summary>
    /// Blocks or unblocks a shop. Blocked, it can no longer sign in, back up or bind a machine here,
    /// and each till learns it - with the reason, shown to the shop - at its next licence check.
    /// </summary>
    private static async Task<IResult> SetBlockedAsync(
        string id,
        SetBlockedRequest request,
        ClaimsPrincipal admin,
        LonniiDbContext db,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        var reason = request.Reason?.Trim();
        if (request.Blocked && string.IsNullOrEmpty(reason))
            return Results.BadRequest(new ApiError("Indiquez la raison du blocage : elle sera affichée à la boutique."));
        if (reason?.Length > 300)
            return Results.BadRequest(new ApiError("La raison ne doit pas dépasser 300 caractères."));

        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == id && !g.IsDeleted, ct);
        if (groupe is null) return Results.NotFound(new ApiError("Boutique introuvable"));

        groupe.IsBlocked = request.Blocked;
        groupe.BlockReason = request.Blocked ? reason : null;
        groupe.BlockedAt = request.Blocked ? DateTime.UtcNow : null;
        await db.SaveChangesAsync(ct);

        logs.CreateLogger("Lonnii.Admin").LogInformation(
            "Dashboard admin {Admin} {Action} shop {Shop} ({Reason})",
            DashboardAdminAuth.AdminId(admin), request.Blocked ? "blocked" : "unblocked", id, reason);

        return Results.NoContent();
    }

    /// <summary>
    /// Grants or withdraws Gestion (Opérations, Finance, Administration). Without it the shop only
    /// has Programme. Phones and remote access read this row directly; a shop's own computer gets it
    /// inside its signed licence at its next check (within the hour while it is online).
    /// </summary>
    private static async Task<IResult> SetGestionAccessAsync(
        string id,
        SetGestionAccessRequest request,
        ClaimsPrincipal admin,
        LonniiDbContext db,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == id && !g.IsDeleted, ct);
        if (groupe is null) return Results.NotFound(new ApiError("Boutique introuvable"));

        groupe.GestionAccess = request.Enabled;
        await db.SaveChangesAsync(ct);

        logs.CreateLogger("Lonnii.Admin").LogInformation(
            "Dashboard admin {Admin} set gestion access of shop {Shop} to {Enabled}",
            DashboardAdminAuth.AdminId(admin), id, request.Enabled);

        return Results.Ok(new { gestionAccess = groupe.GestionAccess });
    }

    /// <summary>
    /// Makes a shop phone-only (our server is its host) or gives it back to computers. Turning it on
    /// puts the shop online and retires its bound machines: a computer kept working would hold its own
    /// copy of the data, apart from what the phones now change here. Its data on our server is what
    /// the phones see - a computer shop moved over brings only what was last sent here.
    /// </summary>
    private static async Task<IResult> SetPhoneOnlyAsync(
        string id,
        SetPhoneOnlyRequest request,
        ClaimsPrincipal admin,
        LonniiDbContext db,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == id && !g.IsDeleted, ct);
        if (groupe is null) return Results.NotFound(new ApiError("Boutique introuvable"));

        groupe.PhoneOnly = request.PhoneOnly;
        var retired = 0;
        var restored = 0;

        if (request.PhoneOnly)
        {
            groupe.Mode = DeploymentModes.Online;

            var now = DateTime.UtcNow;
            var devices = await db.Devices.Where(d => d.GroupId == id && d.RevokedAt == null).ToListAsync(ct);
            foreach (var device in devices)
            {
                device.RevokedAt = now;
                device.RevokedReason = PhoneOnlyRevokedReason;
            }
            retired = devices.Count;
        }
        else if (request.RestoreComputers)
        {
            // Unticking used to leave every machine the switch had removed locked out, with no way back
            // but the database. The ones it removed come back, most recently used first, within the limit.
            var inUse = await db.Devices.CountAsync(d => d.GroupId == id && d.RevokedAt == null, ct);
            var removed = await db.Devices
                .Where(d => d.GroupId == id && d.RevokedAt != null && d.RevokedReason == PhoneOnlyRevokedReason)
                .OrderByDescending(d => d.LastSeenAt)
                .ToListAsync(ct);
            foreach (var device in removed.Take(Math.Max(0, groupe.MaxDevices - inUse)))
            {
                device.RevokedAt = null;
                device.RevokedReason = null;
                restored++;
            }
        }

        await db.SaveChangesAsync(ct);

        logs.CreateLogger("Lonnii.Admin").LogInformation(
            "Dashboard admin {Admin} set shop {Shop} phone-only={PhoneOnly} ({Retired} machines retired, {Restored} restored)",
            DashboardAdminAuth.AdminId(admin), id, request.PhoneOnly, retired, restored);

        return Results.Ok(new { phoneOnly = groupe.PhoneOnly, mode = groupe.Mode, retiredDevices = retired, restoredDevices = restored });
    }
}

// --- Contracts (read by the dashboard's JavaScript, so camelCase JSON) ----------------------

public sealed record AttentionItemDto(string ShopId, string ShopName, string Kind, string Severity, string Message);

public sealed record DesktopOverviewDto(
    int TotalShops,
    int OnlineShops,
    int LocalShops,
    int ActiveToday,
    int PendingRegistrations,
    int OfflineBeyondGrace,
    int SubscriptionsEndingSoon,
    int ShopsAtDeviceLimit,
    int ActiveDevices,
    int StaleBackups,
    IReadOnlyList<AttentionItemDto> Attention,
    DateTime GeneratedAt,
    int SecurityAlerts = 0);

public sealed record ShopSummaryDto(
    string Id,
    string Name,
    string Mode,
    string ApprovalStatus,
    bool IsBlocked,
    int MaxDevices,
    int ActiveDevices,
    int MaxOfflineDays,
    DateTime? LastLicenceCheckAt,
    DateTime? LastActivityAt,
    string? AppVersion,
    DateTime? SubscriptionEndsAt,
    DateTime? LastBackupAt,
    DateTime CreatedAt,
    int AttentionLevel,
    bool IsDeleted,
    bool PhoneOnly,
    bool GestionAccess,
    int SecurityEvents = 0,
    DateTime? LastSecurityEventAt = null);

/// <summary>One attempt to get round the licence, for a shop's page in the dashboard.</summary>
/// <param name="At">When the shop's computer noticed it, by its own clock.</param>
/// <param name="ReceivedAt">When our server received it (or noticed it, for <paramref name="FromServer"/>).</param>
public sealed record AdminSecurityEventDto(
    string Kind,
    string Label,
    string Severity,
    DateTime At,
    DateTime ReceivedAt,
    string? Detail,
    string? DeviceId,
    string? IpAddress,
    string? AppVersion,
    bool FromServer);

public sealed record ShopOwnerDto(string Email, string Name, string? Phone);

public sealed record AdminDeviceDto(
    string Id,
    string? Name,
    string Platform,
    string? AppVersion,
    string? LastIpAddress,
    DateTime RegisteredAt,
    DateTime LastSeenAt,
    DateTime? RevokedAt,
    string? RevokedReason);

public sealed record AdminSubscriptionDto(
    string Status, DateTime? StartsAt, DateTime? EndsAt, decimal Amount, bool IsCurrent);

public sealed record ShopDetailDto(
    ShopSummaryDto Shop,
    ShopOwnerDto? Owner,
    int MemberCount,
    string? BlockReason,
    IReadOnlyList<AdminDeviceDto> Devices,
    AdminSubscriptionDto? Subscription,
    BackupInfoDto? Backup,
    IReadOnlyList<AttentionItemDto> Attention,
    IReadOnlyList<AdminSecurityEventDto>? SecurityEvents = null);

public sealed record SetMaxDevicesRequest(int MaxDevices);

public sealed record SetBlockedRequest(bool Blocked, string? Reason);

/// <param name="RestoreComputers">When unticking: bring back the machines that ticking it removed.</param>
public sealed record SetPhoneOnlyRequest(bool PhoneOnly, bool RestoreComputers = false);

public sealed record SetGestionAccessRequest(bool Enabled);
