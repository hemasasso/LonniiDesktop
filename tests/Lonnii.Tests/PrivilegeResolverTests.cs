using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Data.Services;
using Lonnii.Shared.Navigation;
using Lonnii.Shared.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Tests;

/// <summary>
/// Covers the rules ported from Lonnii Business. These are the tests that would catch a
/// desktop client showing someone a module the web app would have hidden.
/// </summary>
public class PrivilegeResolverTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private LonniiDbContext _db = null!;
    private PrivilegeResolver _resolver = null!;

    private const string CreatorId = "user-creator";
    private const string MemberId = "user-member";
    private const string SubAdminId = "user-subadmin";
    private const string GroupId = "group-1";

    public async Task InitializeAsync()
    {
        // A real SQLite database, kept in memory, so the schema and converters are exercised.
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<LonniiDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new LonniiDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        await new DatabaseSeeder(_db).SeedAsync();

        _db.Users.AddRange(
            new User { IdUser = CreatorId, Email = "creator@test" },
            new User { IdUser = MemberId, Email = "member@test" },
            new User { IdUser = SubAdminId, Email = "subadmin@test" });

        _db.Groupes.Add(new Groupe
        {
            Id = GroupId,
            Nom = "Test",
            IdUserAdmin = CreatorId,
            GestionAccess = true,
        });

        _db.GroupMembers.AddRange(
            new GroupMember { IdGroupe = GroupId, IdUser = CreatorId },
            new GroupMember { IdGroupe = GroupId, IdUser = MemberId },
            new GroupMember { IdGroupe = GroupId, IdUser = SubAdminId });

        _db.UserRoles.AddRange(
            new UserRole { UserId = MemberId, GroupId = GroupId, Role = GroupRoles.Member },
            new UserRole { UserId = SubAdminId, GroupId = GroupId, Role = GroupRoles.SubAdmin });

        await _db.SaveChangesAsync();

        _resolver = new PrivilegeResolver(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // --- Admin Général ---

    [Fact]
    public async Task GroupCreator_HasEveryPrivilege_WithoutAnyGrantRows()
    {
        var result = await _resolver.ResolveAsync(CreatorId, GroupId);

        Assert.True(result.IsAdminGeneral);
        Assert.Equal(GroupRoles.Admin, result.Role);

        // No rows were inserted into the grant tables for the creator.
        Assert.Empty(_db.GestionUserPrivileges.Where(p => p.UserId == CreatorId));

        Assert.All(PrivilegeCatalog.Gestion, p => Assert.True(result.HasGestion(p.Name), p.Name));
        Assert.All(PrivilegeCatalog.Option, p => Assert.True(result.HasOption(p.Name), p.Name));
    }

    [Fact]
    public async Task GroupCreator_HasTheVirtualAuditAndParametresPrivileges()
    {
        var result = await _resolver.ResolveAsync(CreatorId, GroupId);

        Assert.True(result.HasGestion(Priv.Gestion.ViewAudit));
        Assert.True(result.HasGestion(Priv.Gestion.ViewParametres));
    }

    // --- Ordinary members ---

    [Fact]
    public async Task NewMember_HasNoGestionPrivileges()
    {
        var result = await _resolver.ResolveAsync(MemberId, GroupId);

        Assert.False(result.IsAdminGeneral);
        Assert.False(result.IsAdmin);
        Assert.Equal(GroupRoles.Member, result.Role);
        Assert.All(PrivilegeCatalog.Gestion, p => Assert.False(result.HasGestion(p.Name), p.Name));
    }

    [Fact]
    public async Task MemberRole_AloneGrantsNothing_BecauseOptionPrivilegesComeFromGrantsOnly()
    {
        // The web app's PrivilegeService reads option_user_privileges and never consults
        // role_privileges, so a role by itself confers no option privilege.
        var result = await _resolver.ResolveAsync(MemberId, GroupId);

        Assert.All(PrivilegeCatalog.Option, p => Assert.False(result.HasOption(p.Name), p.Name));
    }

    [Fact]
    public async Task GrantedPrivilege_Resolves()
    {
        await GrantGestionAsync(MemberId, Priv.Gestion.ViewStock);

        var result = await _resolver.ResolveAsync(MemberId, GroupId);

        Assert.True(result.HasGestion(Priv.Gestion.ViewStock));
        Assert.False(result.HasGestion(Priv.Gestion.AddProducts));
    }

    [Fact]
    public async Task InactiveGrant_DoesNotResolve()
    {
        await GrantGestionAsync(MemberId, Priv.Gestion.ViewStock, isActive: false);

        var result = await _resolver.ResolveAsync(MemberId, GroupId);

        Assert.False(result.HasGestion(Priv.Gestion.ViewStock));
    }

    [Fact]
    public async Task ExpiredGrant_DoesNotResolve()
    {
        await GrantGestionAsync(MemberId, Priv.Gestion.ViewStock,
            expiresAt: DateTime.UtcNow.AddMinutes(-1));

        var result = await _resolver.ResolveAsync(MemberId, GroupId);

        Assert.False(result.HasGestion(Priv.Gestion.ViewStock));
    }

    [Fact]
    public async Task FutureExpiry_StillResolves()
    {
        await GrantGestionAsync(MemberId, Priv.Gestion.ViewStock,
            expiresAt: DateTime.UtcNow.AddHours(1));

        var result = await _resolver.ResolveAsync(MemberId, GroupId);

        Assert.True(result.HasGestion(Priv.Gestion.ViewStock));
    }

    // --- Admin-only privileges ---

    [Fact]
    public async Task AdminOnlyPrivilege_StaysFalseForAMember_EvenWhenGranted()
    {
        // can_delete_products is is_admin_only. The web app forces such privileges to false
        // when resolving, so a stray grant row must not take effect.
        await GrantGestionAsync(MemberId, Priv.Gestion.DeleteProducts);

        var result = await _resolver.ResolveAsync(MemberId, GroupId);

        Assert.False(result.HasGestion(Priv.Gestion.DeleteProducts));
    }

    [Fact]
    public async Task AdminOnlyPrivilege_StaysFalseForASubAdmin_EvenWhenGranted()
    {
        await GrantGestionAsync(SubAdminId, Priv.Gestion.DeleteProducts);

        var result = await _resolver.ResolveAsync(SubAdminId, GroupId);

        Assert.True(result.IsAdmin);
        Assert.False(result.HasGestion(Priv.Gestion.DeleteProducts));
    }

    // --- Virtual privileges ---

    [Fact]
    public async Task SubAdmin_GetsAuditAndParametres_WithoutAnyGrant()
    {
        var result = await _resolver.ResolveAsync(SubAdminId, GroupId);

        Assert.True(result.IsAdmin);
        Assert.True(result.HasGestion(Priv.Gestion.ViewAudit));
        Assert.True(result.HasGestion(Priv.Gestion.ViewParametres));
    }

    [Fact]
    public async Task Member_DoesNotGetAuditOrParametres()
    {
        var result = await _resolver.ResolveAsync(MemberId, GroupId);

        Assert.False(result.HasGestion(Priv.Gestion.ViewAudit));
        Assert.False(result.HasGestion(Priv.Gestion.ViewParametres));
    }

    // --- Scoping ---

    [Fact]
    public async Task GrantInOneGroup_DoesNotLeakIntoAnother()
    {
        _db.Groupes.Add(new Groupe { Id = "group-2", Nom = "Autre", IdUserAdmin = SubAdminId });
        _db.GroupMembers.Add(new GroupMember { IdGroupe = "group-2", IdUser = MemberId });
        await _db.SaveChangesAsync();

        await GrantGestionAsync(MemberId, Priv.Gestion.ViewStock);

        var other = await _resolver.ResolveAsync(MemberId, "group-2");

        Assert.False(other.HasGestion(Priv.Gestion.ViewStock));
    }

    [Fact]
    public async Task CreatorOfOneGroup_IsAnOrdinaryMemberOfAnother()
    {
        _db.Groupes.Add(new Groupe { Id = "group-2", Nom = "Autre", IdUserAdmin = SubAdminId });
        _db.GroupMembers.Add(new GroupMember { IdGroupe = "group-2", IdUser = CreatorId });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(CreatorId, "group-2");

        Assert.False(result.IsAdminGeneral);
        Assert.Equal(GroupRoles.Member, result.Role);
        Assert.False(result.HasGestion(Priv.Gestion.ViewStock));
    }

    // --- Menu filtering ---

    [Fact]
    public async Task Menu_HidesGestionEntriesTheUserCannotOpen()
    {
        await GrantGestionAsync(MemberId, Priv.Gestion.ViewStock);
        await GrantGestionAsync(MemberId, Priv.Gestion.ViewVentes);

        var result = await _resolver.ResolveAsync(MemberId, GroupId);
        var visible = AppMenu.Visible(AppMenu.Gestion, result.All(), gestionAccess: true, prestationsEnabled: false);

        Assert.Equal(["gestion-de-stock", "ventes"], visible.Select(e => e.Key));
    }

    [Fact]
    public async Task Menu_ShowsEveryGestionEntryForTheCreator()
    {
        var result = await _resolver.ResolveAsync(CreatorId, GroupId);
        var visible = AppMenu.Visible(AppMenu.Gestion, result.All(), gestionAccess: true, prestationsEnabled: true);

        Assert.Equal(AppMenu.Gestion.Count, visible.Count);
    }

    [Fact]
    public async Task Menu_HidesPrestationsWhenTheToggleIsOff()
    {
        var result = await _resolver.ResolveAsync(CreatorId, GroupId);
        var visible = AppMenu.Visible(AppMenu.Gestion, result.All(), gestionAccess: true, prestationsEnabled: false);

        Assert.DoesNotContain(visible, e => e.Key == "prestations");
    }

    [Fact]
    public async Task Menu_HidesTheGestionEntryWhenTheGroupHasNoGestionAccess()
    {
        var result = await _resolver.ResolveAsync(CreatorId, GroupId);
        var visible = AppMenu.Visible(AppMenu.Espace, result.All(), gestionAccess: false, prestationsEnabled: false);

        Assert.DoesNotContain(visible, e => e.Key == "espace/gestion");
    }

    // --- Default grants on joining ---

    [Fact]
    public async Task JoiningMember_ReceivesTheBaselineOptionPrivileges()
    {
        await new DatabaseSeeder(_db).GrantDefaultOptionPrivilegesAsync(MemberId, GroupId, CreatorId);

        var result = await _resolver.ResolveAsync(MemberId, GroupId);

        Assert.True(result.HasOption(Priv.Option.ViewProgramme));
        Assert.True(result.HasOption(Priv.Option.ViewChat));
        Assert.True(result.HasOption(Priv.Option.SendMessages));
        Assert.True(result.HasOption(Priv.Option.ViewFormulaires));
        Assert.True(result.HasOption(Priv.Option.RespondToForms));

        // Anything outside the baseline stays off.
        Assert.False(result.HasOption(Priv.Option.CreateFormulaires));
    }

    [Fact]
    public async Task DefaultGrants_AreNotWrittenForTheCreator()
    {
        await new DatabaseSeeder(_db).GrantDefaultOptionPrivilegesAsync(CreatorId, GroupId, CreatorId);

        Assert.Empty(_db.OptionUserPrivileges.Where(p => p.UserId == CreatorId));
    }

    private async Task GrantGestionAsync(
        string userId, string privilegeName, bool isActive = true, DateTime? expiresAt = null)
    {
        var privilege = await _db.GestionPrivileges.FirstAsync(p => p.Name == privilegeName);

        _db.GestionUserPrivileges.Add(new GestionUserPrivilege
        {
            UserId = userId,
            GroupId = GroupId,
            PrivilegeId = privilege.Id,
            IsActive = isActive,
            ExpiresAt = expiresAt,
        });

        await _db.SaveChangesAsync();
    }
}
