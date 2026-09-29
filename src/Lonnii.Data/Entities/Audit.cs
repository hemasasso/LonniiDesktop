namespace Lonnii.Data.Entities;

/// <summary>
/// One stretch of a member being connected to a workspace, for the Audit screen's attendance
/// view. Ported from <c>member_work_log</c> (backend/migrations/add_work_schedule_tables.sql,
/// written by backend/utils/groupSessions.js).
///
/// <para>
/// The web app knew who was online from its socket connections and wrote nothing about it.
/// The desktop has no socket, so each client sends a heartbeat instead and three columns are
/// new here: <see cref="LastSeenAt"/>, <see cref="CurrentModule"/> and
/// <see cref="DeviceName"/>. <see cref="LastSeenAt"/> also closes a row honestly when a till
/// is switched off without signing out: the stretch ends at the last heartbeat, not whenever
/// somebody next happens to look.
/// </para>
/// </summary>
public class MemberWorkLog
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;

    /// <summary>The shop's local calendar day the stretch started on.</summary>
    public DateTime SessionDate { get; set; }

    public DateTime LoginAt { get; set; }

    /// <summary>Null while the stretch is still open.</summary>
    public DateTime? LogoutAt { get; set; }

    /// <summary>Set when the stretch closes, as the web app does.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>Kept for the shared schema; the desktop does not tie a row to a session token.</summary>
    public string? SessionToken { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>Desktop only: the last heartbeat from the member's client.</summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Desktop only: the module open on screen at that heartbeat.</summary>
    public string? CurrentModule { get; set; }

    /// <summary>Desktop only: the machine the member is working on.</summary>
    public string? DeviceName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
