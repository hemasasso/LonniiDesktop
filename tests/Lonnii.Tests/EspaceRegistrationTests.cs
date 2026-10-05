using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Api.Features.Registration;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// Adding another espace to a shop we have already approved, on the licence server: instant for
/// the owner of an approved shop, and for nobody else. The approval step must not be a door that
/// a second, unapproved registration can walk around.
/// </summary>
public class EspaceRegistrationServerTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string Email = "owner@pharmacie-sud.bf";
    private const string Password = "MotDePasse123";

    private sealed class NoEmail : IEmailSender
    {
        public bool IsConfigured => true;
        public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct) => Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.UseSetting("Lonnii:Registration:Enabled", "true");
            builder.ConfigureServices(services => services.AddSingleton<IEmailSender>(new NoEmail()));
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private async Task SeedOwnerAsync(string approval, bool blockedShop = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var owner = new User { Email = Email, Password = BCrypt.Net.BCrypt.HashPassword(Password), IsVerified = true };
        db.Users.Add(owner);
        db.Groupes.Add(new Groupe
        {
            Nom = "Pharmacie Sud",
            IdUserAdmin = owner.IdUser,
            ApprovalStatus = approval,
            IsBlocked = blockedShop,
        });
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> NewEspace(string password = Password, string name = "Succursale") =>
        _client.PostAsJsonAsync("/api/registration/espace",
            new RegistrationEspaceRequest(Email, password, name, "machine-1"));

    [Fact]
    public async Task The_owner_of_an_approved_shop_gets_a_new_espace_at_once()
    {
        await SeedOwnerAsync(ApprovalStatuses.Approved);

        var response = await NewEspace();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<RegistrationVerifyResponse>())!;
        Assert.Equal(ApprovalStatuses.Approved, result.ApprovalStatus);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var created = await db.Groupes.SingleAsync(g => g.Id == result.GroupId);
        Assert.Equal("Succursale", created.Nom);
        Assert.Equal(await db.Users.Select(u => u.IdUser).SingleAsync(), created.IdUserAdmin);
        Assert.Single(await db.UserRoles.Where(r => r.GroupId == created.Id && r.Role == GroupRoles.Admin).ToListAsync());
        Assert.Equal(2, await db.Groupes.CountAsync());
    }

    [Fact]
    public async Task An_owner_whose_shop_is_still_pending_cannot_get_one()
    {
        await SeedOwnerAsync(ApprovalStatuses.Pending);

        Assert.Equal(HttpStatusCode.Forbidden, (await NewEspace()).StatusCode);

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.CountAsync());
    }

    [Fact]
    public async Task A_blocked_or_declined_shop_cannot_get_one()
    {
        await SeedOwnerAsync(ApprovalStatuses.Approved, blockedShop: true);

        Assert.Equal(HttpStatusCode.Forbidden, (await NewEspace()).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        await SeedOwnerAsync(ApprovalStatuses.Approved);

        Assert.Equal(HttpStatusCode.Forbidden, (await NewEspace("not-the-password")).StatusCode);
    }

    [Fact]
    public async Task Guessing_passwords_here_is_rate_limited()
    {
        await SeedOwnerAsync(ApprovalStatuses.Approved);

        for (var i = 0; i < RegistrationLimiter.PerEmailPerHour; i++)
            Assert.Equal(HttpStatusCode.Forbidden, (await NewEspace("wrong-" + i)).StatusCode);

        // Even the right password is now turned away until the hour is up.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await NewEspace()).StatusCode);
    }

    [Fact]
    public async Task A_name_that_is_too_short_is_refused()
    {
        await SeedOwnerAsync(ApprovalStatuses.Approved);

        Assert.Equal(HttpStatusCode.BadRequest, (await NewEspace(name: "x")).StatusCode);
    }
}

