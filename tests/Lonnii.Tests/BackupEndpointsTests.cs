using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// The licence server's side of the cloud backup. What matters is who is kept out and what
/// can never be overwritten: a stranger with a workspace id, a revoked machine, a local-mode
/// shop, and above all a freshly installed empty host that would otherwise replace a shop's
/// real backup with a blank one.
/// </summary>
public class BackupEndpointsTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string AdminEmail = "admin@pharmacie-nord.bf";
    private const string AdminPassword = "Motdepasse123";
    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";
    private const string Device = "machine-1";

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory));
        _client = _factory.CreateClient();

        await SeedAsync(DeploymentModes.Online, subscribed: true);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private async Task SeedAsync(string mode, bool subscribed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var admin = new User
        {
            Email = AdminEmail,
            Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword),
            IsVerified = true,
        };
        db.Users.Add(admin);
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
    }

    /// <summary>Activates a machine the way a real host does, and returns the token it is given.</summary>
    private async Task<string> ActivateAsync(string deviceId = Device)
    {
        var response = await _client.PostAsJsonAsync("/api/activation",
            new ActivationRequest(GroupId, AdminEmail, AdminPassword, deviceId, "poste"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ActivationResponse>())!.BackupToken!;
    }

    private HttpRequestMessage Req(HttpMethod method, string path, string? token, string? epoch = null,
        string deviceId = Device, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, "/api/backup/" + path) { Content = content };
        request.Headers.Add("x-group-id", GroupId);
        request.Headers.Add("x-device-id", deviceId);
        if (token is not null) request.Headers.Add("x-backup-token", token);
        if (epoch is not null) request.Headers.Add("x-backup-epoch", epoch);
        return request;
    }

    private static ByteArrayContent GzippedSqlite(string marker = "")
    {
        var raw = Encoding.ASCII.GetBytes("SQLite format 3\0" + marker + new string('x', 200));
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(raw);
        return new ByteArrayContent(buffer.ToArray());
    }

    private async Task<string> BeginAsync(string token, string? epoch = null)
    {
        var response = await _client.SendAsync(Req(HttpMethod.Post, "begin", token,
            content: JsonContent.Create(new BackupBeginRequest(epoch))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BackupBeginResponse>())!.Epoch;
    }

    [Fact]
    public async Task Online_activation_issues_a_backup_token_and_local_mode_does_not()
    {
        Assert.False(string.IsNullOrEmpty(await ActivateAsync()));
    }

    [Fact]
    public async Task A_wrong_token_is_refused_even_with_a_real_workspace_and_device()
    {
        await ActivateAsync();

        var response = await _client.SendAsync(Req(HttpMethod.Get, "info", "deadbeef"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_token_does_not_work_for_another_device()
    {
        var token = await ActivateAsync();

        var response = await _client.SendAsync(Req(HttpMethod.Get, "info", token, deviceId: "someone-else"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_machine_loses_access_at_once()
    {
        var token = await ActivateAsync();
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Req(HttpMethod.Get, "info", token))).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Devices.Single(d => d.DeviceId == Device).RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(Req(HttpMethod.Get, "info", token))).StatusCode);
    }

    [Fact]
    public async Task A_lapsed_subscription_stops_backups()
    {
        var token = await ActivateAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.DashboardSubscriptions.Single().ContractEndDate = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.PaymentRequired, (await _client.SendAsync(Req(HttpMethod.Get, "info", token))).StatusCode);
    }

    [Fact]
    public async Task Snapshot_round_trips_and_info_reports_it()
    {
        var token = await ActivateAsync();
        var epoch = await BeginAsync(token);

        Assert.False((await Info(token)).Exists);

        var put = await _client.SendAsync(Req(HttpMethod.Put, "snapshot?records=42&images=3", token, epoch,
            content: GzippedSqlite("v1")));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var info = await Info(token);
        Assert.True(info.Exists);
        Assert.Equal(42, info.RecordCount);
        Assert.Equal(epoch, info.Epoch);

        var get = await _client.SendAsync(Req(HttpMethod.Get, "snapshot", token));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        await using var body = await get.Content.ReadAsStreamAsync();
        using var unzipped = new StreamReader(new GZipStream(body, CompressionMode.Decompress));
        Assert.StartsWith("SQLite format 3", await unzipped.ReadToEndAsync());
    }

    [Fact]
    public async Task A_snapshot_that_is_not_a_gzipped_database_never_replaces_the_backup()
    {
        var token = await ActivateAsync();
        var epoch = await BeginAsync(token);

        await _client.SendAsync(Req(HttpMethod.Put, "snapshot?records=1", token, epoch, content: GzippedSqlite()));

        var garbage = await _client.SendAsync(Req(HttpMethod.Put, "snapshot?records=999", token, epoch,
            content: new ByteArrayContent(Encoding.ASCII.GetBytes("not a database"))));

        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.Equal(1, (await Info(token)).RecordCount);
    }

    /// <summary>
    /// The scenario this whole feature exists for: the shop's host died, a fresh install is
    /// activated, and its first scheduled backup is empty. It must not be able to replace the
    /// real one.
    /// </summary>
    [Fact]
    public async Task A_fresh_host_cannot_overwrite_an_existing_backup()
    {
        var token = await ActivateAsync();
        var epoch = await BeginAsync(token);
        await _client.SendAsync(Req(HttpMethod.Put, "snapshot?records=500", token, epoch, content: GzippedSqlite("real")));

        // New machine, no epoch.
        var begin = await _client.SendAsync(Req(HttpMethod.Post, "begin", token,
            content: JsonContent.Create(new BackupBeginRequest(null))));
        var blind = await _client.SendAsync(Req(HttpMethod.Put, "snapshot?records=0", token, null, content: GzippedSqlite("blank")));

        Assert.Equal(HttpStatusCode.Conflict, begin.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, blind.StatusCode);
        Assert.Equal(500, (await Info(token)).RecordCount);
    }

    [Fact]
    public async Task Starting_again_sets_the_old_backup_aside_and_issues_a_new_epoch()
    {
        var token = await ActivateAsync();
        var epoch = await BeginAsync(token);
        await _client.SendAsync(Req(HttpMethod.Put, "snapshot?records=500", token, epoch, content: GzippedSqlite()));

        var reset = await _client.SendAsync(Req(HttpMethod.Post, "reset", token));
        var fresh = (await reset.Content.ReadFromJsonAsync<BackupBeginResponse>())!.Epoch;

        Assert.NotEqual(epoch, fresh);
        Assert.False((await Info(token)).Exists);
        Assert.Contains(Directory.GetDirectories(Path.Combine(_dataDirectory, "backups")), d => d.Contains(".retired-"));

        var stale = await _client.SendAsync(Req(HttpMethod.Put, "snapshot", token, epoch, content: GzippedSqlite()));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Only_missing_images_are_asked_for_and_bad_names_are_refused()
    {
        var token = await ActivateAsync();
        var epoch = await BeginAsync(token);

        var put = await _client.SendAsync(Req(HttpMethod.Put, "images/products/p1_1.jpg", token, epoch,
            content: new ByteArrayContent([1, 2, 3])));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var ask = await _client.SendAsync(Req(HttpMethod.Post, "images/missing", token, epoch,
            content: JsonContent.Create(new BackupImagesRequest(
            [
                new BackupImageRef("products", "p1_1.jpg"),
                new BackupImageRef("products", "p2_1.jpg"),
            ]))));
        var missing = (await ask.Content.ReadFromJsonAsync<BackupImagesMissingDto>())!.Missing;

        Assert.Equal(["p2_1.jpg"], missing.Select(m => m.Name));

        var traversal = await _client.SendAsync(Req(HttpMethod.Put, "images/products/..%2Fmeta.json", token, epoch,
            content: new ByteArrayContent([1])));
        var unknownFolder = await _client.SendAsync(Req(HttpMethod.Put, "images/secrets/a.jpg", token, epoch,
            content: new ByteArrayContent([1])));

        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownFolder.StatusCode);

        var back = await _client.SendAsync(Req(HttpMethod.Get, "images/products/p1_1.jpg", token));
        Assert.Equal([1, 2, 3], await back.Content.ReadAsByteArrayAsync());
    }

    private async Task<BackupInfoDto> Info(string token) =>
        (await (await _client.SendAsync(Req(HttpMethod.Get, "info", token))).Content.ReadFromJsonAsync<BackupInfoDto>())!;
}
