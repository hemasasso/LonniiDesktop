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
/// A shop that has only ever lived on our server - created online, members added from afar - has
/// no backup a host uploaded. A computer being set up for it must still find one to restore: the
/// server builds it from its live data, and stops doing so once a real host uploads its own.
/// </summary>
public class LiveSnapshotTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string AdminEmail = "owner@boutique-en-ligne.bf";
    private const string AdminPassword = "Motdepasse123";
    private const string GroupId = "5b0e3f3c-6a43-4c55-9d0f-0c7e4a2b9f11";
    private const string Device = "nouveau-poste";

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            // On by default only on PostgreSQL; the tests stand in for the server on SQLite.
            builder.UseSetting("Lonnii:Backup:BuildFromLiveData", "true");
        });
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var owner = new User { Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword), IsVerified = true };
        var cashier = new User { Email = "caisse@boutique-en-ligne.bf", Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword), IsVerified = true };
        db.Users.AddRange(owner, cashier);
        db.Groupes.Add(new Groupe { Id = GroupId, Nom = "Boutique en ligne", IdUserAdmin = owner.IdUser, Mode = DeploymentModes.Online, MaxDevices = 3 });
        db.GroupMembers.AddRange(
            new GroupMember { IdGroupe = GroupId, IdUser = owner.IdUser },
            new GroupMember { IdGroupe = GroupId, IdUser = cashier.IdUser });
        db.UserRoles.Add(new UserRole { UserId = owner.IdUser, GroupId = GroupId, Role = GroupRoles.Admin });
        db.Products.Add(new Product { GroupId = GroupId, Name = "Savon", Quantity = 12 });
        db.DashboardSubscriptions.Add(new DashboardSubscription
        {
            GroupId = GroupId,
            GroupName = "Boutique en ligne",
            AdminName = "Owner",
            Montant = 60_000m,
            Statut = StatutAbonnement.Active,
            ContractStartDate = DateTime.UtcNow.AddMonths(-1),
            ContractEndDate = DateTime.UtcNow.AddMonths(11),
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private async Task<string> ActivateAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/activation",
            new ActivationRequest(GroupId, AdminEmail, AdminPassword, Device, "poste"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ActivationResponse>())!.BackupToken!;
    }

    private HttpRequestMessage Req(HttpMethod method, string path, string token, string? epoch = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, "/api/backup/" + path) { Content = content };
        request.Headers.Add("x-group-id", GroupId);
        request.Headers.Add("x-device-id", Device);
        request.Headers.Add("x-backup-token", token);
        if (epoch is not null) request.Headers.Add("x-backup-epoch", epoch);
        return request;
    }

    private async Task<BackupInfoDto> InfoAsync(string token)
    {
        var response = await _client.SendAsync(Req(HttpMethod.Get, "info", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BackupInfoDto>())!;
    }

    [Fact]
    public async Task A_shop_with_no_host_gets_a_backup_built_from_live_data()
    {
        var token = await ActivateAsync();

        var info = await InfoAsync(token);

        Assert.True(info.Exists);
        Assert.True(info.RecordCount > 0);

        // And it is a real snapshot the restore can download: gzip of a SQLite database.
        var snapshot = await _client.SendAsync(Req(HttpMethod.Get, "snapshot", token, info.Epoch));
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        await using var gzip = new GZipStream(await snapshot.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        var header = new byte[16];
        await gzip.ReadExactlyAsync(header);
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(header));
    }

    [Fact]
    public async Task Once_a_host_uploads_its_own_backup_live_data_no_longer_replaces_it()
    {
        var token = await ActivateAsync();
        var epoch = (await InfoAsync(token)).Epoch!;

        // A host restored that copy, then uploads its own.
        var raw = Encoding.ASCII.GetBytes("SQLite format 3\0" + new string('x', 200));
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(raw);
        var upload = await _client.SendAsync(Req(HttpMethod.Put, "snapshot?records=7&images=0", token, epoch,
            new ByteArrayContent(buffer.ToArray())));
        Assert.True(upload.IsSuccessStatusCode, await upload.Content.ReadAsStringAsync());

        var after = await InfoAsync(token);

        Assert.Equal(7, after.RecordCount);
    }
}
