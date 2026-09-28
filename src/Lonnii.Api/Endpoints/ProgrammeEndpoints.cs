using Lonnii.Api.Security;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The Programme module: a printable board of each worker's work days, meeting days and days
/// off, plus announcements shown alongside them. New to the desktop port - Lonnii Business's
/// own "Programme" (backend/routes/calendar.js, <c>calendar_events</c>) is a full RSVP
/// calendar with participants, attachments and comments, built for scheduling meetings rather
/// than posting a shop's staff roster, so it is not reused here; only its name and its
/// existing option-privilege gates (<see cref="Priv.Option"/>: view/create/edit/delete
/// events) carry over; see AppMenu's "program" entry and the seeded
/// <c>option_privileges</c> catalogue, both already wired for a module named exactly this.
/// </summary>
public static class ProgrammeEndpoints
{
    public static void MapProgrammeEndpoints(this IEndpointRouteBuilder app)
    {
        var programme = app.MapGroup("/api/programme").WithTags("Programme");

        programme.MapGet("/", ListAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Option.ViewProgramme);

        programme.MapPost("/entries", SaveEntryAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Option.CreateEvents);
        programme.MapDelete("/entries/{id:int}", DeleteEntryAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Option.DeleteEvents);

        programme.MapPost("/announcements", SaveAnnouncementAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Option.CreateEvents);
        programme.MapPut("/announcements/{id:int}", UpdateAnnouncementAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Option.EditEvents);
        programme.MapDelete("/announcements/{id:int}", DeleteAnnouncementAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Option.DeleteEvents);
    }

    /// <summary>Every entry and announcement in range. <paramref name="userId"/> narrows the
    /// entries to one worker's board and drops announcements addressed to somebody else -
    /// group-wide ones (<see cref="ProgrammeAnnouncement.UserId"/> null) always come through,
    /// which is what lets a worker's printed sheet carry both.</summary>
    private static async Task<IResult> ListAsync(
        DateOnly start, DateOnly end, string? userId,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (end < start) return Results.BadRequest(new ApiError("La date de fin doit suivre la date de début"));

        var from = start.ToDateTime(TimeOnly.MinValue);
        var to = end.ToDateTime(TimeOnly.MinValue).AddDays(1);

        var entryQuery = db.ProgrammeEntries.AsNoTracking()
            .Where(e => e.GroupId == scope.GroupId && e.Date >= from && e.Date < to);
        if (!string.IsNullOrWhiteSpace(userId)) entryQuery = entryQuery.Where(e => e.UserId == userId);

        var entries = await entryQuery.OrderBy(e => e.Date).ToListAsync(ct);

        var announcementQuery = db.ProgrammeAnnouncements.AsNoTracking()
            .Where(a => a.GroupId == scope.GroupId && a.Date >= from && a.Date < to);
        if (!string.IsNullOrWhiteSpace(userId))
            announcementQuery = announcementQuery.Where(a => a.UserId == null || a.UserId == userId);

        var announcements = await announcementQuery.OrderBy(a => a.Date).ToListAsync(ct);

        var names = await DisplayNamesAsync(db,
            entries.Select(e => (string?)e.UserId)
                .Concat(announcements.Select(a => a.UserId))
                .Concat(announcements.Select(a => a.CreatedBy)),
            ct);

        return Results.Ok(new ProgrammeResponse(
            entries.Select(e => ToDto(e, names)).ToList(),
            announcements.Select(a => ToDto(a, names)).ToList()));
    }

    /// <summary>Sets one worker's day: replaces whatever entry already exists for that worker
    /// and date, or creates one - the board has exactly one card per cell, so there is nothing
    /// to merge.</summary>
    private static async Task<IResult> SaveEntryAsync(
        SaveProgrammeEntryRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return Results.BadRequest(new ApiError("Le membre est requis"));

        if (request.Type is not (ProgrammeEntryTypes.Travail or ProgrammeEntryTypes.Reunion or ProgrammeEntryTypes.Repos))
            return Results.BadRequest(new ApiError("Type de journée invalide"));

        var isMember = await db.GroupMembers
            .AnyAsync(m => m.IdGroupe == scope.GroupId && m.IdUser == request.UserId, ct);
        if (!isMember) return Results.NotFound(new ApiError("Ce membre est introuvable dans le groupe"));

        var date = request.Date.ToDateTime(TimeOnly.MinValue);
        var entry = await db.ProgrammeEntries.FirstOrDefaultAsync(
            e => e.GroupId == scope.GroupId && e.UserId == request.UserId && e.Date == date, ct);

        if (entry is null)
        {
            entry = new ProgrammeEntry
            {
                GroupId = scope.GroupId, UserId = request.UserId, Date = date, CreatedBy = scope.UserId,
            };
            db.ProgrammeEntries.Add(entry);
        }

        entry.Type = request.Type;
        entry.HeureDebut = request.HeureDebut;
        entry.HeureFin = request.HeureFin;
        entry.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        entry.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        var names = await DisplayNamesAsync(db, [entry.UserId], ct);
        return Results.Ok(ToDto(entry, names));
    }

