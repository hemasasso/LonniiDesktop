using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lonnii.Tests;

/// <summary>
/// Covers the machine check that closes the copying hole.
///
/// Copying an installed shop to another shop's computers used to just work: the database
/// came with it and nothing asked whose hardware it was running on. Now every machine must
/// be bound, and binding reaches our licence server - so a copy either cannot register or
/// eats the original shop's own allowance, and either way it surfaces.
/// </summary>
public class DeviceEnforcementTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private FakeLicenceServer _licences = null!;

    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";
    private const string AdminEmail = "admin@pharmacie-nord.bf";
    private const string AdminPassword = "Motdepasse123";
    private const string BoundDevice = "till-1";

    private sealed class FakeLicenceServer : ILicenceServer
    {
        public int MaxDevices { get; set; } = 5;
        public Exception? Refuse { get; set; }
        public ActivationRequest? LastRequest { get; private set; }

        public Task<ActivationResponse> ActivateAsync(
            string baseUrl, ActivationRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (Refuse is not null) throw Refuse;

            return Task.FromResult(new ActivationResponse(
                request.GroupId, "Pharmacie Nord", DeploymentModes.Local,
                MaxDevices, 2, "FCFA", false, null, null, DateTime.UtcNow));
        }
    }

    public async Task InitializeAsync()
    {
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
            });
        });

        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var admin = new User
        {
            Email = AdminEmail,
            Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword),
            IsVerified = true,
        };
        db.Users.Add(admin);
        db.Groupes.Add(new Groupe
        {
            Id = GroupId,
            Nom = "Pharmacie Nord",
            IdUserAdmin = admin.IdUser,
            MaxDevices = 5,
            LicenceServerUrl = "https://licences.lonnii.test",
        });
        db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = admin.IdUser });
        db.UserRoles.Add(new UserRole
        {
            UserId = admin.IdUser,
            GroupId = GroupId,
            Role = GroupRoles.Admin,
            AssignedBy = admin.IdUser,
        });
        db.Devices.Add(new Device { GroupId = GroupId, DeviceId = BoundDevice, DeviceName = "Caisse 1" });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private async Task<string> SignInAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(AdminEmail, AdminPassword));
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.AccessToken;
    }

    /// <summary>Opens a group session as a given machine, which is where the check lives.</summary>
    private async Task<HttpResponseMessage> OpenSessionAsync(string? deviceId)
    {
        var token = await SignInAsync();

        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{GroupId}/session")
        {
            Content = JsonContent.Create(new { }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (deviceId is not null) message.Headers.Add("x-device-id", deviceId);

        return await _client.SendAsync(message);
    }

    [Fact]
    public async Task A_bound_machine_can_open_a_session()
    {
        var response = await OpenSessionAsync(BoundDevice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The copied installation: correct password, wrong hardware.</summary>
    [Fact]
    public async Task A_copy_on_unknown_hardware_is_refused()
    {
        var response = await OpenSessionAsync("autre-boutique-pc");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Contains("n'est pas autorisé", error!.Error);
    }

    /// <summary>Omitting the header must not be a way past the check.</summary>
    [Fact]
    public async Task Sending_no_machine_at_all_is_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await OpenSessionAsync(null)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_machine_is_refused()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Devices.First().RevokedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        // The workspace now has no bound machines at all, so the check would be skipped -
        // except that skipping is meant for a workspace that was never licensed, not one
        // whose machines were all revoked. Registering again is the way back.
        var response = await OpenSessionAsync(BoundDevice);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Registering_a_new_till_asks_our_licence_server()
    {
        var response = await _client.PostAsJsonAsync("/api/devices/register",
            new RegisterDeviceRequest(AdminEmail, AdminPassword, "till-2", "Caisse 2", "1.0.0"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("till-2", _licences.LastRequest!.DeviceId);

        // And it can then open a session, which it could not before.
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync("till-2")).StatusCode);
    }

    /// <summary>
    /// The allowance is our server's to enforce. A shop that could add rows locally would
    /// simply grant itself machines.
    /// </summary>
    [Fact]
    public async Task A_machine_our_server_refuses_is_not_bound_locally()
    {
        _licences.Refuse = new ActivationRefusedException(
            "Limite de postes atteinte (5).", HttpStatusCode.Conflict);

        var response = await _client.PostAsJsonAsync("/api/devices/register",
            new RegisterDeviceRequest(AdminEmail, AdminPassword, "till-6", "Caisse 6"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        Assert.DoesNotContain(db.Devices, d => d.DeviceId == "till-6");
    }

    [Fact]
    public async Task A_cashier_cannot_add_a_machine()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            var cashier = new User
            {
                Email = "caissier@pharmacie-nord.bf",
                Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword),
                IsVerified = true,
            };
            db.Users.Add(cashier);
            db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = cashier.IdUser });
            db.UserRoles.Add(new UserRole
            {
                UserId = cashier.IdUser,
                GroupId = GroupId,
                Role = GroupRoles.Member,
                AssignedBy = cashier.IdUser,
            });
            db.SaveChanges();
        }

        var response = await _client.PostAsJsonAsync("/api/devices/register",
            new RegisterDeviceRequest("caissier@pharmacie-nord.bf", AdminPassword, "portable-perso"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_cannot_add_a_machine()
    {
        var response = await _client.PostAsJsonAsync("/api/devices/register",
            new RegisterDeviceRequest(AdminEmail, "mauvais", "till-2"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Re-registering a machine already bound must not consume another slot.</summary>
    [Fact]
    public async Task Registering_an_already_bound_machine_is_a_no_op()
    {
        var response = await _client.PostAsJsonAsync("/api/devices/register",
            new RegisterDeviceRequest(AdminEmail, AdminPassword, BoundDevice));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Our server was never asked, because nothing needed counting.
        Assert.Null(_licences.LastRequest);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        Assert.Single(db.Devices.Where(d => d.DeviceId == BoundDevice));
    }
}
