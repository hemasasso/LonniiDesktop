using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The attendance arithmetic behind the Audit screen, kept apart from the endpoints so it can
/// be tested without a database. The thresholds are backend/routes/audit.js's own: 5 minutes'
/// grace before a late arrival counts as <i>retard</i>, 30 before a no-show counts as
/// <i>absent</i>.
/// </summary>
public static class AttendanceRules
{
    public static readonly TimeSpan LateGrace = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AbsentAfter = TimeSpan.FromMinutes(30);

    /// <summary>A heartbeat this recent means the member is at the screen.</summary>
    public static readonly TimeSpan OnlineWithin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Past this without a heartbeat the stretch is over. Clients beat once a minute, so this
    /// tolerates a few missed beats (a brief network drop) without splitting the day into
    /// pieces, while a machine that was switched off stops counting within minutes.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>Ends an open stretch at <paramref name="at"/>, as the web app does on logout.</summary>
    public static void Close(MemberWorkLog row, DateTime at)
    {
        if (at < row.LoginAt) at = row.LoginAt;
        row.LogoutAt = at;
        row.DurationMinutes = (int)Math.Round((at - row.LoginAt).TotalMinutes);
    }

    /// <summary>Minutes a stretch counts for: its recorded duration once closed; up to the last
    /// heartbeat while open, so a till left on overnight is not credited with the night.</summary>
    public static int Minutes(MemberWorkLog row)
    {
        if (row.LogoutAt is { } logout)
            return row.DurationMinutes ?? (int)Math.Round((logout - row.LoginAt).TotalMinutes);

        var end = row.LastSeenAt ?? row.LoginAt;
        return Math.Max(0, (int)Math.Round((end - row.LoginAt).TotalMinutes));
    }

    /// <summary>Planned minutes for one Programme day: nothing for a day off or a day with no
    /// hours set. An end before the start is read as running past midnight.</summary>
    public static int PlannedMinutes(ProgrammeEntry entry)
    {
        if (entry.Type == ProgrammeEntryTypes.Repos) return 0;
        if (entry.HeureDebut is not { } start || entry.HeureFin is not { } end) return 0;

        var span = end - start;
        if (span < TimeSpan.Zero) span += TimeSpan.FromDays(1);
        return (int)span.TotalMinutes;
    }

    /// <summary>Minutes late for the first connection of the day, or null within the grace.</summary>
    public static int? RetardMinutes(DateTime? firstLoginUtc, DateTime? plannedStartUtc)
    {
        if (firstLoginUtc is not { } login || plannedStartUtc is not { } start) return null;
        var late = login - start;
        return late > LateGrace ? (int)Math.Round(late.TotalMinutes) : null;
    }

    /// <summary>
    /// Today's status for one member. Being connected wins over everything else - a member on a
    /// day off who is working anyway shows as working. After that the Programme decides.
    /// </summary>
    public static string Status(
        DateTime nowUtc,
        DateTime? lastSeenUtc,
        bool hasOpenStretch,
        DateTime? firstLoginUtc,
        string? scheduleType,
        DateTime? plannedStartUtc)
    {
        if (hasOpenStretch && lastSeenUtc is { } seen)
        {
            if (nowUtc - seen <= OnlineWithin) return AttendanceStatuses.EnLigne;
            if (nowUtc - seen <= StaleAfter) return AttendanceStatuses.Inactif;
        }

        if (scheduleType == ProgrammeEntryTypes.Repos) return AttendanceStatuses.Repos;

        if (plannedStartUtc is { } start)
        {
            if (firstLoginUtc is not null)
                return RetardMinutes(firstLoginUtc, start) is not null
                    ? AttendanceStatuses.Retard
                    : AttendanceStatuses.HorsLigne;

            if (nowUtc > start + AbsentAfter) return AttendanceStatuses.Absent;
        }

        return AttendanceStatuses.HorsLigne;
    }

    /// <summary>Worked ÷ planned as a percentage capped at 100, or null with nothing planned.</summary>
    public static int? Productivity(int workedMinutes, int plannedMinutes) =>
        plannedMinutes > 0 ? Math.Min(100, (int)Math.Round(workedMinutes * 100.0 / plannedMinutes)) : null;

    /// <summary>The Monday on or before <paramref name="day"/>.</summary>
    public static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}
