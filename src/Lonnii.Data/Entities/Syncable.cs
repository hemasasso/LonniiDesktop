namespace Lonnii.Data.Entities;

/// <summary>
/// A table keyed by an integer that is only unique within one database. Once phones save to OCI
/// while the shop's host saves too, both can hand out the same number; <see cref="SyncId"/> is the
/// id live sync matches rows by. The integer key stays: the live PostgreSQL tables are shared with
/// Lonnii Business, and every link between these tables uses it.
/// </summary>
public interface ISyncable
{
    /// <summary>A GUID, set when the row is created and never changed. Column <c>sync_id</c>, unique.</summary>
    string SyncId { get; set; }
}
