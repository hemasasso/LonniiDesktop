namespace Lonnii.Data.Entities;

/// <summary>
/// A shop host's copy of its licence as the licence server signed it (see LicenceToken), one row
/// per espace. The host enforces only what this token says - mode, deadline, bound machines -
/// never the matching columns of <c>groupes</c>, which anyone can edit in the SQLite file.
///
/// <para>Local to the installation, like <see cref="CloudBackupState"/>: the licence server never
/// reads this table, so it has no PostgreSQL counterpart.</para>
/// </summary>
public class HostLicence
{
    public string GroupId { get; set; } = string.Empty;

    /// <summary>The signed licence from the last successful refresh; null until the first one.</summary>
    public string? Token { get; set; }

    /// <summary>When this host first ran without a signed licence - the start of its window to
    /// fetch one. Editable like any row, which is why that window is also capped by the build date.</summary>
    public DateTime? UnsignedSince { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
