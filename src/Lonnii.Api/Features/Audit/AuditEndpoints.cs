using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Audit;

/// <summary>
/// The Audit module's "Planning &amp; Présences": who is connected, who arrived late, who has
/// not come in, and hours worked against hours planned. Ported from the attendance half of
/// backend/routes/audit.js.
///
/// <para>
/// Two things differ from the web app. The planned hours come from the Programme board
/// (<see cref="ProgrammeEntry"/>) rather than <c>work_schedules</c>, which the desktop replaced
/// with that board. And presence comes from a client heartbeat rather than sockets - see
/// <see cref="MemberWorkLog"/>.
/// </para>
///
/// <para>
/// Times are stored in UTC. The shop's calendar day and its planned hours are local, so the
/// client sends its UTC offset; the server's own clock zone (UTC on OCI) never enters into it.
/// </para>
/// </summary>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var audit = app.MapGroup("/api/audit").WithTags("Audit");

        // Every signed-in member reports their own presence; only viewing it is restricted.
        audit.MapPost("/presence", HeartbeatAsync).RequireGroupScope();
        audit.MapPost("/presence/end", EndAsync).RequireGroupScope();

        audit.MapGet("/attendance", AttendanceAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewAudit);
        audit.MapGet("/attendance/{userId}/history", HistoryAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewAudit);
    }

    /// <summary>
    /// Extends the caller's open stretch for today, or opens one. Any other open stretch -
    /// yesterday's, or one gone quiet past <see cref="AttendanceRules.StaleAfter"/> - is closed
    /// at its last heartbeat first, so a till switched off mid-afternoon stops counting then.
    /// </summary>
    private static async Task<IResult> HeartbeatAsync(
        PresenceHeartbeatRequest request, GroupScope scope, HttpContext http, LonniiDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var today = LocalToday(now, request.UtcOffsetMinutes);

        var open = await db.MemberWorkLogs
            .Where(l => l.GroupId == scope.GroupId && l.UserId == scope.UserId && l.LogoutAt == null)
            .OrderByDescending(l => l.LoginAt)
            .ToListAsync(ct);

        MemberWorkLog? current = null;
        foreach (var row in open)
        {
            var lastSeen = row.LastSeenAt ?? row.LoginAt;
            if (current is null && row.SessionDate == today && now - lastSeen <= AttendanceRules.StaleAfter)
                current = row;
            else
                AttendanceRules.Close(row, lastSeen);
        }

        if (current is null)
        {
            current = new MemberWorkLog
            {
                GroupId = scope.GroupId,
                UserId = scope.UserId,
                SessionDate = today,
                LoginAt = now,
                IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            };
            db.MemberWorkLogs.Add(current);
        }

        current.LastSeenAt = now;
        current.CurrentModule = Trim(request.Module, 64);
        current.DeviceName = Trim(request.DeviceName, 255) ?? current.DeviceName;

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Closes the caller's open stretches now - sent on sign-out and when the window closes.</summary>
    private static async Task<IResult> EndAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var open = await db.MemberWorkLogs
            .Where(l => l.GroupId == scope.GroupId && l.UserId == scope.UserId && l.LogoutAt == null)
            .ToListAsync(ct);

        foreach (var row in open) AttendanceRules.Close(row, now);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AttendanceAsync(
        int? offset, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var utcOffset = offset ?? 0;
        var now = DateTime.UtcNow;
        var today = LocalToday(now, utcOffset);
        var weekStart = AttendanceRules.WeekStart(today);

        var members = await MembersAsync(db, scope.GroupId, ct);
        var ids = members.Select(m => m.UserId).ToList();

        var logs = await db.MemberWorkLogs.AsNoTracking()
            .Where(l => l.GroupId == scope.GroupId && ids.Contains(l.UserId) && l.SessionDate >= weekStart)
            .ToListAsync(ct);

        var entries = await db.ProgrammeEntries.AsNoTracking()
            .Where(e => e.GroupId == scope.GroupId && ids.Contains(e.UserId) && e.Date >= weekStart && e.Date <= today)
            .ToListAsync(ct);

        var result = members.Select(m =>
        {
            var mine = logs.Where(l => l.UserId == m.UserId).ToList();
            var todayRows = mine.Where(l => l.SessionDate == today).OrderBy(l => l.LoginAt).ToList();
            var openRow = mine.Where(l => l.LogoutAt == null).MaxBy(l => l.LastSeenAt ?? l.LoginAt);
            var latest = mine.MaxBy(l => l.LastSeenAt ?? l.LogoutAt ?? l.LoginAt);

            var todayEntry = entries.FirstOrDefault(e => e.UserId == m.UserId && e.Date == today);
            var plannedStart = todayEntry?.HeureDebut is { } start && todayEntry.Type != ProgrammeEntryTypes.Repos
                ? ToUtc(today + start, utcOffset)
                : (DateTime?)null;

            var firstLogin = todayRows.FirstOrDefault()?.LoginAt;
            var worked = mine.Sum(AttendanceRules.Minutes);
            var planned = entries.Where(e => e.UserId == m.UserId).Sum(AttendanceRules.PlannedMinutes);

            return new AttendanceMemberDto(
                m.UserId, m.Name, m.Email, m.Role,
                AttendanceRules.Status(now, openRow?.LastSeenAt, openRow is not null, firstLogin,
                    todayEntry?.Type, plannedStart),
                AttendanceRules.RetardMinutes(firstLogin, plannedStart),
                firstLogin,
                latest?.LastSeenAt ?? latest?.LogoutAt,
                openRow?.CurrentModule,
                latest?.DeviceName,
                todayRows.FirstOrDefault()?.IpAddress ?? latest?.IpAddress,
                todayEntry?.Type,
                todayEntry?.HeureDebut,
                todayEntry?.HeureFin,
                worked,
                planned,
                AttendanceRules.Productivity(worked, planned));
        })
        .OrderBy(a => StatusOrder(a.Status))
        .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

        return Results.Ok(new AttendanceResponse(DateOnly.FromDateTime(today), result));
    }

    /// <summary>The last 30 days for one member: every day they connected, and every day they
    /// were planned to work even if they never did - an absence is the thing worth seeing.</summary>
    private static async Task<IResult> HistoryAsync(
        string userId, int? offset, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var members = await MembersAsync(db, scope.GroupId, ct);
        var member = members.FirstOrDefault(m => m.UserId == userId);
        if (member is null) return Results.NotFound(new ApiError("Ce membre est introuvable dans le groupe"));

        var today = LocalToday(DateTime.UtcNow, offset ?? 0);
        var from = today.AddDays(-29);

        var logs = await db.MemberWorkLogs.AsNoTracking()
            .Where(l => l.GroupId == scope.GroupId && l.UserId == userId && l.SessionDate >= from)
            .ToListAsync(ct);

        var entries = await db.ProgrammeEntries.AsNoTracking()
            .Where(e => e.GroupId == scope.GroupId && e.UserId == userId && e.Date >= from && e.Date <= today)
            .ToListAsync(ct);

        var days = logs.Select(l => l.SessionDate.Date)
            .Concat(entries.Where(e => AttendanceRules.PlannedMinutes(e) > 0).Select(e => e.Date.Date))
            .Distinct()
            .OrderByDescending(d => d)
            .Select(d =>
            {
                var rows = logs.Where(l => l.SessionDate.Date == d).OrderBy(l => l.LoginAt).ToList();
                var entry = entries.FirstOrDefault(e => e.Date.Date == d);
                return new WorkDayDto(
                    DateOnly.FromDateTime(d),
                    rows.Sum(AttendanceRules.Minutes),
                    entry?.Type, entry?.HeureDebut, entry?.HeureFin,
                    rows.Select(r => new WorkSessionDto(
                        r.LoginAt, r.LogoutAt ?? r.LastSeenAt, AttendanceRules.Minutes(r),
                        r.LogoutAt is null, r.DeviceName, r.IpAddress)).ToList());
            })
            .ToList();

        return Results.Ok(new MemberWorkHistoryResponse(userId, member.Name, days));
    }

    // --- Helpers ---

    private sealed record Member(string UserId, string Name, string? Email, string Role);

    /// <summary>Everyone in the group with their role, the creator reading as admin - the same
    /// resolution as the member list in GroupEndpoints.</summary>
    private static async Task<List<Member>> MembersAsync(LonniiDbContext db, string groupId, CancellationToken ct)
    {
        var creatorId = await db.Groupes.Where(g => g.Id == groupId).Select(g => g.IdUserAdmin).FirstAsync(ct);

        var rows = await db.GroupMembers
            .Where(m => m.IdGroupe == groupId)
            .Join(db.Users, m => m.IdUser, u => u.IdUser, (m, u) => u)
            .GroupJoin(
                db.UserRoles.Where(r => r.GroupId == groupId && r.IsActive),
                u => u.IdUser, r => r.UserId,
                (u, roles) => new { u.IdUser, u.FirstName, u.LastName, u.Username, u.Email, Role = roles.Select(r => r.Role).FirstOrDefault() })
            .ToListAsync(ct);

        return rows.Select(r =>
        {
            var full = $"{r.FirstName} {r.LastName}".Trim();
            var name = !string.IsNullOrWhiteSpace(full) ? full : r.Username ?? r.Email ?? r.IdUser;
            var role = r.IdUser == creatorId ? GroupRoles.Admin : r.Role ?? GroupRoles.Member;
            return new Member(r.IdUser, name, r.Email, role);
        }).ToList();
    }

    private static DateTime LocalToday(DateTime utcNow, int offsetMinutes) =>
        DateTime.SpecifyKind(utcNow.AddMinutes(offsetMinutes).Date, DateTimeKind.Unspecified);

    private static DateTime ToUtc(DateTime local, int offsetMinutes) =>
        DateTime.SpecifyKind(local.AddMinutes(-offsetMinutes), DateTimeKind.Utc);

    /// <summary>Who needs attention first: those at work, then the late, then the missing.</summary>
    private static int StatusOrder(string status) => status switch
    {
        AttendanceStatuses.EnLigne => 0,
        AttendanceStatuses.Inactif => 1,
        AttendanceStatuses.Retard => 2,
        AttendanceStatuses.Absent => 3,
        AttendanceStatuses.HorsLigne => 4,
        _ => 5,
    };

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length > max ? value[..max] : value;
    }
}
