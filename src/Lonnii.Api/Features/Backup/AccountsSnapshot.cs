using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Backup;

/// <summary>
/// The people side of a cloud backup: members, their roles and privileges, and password
/// hashes. Left out of the ordinary espace export on purpose (see <c>EspaceCopier</c>) - a file
/// handed to another espace must not bring accounts with it. A disaster restore is the opposite
/// case: the shop's own staff must come back, with the same ids, or every sale's
/// <c>created_by</c> points at someone who no longer exists.
///
/// <para>
/// The snapshot carries the three privilege <em>catalogues</em> as well, because privilege
/// grants refer to them by integer id and ids are not stable between installations - restore
/// maps them back by privilege name.
/// </para>
/// <para>
/// Password hashes are BCrypt. They are stored on the licence server with the rest of the
/// snapshot, which is already where the admin's hash lives for activation.
/// </para>
/// </summary>
internal static class AccountsSnapshot
{
    // --- Backup ----------------------------------------------------------------------

    public static async Task WriteAsync(
        LonniiDbContext source, LonniiDbContext target, string groupId, CancellationToken ct)
    {
        var groupe = await source.Groupes.AsNoTracking().FirstAsync(g => g.Id == groupId, ct);

        var userIds = await source.GroupMembers.Where(m => m.IdGroupe == groupId)
            .Select(m => m.IdUser).ToListAsync(ct);
        userIds.Add(groupe.IdUserAdmin);

        var users = await source.Users.AsNoTracking().Where(u => userIds.Contains(u.IdUser)).ToListAsync(ct);
        await InsertAsync(target, users, ct);

        // A stripped copy: the archive needs a groupes row for the memberships to hang off,
        // but none of the licence fields - a restore must never be able to carry them.
        await InsertAsync(target, [new Groupe
        {
            Id = groupe.Id,
            Nom = groupe.Nom,
            IdUserAdmin = groupe.IdUserAdmin,
            GestionAccess = groupe.GestionAccess,
            PrestationsEnabled = groupe.PrestationsEnabled,
            PrestationsLocation = groupe.PrestationsLocation,
            CurrencyLabel = groupe.CurrencyLabel,
            CurrencyBefore = groupe.CurrencyBefore,
            PhotoUrl = groupe.PhotoUrl,
            Mode = DeploymentModes.Local,
            CreatedAt = groupe.CreatedAt,
        }], ct);

        await InsertAsync(target, await All(source.Privileges), ct);
        await InsertAsync(target, await All(source.OptionPrivileges), ct);
        await InsertAsync(target, await All(source.GestionPrivileges), ct);

        await InsertAsync(target, await All(source.GroupMembers.Where(x => x.IdGroupe == groupId)), ct);
        await InsertAsync(target, await All(source.UserRoles.Where(x => x.GroupId == groupId)), ct);
        await InsertAsync(target, await All(source.UserPrivileges.Where(x => x.GroupId == groupId)), ct);
        await InsertAsync(target, await All(source.OptionUserPrivileges.Where(x => x.GroupId == groupId)), ct);
        await InsertAsync(target, await All(source.GestionUserRoles.Where(x => x.GroupId == groupId)), ct);
        await InsertAsync(target, await All(source.GestionUserPrivileges.Where(x => x.GroupId == groupId)), ct);
        await InsertAsync(target, await All(source.PasswordHistories.Where(x => userIds.Contains(x.IdUser))), ct);

        Task<List<T>> All<T>(IQueryable<T> query) where T : class => query.AsNoTracking().ToListAsync(ct);
    }

    private static async Task InsertAsync<T>(LonniiDbContext target, List<T> rows, CancellationToken ct) where T : class
    {
        if (rows.Count == 0) return;

        target.Set<T>().AddRange(rows);
        await target.SaveChangesAsync(ct);
        target.ChangeTracker.Clear();
    }

    // --- Restore ---------------------------------------------------------------------

    /// <summary>
    /// Puts the backed-up accounts into a freshly set-up workspace. Must run inside the
    /// caller's transaction.
    ///
    /// <para>
    /// Setup has already created an admin (from the credentials file) with a <em>new</em> id.
    /// That row is renumbered to the backed-up admin's id rather than deleted and recreated:
    /// deleting it would cascade into the groupe itself. SQLite refuses a primary-key change
    /// while children point at it, so foreign-key checks are deferred to commit, by which point
    /// every child has been moved with it.
    /// </para>
    /// </summary>
    /// <returns>How many accounts were restored; 0 for an old snapshot that carries none.</returns>
    public static async Task<int> RestoreAsync(
        LonniiDbContext archive, LonniiDbContext db, string groupId, CancellationToken ct)
    {
        var archivedGroupe = await archive.Groupes.AsNoTracking().FirstOrDefaultAsync(ct);
        if (archivedGroupe is null) return 0;

        var archivedUsers = await archive.Users.AsNoTracking().ToListAsync(ct);
        if (archivedUsers.Count == 0) return 0;

        await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", ct);

        var groupe = await db.Groupes.AsNoTracking().FirstAsync(g => g.Id == groupId, ct);
        var setupAdmin = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.IdUser == groupe.IdUserAdmin, ct);