/// <summary>
/// The shop's own host creating an espace: it registers with Lonnii first, and only then builds
/// the espace locally under the id Lonnii issued - bound to this machine, so it can be opened.
/// The licence server is faked here; its side is <see cref="EspaceRegistrationServerTests"/>.
/// </summary>
public class EspaceRegistrationHostTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private readonly FakeLicenceServer _licences = new();

    private const string OwnerEmail = "patron@lonnii.test";
    private const string OwnerPassword = "MotDePasse123";
    private const string IssuedGroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";
    private const string DeviceId = "machine-1";

    private sealed class FakeLicenceServer : ILicenceServer
    {
        public ActivationRefusedException? Refuse { get; set; }
        public RegistrationEspaceRequest? Registered { get; private set; }

        public Task<RegistrationVerifyResponse> RegisterEspaceAsync(
            string baseUrl, RegistrationEspaceRequest request, CancellationToken ct)
        {
            if (Refuse is not null) throw Refuse;
            Registered = request;
            return Task.FromResult(new RegistrationVerifyResponse(IssuedGroupId, ApprovalStatuses.Approved));
        }

        public Task<ActivationResponse> ActivateAsync(string baseUrl, ActivationRequest request, CancellationToken ct) =>
            Task.FromResult(new ActivationResponse(
                request.GroupId, request.GroupId == IssuedGroupId ? "Succursale" : "?", DeploymentModes.Local,
                MaxDevices: 4, DevicesUsed: 1, CurrencyLabel: "FCFA",
                SubscriptionRequired: false, SubscriptionStatus: null, SubscriptionExpiresAt: null,
                ActivatedAt: DateTime.UtcNow));
    }

    public Task InitializeAsync() => StartAsync(required: true);

    private Task StartAsync(bool required, bool manualSetup = true)
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);

            // Added last, so they win over the environment the test assembly sets for the others.
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Lonnii:LicenceServerUrl"] = "https://licence.test",
                    ["Lonnii:OnlineRegistration:Required"] = required.ToString(),
                    ["Lonnii:AllowManualSetup"] = manualSetup.ToString(),
                }));

            builder.ConfigureServices(services => services.AddSingleton<ILicenceServer>(_licences));
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
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
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(OwnerEmail, OwnerPassword, "patron"));
        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(OwnerEmail, OwnerPassword))).Content.ReadFromJsonAsync<LoginResponse>())!;
        return login.AccessToken;
    }

    private async Task<HttpResponseMessage> CreateEspaceAsync(string token, string? password = OwnerPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
        {
            Content = JsonContent.Create(new CreateGroupeRequest("Succursale", GestionAccess: true, Password: password)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("x-device-id", DeviceId);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task A_new_espace_is_registered_online_and_built_under_the_id_lonnii_issued()
    {
        var token = await SignInAsync();

        var response = await CreateEspaceAsync(token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(IssuedGroupId, (await response.Content.ReadFromJsonAsync<GroupeDto>())!.Id);

        // Lonnii was asked as the signed-in owner, with the password they typed.
        Assert.Equal(OwnerEmail, _licences.Registered!.Email);
        Assert.Equal(OwnerPassword, _licences.Registered.Password);
        Assert.Equal("Succursale", _licences.Registered.ShopName);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var groupe = await db.Groupes.SingleAsync();
        Assert.Equal("https://licence.test", groupe.LicenceServerUrl);
        Assert.Equal(4, groupe.MaxDevices);

        // Bound to this machine, so the espace can actually be opened afterwards.
        Assert.Single(await db.Devices.Where(d => d.GroupId == IssuedGroupId && d.DeviceId == DeviceId).ToListAsync());
    }

    [Fact]
    public async Task The_new_espace_can_be_opened_from_this_machine()
    {
        var token = await SignInAsync();
        await CreateEspaceAsync(token);

        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{IssuedGroupId}/session");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        open.Headers.Add("x-device-id", DeviceId);

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(open)).StatusCode);
    }

    [Fact]
    public async Task Without_the_password_nothing_is_created()
    {
        var token = await SignInAsync();

        var response = await CreateEspaceAsync(token, password: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_licences.Registered);

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.ToListAsync());
    }

    [Fact]
    public async Task When_lonnii_refuses_nothing_is_created_here()
    {
        var token = await SignInAsync();
        _licences.Refuse = new ActivationRefusedException(
            "Seul l'administrateur d'une boutique déjà approuvée peut créer un espace.", HttpStatusCode.Forbidden);

        var response = await CreateEspaceAsync(token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("approuvée", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.ToListAsync());
    }

    [Fact]
    public async Task With_the_switch_off_an_espace_is_still_created_locally()
    {
        await DisposeAsync();
        await StartAsync(required: false);
        var token = await SignInAsync();

        var response = await CreateEspaceAsync(token, password: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(_licences.Registered);
    }

    [Fact]
    public async Task A_shipped_host_does_not_create_the_first_account_by_hand()
    {
        await DisposeAsync();
        await StartAsync(required: true, manualSetup: false);

        var state = await _client.GetFromJsonAsync<SetupStateResponse>("/api/auth/setup-state");
        Assert.False(state!.ManualSetupAllowed);

        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(OwnerEmail, OwnerPassword, "patron"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Users.ToListAsync());
    }
}
