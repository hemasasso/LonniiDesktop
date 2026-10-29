using System.Net;
using System.Net.Http.Json;
using Lonnii.Api.Features.Backup;
using Lonnii.Api.Features.Licensing;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Lonnii.Tests;

/// <summary>
/// A member added on an online shop's own computer must exist on our server too, or they cannot
/// pull the shop onto another computer nor open it from afar. Two apps: our server and the shop's
/// computer, each with its own database.
/// </summary>
[Collection(BackupGateCollection.Name)]
public class AccountSyncTests : IAsyncLifetime
{
    private const string OwnerEmail = "patron@boutique-sync.bf";
    private const string Password = "Motdepasse123";
    private const string GroupId = "7e2d4c1a-3b5f-4a6e-8c9d-0f1e2d3c4b5a";
    private const string OtherGroupId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
    private const string Device = "poste-principal";

    private WebApplicationFactory<Program> _server = null!;
    private WebApplicationFactory<Program> _host = null!;
    private string _serverData = null!, _hostData = null!;

    public async Task InitializeAsync()
    {
        _serverData = NewDirectory();
        _hostData = NewDirectory();

        _server = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Lonnii:DataDirectory", _serverData);
            b.ConfigureServices(NoBackgroundJobs);
        });
        _host = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Lonnii:DataDirectory", _hostData);
            b.ConfigureServices(NoBackgroundJobs);
            b.ConfigureServices(s => s.AddHttpClient(CloudBackupClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => _server.Server.CreateHandler()));
        });

        await OnServerAsync(db =>
        {
            var owner = NewUser(OwnerEmail);
            db.Users.Add(owner);
            db.Groupes.Add(new Groupe { Id = GroupId, Nom = "Boutique", IdUserAdmin = owner.IdUser, Mode = DeploymentModes.Online, MaxDevices = 5 });
            db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = owner.IdUser });
            db.DashboardSubscriptions.Add(new DashboardSubscription
            {
                GroupId = GroupId, GroupName = "Boutique", AdminName = "Patron", Montant = 60_000m,
                Statut = StatutAbonnement.Active,
                ContractStartDate = DateTime.UtcNow.AddMonths(-1), ContractEndDate = DateTime.UtcNow.AddMonths(11),
            });
        });

        var activation = await _server.CreateClient().PostAsJsonAsync("/api/activation",
            new ActivationRequest(GroupId, OwnerEmail, Password, Device, "poste"));
        var token = (await activation.Content.ReadFromJsonAsync<ActivationResponse>())!.BackupToken;

        // The shop's computer: the owner under its own id, as a computer set up by hand has.
        await OnHostAsync(db =>
        {
            var owner = NewUser(OwnerEmail);
            db.Users.Add(owner);
            db.Groupes.Add(new Groupe
            {
                Id = GroupId, Nom = "Boutique", IdUserAdmin = owner.IdUser,
                Mode = DeploymentModes.Online, MaxDevices = 5, LicenceServerUrl = "http://localhost",
            });
            db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = owner.IdUser });
            LicenceGuard.StoreBackupToken(db, GroupId, Device, token);
        });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _server.DisposeAsync();
        foreach (var dir in new[] { _serverData, _hostData })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// The backup, update and discovery jobs would otherwise run a pass of their own a minute in,
    /// alongside the one the test runs - on a slow machine, in the middle of it.
    /// </summary>
    private static void NoBackgroundJobs(IServiceCollection services) =>
        services.RemoveAll<IHostedService>();

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static User NewUser(string email, string password = Password) =>
        new() { Email = email, Password = BCrypt.Net.BCrypt.HashPassword(password), IsVerified = true };

    private async Task OnServerAsync(Action<LonniiDbContext> change) => await OnAsync(_server, change);
    private async Task OnHostAsync(Action<LonniiDbContext> change) => await OnAsync(_host, change);

    private static async Task OnAsync(WebApplicationFactory<Program> app, Action<LonniiDbContext> change)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        change(db);
        await db.SaveChangesAsync();
    }

    private async Task<T> FromServerAsync<T>(Func<LonniiDbContext, Task<T>> read)
    {
        using var scope = _server.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<LonniiDbContext>());
    }

    /// <summary>Adds a member on the shop's computer, as Gérer does.</summary>
    private async Task<User> AddOnHostAsync(string email, string role, string password = Password, DateTime? passwordChangedAt = null)
    {
        var user = NewUser(email, password);
        user.PasswordChangedAt = passwordChangedAt;
        await OnHostAsync(db =>
        {
            db.Users.Add(user);
            db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = user.IdUser });
            db.UserRoles.Add(new UserRole { UserId = user.IdUser, GroupId = GroupId, Role = role });
            var viewAll = db.GestionPrivileges.Single(p => p.Name == Priv.Gestion.ViewAllVentes);
            db.GestionUserPrivileges.Add(new GestionUserPrivilege { UserId = user.IdUser, GroupId = GroupId, PrivilegeId = viewAll.Id });
        });
        return user;
    }

    private async Task SyncAsync()
    {
        using var scope = _host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CloudBackupRunner>().RunAsync(GroupId, CancellationToken.None);
    }

    [Fact]
    public async Task A_sub_admin_added_on_the_computer_can_pull_the_shop_onto_another_one()
    {
        await AddOnHostAsync("adjoint@boutique-sync.bf", GroupRoles.SubAdmin);

        await SyncAsync();

        var (user, role, privileges) = await FromServerAsync(async db =>
        {
            var u = await db.Users.SingleAsync(x => x.Email == "adjoint@boutique-sync.bf");
            var r = (await db.UserRoles.Where(x => x.UserId == u.IdUser && x.GroupId == GroupId).ToListAsync()).Single().Role;
            var p = await (from g in db.GestionUserPrivileges
                           where g.UserId == u.IdUser && g.GroupId == GroupId
                           join c in db.GestionPrivileges on g.PrivilegeId equals c.Id
                           select c.Name).ToListAsync();
            return (u, r, p);
        });
        Assert.Equal(GroupRoles.SubAdmin, role);
        Assert.Contains(Priv.Gestion.ViewAllVentes, privileges);
        Assert.True(await FromServerAsync(db => db.GroupMembers.AnyAsync(m => m.IdGroupe == GroupId && m.IdUser == user.IdUser)));

        // "J'ai déjà un compte" on a laptop elsewhere: activation on our server, with their own password.
        var activation = await _server.CreateClient().PostAsJsonAsync("/api/activation",
            new ActivationRequest(GroupId, "adjoint@boutique-sync.bf", Password, "portable-afrique", "portable"));
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
    }

    [Fact]
    public async Task A_second_pass_changes_nothing_and_a_newer_password_follows()
    {
        var cashier = await AddOnHostAsync("caisse@boutique-sync.bf", GroupRoles.Member, passwordChangedAt: DateTime.UtcNow.AddDays(-2));
        await SyncAsync();
        await SyncAsync();

        Assert.Single(await FromServerAsync(db => db.Users.Where(u => u.Email == "caisse@boutique-sync.bf").ToListAsync()));

        await OnHostAsync(db =>
        {
            var u = db.Users.Single(x => x.IdUser == cashier.IdUser);
            u.Password = BCrypt.Net.BCrypt.HashPassword("NouveauMotdepasse1");
            u.PasswordChangedAt = DateTime.UtcNow;
        });
        await SyncAsync();

        var hash = await FromServerAsync(db => db.Users.Where(u => u.Email == "caisse@boutique-sync.bf").Select(u => u.Password).SingleAsync());
        Assert.True(BCrypt.Net.BCrypt.Verify("NouveauMotdepasse1", hash));
    }

    [Fact]
    public async Task An_account_used_by_another_shop_keeps_its_password_but_joins()
    {
        // On our server this person already works for another shop.
        await OnServerAsync(db =>
        {
            var other = NewUser("partage@boutique-sync.bf", "SonVraiMotdepasse1");
            var otherOwner = NewUser("autre-patron@boutique-sync.bf");
            db.Users.AddRange(other, otherOwner);
            db.Groupes.Add(new Groupe { Id = OtherGroupId, Nom = "Autre boutique", IdUserAdmin = otherOwner.IdUser, Mode = DeploymentModes.Online });
            db.GroupMembers.Add(new GroupMember { IdGroupe = OtherGroupId, IdUser = other.IdUser });
        });

        // This shop's computer claims the same email with a password of its choosing, set "now".
        await AddOnHostAsync("partage@boutique-sync.bf", GroupRoles.Member, "VolDeCompte123", DateTime.UtcNow);

        await SyncAsync();

        var user = await FromServerAsync(db => db.Users.SingleAsync(u => u.Email == "partage@boutique-sync.bf"));
        Assert.True(BCrypt.Net.BCrypt.Verify("SonVraiMotdepasse1", user.Password));
        Assert.True(await FromServerAsync(db => db.GroupMembers.AnyAsync(m => m.IdGroupe == GroupId && m.IdUser == user.IdUser)));
    }

    /// <summary>A member added on our server - from the phone, the web, another computer.</summary>
    private async Task<User> AddOnServerAsync(string email, string role)
    {
        var user = NewUser(email);
        await OnServerAsync(db =>
        {
            db.Users.Add(user);
            db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = user.IdUser });
            db.UserRoles.Add(new UserRole { UserId = user.IdUser, GroupId = GroupId, Role = role });
            var viewAll = db.GestionPrivileges.Single(p => p.Name == Priv.Gestion.ViewAllVentes);
            db.GestionUserPrivileges.Add(new GestionUserPrivilege { UserId = user.IdUser, GroupId = GroupId, PrivilegeId = viewAll.Id });
        });
        return user;
    }

    private async Task<T> FromHostAsync<T>(Func<LonniiDbContext, Task<T>> read)
    {
        using var scope = _host.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<LonniiDbContext>());
    }

    [Fact]
    public async Task A_member_added_on_our_server_arrives_on_the_computer_with_their_password()
    {
        var remote = await AddOnServerAsync("vendeur@boutique-sync.bf", GroupRoles.Member);

        await SyncAsync();

        var user = await FromHostAsync(db => db.Users.SingleAsync(u => u.Email == "vendeur@boutique-sync.bf"));
        Assert.Equal(remote.IdUser, user.IdUser);
        Assert.True(BCrypt.Net.BCrypt.Verify(Password, user.Password));
        Assert.True(await FromHostAsync(db => db.GroupMembers.AnyAsync(m => m.IdGroupe == GroupId && m.IdUser == remote.IdUser)));
        Assert.True(await FromHostAsync(async db => (await db.GestionUserPrivileges
            .Where(p => p.UserId == remote.IdUser && p.GroupId == GroupId).CountAsync()) == 1));
    }

    [Fact]
    public async Task Someone_the_computer_knows_under_another_id_takes_the_server_s_id()
    {
        // Created on both sides separately, before any sync: two ids for one person.
        var remote = await AddOnServerAsync("double@boutique-sync.bf", GroupRoles.Member);
        var local = NewUser("double@boutique-sync.bf");
        await OnHostAsync(db =>
        {
            db.Users.Add(local);
            db.Groupes.Add(new Groupe { Id = OtherGroupId, Nom = "Dépôt", IdUserAdmin = local.IdUser, Mode = DeploymentModes.Local });
            db.GroupMembers.Add(new GroupMember { IdGroupe = OtherGroupId, IdUser = local.IdUser });
        });

        await SyncAsync();

        Assert.Single(await FromHostAsync(db => db.Users.Where(u => u.Email == "double@boutique-sync.bf").ToListAsync()));
        Assert.False(await FromHostAsync(db => db.Users.AnyAsync(u => u.IdUser == local.IdUser)));
        // Everything that pointed at the old id follows: the other espace still has its owner.
        Assert.Equal(remote.IdUser, await FromHostAsync(db => db.Groupes.Where(g => g.Id == OtherGroupId).Select(g => g.IdUserAdmin).SingleAsync()));
    }

    [Fact]
    public async Task Adding_a_member_known_only_to_our_server_needs_no_password()
    {
        var remote = await AddOnServerAsync("nouveau@boutique-sync.bf", GroupRoles.Member);

        var host = _host.CreateClient();
        var login = (await (await host.PostAsJsonAsync("/api/auth/login", new LoginRequest(OwnerEmail, Password)))
            .Content.ReadFromJsonAsync<LoginResponse>())!;
        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{GroupId}/session");
        open.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);
        var session = (await (await host.SendAsync(open)).Content.ReadFromJsonAsync<GroupSessionResponse>())!;

        using var add = new HttpRequestMessage(HttpMethod.Post, "/api/groupe/members")
        {
            Content = JsonContent.Create(new AddMemberRequest("nouveau@boutique-sync.bf")),
        };
        add.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);
        add.Headers.Add("x-group-session", session.SessionToken);
        var response = await host.SendAsync(add);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await FromHostAsync(db => db.GroupMembers.AnyAsync(m => m.IdGroupe == GroupId && m.IdUser == remote.IdUser)));
    }

    [Fact]
    public async Task An_account_in_no_shop_cannot_be_taken_over_by_naming_its_email()
    {
        // Registered on our server, member of nothing (never added, or has left).
        await OnServerAsync(db => db.Users.Add(NewUser("seul@boutique-sync.bf", "SonVraiMotdepasse1")));

        await AddOnHostAsync("seul@boutique-sync.bf", GroupRoles.Member, "VolDeCompte123", DateTime.UtcNow);
        await SyncAsync();

        var hash = await FromServerAsync(db => db.Users.Where(u => u.Email == "seul@boutique-sync.bf").Select(u => u.Password).SingleAsync());
        Assert.True(BCrypt.Net.BCrypt.Verify("SonVraiMotdepasse1", hash));
    }

    [Fact]
    public async Task A_member_shared_with_another_shop_arrives_without_their_password_hash()
    {
        var shared = NewUser("partage2@boutique-sync.bf", "SonVraiMotdepasse1");
        await OnServerAsync(db =>
        {
            var otherOwner = NewUser("autre-patron2@boutique-sync.bf");
            db.Users.AddRange(shared, otherOwner);
            db.Groupes.Add(new Groupe { Id = OtherGroupId, Nom = "Autre boutique", IdUserAdmin = otherOwner.IdUser, Mode = DeploymentModes.Online });
            db.GroupMembers.Add(new GroupMember { IdGroupe = OtherGroupId, IdUser = shared.IdUser });
            // ...and someone added them to this shop too.
            db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = shared.IdUser });
        });

        await SyncAsync();

        var onHost = await FromHostAsync(db => db.Users.SingleAsync(u => u.Email == "partage2@boutique-sync.bf"));
        Assert.Null(onHost.Password);
        Assert.True(await FromHostAsync(db => db.GroupMembers.AnyAsync(m => m.IdGroupe == GroupId && m.IdUser == shared.IdUser)));
    }

    [Fact]
    public async Task The_owner_on_our_server_is_never_changed_from_the_shop()
    {
        await OnHostAsync(db =>
        {
            var owner = db.Users.Single(u => u.Email == OwnerEmail);
            owner.Password = BCrypt.Net.BCrypt.HashPassword("AutreMotdepasse1");
            owner.PasswordChangedAt = DateTime.UtcNow;
        });

        await SyncAsync();

        var hash = await FromServerAsync(db => db.Users.Where(u => u.Email == OwnerEmail).Select(u => u.Password).SingleAsync());
        Assert.True(BCrypt.Net.BCrypt.Verify(Password, hash));
    }
}
