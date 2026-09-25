using Lonnii.Shared.Security;

namespace Lonnii.Data.Entities;

/// <summary>
/// A user account. Ported from the <c>users</c> table in postgres_schema.sql.
/// <see cref="IdUser"/> keeps the web app's VARCHAR(36) GUID-string shape so records
/// can be imported from Lonnii Business without re-keying.
/// </summary>
public class User
{
    public string IdUser { get; set; } = Guid.NewGuid().ToString();
    public string Email { get; set; } = string.Empty;
    public string? Username { get; set; }

    /// <summary>BCrypt hash. Never stores a plaintext password.</summary>
    public string? Password { get; set; }

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Phone { get; set; }
    public string? Country { get; set; }
    public string? Poste { get; set; }
    public string? Description { get; set; }

    public string? ResetCode { get; set; }
    public DateTime? ResetCodeCreatedAt { get; set; }

    public bool IsVerified { get; set; }

    /// <summary>Mirrors <c>users.is_blocked</c>, checked by the web app's auth middleware on every request.</summary>
    public bool IsBlocked { get; set; }

    public DateTime? LastLogin { get; set; }
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// When the password last changed. Access tokens issued before this moment are
    /// refused, so resetting a password ends the person's existing sessions instead of
    /// leaving them valid until the token expires hours later.
    /// </summary>
    public DateTime? PasswordChangedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GroupMember> Memberships { get; set; } = [];
}

/// <summary>A group (a company workspace). Ported from <c>groupes</c>.</summary>
public class Groupe
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Nom { get; set; } = string.Empty;

    /// <summary>
    /// The group creator, called "Admin Général" in the UI. This user bypasses every
    /// privilege check - see PrivilegeResolver.
    /// </summary>
    public string IdUserAdmin { get; set; } = string.Empty;

    /// <summary>Mirrors <c>groupes.gestion_access</c>: hides the whole Gestion module when false.</summary>
    public bool GestionAccess { get; set; }

    /// <summary>
    /// Feature toggle for the Prestations module. Maps to <c>prestations_access</c>, not the
    /// <c>prestations_enabled</c> this name would otherwise produce - see the column map in
    /// LonniiDbContext. Named for the live column, which is what an import has to match.
    /// </summary>
    public bool PrestationsEnabled { get; set; }

    /// <summary>Where Prestations appears: <c>gestion</c>, <c>espace</c> or <c>both</c>.</summary>
    public string? PrestationsLocation { get; set; }

    /// <summary>
    /// The label shown after every amount in this workspace, e.g. <c>FCFA</c>. Lives on the
    /// group rather than the client so every till in the shop shows the same currency.
    /// </summary>
    public string CurrencyLabel { get; set; } = "FCFA";

    public bool IsBlocked { get; set; }
    public string? BlockReason { get; set; }
    public DateTime? BlockedAt { get; set; }

    /// <summary>
    /// Soft delete, mirroring the live column. A deleted workspace is kept for a recovery
    /// window rather than removed, so activation has to check this as well as
    /// <see cref="IsBlocked"/> - the two are separate states and a deleted group is not
    /// necessarily blocked.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// How this workspace is deployed: one of <see cref="DeploymentModes"/>. Decides whether
    /// a <see cref="DashboardSubscription"/> is checked at login and sync - local-mode
    /// workspaces run on the company's own machine and are never billed.
    ///
    /// New to the desktop product; Lonnii Business has no equivalent, since every web
    /// workspace is online by definition. It lives on <c>groupes</c> because that is where
    /// the web app already keeps per-workspace facts (gestion_access, is_blocked, schema_name).
    /// Defaults to local so an imported workspace is never locked out by a missing subscription.
    /// </summary>
    public string Mode { get; set; } = DeploymentModes.Local;

    /// <summary>
    /// How many machines this shop may bind at once - a pharmacy with five tills gets 5,
    /// a small boutique 3. Counted against the <c>devices</c> table at login.
    ///
    /// Deliberately not settable through the API by the shop itself: a shop that can raise
    /// its own limit has no limit. The authoritative value comes from the dashboard and
    /// arrives with the first-launch licence sync, which is why this default is only a
    /// placeholder for a workspace that has not synced yet.
    /// </summary>
    public int MaxDevices { get; set; } = 3;

    /// <summary>
    /// How many days this workspace may run without reaching the licence server before it
    /// stops. The defence against an online-mode shop - bought cheaply, with a yearly fee -
    /// simply unplugging the internet and using it for ever as if it were the far more
    /// expensive offline licence.
    ///
    /// <para>
    /// Per workspace rather than a constant for the same reason as <see cref="MaxDevices"/>:
    /// a shop with genuinely poor connectivity needs a longer leash, and that must be
    /// grantable without shipping a new build.
    /// </para>
    /// </summary>
    public int MaxOfflineDays { get; set; } = 7;

    /// <summary>
    /// When this workspace last reached the licence server. Server time, never the client's,
    /// so winding a till's clock back cannot buy more offline days.
    /// </summary>
    public DateTime? LastLicenceCheckAt { get; set; }

    /// <summary>
    /// Where this workspace's licence server lives, taken from the credentials file at
    /// setup and kept because everything afterwards needs it: refreshing settings, and
    /// registering a new till - which must be counted centrally, not locally, or a shop
    /// could grant itself machines by adding rows on its own hardware.
    /// </summary>
    public string? LicenceServerUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? Admin { get; set; }
    public ICollection<GroupMember> Members { get; set; } = [];
}

/// <summary>Group membership. Ported from <c>groupe_membres</c> (composite key).</summary>
public class GroupMember
{
    public string IdGroupe { get; set; } = string.Empty;
    public string IdUser { get; set; } = string.Empty;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public Groupe? Groupe { get; set; }
    public User? User { get; set; }
}

/// <summary>
/// A scoped session binding a signed-in user to one group, ported from <c>groupe_sessions</c>.
/// The web client sends this token as the <c>x-group-session</c> header; the desktop client
/// does the same so both talk to an API with identical semantics.
/// </summary>
public class GroupeSession
{
    public int Id { get; set; }
    public string SessionToken { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Previous password hashes, ported from <c>password_history</c>.</summary>
public class PasswordHistory
{
    public int Id { get; set; }
    public string IdUser { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A device login, ported from <c>user_sessions</c>. On the desktop this records which
/// machine on the local network a user signed in from.
/// </summary>
public class UserSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? IpAddress { get; set; }

    /// <summary>Mirrors <c>login_source</c>: <c>web</c>, <c>mobile</c>, <c>electron</c> or <c>desktop</c>.</summary>
    public string LoginSource { get; set; } = "desktop";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}
