using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Data.Services;

/// <summary>Everything the UI needs to decide what a user may see and do in one group.</summary>
/// <param name="Role">The user's group role, one of <see cref="GroupRoles"/>.</param>
/// <param name="IsAdminGeneral">True when the user created the group and bypasses every check.</param>
/// <param name="IsAdmin">True when the role is admin or sub_admin.</param>
/// <param name="Option">Resolved option privileges (Programme, Chat, Formulaire).</param>
/// <param name="Gestion">Resolved gestion privileges, including the two virtual ones.</param>
public sealed record PrivilegeSet(
    string Role,
    bool IsAdminGeneral,
    bool IsAdmin,
    IReadOnlyDictionary<string, bool> Option,
    IReadOnlyDictionary<string, bool> Gestion)
{
    /// <summary>True when the named option privilege is granted.</summary>
    public bool HasOption(string name) => Option.TryGetValue(name, out var v) && v;

    /// <summary>True when the named gestion privilege is granted.</summary>
    public bool HasGestion(string name) => Gestion.TryGetValue(name, out var v) && v;

    /// <summary>Option and gestion privileges merged, for menu filtering across both systems.</summary>
    public IReadOnlyDictionary<string, bool> All()
    {
        var merged = new Dictionary<string, bool>(Option, StringComparer.Ordinal);
        foreach (var (key, value) in Gestion)
        {
            // A grant in either system is enough; the two catalogues share some names.
            merged[key] = value || (merged.TryGetValue(key, out var existing) && existing);
        }
        return merged;
    }
}

/// <summary>
/// Resolves a user's effective privileges in a group.
///
/// This is a direct port of Lonnii Business, and deliberately keeps its quirks so the
/// desktop and the web app agree on who can do what:
///
/// * The group creator (<c>groupes.iduser_admin</c>) is "Admin Général" and gets every
///   privilege without consulting any grant table. See PrivilegeService.getUserRole and
///   the hardcoded map in routes/gestion.js.
/// * Option privileges come only from individual grants in <c>option_user_privileges</c>.
///   PrivilegeService.getUserPrivileges reads that table alone - it does not consult
///   <c>role_privileges</c>, so a role grants nothing by itself unless the user is admin.
/// * Gestion privileges come from <c>gestion_user_privileges</c>, and any privilege marked
///   <c>is_admin_only</c> resolves to false for a non-Admin-Général regardless of grants.
/// * <c>can_view_audit</c> and <c>can_view_parametres</c> are not rows in any table. The
///   my-privileges endpoint synthesises them for any admin role.
/// </summary>
public class PrivilegeResolver(LonniiDbContext db)
{
    /// <summary>Resolves the user's role in the group, as PrivilegeService.getUserRole does.</summary>
    public async Task<string> GetRoleAsync(string userId, string groupId, CancellationToken ct = default)
    {
        var creatorId = await db.Groupes
            .Where(g => g.Id == groupId)
            .Select(g => g.IdUserAdmin)
            .FirstOrDefaultAsync(ct);

        if (creatorId == userId) return GroupRoles.Admin;

        var role = await db.UserRoles
            .Where(r => r.UserId == userId && r.GroupId == groupId && r.IsActive)
            .Select(r => r.Role)
            .FirstOrDefaultAsync(ct);

        return role ?? GroupRoles.Member;
    }

    /// <summary>True when the user created this group and therefore bypasses every check.</summary>
    public async Task<bool> IsAdminGeneralAsync(string userId, string groupId, CancellationToken ct = default) =>
        await db.Groupes.AnyAsync(g => g.Id == groupId && g.IdUserAdmin == userId, ct);

    /// <summary>Resolves the full privilege set for a user in a group.</summary>
    public async Task<PrivilegeSet> ResolveAsync(string userId, string groupId, CancellationToken ct = default)
    {
        var isAdminGeneral = await IsAdminGeneralAsync(userId, groupId, ct);
        var role = isAdminGeneral ? GroupRoles.Admin : await GetRoleAsync(userId, groupId, ct);
        var isAdmin = GroupRoles.IsAdminRole(role);

        if (isAdminGeneral)
        {
            return new PrivilegeSet(
                GroupRoles.Admin,
                IsAdminGeneral: true,
                IsAdmin: true,
                Option: GrantAll(PrivilegeCatalog.Option.Select(p => p.Name)),
                Gestion: GrantAll(AllGestionNames()));
        }

        var option = await ResolveOptionAsync(userId, groupId, ct);
        var gestion = await ResolveGestionAsync(userId, groupId, isAdmin, ct);

        return new PrivilegeSet(role, IsAdminGeneral: false, isAdmin, option, gestion);
    }

    /// <summary>
    /// Resolves option privileges from individual grants, honouring is_active and expires_at.
    /// Every catalogued name is present in the result so callers can index without a miss.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, bool>> ResolveOptionAsync(
        string userId, string groupId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var granted = await db.OptionUserPrivileges
            .Where(up => up.UserId == userId
                         && up.GroupId == groupId
                         && up.IsActive
                         && (up.ExpiresAt == null || up.ExpiresAt > now))
            .Select(up => up.Privilege!.Name)
            .ToListAsync(ct);

        var grantedSet = granted.ToHashSet(StringComparer.Ordinal);

        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var p in PrivilegeCatalog.Option)
        {
            // An admin-only option privilege is never granted through an individual row.
            result[p.Name] = !p.IsAdminOnly && grantedSet.Contains(p.Name);
        }
        return result;
    }

    /// <summary>
    /// Resolves gestion privileges. Mirrors the non-admin branch of
    /// GET /gestion/:sessionToken/my-privileges in routes/gestion.js.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, bool>> ResolveGestionAsync(
        string userId, string groupId, bool isAdmin, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var granted = await db.GestionUserPrivileges
            .Where(up => up.UserId == userId
                         && up.GroupId == groupId
                         && up.IsActive
                         && (up.ExpiresAt == null || up.ExpiresAt > now))
            .Select(up => up.Privilege!.Name)
            .ToListAsync(ct);

        var grantedSet = granted.ToHashSet(StringComparer.Ordinal);

        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var p in PrivilegeCatalog.Gestion)
        {
            result[p.Name] = !p.IsAdminOnly && grantedSet.Contains(p.Name);
        }

        // Virtual privileges: any admin role sees Audit and Paramètres, whatever the toggles say.
        result[Priv.Gestion.ViewAudit] = isAdmin;
        result[Priv.Gestion.ViewParametres] = isAdmin;

        return result;
    }

    /// <summary>All gestion privilege names, including the two virtual ones.</summary>
    private static IEnumerable<string> AllGestionNames() =>
        PrivilegeCatalog.Gestion.Select(p => p.Name)
            .Concat([Priv.Gestion.ViewAudit, Priv.Gestion.ViewParametres]);

    private static Dictionary<string, bool> GrantAll(IEnumerable<string> names)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in names) map[name] = true;
        return map;
    }
}
