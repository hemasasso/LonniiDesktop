using System.Net;
using System.Net.Http.Json;
using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lonnii.Tests;

/// <summary>
/// Covers first launch: a credentials file plus its passphrase becoming a working
/// installation.
///
/// The licence server is faked, so these tests are about what the installation does with
/// its answer - in particular that a refusal leaves the database untouched. A half-built
/// workspace left behind by a failed attempt would block every later one, and the
/// shopkeeper's only recourse would be deleting a file they have never heard of.
/// </summary>
public class FirstLaunchTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private FakeLicenceServer _licences = null!;

    private const string Passphrase = "soleil-cafe-42";
    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";
    private const string AdminEmail = "admin@pharmacie-nord.bf";

    /// <summary>Stands in for our server, so the answer can be dictated per test.</summary>
    private sealed class FakeLicenceServer : ILicenceServer
    {
        public Func<ActivationRequest, ActivationResponse> Respond { get; set; } = r => new ActivationResponse(
            GroupId: r.GroupId,
            GroupName: "Pharmacie Nord",
            Mode: DeploymentModes.Local,
            MaxDevices: 5,
            DevicesUsed: 1,
            CurrencyLabel: "FCFA",
            SubscriptionRequired: false,
            SubscriptionStatus: null,
            SubscriptionExpiresAt: null,
            ActivatedAt: DateTime.UtcNow);

        public ActivationRequest? LastRequest { get; private set; }

        public Task<ActivationResponse> ActivateAsync(
            string baseUrl, ActivationRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(Respond(request));
        }
    }

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        _licences = new FakeLicenceServer();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILicenceServer>();
                services.AddSingleton<ILicenceServer>(_licences);
            });
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

    private static string FileFor(string mode = DeploymentModes.Local, int maxDevices = 5)
    {
        var credentials = new StoreCredentials(
            GroupId: GroupId,
            StoreName: "Pharmacie Nord",
            AdminEmail: AdminEmail,
            AdminPassword: "Motdepasse123",
            Mode: mode,
            MaxDevices: maxDevices,
            ServerUrl: "https://licences.lonnii.test",
            IssuedAt: DateTime.UtcNow);

        return Convert.ToBase64String(CredentialsFile.Protect(credentials, Passphrase));
    }

    private Task<HttpResponseMessage> ApplyAsync(
        string? file = null, string passphrase = Passphrase, string deviceId = "machine-1") =>
        _client.PostAsJsonAsync("/api/setup/apply",
            new ApplyCredentialsRequest(file ?? FileFor(), passphrase, deviceId, "poste-caisse", "1.0.0"));

    private int WorkspaceCount()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.Count();
    }

    [Fact]
    public async Task A_valid_file_creates_the_workspace_and_its_admin()
    {
        var response = await ApplyAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ApplyCredentialsResponse>();
        Assert.Equal(GroupId, body!.GroupId);
        Assert.Equal("Pharmacie Nord", body.GroupName);
        Assert.Equal(AdminEmail, body.AdminEmail);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var groupe = Assert.Single(db.Groupes);
        var admin = Assert.Single(db.Users);
        Assert.Equal(admin.IdUser, groupe.IdUserAdmin);
        Assert.Single(db.GroupMembers);
        Assert.Single(db.Devices);

        var role = Assert.Single(db.UserRoles);
        Assert.Equal(GroupRoles.Admin, role.Role);
    }

    /// <summary>The admin must be able to sign in straight afterwards, with no extra step.</summary>
    [Fact]
    public async Task The_admin_can_sign_in_immediately()
    {
        await ApplyAsync();

        var login = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(AdminEmail, "Motdepasse123"));

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task The_password_is_never_stored_in_clear()
    {
        await ApplyAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var admin = db.Users.Single();
        Assert.NotEqual("Motdepasse123", admin.Password);
        Assert.True(BCrypt.Net.BCrypt.Verify("Motdepasse123", admin.Password));
    }

    [Fact]
    public async Task A_wrong_passphrase_is_refused()
    {
        var response = await ApplyAsync(passphrase: "mauvaise-phrase");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, WorkspaceCount());
    }

    /// <summary>
    /// The reason the licence server is called before anything is written: a shop refused
    /// for an expired subscription must be able to pay and simply try again.
    /// </summary>
    [Fact]
    public async Task A_refused_activation_leaves_nothing_behind()
    {
        _licences.Respond = _ => throw new ActivationRefusedException(
            "Abonnement inactif ou expiré.", HttpStatusCode.PaymentRequired);

        var response = await ApplyAsync();

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal(0, WorkspaceCount());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        Assert.Empty(db.Users);
        Assert.Empty(db.Devices);
    }

    [Fact]
    public async Task And_the_shop_can_retry_once_the_problem_is_fixed()
    {
        var refused = _licences.Respond;
        _licences.Respond = _ => throw new ActivationRefusedException("Abonnement expiré.");
        Assert.Equal(HttpStatusCode.Forbidden, (await ApplyAsync()).StatusCode);

        // Subscription renewed on our side; nothing changes on the shop's.
        _licences.Respond = refused;
        var response = await ApplyAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, WorkspaceCount());
    }

    [Fact]
    public async Task An_unreachable_server_says_so_plainly()
    {
        _licences.Respond = _ => throw new ActivationRefusedException(
            "Impossible de joindre le serveur Lonnii. Connectez cet ordinateur à Internet.");

        var response = await ApplyAsync();
        var error = await response.Content.ReadFromJsonAsync<ApiError>();

        Assert.Contains("Internet", error!.Error);
        Assert.Equal(0, WorkspaceCount());
    }

    /// <summary>
    /// The server's answer wins over the file. The customer holds the file, and until it is
    /// signed, nothing stops them editing the machine limit in it before installing.
    /// </summary>
    [Fact]
    public async Task The_servers_settings_override_the_files()
    {
        _licences.Respond = r => new ActivationResponse(
            r.GroupId, "Pharmacie Nord", DeploymentModes.Local,
            MaxDevices: 3, DevicesUsed: 1, CurrencyLabel: "XOF",
            SubscriptionRequired: false, SubscriptionStatus: null,
            SubscriptionExpiresAt: null, ActivatedAt: DateTime.UtcNow);

        // The file claims 99 machines.
        await ApplyAsync(file: FileFor(maxDevices: 99));

        using var scope = _factory.Services.CreateScope();
        var groupe = scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.Single();

        Assert.Equal(3, groupe.MaxDevices);
        Assert.Equal("XOF", groupe.CurrencyLabel);
    }

    [Fact]
    public async Task Setting_up_twice_is_refused()
    {
        Assert.Equal(HttpStatusCode.OK, (await ApplyAsync()).StatusCode);

        var second = await ApplyAsync(deviceId: "machine-2");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, WorkspaceCount());
    }

    [Fact]
    public async Task A_file_that_is_not_base64_is_refused_without_crashing()
    {
        var response = await _client.PostAsJsonAsync("/api/setup/apply",
            new ApplyCredentialsRequest("pas du base64 !", Passphrase, "machine-1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, WorkspaceCount());
    }

    [Fact]
    public async Task The_device_fingerprint_is_passed_to_the_licence_server()
    {
        await ApplyAsync(deviceId: "empreinte-abc");

        Assert.Equal("empreinte-abc", _licences.LastRequest!.DeviceId);
        Assert.Equal(GroupId, _licences.LastRequest.GroupId);
    }
}