        // The account setup made is the same person as the backed-up one with that email.
        var same = setupAdmin is null
            ? null
            : archivedUsers.FirstOrDefault(u => string.Equals(u.Email, setupAdmin.Email, StringComparison.OrdinalIgnoreCase));

        if (setupAdmin is not null && same is not null && same.IdUser != setupAdmin.IdUser)
        {
            var from = setupAdmin.IdUser;
            var to = same.IdUser;

            await db.Database.ExecuteSqlRawAsync("UPDATE users SET iduser = {0} WHERE iduser = {1};", [to, from], ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE groupes SET iduser_admin = {0} WHERE iduser_admin = {1};", [to, from], ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE groupe_membres SET iduser = {0} WHERE iduser = {1};", [to, from], ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE user_roles SET user_id = {0} WHERE user_id = {1};", [to, from], ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE user_roles SET assigned_by = {0} WHERE assigned_by = {1};", [to, from], ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE password_history SET iduser = {0} WHERE iduser = {1};", [to, from], ct);
        }

        db.ChangeTracker.Clear();

        // Whatever setup created for this workspace gives way to the backed-up truth.
        await db.UserRoles.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GroupMembers.Where(x => x.IdGroupe == groupId).ExecuteDeleteAsync(ct);
        await db.UserPrivileges.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.OptionUserPrivileges.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GestionUserRoles.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GestionUserPrivileges.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        var archivedIds = archivedUsers.Select(u => u.IdUser).ToList();
        await db.PasswordHistories.Where(x => archivedIds.Contains(x.IdUser)).ExecuteDeleteAsync(ct);

        foreach (var user in archivedUsers)
        {
            var existing = await db.Users.FirstOrDefaultAsync(u => u.IdUser == user.IdUser, ct);
            if (existing is null) db.Users.Add(user);
            else db.Entry(existing).CurrentValues.SetValues(user);
        }
        await db.SaveChangesAsync(ct);

        var tracked = await db.Groupes.FirstAsync(g => g.Id == groupId, ct);
        if (archivedIds.Contains(archivedGroupe.IdUserAdmin)) tracked.IdUserAdmin = archivedGroupe.IdUserAdmin;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        db.GroupMembers.AddRange(await archive.GroupMembers.AsNoTracking()
            .Where(m => m.IdGroupe == groupId).ToListAsync(ct));

        foreach (var row in await archive.UserRoles.AsNoTracking().ToListAsync(ct))
        {
            row.Id = 0;
            db.UserRoles.Add(row);
        }
        foreach (var row in await archive.PasswordHistories.AsNoTracking().ToListAsync(ct))
        {
            row.Id = 0;
            db.PasswordHistories.Add(row);
        }
        foreach (var row in await archive.GestionUserRoles.AsNoTracking().ToListAsync(ct))
        {
            row.Id = 0;
            db.GestionUserRoles.Add(row);
        }

        // Grants name a privilege by id; ids differ between installations, names do not.
        var core = Map(
            (await archive.Privileges.Select(p => new { p.Id, p.Name }).ToListAsync(ct)).Select(p => (p.Id, p.Name)),
            (await db.Privileges.Select(p => new { p.Id, p.Name }).ToListAsync(ct)).Select(p => (p.Id, p.Name)));
        var option = Map(
            (await archive.OptionPrivileges.Select(p => new { p.Id, p.Name }).ToListAsync(ct)).Select(p => (p.Id, p.Name)),
            (await db.OptionPrivileges.Select(p => new { p.Id, p.Name }).ToListAsync(ct)).Select(p => (p.Id, p.Name)));
        var gestion = Map(
            (await archive.GestionPrivileges.Select(p => new { p.Id, p.Name }).ToListAsync(ct)).Select(p => (p.Id, p.Name)),
            (await db.GestionPrivileges.Select(p => new { p.Id, p.Name }).ToListAsync(ct)).Select(p => (p.Id, p.Name)));

        foreach (var row in await archive.UserPrivileges.AsNoTracking().ToListAsync(ct))
        {
            if (!core.TryGetValue(row.PrivilegeId, out var id)) continue;
            row.Id = 0; row.PrivilegeId = id; db.UserPrivileges.Add(row);
        }
        foreach (var row in await archive.OptionUserPrivileges.AsNoTracking().ToListAsync(ct))
        {
            if (!option.TryGetValue(row.PrivilegeId, out var id)) continue;
            row.Id = 0; row.PrivilegeId = id; db.OptionUserPrivileges.Add(row);
        }
        foreach (var row in await archive.GestionUserPrivileges.AsNoTracking().ToListAsync(ct))
        {
            if (!gestion.TryGetValue(row.PrivilegeId, out var id)) continue;
            row.Id = 0; row.PrivilegeId = id; db.GestionUserPrivileges.Add(row);
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        return archivedUsers.Count;
    }

    /// <summary>Archive privilege id → this installation's id, by name.</summary>
    private static Dictionary<int, int> Map(IEnumerable<(int Id, string Name)> archived, IEnumerable<(int Id, string Name)> local)
    {
        var byName = local.ToDictionary(p => p.Name, p => p.Id);

        return archived
            .Where(p => byName.ContainsKey(p.Name))
            .ToDictionary(p => p.Id, p => byName[p.Name]);
    }
}
