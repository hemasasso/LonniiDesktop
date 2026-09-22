namespace Lonnii.Data.Entities;

// ---------------------------------------------------------------------------
// System 1: core group privileges (privileges / user_roles / role_privileges)
// ---------------------------------------------------------------------------

/// <summary>A core privilege definition. Ported from <c>privileges</c>.</summary>
public class Privilege
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>One of <see cref="Shared.Security.PrivilegeCategories"/>.</summary>
    public string Category { get; set; } = Shared.Security.PrivilegeCategories.MemberManagement;

    /// <summary>When true the privilege is reserved for admins and never granted individually.</summary>
    public bool IsAdminOnly { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A user's role within one group. Ported from <c>user_roles</c>.</summary>
public class UserRole
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;

    /// <summary>One of <see cref="Shared.Security.GroupRoles"/>.</summary>
    public string Role { get; set; } = Shared.Security.GroupRoles.Member;

    public string? AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>An individual core privilege grant. Ported from <c>user_privileges</c>.</summary>
public class UserPrivilege
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public int PrivilegeId { get; set; }
    public string? GrantedBy { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;

    public Privilege? Privilege { get; set; }
}

/// <summary>Which privileges each role carries. Ported from <c>role_privileges</c>.</summary>
public class RolePrivilege
{
    public int Id { get; set; }
    public string Role { get; set; } = string.Empty;
    public int PrivilegeId { get; set; }

    public Privilege? Privilege { get; set; }
}

/// <summary>Core privilege change log. Ported from <c>privilege_audit</c>.</summary>
public class PrivilegeAudit
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string TargetUserId { get; set; } = string.Empty;

    /// <summary>One of <c>grant</c>, <c>revoke</c>, <c>promote</c>, <c>demote</c>.</summary>
    public string Action { get; set; } = string.Empty;

    public int? PrivilegeId { get; set; }
    public string? RoleFrom { get; set; }
    public string? RoleTo { get; set; }
    public string PerformedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ---------------------------------------------------------------------------
// System 2: option privileges (programme / chat / formulaire)
// ---------------------------------------------------------------------------

/// <summary>An option privilege definition. Ported from <c>option_privileges</c>.</summary>
public class OptionPrivilege
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>One of <see cref="Shared.Security.OptionModules"/>.</summary>
    public string Module { get; set; } = string.Empty;

    public bool IsAdminOnly { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>An option privilege grant. Ported from <c>option_user_privileges</c>.</summary>
public class OptionUserPrivilege
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public int PrivilegeId { get; set; }
    public string? GrantedBy { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;

    public OptionPrivilege? Privilege { get; set; }
}

/// <summary>Option privilege change log. Ported from <c>option_privilege_audit</c>.</summary>
public class OptionPrivilegeAudit
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string TargetUserId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int? PrivilegeId { get; set; }
    public string? Module { get; set; }
    public string PerformedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ---------------------------------------------------------------------------
// System 3: gestion privileges (stock / ventes / finance / ...)
// ---------------------------------------------------------------------------

/// <summary>A gestion privilege definition. Ported from <c>gestion_privileges</c>.</summary>
public class GestionPrivilege
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>One of <see cref="Shared.Security.GestionModules"/>.</summary>
    public string Module { get; set; } = string.Empty;

    public bool IsAdminOnly { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A per-module gestion role. Ported from <c>gestion_user_roles</c>.</summary>
public class GestionUserRole
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;

    /// <summary>One of <see cref="Shared.Security.GestionRoles"/>.</summary>
    public string Role { get; set; } = Shared.Security.GestionRoles.Viewer;

    public string Module { get; set; } = string.Empty;
    public string? AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}

/// <summary>A gestion privilege grant. Ported from <c>gestion_user_privileges</c>.</summary>
public class GestionUserPrivilege
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public int PrivilegeId { get; set; }
    public string? GrantedBy { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;

    public GestionPrivilege? Privilege { get; set; }
}

/// <summary>Gestion privilege change log. Ported from <c>gestion_privilege_audit</c>.</summary>
public class GestionPrivilegeAudit
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string TargetUserId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int? PrivilegeId { get; set; }
    public string? Module { get; set; }
    public string? RoleFrom { get; set; }
    public string? RoleTo { get; set; }
    public string PerformedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
