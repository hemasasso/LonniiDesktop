namespace Lonnii.Data.Entities;

/// <summary>
/// An installation's own bookkeeping for its cloud backup, one row per workspace.
///
/// <para>
/// Lives in the same database it describes, on purpose. If the host's <c>lonnii.db</c> is
/// deleted, this row goes with it - and a machine that no longer remembers which line of
/// backups it belongs to is exactly the machine the server must refuse to overwrite the real
/// backup (see <see cref="Epoch"/>). Keeping the state in a separate file would survive the
/// very accident it exists to detect.
/// </para>
/// <para>
/// Local to the installation: the licence server never reads this table, so it has no
/// PostgreSQL counterpart and needs no hand-written script.
/// </para>
/// </summary>
public class CloudBackupState
{
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// The line of backups this installation adds to, issued by the server. Null until the
    /// first backup (or restore) - an installation with no epoch is not allowed to upload over
    /// a backup that already exists.
    /// </summary>
    public string? Epoch { get; set; }

    /// <summary>
    /// The secret this machine proves itself with, issued by the licence server at activation
    /// and with every licence refresh. Not a user credential: it only opens this workspace's
    /// backup, and only while this machine is still bound and the subscription current.
    /// </summary>
    public string? DeviceToken { get; set; }

    /// <summary>The machine the token was issued to. The token is only valid with this id,
    /// and the host - which has no fingerprint of its own - borrows the one that fetched it.</summary>
    public string? DeviceId { get; set; }

    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int LastRecordCount { get; set; }
    public int LastImageCount { get; set; }
}
