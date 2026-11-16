using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Api.Features.Infrastructure;
using Lonnii.Api.Features.Licensing;
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
/// Flips <see cref="ShippedHost.IsShipped"/>, which every in-process host reads: these tests must
/// never run beside the others.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ShippedHostCollection
{
    public const string Name = "Shipped host rules";
}

/// <summary>
/// The installer build (<see cref="ShippedHost"/>): what a shop can edit on its own computer -
/// lonnii.db, appsettings.json - no longer switches its licence off.
/// </summary>
[Collection(ShippedHostCollection.Name)]
public class ShippedHostTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private FakeLicenceServer _licences = null!;

    private const string GroupId = "5b0f4c1e-2d7a-4f8e-9b61-3c2a8e7d9f10";
    private const string AdminEmail = "admin@boutique-sud.bf";
    private const string AdminPassword = "Motdepasse123";
    private const string Device = "till-1";

    private static readonly System.Security.Cryptography.ECDsa TestKey =
        System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    private static readonly string TestPublicKey = Convert.ToBase64String(TestKey.ExportSubjectPublicKeyInfo());

    private static string Sign(LicenceClaims claims) => LicenceToken.Issue(TestKey.ExportPkcs8PrivateKey(), claims);

    private sealed class FakeLicenceServer : ILicenceServer
    {
        public List<string> Urls { get; } = [];

        public Task<ActivationResponse> ActivateAsync(string baseUrl, ActivationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LicenceRefreshResponse> RefreshAsync(string baseUrl, LicenceRefreshRequest request, CancellationToken ct)
        {
            Urls.Add(baseUrl);
            var now = DateTime.UtcNow;
            var deadline = now.AddDays(14);
            return Task.FromResult(new LicenceRefreshResponse(
                request.GroupId, "Boutique Sud", DeploymentModes.Online, 5, 1, "FCFA",
                true, "Active", now.AddDays(200), false, null, now, deadline, null,
                Sign(new LicenceClaims(request.GroupId, DeploymentModes.Online, deadline, now, [Device], false, null))));
        }
    }

    public async Task InitializeAsync()
    {
        ShippedHost.IsShipped = true;

        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        _licences = new FakeLicenceServer();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<ILicenceServer>();
                s.AddSingleton<ILicenceServer>(_licences);
                s.RemoveAll<LicenceGuard>();
                s.AddScoped(sp => new LicenceGuard(
                    sp.GetRequiredService<Lonnii.Api.Features.Remote.ControlDb>().Db, TestPublicKey));
            });
        });
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var admin = new User { Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword), IsVerified = true };
        db.Users.Add(admin);
        db.Groupes.Add(new Groupe
        {
            Id = GroupId,
            Nom = "Boutique Sud",
            IdUserAdmin = admin.IdUser,
            Mode = DeploymentModes.Online,
            LicenceServerUrl = ShippedHost.LicenceServerUrl,
        });
        db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = admin.IdUser });
        db.UserRoles.Add(new UserRole { UserId = admin.IdUser, GroupId = GroupId, Role = GroupRoles.Admin, AssignedBy = admin.IdUser });
        db.Devices.Add(new Device { GroupId = GroupId, DeviceId = Device, DeviceName = "Caisse 1" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        ShippedHost.IsShipped = ShippedHost.CompiledAsShipped;
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    // --- Plumbing ---

    private async Task Mutate(Action<LonniiDbContext, Groupe> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        change(db, await db.Groupes.FirstAsync(g => g.Id == GroupId));
        await db.SaveChangesAsync();
    }

    private async Task StoreSigned(DateTime deadline, DateTime issuedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        db.HostLicences.Add(new HostLicence
        {
            GroupId = GroupId,
            Token = Sign(new LicenceClaims(GroupId, DeploymentModes.Online, deadline, issuedAt, [Device], false, null)),
        });
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> OpenSessionAsync(string device = Device)
    {
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{GroupId}/session") { Content = JsonContent.Create(new { }) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Add("x-device-id", device);
        return await _client.SendAsync(message);
    }

    // --- lonnii.db ---

    [Fact]
    public async Task Emptying_licence_server_url_does_not_lift_an_expired_licence()
    {
        await StoreSigned(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-15));
        await Mutate((_, g) => g.LicenceServerUrl = null);

        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task Emptying_licence_server_url_and_the_licence_still_leaves_the_unsigned_window()
    {
        await Mutate((_, g) => g.LicenceServerUrl = "");

        var status = await _client.GetFromJsonAsync<LicenceStatusDto>($"/api/licence/status/{GroupId}");

        Assert.True(status!.Enforced);
        Assert.InRange((status.Deadline!.Value - DateTime.UtcNow).TotalDays, 13.9, 14.1);
    }

    [Fact]
    public async Task Emptying_licence_server_url_and_the_device_rows_still_refuses_an_unbound_computer()
    {
        await StoreSigned(DateTime.UtcNow.AddDays(10), DateTime.UtcNow);
        await Mutate((db, g) =>
        {
            g.LicenceServerUrl = null;
            db.Devices.RemoveRange(db.Devices);
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await OpenSessionAsync("copied-pc")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task Marking_the_espace_phone_only_does_not_skip_the_machine_check()
    {
        await StoreSigned(DateTime.UtcNow.AddDays(10), DateTime.UtcNow);
        await Mutate((_, g) => g.PhoneOnly = true);

        Assert.Equal(HttpStatusCode.Forbidden, (await OpenSessionAsync("copied-pc")).StatusCode);
    }

    [Fact]
    public async Task A_check_in_goes_to_our_server_and_puts_back_an_emptied_or_changed_address()
    {
        await Mutate((_, g) => g.LicenceServerUrl = "https://licences.example.invalid");

        var message = new HttpRequestMessage(HttpMethod.Post, "/api/licence/sync") { Content = JsonContent.Create(new LicenceSyncRequest(GroupId)) };
        message.Headers.Add("x-device-id", Device);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(message)).StatusCode);

        Assert.Equal([ShippedHost.LicenceServerUrl], _licences.Urls);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        Assert.Equal(ShippedHost.LicenceServerUrl, (await db.Groupes.AsNoTracking().FirstAsync(g => g.Id == GroupId)).LicenceServerUrl);
    }

    // --- appsettings.json ---

    [Fact]
    public async Task Manual_setup_stays_off_whatever_the_configuration_says()
    {
        // TestEnvironment turns Lonnii:AllowManualSetup on for the whole run, as an edited
        // appsettings.json would.
        var state = await _client.GetFromJsonAsync<SetupStateResponse>("/api/auth/setup-state");

        Assert.False(state!.ManualSetupAllowed);
    }

    [Fact]
    public void A_shipped_host_refuses_to_start_on_postgresql()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.UseSetting("Lonnii:Database:Provider", "postgres");
            builder.UseSetting("Lonnii:Database:ConnectionString", "Host=localhost;Database=lonnii;Username=x;Password=y");
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("base locale", error.ToString());
    }
}