    private static async Task<IResult> DeleteEntryAsync(
        int id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var entry = await db.ProgrammeEntries.FirstOrDefaultAsync(e => e.Id == id && e.GroupId == scope.GroupId, ct);
        if (entry is null) return Results.NotFound(new ApiError("Journée introuvable"));

        db.ProgrammeEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    private static async Task<IResult> SaveAnnouncementAsync(
        SaveProgrammeAnnouncementRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Titre) || string.IsNullOrWhiteSpace(request.Message))
            return Results.BadRequest(new ApiError("Le titre et le message sont requis"));

        if (request.UserId is not null)
        {
            var isMember = await db.GroupMembers
                .AnyAsync(m => m.IdGroupe == scope.GroupId && m.IdUser == request.UserId, ct);
            if (!isMember) return Results.NotFound(new ApiError("Ce membre est introuvable dans le groupe"));
        }

        var announcement = new ProgrammeAnnouncement
        {
            GroupId = scope.GroupId,
            UserId = request.UserId,
            Titre = request.Titre.Trim(),
            Message = request.Message.Trim(),
            Date = request.Date.ToDateTime(TimeOnly.MinValue),
            CreatedBy = scope.UserId,
        };

        db.ProgrammeAnnouncements.Add(announcement);
        await db.SaveChangesAsync(ct);

        var names = await DisplayNamesAsync(db, [announcement.UserId, announcement.CreatedBy], ct);
        return Results.Created($"/api/programme/announcements/{announcement.Id}", ToDto(announcement, names));
    }

    private static async Task<IResult> UpdateAnnouncementAsync(
        int id, SaveProgrammeAnnouncementRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var announcement = await db.ProgrammeAnnouncements
            .FirstOrDefaultAsync(a => a.Id == id && a.GroupId == scope.GroupId, ct);
        if (announcement is null) return Results.NotFound(new ApiError("Annonce introuvable"));

        if (string.IsNullOrWhiteSpace(request.Titre) || string.IsNullOrWhiteSpace(request.Message))
            return Results.BadRequest(new ApiError("Le titre et le message sont requis"));

        if (request.UserId is not null)
        {
            var isMember = await db.GroupMembers
                .AnyAsync(m => m.IdGroupe == scope.GroupId && m.IdUser == request.UserId, ct);
            if (!isMember) return Results.NotFound(new ApiError("Ce membre est introuvable dans le groupe"));
        }

        announcement.UserId = request.UserId;
        announcement.Titre = request.Titre.Trim();
        announcement.Message = request.Message.Trim();
        announcement.Date = request.Date.ToDateTime(TimeOnly.MinValue);

        await db.SaveChangesAsync(ct);

        var names = await DisplayNamesAsync(db, [announcement.UserId, announcement.CreatedBy], ct);
        return Results.Ok(ToDto(announcement, names));
    }

    private static async Task<IResult> DeleteAnnouncementAsync(
        int id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var announcement = await db.ProgrammeAnnouncements
            .FirstOrDefaultAsync(a => a.Id == id && a.GroupId == scope.GroupId, ct);
        if (announcement is null) return Results.NotFound(new ApiError("Annonce introuvable"));

        db.ProgrammeAnnouncements.Remove(announcement);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    // --- Mapping ---

    private static ProgrammeEntryDto ToDto(ProgrammeEntry e, IReadOnlyDictionary<string, string> names) => new(
        e.Id, e.UserId, names.TryGetValue(e.UserId, out var name) ? name : e.UserId,
        DateOnly.FromDateTime(e.Date), e.Type, e.HeureDebut, e.HeureFin, e.Note);

    private static ProgrammeAnnouncementDto ToDto(ProgrammeAnnouncement a, IReadOnlyDictionary<string, string> names) => new(
        a.Id, a.UserId, a.UserId is { } uid && names.TryGetValue(uid, out var name) ? name : null,
        a.Titre, a.Message, DateOnly.FromDateTime(a.Date),
        a.CreatedBy is { } cid && names.TryGetValue(cid, out var creator) ? creator : null);

    /// <summary>Duplicated from other endpoint classes rather than shared - see
    /// CaisseEndpoints's own copy for why.</summary>
    private static async Task<Dictionary<string, string>> DisplayNamesAsync(
        LonniiDbContext db, IEnumerable<string?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => id is not null).Distinct().ToList();
        if (ids.Count == 0) return [];

        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.IdUser))
            .Select(u => new { u.IdUser, u.FirstName, u.LastName, u.Username, u.Email })
            .ToListAsync(ct);

        return users.ToDictionary(u => u.IdUser!, u =>
        {
            var full = $"{u.FirstName} {u.LastName}".Trim();
            return !string.IsNullOrWhiteSpace(full) ? full : u.Username ?? u.Email;
        })!;
    }
}
