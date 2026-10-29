using Lonnii.Api.Features.Backup;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Data.Services;
using Lonnii.Shared.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Tests;

/// <summary>
/// A shop pulled onto a new computer with "J'ai déjà un compte": the server's copy of the shop and
/// the computer's database are two different databases, and whoever installed it - often not the
/// owner - is the only account the computer has. The restore must bring every member back with
/// their role, so each of them signs in at that computer with their usual password.
/// </summary>
public class PulledShopAccountsTests : IAsyncLifetime
{
    private const string GroupId = "8d1c2b6e-0f5a-4e7b-9a39-2b7e5d1c4f00";
    private const string OtherGroupId = "2f6a9c1e-7b3d-4e58-8a01-6c4d2e9b7f33";

    private string _directory = null!;
    private LonniiDbContext _server = null!;
    private LonniiDbContext _archive = null!;
    private LonniiDbContext _host = null!;

    private User _owner = null!, _subAdmin = null!, _cashier = null!;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _server = await OpenAsync("server.db");
        _archive = await OpenAsync("archive.db", seed: false);
        _host = await OpenAsync("host.db");

        _owner = NewUser("owner@boutique.bf");
        _subAdmin = NewUser("adjoint@boutique.bf");
        _cashier = NewUser("caisse@boutique.bf");
        _server.Users.AddRange(_owner, _subAdmin, _cashier);
        _server.Groupes.Add(new Groupe { Id = GroupId, Nom = "Boutique", IdUserAdmin = _owner.IdUser });
        _server.GroupMembers.AddRange(
            new GroupMember { IdGroupe = GroupId, IdUser = _owner.IdUser },
            new GroupMember { IdGroupe = GroupId, IdUser = _subAdmin.IdUser },
            new GroupMember { IdGroupe = GroupId, IdUser = _cashier.IdUser });
        _server.UserRoles.AddRange(
            new UserRole { UserId = _owner.IdUser, GroupId = GroupId, Role = GroupRoles.Admin },
            new UserRole { UserId = _subAdmin.IdUser, GroupId = GroupId, Role = GroupRoles.SubAdmin },
            new UserRole { UserId = _cashier.IdUser, GroupId = GroupId, Role = GroupRoles.Member });
        await _server.SaveChangesAsync();

        await AccountsSnapshot.WriteAsync(_server, _archive, GroupId, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _archive.DisposeAsync();
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private async Task<LonniiDbContext> OpenAsync(string name, bool seed = true)
    {
        var options = new DbContextOptionsBuilder<LonniiDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_directory, name)}").Options;
        var db = new LonniiDbContext(options);
        await db.Database.MigrateAsync();
        if (seed) await new DatabaseSeeder(db).SeedAsync();
        return db;
    }

    private static User NewUser(string email) =>
        new() { Email = email, Password = BCrypt.Net.BCrypt.HashPassword("Motdepasse123"), IsVerified = true };

    /// <summary>What SetupEndpoints.CreateWorkspaceAsync leaves: the installer, alone, as admin.</summary>
    private async Task InstallAsAsync(User installer, string groupId)
    {
        _host.Groupes.Add(new Groupe { Id = groupId, Nom = "Boutique", IdUserAdmin = installer.IdUser });
        _host.GroupMembers.Add(new GroupMember { IdGroupe = groupId, IdUser = installer.IdUser });
        _host.UserRoles.Add(new UserRole { UserId = installer.IdUser, GroupId = groupId, Role = GroupRoles.Admin });
        await _host.SaveChangesAsync();
    }

    private async Task<int> RestoreAsync()
    {
        await using var tx = await _host.Database.BeginTransactionAsync();
        var count = await AccountsSnapshot.RestoreAsync(_archive, _host, GroupId, CancellationToken.None);
        await tx.CommitAsync();
        _host.ChangeTracker.Clear();
        return count;
    }

    private async Task AssertEveryoneIsBackAsync()
    {
        Assert.Equal(_owner.IdUser, (await _host.Groupes.SingleAsync(g => g.Id == GroupId)).IdUserAdmin);

        var members = await _host.GroupMembers.Where(m => m.IdGroupe == GroupId).Select(m => m.IdUser).ToListAsync();
        Assert.Equal(
            new[] { _owner.IdUser, _subAdmin.IdUser, _cashier.IdUser }.Order(),
            members.Order());

        var roles = await _host.UserRoles.Where(r => r.GroupId == GroupId).ToDictionaryAsync(r => r.UserId, r => r.Role);
        Assert.Equal(GroupRoles.SubAdmin, roles[_subAdmin.IdUser]);
        Assert.Equal(GroupRoles.Member, roles[_cashier.IdUser]);

        // Same password as everywhere else.
        var cashier = await _host.Users.SingleAsync(u => u.Email == _cashier.Email);
        Assert.True(BCrypt.Net.BCrypt.Verify("Motdepasse123", cashier.Password));
    }

    [Fact]
    public async Task A_sub_admin_who_installs_gets_the_owner_and_the_staff_back()
    {
        // Setup gave the sub-admin a fresh id on this computer.
        var installer = NewUser(_subAdmin.Email);
        _host.Users.Add(installer);
        await InstallAsAsync(installer, GroupId);

        Assert.Equal(3, await RestoreAsync());

        await AssertEveryoneIsBackAsync();
    }

    [Fact]
    public async Task Staff_already_on_this_computer_under_another_id_are_not_duplicated()
    {
        // The cashier also works at a shop made on this computer, so the account exists here with
        // a local id and the same email.
        var localCashier = NewUser(_cashier.Email);
        _host.Users.Add(localCashier);
        _host.Groupes.Add(new Groupe { Id = OtherGroupId, Nom = "Dépôt", IdUserAdmin = localCashier.IdUser });
        _host.GroupMembers.Add(new GroupMember { IdGroupe = OtherGroupId, IdUser = localCashier.IdUser });
        await _host.SaveChangesAsync();

        var installer = NewUser(_owner.Email);
        _host.Users.Add(installer);
        await InstallAsAsync(installer, GroupId);

        await RestoreAsync();

        await AssertEveryoneIsBackAsync();
        Assert.Single(await _host.Users.Where(u => u.Email == _cashier.Email).ToListAsync());
        Assert.True(await _host.GroupMembers.AnyAsync(m => m.IdGroupe == OtherGroupId && m.IdUser == _cashier.IdUser));
    }
}
