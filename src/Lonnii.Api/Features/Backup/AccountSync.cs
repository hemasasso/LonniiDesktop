using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Backup;

/// <summary>
/// Members added on an online shop's own computer, carried to our server so the same people can
/// sign in there: pull the shop onto another computer, open it from afar, use the phone. Without
/// it a member existed only on the computer that created them.
///
/// <para>
/// Additive only: an account, a membership, a role, a privilege can be added or changed, never
/// removed - removing safely needs to know which side changed last for every row, which is the
/// live sync to come. Where both sides hold a value, the more recent change wins (the password by
/// <see cref="User.PasswordChangedAt"/>, the role by <see cref="UserRole.AssignedAt"/>).
/// </para>
/// <para>
/// One shop must not be able to take over an account that is also someone else's: a person who is
/// a member or owner of another shop keeps their password and profile as our server has them -
/// the shop only gains their membership. Nor is the shop's Admin Général ever changed from here.
/// </para>
/// </summary>
internal static class AccountSync
{
    // --- The shop's computer: what it knows ---------------------------------------------------

    /// <param name="forAShop">
    /// True on our server, answering a shop's computer: a password hash is only handed over for
    /// someone who belongs to this shop alone. Anyone can add an existing email to their shop;
    /// that must not earn them the person's hash to try to crack. Such a member arrives without a
    /// password, and that computer's admin gives them one there ("Mot de passe...") - which stays
    /// on that computer, since our server keeps a shared account's password as it is.
    /// </param>
    public static async Task<AccountSyncRequest> BuildAsync(
        LonniiDbContext db, string groupId, CancellationToken ct, bool forAShop = false)
    {
        var members = await (
            from m in db.GroupMembers.AsNoTracking()
            where m.IdGroupe == groupId
            join u in db.Users.AsNoTracking() on m.IdUser equals u.IdUser
            select new { User = u, m.JoinedAt }).ToListAsync(ct);

        var shared = new HashSet<string>(StringComparer.Ordinal);
        if (forAShop)
        {
            foreach (var x in members)
            {
                if (!await BelongsOnlyToAsync(db, x.User.IdUser, groupId, ct)) shared.Add(x.User.IdUser);
            }
        }

        var roles = (await db.UserRoles.AsNoTracking()
                .Where(r => r.GroupId == groupId && r.IsActive)
                .ToListAsync(ct))
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AssignedAt).First());

        var core = await (
            from up in db.UserPrivileges.AsNoTracking()
            where up.GroupId == groupId && up.IsActive
            join p in db.Privileges.AsNoTracking() on up.PrivilegeId equals p.Id
            select new { up.UserId, p.Name }).ToListAsync(ct);
        var option = await (
            from up in db.OptionUserPrivileges.AsNoTracking()
            where up.GroupId == groupId && up.IsActive
            join p in db.OptionPrivileges.AsNoTracking() on up.PrivilegeId equals p.Id
            select new { up.UserId, p.Name }).ToListAsync(ct);
        var gestion = await (
            from up in db.GestionUserPrivileges.AsNoTracking()
            where up.GroupId == groupId && up.IsActive
            join p in db.GestionPrivileges.AsNoTracking() on up.PrivilegeId equals p.Id
            select new { up.UserId, p.Name }).ToListAsync(ct);

        return new AccountSyncRequest(members.Select(x =>
        {
            var u = x.User;
            var role = roles.GetValueOrDefault(u.IdUser);
            return new AccountSyncMember(
                u.IdUser, u.Email, u.Username, u.FirstName, u.LastName, u.Phone,
                shared.Contains(u.IdUser) ? null : u.Password,
                shared.Contains(u.IdUser) ? null : u.PasswordChangedAt,
                role?.Role ?? GroupRoles.Member, role?.AssignedAt ?? x.JoinedAt,
                core.Where(p => p.UserId == u.IdUser).Select(p => p.Name).ToList(),
                option.Where(p => p.UserId == u.IdUser).Select(p => p.Name).ToList(),
                gestion.Where(p => p.UserId == u.IdUser).Select(p => p.Name).ToList());
        }).ToList());
    }

    // --- Our server: what it takes from it ------------------------------------------------------

    /// <param name="fromServer">
    /// False on our server, taking a shop's word: the protections above apply. True on the shop's
    /// computer, taking our server's: it is the authority for accounts, so its values are taken as
    /// they come (newest still wins), and a person the computer knows under another id is moved
    /// onto ours - the id the phones and every other computer use.
    /// </param>
    public static async Task<AccountSyncResponse> ApplyAsync(
        LonniiDbContext db, string groupId, AccountSyncRequest request, CancellationToken ct, bool fromServer = false)
    {
        int created = 0, joined = 0, updated = 0;

        var groupe = await db.Groupes.AsNoTracking().FirstAsync(g => g.Id == groupId, ct);
        var corePrivileges = await db.Privileges.AsNoTracking().ToDictionaryAsync(p => p.Name, p => p.Id, ct);
        var optionPrivileges = await db.OptionPrivileges.AsNoTracking().ToDictionaryAsync(p => p.Name, p => p.Id, ct);
        var gestionPrivileges = await db.GestionPrivileges.AsNoTracking().ToDictionaryAsync(p => p.Name, p => p.Id, ct);

        // Rows go in dependency order, each step saved before the next: the live database has
        // foreign keys the model does not describe (see RegistrationEndpoints.VerifyAsync).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Moving an account to another id changes a primary key that rows point at: SQLite allows
        // it only with the foreign-key checks left until the commit (see AccountsSnapshot.RestoreAsync).
        if (fromServer && !db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON;", ct);

        foreach (var m in request.Members)
        {
            var email = m.Email?.Trim().ToLowerInvariant() ?? string.Empty;
            if (email.Length is 0 or > 255 || !email.Contains('@')) continue;

            // Only a BCrypt hash: anything else would be a password nobody can ever type.
            var hash = m.PasswordHash is { } h && h.StartsWith("$2", StringComparison.Ordinal) ? h : null;

            var user = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == email, ct);
            var isNew = false;

            if (fromServer && user is not null && user.IdUser != m.IdUser
                && !await db.Users.AnyAsync(u => u.IdUser == m.IdUser, ct))
            {
                await AccountsSnapshot.RenumberAsync(db, user.IdUser, m.IdUser, ct);
                db.ChangeTracker.Clear();
                user = await db.Users.FirstAsync(u => u.IdUser == m.IdUser, ct);
                groupe = await db.Groupes.AsNoTracking().FirstAsync(g => g.Id == groupId, ct);
            }

            if (user is null)
            {
                // A shop names someone new only with a password. Our server may send a member
                // without one (shared with another shop, see BuildAsync): they still belong here.
                if (hash is null && !fromServer) continue;

                var username = string.IsNullOrWhiteSpace(m.Username) ? null : m.Username.Trim();
                if (username is not null && await db.Users.AnyAsync(u => u.Username == username, ct)) username = null;

                user = new User
                {
                    // The shop's own id when it is free, so both sides name the person alike.
                    IdUser = await db.Users.AnyAsync(u => u.IdUser == m.IdUser, ct) ? Guid.NewGuid().ToString() : m.IdUser,
                    Email = email,
                    Username = username,
                    FirstName = m.FirstName,
                    LastName = m.LastName,
                    Phone = m.Phone,
                    Password = hash,
                    PasswordChangedAt = m.PasswordChangedAt,
                    IsVerified = true,
                };
                db.Users.Add(user);
                await db.SaveChangesAsync(ct);

                if (hash is not null) db.PasswordHistories.Add(new PasswordHistory { IdUser = user.IdUser, PasswordHash = hash });
                isNew = true;
                created++;
            }
            else if (fromServer || (user.IdUser != groupe.IdUserAdmin && await SpeaksForAsync(db, user.IdUser, groupId, ct)))
            {
                var changed = false;

                if (hash is not null && hash != user.Password
                    && (m.PasswordChangedAt ?? DateTime.MinValue) > (user.PasswordChangedAt ?? DateTime.MinValue))
                {
                    user.Password = hash;
                    user.PasswordChangedAt = m.PasswordChangedAt;
                    db.PasswordHistories.Add(new PasswordHistory { IdUser = user.IdUser, PasswordHash = hash });
                    changed = true;
                }

                changed |= Fill(m.FirstName, user.FirstName, v => user.FirstName = v);
                changed |= Fill(m.LastName, user.LastName, v => user.LastName = v);
                changed |= Fill(m.Phone, user.Phone, v => user.Phone = v);

                if (changed) updated++;
            }

            if (!await db.GroupMembers.AnyAsync(x => x.IdGroupe == groupId && x.IdUser == user.IdUser, ct))
            {
                db.GroupMembers.Add(new GroupMember { IdGroupe = groupId, IdUser = user.IdUser });
                if (!isNew) joined++;
            }
            await db.SaveChangesAsync(ct);

            if (user.IdUser != groupe.IdUserAdmin && GroupRoles.All.Contains(m.Role))
                await ApplyRoleAsync(db, user.IdUser, groupId, m.Role, m.RoleAssignedAt, ct);

            await GrantAsync(db.UserPrivileges, m.CorePrivileges, corePrivileges, user.IdUser, groupId,
                id => new UserPrivilege { UserId = user.IdUser, GroupId = groupId, PrivilegeId = id }, ct);
            await GrantAsync(db.OptionUserPrivileges, m.OptionPrivileges, optionPrivileges, user.IdUser, groupId,
                id => new OptionUserPrivilege { UserId = user.IdUser, GroupId = groupId, PrivilegeId = id }, ct);
            await GrantAsync(db.GestionUserPrivileges, m.GestionPrivileges, gestionPrivileges, user.IdUser, groupId,
                id => new GestionUserPrivilege { UserId = user.IdUser, GroupId = groupId, PrivilegeId = id }, ct);
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new AccountSyncResponse(created, joined, updated);
    }

    /// <summary>Takes the shop's value where it has one and ours differs.</summary>
    private static bool Fill(string? theirs, string? ours, Action<string> set)
    {
        if (string.IsNullOrWhiteSpace(theirs) || theirs == ours) return false;
        set(theirs);
        return true;
    }

    /// <summary>
    /// Whether a shop may change this person's password and profile on our server: they must
    /// already be its member here, and nobody else's. "Nobody else's" alone is not enough - an
    /// account in no shop at all (registered and never added, or one that has left) would pass,
    /// and any shop could then claim its email and set its password.
    /// </summary>
    private static async Task<bool> SpeaksForAsync(LonniiDbContext db, string userId, string groupId, CancellationToken ct) =>
        await db.GroupMembers.AnyAsync(x => x.IdUser == userId && x.IdGroupe == groupId, ct)
        && await BelongsOnlyToAsync(db, userId, groupId, ct);

    /// <summary>True when this person is in no other shop and owns none.</summary>
    private static async Task<bool> BelongsOnlyToAsync(LonniiDbContext db, string userId, string groupId, CancellationToken ct) =>
        !await db.GroupMembers.AnyAsync(x => x.IdUser == userId && x.IdGroupe != groupId, ct)
        && !await db.Groupes.AnyAsync(g => g.IdUserAdmin == userId && g.Id != groupId, ct);

    /// <summary>
    /// The role, unless ours was set more recently. On PostgreSQL written with an explicit cast:
    /// the live <c>user_roles.role</c> is the enum <c>role_type</c> (see
    /// RegistrationEndpoints.AddAdminRoleAsync).
    /// </summary>
    private static async Task ApplyRoleAsync(
        LonniiDbContext db, string userId, string groupId, string role, DateTime assignedAt, CancellationToken ct)
    {
        // Compared in memory: on PostgreSQL the enum column refuses a text parameter in a WHERE.
        var current = (await db.UserRoles.AsNoTracking()
                .Where(r => r.UserId == userId && r.GroupId == groupId)
                .ToListAsync(ct))
            .OrderByDescending(r => r.AssignedAt)
            .FirstOrDefault();

        if (current is not null && (current.Role == role || current.AssignedAt >= assignedAt)) return;

        if (db.Database.IsNpgsql())
        {
            if (current is null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO user_roles (user_id, group_id, role, assigned_at, is_active)
                    VALUES ({userId}, {groupId}, CAST({role} AS role_type), {assignedAt}, TRUE)
                    """, ct);
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE user_roles SET role = CAST({role} AS role_type), assigned_at = {assignedAt}, is_active = TRUE WHERE id = {current.Id}",
                    ct);
            }
            return;
        }

        if (current is null)
        {
            db.UserRoles.Add(new UserRole { UserId = userId, GroupId = groupId, Role = role, AssignedAt = assignedAt });
        }
        else
        {
            var tracked = await db.UserRoles.FirstAsync(r => r.Id == current.Id, ct);
            tracked.Role = role;
            tracked.AssignedAt = assignedAt;
            tracked.IsActive = true;
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task GrantAsync<T>(
        DbSet<T> grants, IReadOnlyList<string>? names, Dictionary<string, int> catalogue,
        string userId, string groupId, Func<int, T> create, CancellationToken ct) where T : class
    {
        if (names is not { Count: > 0 }) return;

        var held = (await grants.AsNoTracking()
                .Where(g => EF.Property<string>(g, "UserId") == userId && EF.Property<string>(g, "GroupId") == groupId)
                .Select(g => EF.Property<int>(g, "PrivilegeId"))
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var name in names.Distinct())
        {
            if (catalogue.TryGetValue(name, out var id) && held.Add(id)) grants.Add(create(id));
        }
    }
}
