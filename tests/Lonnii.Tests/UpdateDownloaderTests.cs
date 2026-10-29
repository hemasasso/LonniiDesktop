using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Lonnii.Api.Features.Backup;
using Lonnii.Api.Features.Licensing;
using Lonnii.Api.Features.Updates;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lonnii.Tests;

/// <summary>
/// A shop's main computer fetching a new version from our server and offering it to its tills.
/// Two apps: one plays our server, the other the shop's computer, each with its own data - so the
/// file really travels from one updates folder to the other. Only an online, unblocked, paid-up
/// shop is given one.
/// </summary>
public class UpdateDownloaderTests : IAsyncLifetime
{
    private const string AdminEmail = "admin@boutique-maj.bf";
    private const string AdminPassword = "Motdepasse123";
    private const string GroupId = "3c1f8e2a-5b6d-4f70-9e81-a2b3c4d5e6f7";
    private const string Device = "poste-principal";
    private const string FileName = "LonniiBusiness-Setup-1.0.1.exe";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private WebApplicationFactory<Program> _server = null!;
    private WebApplicationFactory<Program> _host = null!;
    private string _serverData = null!, _hostData = null!;
    private readonly byte[] _installer = RandomNumberGenerator.GetBytes(4096);

    private string ServerUpdates => Path.Combine(_serverData, "updates");
    private string HostUpdates => Path.Combine(_hostData, "updates");

    public async Task InitializeAsync()
    {
        _serverData = NewDirectory();
        _hostData = NewDirectory();
        var publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        _server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _serverData);
            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton(new UpdatePaths(_serverData, publicKey))));
        });
        _host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _hostData);
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(new UpdatePaths(_hostData, publicKey)));
                services.AddHttpClient(CloudBackupClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _server.Server.CreateHandler());
            });
        });

        // Our server knows the shop, paid up, in online mode.
        using (var scope = _server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            var admin = new User { Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword), IsVerified = true };
            db.Users.Add(admin);
            db.Groupes.Add(new Groupe { Id = GroupId, Nom = "Boutique", IdUserAdmin = admin.IdUser, Mode = DeploymentModes.Online, MaxDevices = 3 });
            db.DashboardSubscriptions.Add(new DashboardSubscription
            {
                GroupId = GroupId, GroupName = "Boutique", AdminName = "Admin", Montant = 60_000m,
                Statut = StatutAbonnement.Active,
                ContractStartDate = DateTime.UtcNow.AddMonths(-1), ContractEndDate = DateTime.UtcNow.AddMonths(11),
            });
            await db.SaveChangesAsync();
        }

        var activation = await _server.CreateClient().PostAsJsonAsync("/api/activation",
            new ActivationRequest(GroupId, AdminEmail, AdminPassword, Device, "poste"));
        var token = (await activation.Content.ReadFromJsonAsync<ActivationResponse>())!.BackupToken;

        // The shop's computer holds the same espace and the backup token its activation gave it.
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            var admin = new User { Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword), IsVerified = true };
            db.Users.Add(admin);
            db.Groupes.Add(new Groupe
            {
                Id = GroupId, Nom = "Boutique", IdUserAdmin = admin.IdUser,
                Mode = DeploymentModes.Online, MaxDevices = 3, LicenceServerUrl = "http://localhost",
            });
            LicenceGuard.StoreBackupToken(db, GroupId, Device, token);
            await db.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _server.DisposeAsync();
        _key.Dispose();
        foreach (var dir in new[] { _serverData, _hostData })
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task PublishAsync()
    {
        Directory.CreateDirectory(ServerUpdates);
        var path = Path.Combine(ServerUpdates, FileName);
        await File.WriteAllBytesAsync(path, _installer);
        var manifest = UpdateManifest.Sign(
            _key.ExportPkcs8PrivateKey(), "1.0.1", FileName, _installer.Length,
            await UpdateManifest.HashFileAsync(path), DateTime.UtcNow, null);
        await File.WriteAllTextAsync(Path.Combine(ServerUpdates, "latest.json"),
            JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web));
    }

    private async Task<UpdateCheckResult> CheckAsync()
    {
        using var scope = _host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UpdateDownloader>().CheckAsync(CancellationToken.None);
    }

    private async Task OnServerAsync(Action<LonniiDbContext> change)
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        change(db);
        await db.SaveChangesAsync();
    }

    private async Task OnHostAsync(Action<LonniiDbContext> change)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        change(db);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_online_shop_downloads_the_version_and_offers_it_to_its_tills()
    {
        await PublishAsync();

        Assert.Equal(UpdateCheckResult.Downloaded, await CheckAsync());
        Assert.Equal(_installer, await File.ReadAllBytesAsync(Path.Combine(HostUpdates, FileName)));

        // A till asks the shop's computer, not our server.
        var till = _host.CreateClient();
        var offered = await till.GetFromJsonAsync<UpdateManifest>("/api/updates/latest");
        Assert.Equal("1.0.1", offered!.Version);
        Assert.Equal(_installer, await till.GetByteArrayAsync("/api/updates/files/" + FileName));

        Assert.Equal(UpdateCheckResult.UpToDate, await CheckAsync());
    }

    [Fact]
    public async Task Our_server_never_gives_an_update_to_a_stranger()
    {
        await PublishAsync();

        var response = await _server.CreateClient().GetAsync("/api/backup/update/latest");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_local_mode_shop_gets_nothing_and_its_tills_stop_being_offered_it()
    {
        await PublishAsync();
        Assert.Equal(UpdateCheckResult.Downloaded, await CheckAsync());

        await OnHostAsync(db => db.Groupes.Single().Mode = DeploymentModes.Local);

        Assert.Equal(UpdateCheckResult.NotEntitled, await CheckAsync());
        Assert.False(File.Exists(Path.Combine(HostUpdates, FileName)));
        Assert.Equal(HttpStatusCode.NoContent, (await _host.CreateClient().GetAsync("/api/updates/latest")).StatusCode);
    }

    [Fact]
    public async Task A_shop_our_server_has_in_local_mode_gets_nothing()
    {
        await PublishAsync();
        await OnServerAsync(db => db.Groupes.Single().Mode = DeploymentModes.Local);

        Assert.Equal(UpdateCheckResult.NotEntitled, await CheckAsync());
        Assert.False(File.Exists(Path.Combine(HostUpdates, FileName)));
    }

    [Fact]
    public async Task A_shop_whose_subscription_lapsed_gets_nothing()
    {
        await PublishAsync();
        await OnServerAsync(db => db.DashboardSubscriptions.Single().ContractEndDate = DateTime.UtcNow.AddDays(-1));

        Assert.Equal(UpdateCheckResult.NotEntitled, await CheckAsync());
    }

    [Fact]
    public async Task A_blocked_shop_gets_nothing()
    {
        await PublishAsync();
        await OnServerAsync(db => db.Groupes.Single().IsBlocked = true);

        Assert.Equal(UpdateCheckResult.NotEntitled, await CheckAsync());
    }

    [Fact]
    public async Task A_withdrawn_version_is_removed_from_the_shop()
    {
        await PublishAsync();
        Assert.Equal(UpdateCheckResult.Downloaded, await CheckAsync());

        File.Delete(Path.Combine(ServerUpdates, "latest.json"));

        Assert.Equal(UpdateCheckResult.NothingPublished, await CheckAsync());
        Assert.False(File.Exists(Path.Combine(HostUpdates, FileName)));
    }
}
