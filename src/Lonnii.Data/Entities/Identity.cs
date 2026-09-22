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

    /// <summary>Feature toggle for the Prestations module.</summary>
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
