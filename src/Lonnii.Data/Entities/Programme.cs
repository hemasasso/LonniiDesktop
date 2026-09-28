namespace Lonnii.Data.Entities;

/// <summary>Values stored in <see cref="ProgrammeEntry.Type"/>.</summary>
public static class ProgrammeEntryTypes
{
    /// <summary>A day the worker is expected in.</summary>
    public const string Travail = "travail";

    /// <summary>A day carrying a meeting, on top of or instead of a work day.</summary>
    public const string Reunion = "reunion";

    /// <summary>A day off, marked deliberately rather than left blank - a blank day on the
    /// printed sheet could mean "not yet planned" or "day off", and only one of those is
    /// worth telling a worker about.</summary>
    public const string Repos = "repos";

    public static readonly IReadOnlyList<string> All = [Travail, Reunion, Repos];
}

/// <summary>
/// One day of one worker's schedule - a freeform board an admin fills in day by day, not a
/// recurring template. New to the desktop port: Lonnii Business's own <c>work_schedules</c>
/// table (backend/migrations/add_work_schedule_tables.sql) is a weekly recurring pattern with
/// no room for a one-off meeting day or a printed announcement, so it is not reused here.
/// </summary>
public class ProgrammeEntry
{
    public int Id { get; set; }
    public string GroupId { get; set; } = string.Empty;

    /// <summary>The worker this day belongs to.</summary>
    public string UserId { get; set; } = string.Empty;

    public DateTime Date { get; set; }

    /// <summary>One of <see cref="ProgrammeEntryTypes"/>.</summary>
    public string Type { get; set; } = ProgrammeEntryTypes.Travail;

    public TimeSpan? HeureDebut { get; set; }
    public TimeSpan? HeureFin { get; set; }

    public string? Note { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A notice shown on the printed programme for the period it falls in - either every worker's
/// sheet (<see cref="UserId"/> null) or one worker's alone.
/// </summary>
public class ProgrammeAnnouncement
{
    public int Id { get; set; }
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Null for a group-wide announcement; otherwise the one worker it targets.</summary>
    public string? UserId { get; set; }

    public string Titre { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Date { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
