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
using Microsoft.EntityFrameworkCore;
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
    private async Task UploadSnapshotAsync(string productName = "Paracétamol 500 mg", string? adminShopId = null)
    {
        var shopAdminId = adminShopId ?? _adminId;
        var path = Path.Combine(_dataDirectory, "host-" + Guid.NewGuid().ToString("N") + ".db");

        await using (var host = new LonniiDbContext(ReplicaStore.OptionsFor(path)))
        {
            await new DatabaseSeeder(host).MigrateAndSeedAsync();

            host.Users.AddRange(
                new User { IdUser = shopAdminId, Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(Password), IsVerified = true },
                new User { IdUser = _staffId, Email = StaffEmail, Password = BCrypt.Net.BCrypt.HashPassword(Password), IsVerified = true });
            host.Groupes.Add(new Groupe { Id = GroupId, Nom = "Pharmacie Nord", IdUserAdmin = shopAdminId, Mode = DeploymentModes.Local });
            host.GroupMembers.AddRange(
                new GroupMember { IdGroupe = GroupId, IdUser = shopAdminId },
                new GroupMember { IdGroupe = GroupId, IdUser = _staffId });
            host.UserRoles.AddRange(
                new UserRole { UserId = shopAdminId, GroupId = GroupId, Role = GroupRoles.Admin },
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

    private async Task<string> LoginAsync(string email, string password = Password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
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

    // --- Product requests ----------------------------------------------------------------

    [Fact]
    public async Task A_price_change_is_queued_and_the_copy_keeps_the_old_price()
    {
        var (jwt, session) = await AdminSessionAsync();

        var queued = await QueueAsync(jwt, session.SessionToken, new RemoteCommandRequest(
            RemoteCommandTypes.ProductUpdate, _adminId, ProductId: ProductId,
            Product: new SaveProductRequest("Paracétamol 500 mg", 1750m)));

        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var command = (await queued.Content.ReadFromJsonAsync<RemoteCommandDto>())!;
        Assert.Equal("Paracétamol 500 mg", command.TargetName);
        Assert.Equal(1750m, command.Product!.Price);

        var products = (await (await _client.SendAsync(Remote(HttpMethod.Get, "/api/stock/products", jwt, session.SessionToken)))
            .Content.ReadFromJsonAsync<List<ProductDto>>())!;
        Assert.Equal(1500m, products.Single(p => p.Id == ProductId).Price);
    }

    [Fact]
    public async Task A_product_request_the_shop_would_refuse_is_refused_at_once()
    {
        var (jwt, session) = await AdminSessionAsync();

        // The stock screen's own rules: no zero adjustment, no unknown product, no negative price.
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, new RemoteCommandRequest(
            RemoteCommandTypes.ProductAdjust, _adminId, ProductId: ProductId,
            StockAdjustment: new AdjustStockRequest(0, "adjustment")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await QueueAsync(jwt, session.SessionToken, new RemoteCommandRequest(
            RemoteCommandTypes.ProductDeactivate, _adminId, ProductId: "no-such-product"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, new RemoteCommandRequest(
            RemoteCommandTypes.ProductUpdate, _adminId, ProductId: ProductId,
            Product: new SaveProductRequest("Paracétamol 500 mg", -5m)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, new RemoteCommandRequest(
            RemoteCommandTypes.ProductUpdate, _adminId, ProductId: ProductId))).StatusCode);   // no new details

        var listed = (await (await _client.SendAsync(Remote(HttpMethod.Get, "/api/remote/commands", jwt, session.SessionToken)))
            .Content.ReadFromJsonAsync<RemoteCommandsResponse>())!;
        Assert.Empty(listed.Commands);
    }

    private async Task AddHostProductAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        db.Products.Add(new Product { Id = ProductId, GroupId = GroupId, Name = "Paracétamol 500 mg", Price = 1500m, Quantity = 20 });
        await db.SaveChangesAsync();
    }

    private static RemoteCommandDto ProductCommand(string requestedBy, string type,
        SaveProductRequest? product = null, AdjustStockRequest? adjustment = null, string? productId = ProductId) =>
        new(Guid.NewGuid().ToString(), type, requestedBy, null, null, null, null, null, requestedBy, null,
            DateTime.UtcNow, RemoteCommandStatuses.Pending, ProductId: productId, Product: product, StockAdjustment: adjustment);

    [Fact]
    public async Task The_host_applies_product_requests_with_the_stock_screens_own_rules()
    {
        await MakeThisTheShopsHostAsync();
        await AddHostProductAsync();
        using var scope = _factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<RemoteCommandApplier>();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        // Price.
        Assert.Equal(RemoteCommandStatuses.Applied, (await applier.ApplyAsync(GroupId, ProductCommand(_adminId,
            RemoteCommandTypes.ProductUpdate, product: new SaveProductRequest("Paracétamol 500 mg", 1750m)), CancellationToken.None)).Status);

        // Stock, with its history line.
        Assert.Equal(RemoteCommandStatuses.Applied, (await applier.ApplyAsync(GroupId, ProductCommand(_adminId,
            RemoteCommandTypes.ProductAdjust, adjustment: new AdjustStockRequest(-3, "damaged", "Cassé")), CancellationToken.None)).Status);

        // A new product.
        Assert.Equal(RemoteCommandStatuses.Applied, (await applier.ApplyAsync(GroupId, ProductCommand(_adminId,
            RemoteCommandTypes.ProductCreate, product: new SaveProductRequest("Ibuprofène 400 mg", 900m, Quantity: 10), productId: null),
            CancellationToken.None)).Status);

        var product = db.Products.AsNoTracking().Single(p => p.Id == ProductId);
        Assert.Equal(1750m, product.Price);
        Assert.Equal(17, product.Quantity);
        Assert.Contains(db.StockHistories.AsNoTracking(), h => h.ProductId == ProductId && h.QuantityChanged == -3);
        Assert.Contains(db.Products.AsNoTracking(), p => p.Name == "Ibuprofène 400 mg" && p.Quantity == 10);

        // Off sale, history kept.
        Assert.Equal(RemoteCommandStatuses.Applied, (await applier.ApplyAsync(GroupId, ProductCommand(_adminId,
            RemoteCommandTypes.ProductDeactivate), CancellationToken.None)).Status);
        Assert.NotNull(db.Products.AsNoTracking().Single(p => p.Id == ProductId).DeletedAt);
    }

    [Fact]
    public async Task The_host_refuses_a_product_request_from_someone_who_is_no_longer_an_administrator()
    {
        await MakeThisTheShopsHostAsync();
        await AddHostProductAsync();
        using var scope = _factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<RemoteCommandApplier>();

        var result = await applier.ApplyAsync(GroupId, ProductCommand(_staffId,
            RemoteCommandTypes.ProductUpdate, product: new SaveProductRequest("Paracétamol 500 mg", 1m)), CancellationToken.None);

        Assert.Equal(RemoteCommandStatuses.Failed, result.Status);
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        Assert.Equal(1500m, db.Products.AsNoTracking().Single(p => p.Id == ProductId).Price);
    }

    // --- Photos sent from afar ---------------------------------------------------------

    private static byte[] SmallJpeg()
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(8, 8);
        using var stream = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsJpeg(image, stream);
        return stream.ToArray();
    }

    private async Task<HttpResponseMessage> UploadPhotoAsync(string jwt, string session, byte[] bytes)
    {
        var request = Remote(HttpMethod.Post, "/api/remote/commands/photos", jwt, session);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        request.Content = new MultipartFormDataContent { { file, "file", "photo.jpg" } };
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task A_photo_travels_with_a_new_product_to_the_shop_and_is_then_dropped()
    {
        var (jwt, session) = await AdminSessionAsync();
        var bytes = SmallJpeg();

        var uploaded = await UploadPhotoAsync(jwt, session.SessionToken, bytes);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var photoId = (await uploaded.Content.ReadFromJsonAsync<RemotePhotoResponse>())!.PhotoId;

        var queued = await QueueAsync(jwt, session.SessionToken, new RemoteCommandRequest(
            RemoteCommandTypes.ProductCreate, _adminId, Product: new SaveProductRequest("Vitamine C", 500m), PhotoId: photoId));
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var command = (await queued.Content.ReadFromJsonAsync<RemoteCommandDto>())!;
        Assert.Equal(photoId, command.PhotoId);

        // The shop's host downloads it with its machine token...
        var download = await _client.SendAsync(await HostRequestAsync(HttpMethod.Get, $"commands/{command.Id}/photo"));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());

        // ...and once it has reported, the server no longer keeps it.
        await _client.SendAsync(await HostRequestAsync(HttpMethod.Post, $"commands/{command.Id}/result",
            JsonContent.Create(new RemoteCommandResult(RemoteCommandStatuses.Applied))));
        var again = await _client.SendAsync(await HostRequestAsync(HttpMethod.Get, $"commands/{command.Id}/photo"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_photo_that_is_not_an_image_or_goes_with_the_wrong_request_is_refused()
    {
        var (jwt, session) = await AdminSessionAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadPhotoAsync(jwt, session.SessionToken, [1, 2, 3, 4])).StatusCode);

        var photoId = (await (await UploadPhotoAsync(jwt, session.SessionToken, SmallJpeg()))
            .Content.ReadFromJsonAsync<RemotePhotoResponse>())!.PhotoId;

        // A stock adjustment carries no photo; an unknown photo; a photo that is both sent and removed.
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, new RemoteCommandRequest(
            RemoteCommandTypes.ProductAdjust, _adminId, ProductId: ProductId,
            StockAdjustment: new AdjustStockRequest(2, "ajout"), PhotoId: photoId))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, new RemoteCommandRequest(
            RemoteCommandTypes.ProductUpdate, _adminId, ProductId: ProductId,
            Product: new SaveProductRequest("Paracétamol 500 mg", 1500m), PhotoId: Guid.NewGuid().ToString("N")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, new RemoteCommandRequest(
            RemoteCommandTypes.ProductUpdate, _adminId, ProductId: ProductId,
            Product: new SaveProductRequest("Paracétamol 500 mg", 1500m), PhotoId: photoId, RemovePhoto: true))).StatusCode);
    }

    [Fact]
    public async Task The_host_attaches_a_photo_sent_from_afar_and_removes_one_when_asked()
    {
        await MakeThisTheShopsHostAsync();
        await AddHostProductAsync();
        using var scope = _factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<RemoteCommandApplier>();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        // A new product with its photo.
        var create = ProductCommand(_adminId, RemoteCommandTypes.ProductCreate,
            product: new SaveProductRequest("Vitamine C", 500m), productId: null) with { PhotoId = Guid.NewGuid().ToString("N") };
        Assert.Equal(RemoteCommandStatuses.Applied, (await applier.ApplyAsync(GroupId, create, CancellationToken.None, SmallJpeg())).Status);
        var created = db.Products.AsNoTracking().Single(p => p.Name == "Vitamine C");
        Assert.StartsWith("/api/images/products/" + created.Id, created.ImageUrl);

        // An existing product's photo removed.
        db.Products.Single(p => p.Id == ProductId).ImageUrl = created.ImageUrl!.Replace(created.Id, ProductId);
        await db.SaveChangesAsync();
        var remove = ProductCommand(_adminId, RemoteCommandTypes.ProductUpdate,
            product: new SaveProductRequest("Paracétamol 500 mg", 1500m)) with { RemovePhoto = true };
        Assert.Equal(RemoteCommandStatuses.Applied, (await applier.ApplyAsync(GroupId, remove, CancellationToken.None)).Status);
        Assert.Null(db.Products.AsNoTracking().Single(p => p.Id == ProductId).ImageUrl);

        // A photo that never arrived: the product change stands, with a note saying so.
        var lost = ProductCommand(_adminId, RemoteCommandTypes.ProductUpdate,
            product: new SaveProductRequest("Paracétamol 500 mg", 1600m)) with { PhotoId = Guid.NewGuid().ToString("N") };
        var result = await applier.ApplyAsync(GroupId, lost, CancellationToken.None, photo: null);
        Assert.Equal(RemoteCommandStatuses.Applied, result.Status);
        Assert.Contains("photo", result.Message);
        Assert.Equal(1600m, db.Products.AsNoTracking().Single(p => p.Id == ProductId).Price);
    }

    [Fact]
    public void Remote_tokens_expire_and_cannot_be_forged()
    {
        var tokens = new RemoteSessionTokens("secret-a");
        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var (token, expires) = tokens.Create(GroupId, "user-1", "shop-1", now);

        Assert.Equal(new RemoteSession(GroupId, "user-1", "shop-1", expires), tokens.Verify(token, now));
        Assert.Null(tokens.Verify(token, expires));                                  // expired
        Assert.Null(new RemoteSessionTokens("secret-b").Verify(token, now));         // another server's key
        Assert.Null(tokens.Verify(token.Replace("user-1", "user-2"), now));          // edited account
        Assert.Null(tokens.Verify(token.Replace("shop-1", "shop-2"), now));          // edited shop account
        Assert.Null(tokens.Verify("rs.garbage", now));
        Assert.Null(tokens.Verify(null, now));
    }

    // --- Two accounts, one person ----------------------------------------------------------

    [Fact]
    public async Task An_administrator_whose_shop_account_has_another_id_is_matched_by_email()
    {
        const string shopAdminId = "shop-side-admin-id";
        await UploadSnapshotAsync(adminShopId: shopAdminId);
        var jwt = await LoginAsync(AdminEmail);

        var opened = await OpenRemoteAsync(jwt);

        Assert.True(opened.StatusCode == HttpStatusCode.OK, "open: " + await opened.Content.ReadAsStringAsync());
        var session = (await opened.Content.ReadFromJsonAsync<RemoteSessionResponse>())!;

        var read = await _client.SendAsync(Remote(HttpMethod.Get, "/api/stock/products", jwt, session.SessionToken));
        Assert.True(read.StatusCode == HttpStatusCode.OK, "read: " + read.StatusCode + " " + await read.Content.ReadAsStringAsync());

        // Inside the shop the person is known by the shop's own id - the one the host will check
        // when it applies the request, and the one the audit trail names.
        var queued = await QueueAsync(jwt, session.SessionToken, GrantViewStock(_staffId));
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal(shopAdminId, (await queued.Content.ReadFromJsonAsync<RemoteCommandDto>())!.RequestedBy);
    }

    [Fact]
    public async Task An_account_whose_email_the_server_has_not_verified_is_not_matched()
    {
        await UploadSnapshotAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Users.Single(u => u.IdUser == _adminId).IsVerified = false;
            await db.SaveChangesAsync();
        }

        var response = await OpenRemoteAsync(await LoginAsync(AdminEmail));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_account_with_no_counterpart_in_the_shop_is_refused()
    {
        await UploadSnapshotAsync();
        const string otherEmail = "autre@pharmacie-nord.bf";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Users.Single(u => u.IdUser == _adminId).Email = otherEmail;
            await db.SaveChangesAsync();
        }

        var response = await OpenRemoteAsync(await LoginAsync(otherEmail));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Member requests -----------------------------------------------------------------

    private const string HireEmail = "nouveau@pharmacie.test";
    private const string HirePassword = "Embauche2026";

    private static RemoteCommandRequest AddHire(string? password = HirePassword, string identifier = HireEmail) =>
        new(RemoteCommandTypes.MemberAdd, string.Empty,
            Member: new AddMemberRequest(identifier, password, FirstName: "Awa", LastName: "Traoré"));

    [Fact]
    public async Task A_hire_is_queued_with_a_hashed_password_and_the_copy_is_left_alone()
    {
        var (jwt, session) = await AdminSessionAsync();

        var queued = await QueueAsync(jwt, session.SessionToken, AddHire());

        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var command = (await queued.Content.ReadFromJsonAsync<RemoteCommandDto>())!;
        Assert.Equal("Awa Traoré", command.TargetName);
        Assert.Null(command.Member!.Password);

        // The screen's list never shows the hash; the shop gets it, and it matches the password.
        var listed = (await (await _client.SendAsync(Remote(HttpMethod.Get, "/api/remote/commands", jwt, session.SessionToken)))
            .Content.ReadFromJsonAsync<RemoteCommandsResponse>())!;
        Assert.Null(listed.Commands.Single(c => c.Id == command.Id).PasswordHash);

        var collected = (await (await _client.SendAsync(await HostRequestAsync(HttpMethod.Get, "commands")))
            .Content.ReadFromJsonAsync<RemoteCommandsResponse>())!;
        var forShop = collected.Commands.Single(c => c.Id == command.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(HirePassword, forShop.PasswordHash));

        // Checking the request against the copy must not have added anyone to it.
        var members = await _client.SendAsync(Remote(HttpMethod.Get, "/api/groupe/members", jwt, session.SessionToken));
        Assert.DoesNotContain(HireEmail, await members.Content.ReadAsStringAsync());

        // Once the shop has answered, the server keeps no hash.
        await _client.SendAsync(await HostRequestAsync(HttpMethod.Post, $"commands/{command.Id}/result",
            JsonContent.Create(new RemoteCommandResult(RemoteCommandStatuses.Applied))));
        var store = _factory.Services.GetRequiredService<RemoteCommandStore>();
        Assert.Null(store.Recent(GroupId).Single(c => c.Id == command.Id).PasswordHash);
        Assert.DoesNotContain("$2", await File.ReadAllTextAsync(
            Directory.GetFiles(Path.Combine(_dataDirectory, "remote-commands"), "*.json").Single()));
    }

    [Fact]
    public async Task A_member_request_the_shop_would_refuse_is_refused_at_once()
    {
        var (jwt, session) = await AdminSessionAsync();

        // Already a member; a new account with too short a password or none; no one named; the creator.
        Assert.Equal(HttpStatusCode.Conflict, (await QueueAsync(jwt, session.SessionToken, AddHire(identifier: StaffEmail))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, AddHire(password: "court"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await QueueAsync(jwt, session.SessionToken, AddHire(password: null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken, AddHire(identifier: " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await QueueAsync(jwt, session.SessionToken,
            new RemoteCommandRequest(RemoteCommandTypes.MemberRemove, _adminId))).StatusCode);

        var listed = (await (await _client.SendAsync(Remote(HttpMethod.Get, "/api/remote/commands", jwt, session.SessionToken)))
            .Content.ReadFromJsonAsync<RemoteCommandsResponse>())!;
        Assert.Empty(listed.Commands);
    }

    private static RemoteCommandDto MemberCommand(string requestedBy, string type, string userId = "",
        AddMemberRequest? member = null, string? passwordHash = null) =>
        new(Guid.NewGuid().ToString(), type, userId, null, null, null, null, null, requestedBy, null,
            DateTime.UtcNow, RemoteCommandStatuses.Pending, Member: member, PasswordHash: passwordHash);

    [Fact]
    public async Task The_host_adds_a_hire_who_can_then_sign_in_and_removes_a_member_when_asked()
    {
        await MakeThisTheShopsHostAsync();
        using var scope = _factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<RemoteCommandApplier>();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var added = await applier.ApplyAsync(GroupId, MemberCommand(_adminId, RemoteCommandTypes.MemberAdd,
            member: new AddMemberRequest(HireEmail, FirstName: "Awa"), passwordHash: BCrypt.Net.BCrypt.HashPassword(HirePassword)),
            CancellationToken.None);

        Assert.Equal(RemoteCommandStatuses.Applied, added.Status);
        var hire = db.Users.AsNoTracking().Single(u => u.Email == HireEmail);
        Assert.True(db.GroupMembers.AsNoTracking().Any(m => m.IdGroupe == GroupId && m.IdUser == hire.IdUser));
        Assert.Equal(GroupRoles.Member, db.UserRoles.AsNoTracking().Single(r => r.UserId == hire.IdUser && r.GroupId == GroupId).Role);
        await LoginAsync(HireEmail, HirePassword);

        var removed = await applier.ApplyAsync(GroupId, MemberCommand(_adminId, RemoteCommandTypes.MemberRemove, userId: _staffId),
            CancellationToken.None);

        Assert.Equal(RemoteCommandStatuses.Applied, removed.Status);
        Assert.False(db.GroupMembers.AsNoTracking().Any(m => m.IdGroupe == GroupId && m.IdUser == _staffId));
    }

    [Fact]
    public async Task The_host_refuses_a_member_request_from_someone_who_is_no_longer_an_administrator()
    {
        await MakeThisTheShopsHostAsync();
        using var scope = _factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<RemoteCommandApplier>();

        var result = await applier.ApplyAsync(GroupId, MemberCommand(_staffId, RemoteCommandTypes.MemberAdd,
            member: new AddMemberRequest(HireEmail), passwordHash: BCrypt.Net.BCrypt.HashPassword(HirePassword)), CancellationToken.None);

        Assert.Equal(RemoteCommandStatuses.Failed, result.Status);
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        Assert.False(db.Users.AsNoTracking().Any(u => u.Email == HireEmail));
    }
}
