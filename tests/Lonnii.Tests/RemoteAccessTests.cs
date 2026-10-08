using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Api.Features.Backup;
using Lonnii.Api.Features.Members;
using Lonnii.Api.Features.Remote;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Data.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// An administrator of an online shop reads its data from afar: the server unpacks the shop's
/// newest cloud-backup snapshot, hands out a signed remote session, and answers the usual
/// endpoints from that copy - read-only, and only for administrators.
/// </summary>
public class RemoteAccessTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";
    private const string AdminEmail = "admin@pharmacie-nord.bf";
    private const string StaffEmail = "caissier@pharmacie-nord.bf";
    private const string Password = "Motdepasse123";
    private const string ProductId = "7a3c1f9e-0b52-4d27-9c11-3f5a8e6d2b40";

    private string _adminId = null!;
    private string _staffId = null!;

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory));
        _client = _factory.CreateClient();

        await SeedServerAsync(DeploymentModes.Online, subscribed: true);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>What the licence server holds: the shop's record, its registered administrator and
    /// a second account, and the subscription.</summary>
    private async Task SeedServerAsync(string mode, bool subscribed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var admin = new User { Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(Password), IsVerified = true };
        var staff = new User { Email = StaffEmail, Password = BCrypt.Net.BCrypt.HashPassword(Password), IsVerified = true };
        db.Users.AddRange(admin, staff);
        db.Groupes.Add(new Groupe { Id = GroupId, Nom = "Pharmacie Nord", IdUserAdmin = admin.IdUser, Mode = mode, MaxDevices = 3 });

        if (subscribed)
        {
            db.DashboardSubscriptions.Add(new DashboardSubscription
            {
                GroupId = GroupId,
                GroupName = "Pharmacie Nord",
                AdminName = "Sasso",
                Montant = 60_000m,
                Statut = StatutAbonnement.Active,
                ContractStartDate = DateTime.UtcNow.AddMonths(-2),
                ContractEndDate = DateTime.UtcNow.AddMonths(10),
            });
        }

        await db.SaveChangesAsync();
        _adminId = admin.IdUser;
        _staffId = staff.IdUser;
    }

    /// <summary>What the host uploads: its own database with a product only it knows about, the
    /// administrator as the shop's creator and the other account as an ordinary member.</summary>
    private async Task UploadSnapshotAsync(string productName = "Paracétamol 500 mg")
    {
        var path = Path.Combine(_dataDirectory, "host-" + Guid.NewGuid().ToString("N") + ".db");

        await using (var host = new LonniiDbContext(ReplicaStore.OptionsFor(path)))
        {
            await new DatabaseSeeder(host).MigrateAndSeedAsync();

            host.Users.AddRange(
                new User { IdUser = _adminId, Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(Password), IsVerified = true },
                new User { IdUser = _staffId, Email = StaffEmail, Password = BCrypt.Net.BCrypt.HashPassword(Password), IsVerified = true });
            host.Groupes.Add(new Groupe { Id = GroupId, Nom = "Pharmacie Nord", IdUserAdmin = _adminId, Mode = DeploymentModes.Local });
            host.GroupMembers.AddRange(
                new GroupMember { IdGroupe = GroupId, IdUser = _adminId },
                new GroupMember { IdGroupe = GroupId, IdUser = _staffId });
            host.UserRoles.AddRange(
                new UserRole { UserId = _adminId, GroupId = GroupId, Role = GroupRoles.Admin },
                new UserRole { UserId = _staffId, GroupId = GroupId, Role = GroupRoles.Member });
            host.Products.Add(new Product { Id = ProductId, GroupId = GroupId, Name = productName, Price = 1500m, Quantity = 20 });

            await host.SaveChangesAsync();
        }

        SqliteConnection.ClearAllPools();

        using var gz = new MemoryStream();
        await using (var gzip = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
            await gzip.WriteAsync(await File.ReadAllBytesAsync(path));
        gz.Position = 0;

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<BackupStore>();
        Assert.NotNull(await store.SaveSnapshotAsync(GroupId, gz, recordCount: 1, imageCount: 0, CancellationToken.None));
    }

    private async Task<string> LoginAsync(string email)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }

    private async Task<HttpResponseMessage> OpenRemoteAsync(string jwt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/remote/{GroupId}/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return await _client.SendAsync(request);
    }

    private HttpRequestMessage Remote(HttpMethod method, string path, string jwt, string session)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.Add("x-group-session", session);
        return request;
    }

    private async Task<(string Jwt, RemoteSessionResponse Session)> AdminSessionAsync()
    {
        await UploadSnapshotAsync();
        var jwt = await LoginAsync(AdminEmail);
        var opened = await OpenRemoteAsync(jwt);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        return (jwt, (await opened.Content.ReadFromJsonAsync<RemoteSessionResponse>())!);
    }

    [Fact]
    public async Task An_administrator_reads_the_shops_data_from_its_copy()
    {
        var (jwt, session) = await AdminSessionAsync();

        Assert.StartsWith("rs.", session.SessionToken);

        var response = await _client.SendAsync(Remote(HttpMethod.Get, "/api/stock/products", jwt, session.SessionToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var products = (await response.Content.ReadFromJsonAsync<List<ProductDto>>())!;
        // The server's own database holds no products at all: this one only exists in the copy.
        Assert.Contains(products, p => p.Name == "Paracétamol 500 mg" && p.Price == 1500m);
    }

    [Fact]
    public async Task Every_response_says_how_old_the_copy_is()
    {
        var (jwt, session) = await AdminSessionAsync();

        var response = await _client.SendAsync(Remote(HttpMethod.Get, "/api/stock/products", jwt, session.SessionToken));

        Assert.True(response.Headers.Contains(RemoteRoutingMiddleware.UpdatedHeader));

        var info = (await (await _client.SendAsync(Remote(HttpMethod.Get, "/api/remote/info", jwt, session.SessionToken)))
            .Content.ReadFromJsonAsync<RemoteInfoResponse>())!;
        Assert.True(info.IsCopy);
        Assert.Equal(session.SnapshotAt, info.SnapshotAt);
    }

    [Fact]
    public async Task A_copys_photos_come_from_what_the_host_uploaded()
    {
        var (jwt, session) = await AdminSessionAsync();
        var name = $"{ProductId}_638000000000000000.jpg";
        byte[] photo = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<BackupStore>();
            Assert.True(await store.SaveImageAsync(GroupId, "products", name, new MemoryStream(photo), CancellationToken.None));
        }

        var response = await _client.SendAsync(Remote(HttpMethod.Get, $"/api/images/products/{name}", jwt, session.SessionToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(photo, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_remote_session_cannot_change_anything()
    {
        var (jwt, session) = await AdminSessionAsync();

        var write = Remote(HttpMethod.Post, "/api/stock/products", jwt, session.SessionToken);
        write.Content = JsonContent.Create(new SaveProductRequest("Nouveau", 100m));

        var response = await _client.SendAsync(write);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task A_newer_snapshot_replaces_the_copy()
    {
        var (jwt, session) = await AdminSessionAsync();
        await Task.Delay(20); // snapshots are ordered by their time, to the millisecond
        await UploadSnapshotAsync("Amoxicilline 1 g");

        var response = await _client.SendAsync(Remote(HttpMethod.Get, "/api/stock/products", jwt, session.SessionToken));

        var products = (await response.Content.ReadFromJsonAsync<List<ProductDto>>())!;
        Assert.Contains(products, p => p.Name == "Amoxicilline 1 g");
    }

    [Fact]
    public async Task An_ordinary_member_cannot_open_a_remote_session()
    {
        await UploadSnapshotAsync();

        var response = await OpenRemoteAsync(await LoginAsync(StaffEmail));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_shop_without_a_snapshot_has_nothing_to_show()
    {
        var response = await OpenRemoteAsync(await LoginAsync(AdminEmail));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_lapsed_subscription_refuses_a_new_remote_session()
    {
        await UploadSnapshotAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.DashboardSubscriptions.Single().ContractEndDate = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var response = await OpenRemoteAsync(await LoginAsync(AdminEmail));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [Fact]
    public async Task A_local_mode_shop_has_no_remote_access()
    {
        await UploadSnapshotAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Groupes.Single().Mode = DeploymentModes.Local;
            await db.SaveChangesAsync();
        }

        var response = await OpenRemoteAsync(await LoginAsync(AdminEmail));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_remote_token_belongs_to_the_account_that_opened_it()
    {
        var (_, session) = await AdminSessionAsync();
        var staffJwt = await LoginAsync(StaffEmail);

        var response = await _client.SendAsync(Remote(HttpMethod.Get, "/api/stock/products", staffJwt, session.SessionToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_tampered_remote_token_is_refused()
    {
        var (jwt, session) = await AdminSessionAsync();

        var response = await _client.SendAsync(Remote(HttpMethod.Get, "/api/stock/products", jwt, session.SessionToken + "0"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- The command queue ---------------------------------------------------------

    private async Task<HttpResponseMessage> QueueAsync(string jwt, string session, RemoteCommandRequest command)
    {
        var request = Remote(HttpMethod.Post, "/api/remote/commands", jwt, session);
        request.Content = JsonContent.Create(command);
        return await _client.SendAsync(request);
    }

    private static RemoteCommandRequest GrantViewStock(string userId, bool granted = true) =>
        new(RemoteCommandTypes.Privilege, userId, Priv.Gestion.ViewStock, "gestion", granted);

    /// <summary>The headers the shop's host sends: its machine token, not an account.</summary>
    private async Task<HttpRequestMessage> HostRequestAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        var activation = await _client.PostAsJsonAsync("/api/activation",
            new ActivationRequest(GroupId, AdminEmail, Password, "machine-1", "poste"));
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        var token = (await activation.Content.ReadFromJsonAsync<ActivationResponse>())!.BackupToken!;

        var request = new HttpRequestMessage(method, "/api/backup/" + path) { Content = content };
        request.Headers.Add("x-group-id", GroupId);
        request.Headers.Add("x-device-id", "machine-1");
        request.Headers.Add("x-backup-token", token);
        return request;
    }

    [Fact]
    public async Task A_remote_request_waits_in_a_queue_for_the_shop_and_leaves_the_copy_alone()
    {
        var (jwt, session) = await AdminSessionAsync();

        var queued = await QueueAsync(jwt, session.SessionToken, GrantViewStock(_staffId));

        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var command = (await queued.Content.ReadFromJsonAsync<RemoteCommandDto>())!;
        Assert.Equal(RemoteCommandStatuses.Pending, command.Status);
        Assert.Equal(_adminId, command.RequestedBy);

        // The screen sees it as waiting.
        var listed = (await (await _client.SendAsync(Remote(HttpMethod.Get, "/api/remote/commands", jwt, session.SessionToken)))
            .Content.ReadFromJsonAsync<RemoteCommandsResponse>())!;
        Assert.Contains(listed.Commands, c => c.Id == command.Id && c.Status == RemoteCommandStatuses.Pending);

        // Checking the request against the copy must not have changed the copy.
        var member = await _client.SendAsync(
            Remote(HttpMethod.Get, $"/api/privileges/member/{_staffId}", jwt, session.SessionToken));
        var privileges = (await member.Content.ReadFromJsonAsync<MemberPrivilegesResponse>())!;
        Assert.DoesNotContain(privileges.Gestion, p => p.Name == Priv.Gestion.ViewStock && p.IsGranted);
    }

    [Fact]
    public async Task The_host_collects_its_requests_and_reports_what_it_did()
    {
        var (jwt, session) = await AdminSessionAsync();
        var command = (await (await QueueAsync(jwt, session.SessionToken, GrantViewStock(_staffId)))
            .Content.ReadFromJsonAsync<RemoteCommandDto>())!;

        var collected = await _client.SendAsync(await HostRequestAsync(HttpMethod.Get, "commands"));
        Assert.Equal(HttpStatusCode.OK, collected.StatusCode);
        Assert.Contains((await collected.Content.ReadFromJsonAsync<RemoteCommandsResponse>())!.Commands, c => c.Id == command.Id);

        var reported = await _client.SendAsync(await HostRequestAsync(HttpMethod.Post, $"commands/{command.Id}/result",
            JsonContent.Create(new RemoteCommandResult(RemoteCommandStatuses.Applied))));
        Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);

        // Settled: no longer handed out, and the screen shows it as done.
        var again = (await (await _client.SendAsync(await HostRequestAsync(HttpMethod.Get, "commands")))
            .Content.ReadFromJsonAsync<RemoteCommandsResponse>())!;
        Assert.Empty(again.Commands);

        var listed = (await (await _client.SendAsync(Remote(HttpMethod.Get, "/api/remote/commands", jwt, session.SessionToken)))
            .Content.ReadFromJsonAsync<RemoteCommandsResponse>())!;
        Assert.Contains(listed.Commands, c => c.Id == command.Id && c.Status == RemoteCommandStatuses.Applied && c.AppliedAt is not null);

        // A second report for the same request is refused.
        var twice = await _client.SendAsync(await HostRequestAsync(HttpMethod.Post, $"commands/{command.Id}/result",
            JsonContent.Create(new RemoteCommandResult(RemoteCommandStatuses.Failed, "x"))));
        Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode);
    }

    [Fact]
    public async Task A_request_the_shop_would_refuse_is_refused_at_once()
    {
        var (jwt, session) = await AdminSessionAsync();

        // The creator already holds everything; an unknown privilege; an unknown member; a bad type.
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, GrantViewStock(_adminId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await QueueAsync(jwt, session.SessionToken,
            new(RemoteCommandTypes.Privilege, _staffId, "can_do_magic", "gestion", true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await QueueAsync(jwt, session.SessionToken, GrantViewStock("nobody"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, new("fly", _staffId))).StatusCode);

        var listed = (await (await _client.SendAsync(Remote(HttpMethod.Get, "/api/remote/commands", jwt, session.SessionToken)))
            .Content.ReadFromJsonAsync<RemoteCommandsResponse>())!;
        Assert.Empty(listed.Commands);
    }

    [Fact]
    public async Task A_role_change_can_be_queued_too()
    {
        var (jwt, session) = await AdminSessionAsync();

        var response = await QueueAsync(jwt, session.SessionToken, new(RemoteCommandTypes.Role, _staffId, Role: GroupRoles.Moderator));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public void A_request_left_too_long_for_an_offline_shop_is_dropped()
    {
        var store = _factory.Services.GetRequiredService<RemoteCommandStore>();
        var old = new RemoteCommandDto("old-1", RemoteCommandTypes.Role, _staffId, null, null, null, null, GroupRoles.Moderator,
            _adminId, null, DateTime.UtcNow - RemoteCommandStore.Lifetime - TimeSpan.FromHours(1), RemoteCommandStatuses.Pending);
        store.Enqueue(GroupId, old);

        Assert.Empty(store.Pending(GroupId));
        Assert.Equal(RemoteCommandStatuses.Expired, store.Recent(GroupId).Single().Status);
    }

    // --- The host applying a request ---------------------------------------------------

    /// <summary>The server's database doubles as the shop's own here: members and roles in place.</summary>
    private async Task MakeThisTheShopsHostAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        db.GroupMembers.AddRange(
            new GroupMember { IdGroupe = GroupId, IdUser = _adminId },
            new GroupMember { IdGroupe = GroupId, IdUser = _staffId });
        db.UserRoles.AddRange(
            new UserRole { UserId = _adminId, GroupId = GroupId, Role = GroupRoles.Admin },
            new UserRole { UserId = _staffId, GroupId = GroupId, Role = GroupRoles.Member });
        await db.SaveChangesAsync();
    }

    private static RemoteCommandDto Command(string requestedBy, string userId, bool granted = true) =>
        new(Guid.NewGuid().ToString(), RemoteCommandTypes.Privilege, userId, null, Priv.Gestion.ViewStock, "gestion",
            granted, null, requestedBy, null, DateTime.UtcNow, RemoteCommandStatuses.Pending);

    [Fact]
    public async Task The_host_applies_a_request_from_an_administrator_and_notes_it_was_remote()
    {
        await MakeThisTheShopsHostAsync();
        using var scope = _factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<RemoteCommandApplier>();

        var result = await applier.ApplyAsync(GroupId, Command(_adminId, _staffId), CancellationToken.None);

        Assert.Equal(RemoteCommandStatuses.Applied, result.Status);

        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var privilegeId = db.GestionPrivileges.Single(p => p.Name == Priv.Gestion.ViewStock).Id;
        Assert.True(db.GestionUserPrivileges.Single(g => g.UserId == _staffId && g.PrivilegeId == privilegeId).IsActive);
        Assert.StartsWith(PrivilegeChanges.RemoteReasonPrefix.Trim(), db.GestionPrivilegeAudits.Single().Reason);
    }

    [Fact]
    public async Task The_host_revokes_when_asked()
    {
        await MakeThisTheShopsHostAsync();
        using var scope = _factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<RemoteCommandApplier>();
        await applier.ApplyAsync(GroupId, Command(_adminId, _staffId, granted: true), CancellationToken.None);

        var result = await applier.ApplyAsync(GroupId, Command(_adminId, _staffId, granted: false), CancellationToken.None);

        Assert.Equal(RemoteCommandStatuses.Applied, result.Status);
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        Assert.False(db.GestionUserPrivileges.Single(g => g.UserId == _staffId).IsActive);
    }

    [Fact]
    public async Task The_host_ignores_a_request_from_someone_who_is_no_longer_an_administrator()
    {
        await MakeThisTheShopsHostAsync();
        using var scope = _factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<RemoteCommandApplier>();

        // The sender is an ordinary member on the shop's own data, whatever the server believed.
        var result = await applier.ApplyAsync(GroupId, Command(_staffId, _staffId), CancellationToken.None);

        Assert.Equal(RemoteCommandStatuses.Failed, result.Status);
        Assert.Contains("administrateur", result.Message);

        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        Assert.Empty(db.GestionUserPrivileges.Where(g => g.UserId == _staffId));
    }

    [Fact]
    public void Remote_tokens_expire_and_cannot_be_forged()
    {
        var tokens = new RemoteSessionTokens("secret-a");
        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var (token, expires) = tokens.Create(GroupId, "user-1", now);

        Assert.Equal(new RemoteSession(GroupId, "user-1", expires), tokens.Verify(token, now));
        Assert.Null(tokens.Verify(token, expires));                                  // expired
        Assert.Null(new RemoteSessionTokens("secret-b").Verify(token, now));         // another server's key
        Assert.Null(tokens.Verify(token.Replace("user-1", "user-2"), now));          // edited body
        Assert.Null(tokens.Verify("rs.garbage", now));
        Assert.Null(tokens.Verify(null, now));
    }
}
