namespace Lonnii.Shared.Security;

/// <summary>
/// Group-level roles, ported from the PostgreSQL <c>role_type</c> enum
/// ('admin', 'sub_admin', 'moderator', 'member') in postgres_schema.sql.
/// The stored string values must match the web app exactly so a future
/// import of Lonnii Business data needs no translation.
/// </summary>
public static class GroupRoles
{
    public const string Admin = "admin";
    public const string SubAdmin = "sub_admin";
    public const string Moderator = "moderator";
    public const string Member = "member";

    public static readonly IReadOnlyList<string> All = [Admin, SubAdmin, Moderator, Member];

    /// <summary>Roles that count as "an admin role" when resolving Gestion access.</summary>
    public static bool IsAdminRole(string? role) =>
        role is Admin or SubAdmin;

    /// <summary>
    /// The French label for a role. The stored values stay in English so the schema
    /// matches Lonnii Business; only what the user reads is translated.
    /// </summary>
    public static string DisplayName(string? role) => role switch
    {
        Admin => "Administrateur",
        SubAdmin => "Administrateur délégué",
        Moderator => "Modérateur",
        Member => "Membre",
        null or "" => string.Empty,
        _ => role,
    };

    /// <summary>
    /// The label to show for a member, accounting for the group creator outranking
    /// whatever role row they happen to have.
    /// </summary>
    public static string DisplayName(string? role, bool isAdminGeneral) =>
        isAdminGeneral ? "Administrateur Général" : DisplayName(role);
}

/// <summary>
/// Per-module Gestion roles, ported from <c>gestion_user_roles.role</c>
/// ('viewer', 'operator', 'manager', 'admin') in setup_gestion_privileges.sql.
/// </summary>
public static class GestionRoles
{
    public const string Viewer = "viewer";
    public const string Operator = "operator";
    public const string Manager = "manager";
    public const string Admin = "admin";

    public static readonly IReadOnlyList<string> All = [Viewer, Operator, Manager, Admin];
}

/// <summary>Categories used by the core <c>privileges</c> table.</summary>
public static class PrivilegeCategories
{
    public const string MemberManagement = "member_management";
    public const string ContentManagement = "content_management";
    public const string Moderation = "moderation";
    public const string Admin = "admin";
}

/// <summary>Modules used by <c>option_privileges.module</c>.</summary>
public static class OptionModules
{
    public const string Programme = "programme";
    public const string Chat = "chat";
    public const string Formulaire = "formulaire";
}

/// <summary>Modules used by <c>gestion_privileges.module</c>.</summary>
public static class GestionModules
{
    public const string Stock = "stock";
    public const string Sales = "sales";
    public const string Ventes = "ventes";
    public const string Charges = "charges";
    public const string Marges = "marges";
    public const string Amortissement = "amortissement";
    public const string Bilan = "bilan";
    public const string Prestations = "prestations";
    public const string Finance = "finance";
    public const string Analytics = "analytics";
    public const string Admin = "admin";
}
