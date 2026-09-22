using Lonnii.Data.Entities;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Data.Services;

/// <summary>
/// Brings the privilege catalogues in the database up to date with
/// <see cref="PrivilegeCatalog"/>, which is the ported Lonnii Business seed data.
///
/// Runs on every start, not just the first. Adding a privilege to the catalogue and
/// restarting the API is all it takes to make it available, which mirrors how the web
/// app ships privileges as ON CONFLICT DO NOTHING migrations. Existing rows have their
/// display text refreshed; grants are never touched.
/// </summary>
public class DatabaseSeeder(LonniiDbContext db)
{
    /// <summary>Applies pending EF migrations, then seeds the privilege catalogues.</summary>
    public async Task MigrateAndSeedAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await SeedAsync(ct);
    }

    /// <summary>Seeds the three privilege catalogues and the role-to-privilege map.</summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedCorePrivilegesAsync(ct);
        await SeedOptionPrivilegesAsync(ct);
        await SeedGestionPrivilegesAsync(ct);
        await db.SaveChangesAsync(ct);

        // Needs the privilege ids assigned by the save above.
        await SeedRolePrivilegesAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedCorePrivilegesAsync(CancellationToken ct)
    {
        var existing = await db.Privileges.ToDictionaryAsync(p => p.Name, StringComparer.Ordinal, ct);

        foreach (var def in PrivilegeCatalog.Core)
        {
            if (existing.TryGetValue(def.Name, out var row))
            {
                row.DisplayName = def.DisplayName;
                row.Description = def.Description;
                row.Category = def.Category;
                row.IsAdminOnly = def.IsAdminOnly;
                row.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.Privileges.Add(new Privilege
                {
                    Name = def.Name,
                    DisplayName = def.DisplayName,
                    Description = def.Description,
                    Category = def.Category,
                    IsAdminOnly = def.IsAdminOnly,
                });
            }
        }
    }

    private async Task SeedOptionPrivilegesAsync(CancellationToken ct)
    {
        var existing = await db.OptionPrivileges.ToDictionaryAsync(p => p.Name, StringComparer.Ordinal, ct);

        foreach (var def in PrivilegeCatalog.Option)
        {
            if (existing.TryGetValue(def.Name, out var row))
            {
                row.DisplayName = def.DisplayName;
                row.Description = def.Description;
                row.Module = def.Module;
                row.IsAdminOnly = def.IsAdminOnly;
                row.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.OptionPrivileges.Add(new OptionPrivilege
                {
                    Name = def.Name,
                    DisplayName = def.DisplayName,
                    Description = def.Description,
                    Module = def.Module,
                    IsAdminOnly = def.IsAdminOnly,
                });
            }
        }
    }

    private async Task SeedGestionPrivilegesAsync(CancellationToken ct)
    {
        var existing = await db.GestionPrivileges.ToDictionaryAsync(p => p.Name, StringComparer.Ordinal, ct);

        foreach (var def in PrivilegeCatalog.Gestion)
        {
            if (existing.TryGetValue(def.Name, out var row))
            {
                row.DisplayName = def.DisplayName;
                row.Description = def.Description;
                row.Module = def.Module;
                row.IsAdminOnly = def.IsAdminOnly;
                row.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.GestionPrivileges.Add(new GestionPrivilege
                {
                    Name = def.Name,
                    DisplayName = def.DisplayName,
                    Description = def.Description,
                    Module = def.Module,
                    IsAdminOnly = def.IsAdminOnly,
                });
            }
        }
    }

    private async Task SeedRolePrivilegesAsync(CancellationToken ct)
    {
        var privilegeIds = await db.Privileges
            .ToDictionaryAsync(p => p.Name, p => p.Id, StringComparer.Ordinal, ct);

        var existing = await db.RolePrivileges
            .Select(rp => new { rp.Role, rp.PrivilegeId })
            .ToListAsync(ct);

        var existingSet = existing.Select(x => (x.Role, x.PrivilegeId)).ToHashSet();

        foreach (var (role, names) in PrivilegeCatalog.RolePrivileges)
        {
            foreach (var name in names)
            {
                if (!privilegeIds.TryGetValue(name, out var id)) continue;
                if (existingSet.Contains((role, id))) continue;
                db.RolePrivileges.Add(new RolePrivilege { Role = role, PrivilegeId = id });
            }
        }
    }

    /// <summary>
    /// Grants a newly added member the baseline option privileges, reproducing the
    /// bootstrap INSERT at the foot of setup_option_privileges.sql. The group creator is
    /// skipped because Admin Général already bypasses every check.
    /// </summary>
    public async Task GrantDefaultOptionPrivilegesAsync(
        string userId, string groupId, string? grantedBy, CancellationToken ct = default)
    {
        var creatorId = await db.Groupes
            .Where(g => g.Id == groupId)
            .Select(g => g.IdUserAdmin)
            .FirstOrDefaultAsync(ct);

        if (creatorId == userId) return;

        var wanted = PrivilegeCatalog.DefaultOptionGrants.ToHashSet(StringComparer.Ordinal);

        var privilegeIds = await db.OptionPrivileges
            .Where(p => wanted.Contains(p.Name))
            .Select(p => p.Id)
            .ToListAsync(ct);

        var already = await db.OptionUserPrivileges
            .Where(up => up.UserId == userId && up.GroupId == groupId)
            .Select(up => up.PrivilegeId)
            .ToListAsync(ct);

        var alreadySet = already.ToHashSet();

        foreach (var id in privilegeIds)
        {
            if (alreadySet.Contains(id)) continue;
            db.OptionUserPrivileges.Add(new OptionUserPrivilege
            {
                UserId = userId,
                GroupId = groupId,
                PrivilegeId = id,
                GrantedBy = grantedBy,
                IsActive = true,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
