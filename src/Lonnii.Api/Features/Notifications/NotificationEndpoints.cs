using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Notifications;

/// <summary>
/// The top bar's bell. Nothing is stored: every call works the list out from the data as it is
/// now, so fixing the problem (restocking, closing the till) is what makes its line go away.
/// Stock and cloud backup are for admins only; announcements and open tills are for everyone.
/// "Update available" is not here - the client already checks for updates itself.
/// </summary>
public static class NotificationEndpoints
{
    /// <summary>A backup that has not succeeded for this long is reported even without an error.</summary>
    private static readonly TimeSpan BackupStaleAfter = TimeSpan.FromDays(2);

    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications", ListAsync).WithTags("Notifications").RequireGroupScope();
        // Same list for phones signed in online: on www.lonnii.com Node owns /api/notifications, and
        // nginx already sends /api/live/ to this API.
        app.MapGet("/api/live/notifications", ListAsync).WithTags("Notifications").RequireGroupScope();

        // The phone app's own routes, kept at Lonnii Business's path and body. The user comes from
        // the sign-in token, never from the body's userId: a phone cannot subscribe to someone else.
        app.MapPost("/api/push-token", SavePushTokenAsync).WithTags("Notifications").RequireAuthorization();
        app.MapDelete("/api/push-token", RemovePushTokenAsync).WithTags("Notifications").RequireAuthorization();
    }

    private static async Task<IResult> SavePushTokenAsync(
        PushTokenRequest request, System.Security.Claims.ClaimsPrincipal principal, LonniiDbContext db, CancellationToken ct)
    {
        if (!IsExpoToken(request.PushToken)) return Results.BadRequest(new ApiError("Jeton de notification invalide."));
        var userId = principal.FindFirst(TokenService.UserIdClaim)!.Value;

        var row = await db.PushDevices.FirstOrDefaultAsync(d => d.UserId == userId && d.PushToken == request.PushToken, ct);
        if (row is null)
        {
            row = new PushDevice { UserId = userId, PushToken = request.PushToken! };
            db.PushDevices.Add(row);
        }
        row.Platform = request.Platform is "android" or "ios" ? request.Platform : null;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { success = true });
    }

    /// <summary>On sign-out, so a handed-on phone stops getting the previous user's alerts.</summary>
    private static async Task<IResult> RemovePushTokenAsync(
        [Microsoft.AspNetCore.Mvc.FromBody] PushTokenRequest request, System.Security.Claims.ClaimsPrincipal principal,
        LonniiDbContext db, CancellationToken ct)
    {
        var userId = principal.FindFirst(TokenService.UserIdClaim)!.Value;
        await db.PushDevices.Where(d => d.UserId == userId && d.PushToken == request.PushToken).ExecuteDeleteAsync(ct);
        return Results.Ok(new { success = true });
    }

    /// <summary>Expo's tokens look like <c>ExponentPushToken[...]</c>; anything else would only bounce.</summary>
    private static bool IsExpoToken(string? token) =>
        token is { Length: < 256 } && (token.StartsWith("ExponentPushToken[") || token.StartsWith("ExpoPushToken["))
        && token.EndsWith(']');

    private static async Task<IResult> ListAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct) =>
        Results.Ok(new NotificationsResponse(
            await BuildAsync(scope.GroupId, scope.UserId, scope.IsAdmin || scope.IsAdminGeneral, db, ct)));

    /// <summary>One user's alerts in one espace. Shared with PushAlertService, so the phone's lock
    /// screen and the desktop's bell always follow the same rules.</summary>
    public static async Task<List<NotificationDto>> BuildAsync(
        string groupId, string userId, bool isAdmin, LonniiDbContext db, CancellationToken ct)
    {
        var items = new List<NotificationDto>();

        items.AddRange(await CaisseAsync(groupId, userId, isAdmin, db, ct));
        if (isAdmin)
        {
            items.AddRange(await StockAsync(groupId, db, ct));
            if (await BackupAsync(groupId, db, ct) is { } backup) items.Add(backup);
        }
        items.AddRange(await AnnouncementsAsync(groupId, userId, db, ct));

        return items;
    }

    /// <summary>Tills opened before today and never closed. A user sees their own; an admin
    /// sees everyone's, since they are the one who chases it up.</summary>
    private static async Task<IEnumerable<NotificationDto>> CaisseAsync(
        string groupId, string userId, bool isAdmin, LonniiDbContext db, CancellationToken ct)
    {
        var todayStart = DateTime.Today.ToUniversalTime();
        var query = db.Caisses.AsNoTracking()
            .Where(c => c.GroupId == groupId && c.Status == CaisseStatus.Open && c.DateOuverture < todayStart);
        if (!isAdmin) query = query.Where(c => c.UserId == userId);

        var open = await query.OrderBy(c => c.DateOuverture).ToListAsync(ct);
        return open.Select(c => new NotificationDto(
            "caisse", "warning",
            c.UserId == userId ? "Votre caisse n'est pas clôturée" : $"Caisse de {c.UserName} non clôturée",
            $"Ouverte le {c.DateOuverture.ToLocalTime():dd/MM/yyyy à HH:mm}.",
            // Only someone else's till can be force-closed; one's own is closed by counting it.
            "ventes", c.UserId == userId ? null : c.Id.ToString()));
    }

    /// <summary>Out of stock and low stock, one line each, naming the first few products.
    /// Products that are never counted (vente libre, stock illimité) are left out.</summary>
    private static async Task<IEnumerable<NotificationDto>> StockAsync(
        string groupId, LonniiDbContext db, CancellationToken ct)
    {
        var low = await db.Products.AsNoTracking()
            .Where(p => p.GroupId == groupId && p.IsActive && p.DeletedAt == null
                        && !p.VenteLibre && !p.StockIllimite && p.Quantity <= p.MinimumThreshold)
            .OrderBy(p => p.Quantity).ThenBy(p => p.Name)
            .Select(p => new { p.Name, p.Quantity })
            .ToListAsync(ct);

        var result = new List<NotificationDto>();
        var empty = low.Where(p => p.Quantity <= 0).Select(p => p.Name).ToList();
        var few = low.Where(p => p.Quantity > 0).Select(p => p.Name).ToList();

        if (empty.Count > 0)
            result.Add(new NotificationDto("stock", "danger",
                empty.Count == 1 ? "1 produit en rupture" : $"{empty.Count} produits en rupture",
                Names(empty), "gestion-de-stock"));
        if (few.Count > 0)
            result.Add(new NotificationDto("stock", "warning",
                few.Count == 1 ? "1 produit en stock bas" : $"{few.Count} produits en stock bas",
                Names(few), "gestion-de-stock"));
        return result;
    }

    private static string Names(List<string> names) =>
        string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" et {names.Count - 3} autre(s)" : "");

    /// <summary>Only for espaces that back up to the cloud: the last attempt failed, or nothing
    /// has succeeded for <see cref="BackupStaleAfter"/>.</summary>
    private static async Task<NotificationDto?> BackupAsync(string groupId, LonniiDbContext db, CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        var applicable = groupe is not null
            && Lonnii.Shared.Security.DeploymentModes.RequiresSubscription(groupe.Mode)
            && !string.IsNullOrWhiteSpace(groupe.LicenceServerUrl);
        if (!applicable) return null;

        var state = await db.CloudBackupStates.AsNoTracking().FirstOrDefaultAsync(s => s.GroupId == groupId, ct);
        var last = state?.LastSuccessAt;
        var failed = state?.LastError is not null && (last is null || state.LastAttemptAt > last);
        var stale = last is null || DateTime.UtcNow - last.Value > BackupStaleAfter;
        if (!failed && !stale) return null;

        var since = last is null ? "Aucune sauvegarde réussie pour l'instant." : $"Dernière réussie le {last.Value.ToLocalTime():dd/MM/yyyy à HH:mm}.";
        return new NotificationDto("backup", "danger", "Sauvegarde cloud en échec",
            failed ? $"{state!.LastError} {since}" : since, "parametres");
    }

    /// <summary>Today's and tomorrow's announcements meant for the whole espace or for this user.</summary>
    private static async Task<IEnumerable<NotificationDto>> AnnouncementsAsync(
        string groupId, string userId, LonniiDbContext db, CancellationToken ct)
    {
        var today = DateTime.Today;
        var end = today.AddDays(2);
        var list = await db.ProgrammeAnnouncements.AsNoTracking()
            .Where(a => a.GroupId == groupId && a.Date >= today && a.Date < end
                        && (a.UserId == null || a.UserId == userId))
            .OrderBy(a => a.Date)
            .ToListAsync(ct);

        return list.Select(a => new NotificationDto(
            "annonce", "info",
            (a.Date.Date == today ? "Aujourd'hui · " : "Demain · ") + a.Titre,
            a.Message, "program"));
    }
}
