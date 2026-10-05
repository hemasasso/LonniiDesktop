using System.Net;
using System.Net.Http.Json;
using Lonnii.Api.Features.Registration;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// The first-launch "create my shop" path on the shop's own host: relay the registration to the
/// licence server, remember that the shop is waiting, and only build the local workspace once
/// the server has approved it. The licence server is faked - its own behaviour is covered by
/// <see cref="RegistrationTests"/>.
/// </summary>
public class HostRegistrationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private readonly FakeLicenceServer _licences = new();

    private const string Email = "owner@pharmacie-sud.bf";
    private const string Password = "MotDePasse123";
    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";

    private sealed class FakeLicenceServer : ILicenceServer
    {
        public bool Approved { get; set; }
        public ActivationRefusedException? StartFails { get; set; }
        public string? LastBaseUrl { get; private set; }

        public Task<RegistrationStartResponse> RegistrationStartAsync(
            string baseUrl, RegistrationStartRequest request, CancellationToken ct)
        {
            LastBaseUrl = baseUrl;
            if (StartFails is not null) throw StartFails;
            return Task.FromResult(new RegistrationStartResponse("req-1", DateTime.UtcNow.AddMinutes(15)));
        }

        public Task<RegistrationVerifyResponse> RegistrationVerifyAsync(
            string baseUrl, RegistrationVerifyRequest request, CancellationToken ct) =>
            request.Code == "123456"
                ? Task.FromResult(new RegistrationVerifyResponse(GroupId, ApprovalStatuses.Pending))
                : throw new ActivationRefusedException("Code invalide ou expiré.", HttpStatusCode.BadRequest);

        public Task<ActivationResponse> ActivateAsync(string baseUrl, ActivationRequest request, CancellationToken ct)
        {
            if (!Approved)
            {
                throw new ActivationRefusedException(
                    "Votre inscription est en attente d'approbation. Nous vous contacterons très bientôt.",
                    HttpStatusCode.Forbidden);
            }

            return Task.FromResult(new ActivationResponse(
                request.GroupId, "Pharmacie Sud", DeploymentModes.Local, 3, 1, "FCFA",
                SubscriptionRequired: false, SubscriptionStatus: null, SubscriptionExpiresAt: null,
                ActivatedAt: DateTime.UtcNow));
        }
    }

    public Task InitializeAsync() => StartAsync(serverUrl: "https://licence.test");

    private Task StartAsync(string serverUrl)
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            // In-memory configuration is added last, so it wins over the address now shipped in appsettings.json
            // - including an empty one, which is how "no licence server" is tested.
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Lonnii:LicenceServerUrl"] = serverUrl }));
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

    private Task<HttpResponseMessage> Start() =>
        _client.PostAsJsonAsync("/api/setup/register/start",
            new RegistrationStartRequest("Pharmacie Sud", Email, Password, "machine-1", "poste"));

    private Task<HttpResponseMessage> Verify(string code = "123456") =>
        _client.PostAsJsonAsync("/api/setup/register/verify",
            new SetupRegisterVerifyRequest("req-1", code, Email, "Pharmacie Sud"));

    private Task<HttpResponseMessage> Activate(string password = Password) =>
        _client.PostAsJsonAsync("/api/setup/register/activate",
            new SetupActivatePendingRequest(password, "machine-1", "poste"));

    private async Task<PendingRegistrationDto> PendingAsync() =>
        (await _client.GetFromJsonAsync<PendingRegistrationDto>("/api/setup/register/pending"))!;

    [Fact]
    public async Task Registration_goes_to_the_licence_server_named_in_configuration()
    {
        Assert.Equal(HttpStatusCode.OK, (await Start()).StatusCode);

        Assert.Equal("https://licence.test", _licences.LastBaseUrl);
    }

    [Fact]
    public async Task Without_a_licence_server_address_the_host_says_so()
    {
        await DisposeAsync();
        await StartAsync(serverUrl: "");

        var response = await Start();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("fichier d'identifiants", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task The_servers_refusal_reaches_the_client_with_its_own_status_and_words()
    {
        _licences.StartFails = new ActivationRefusedException("Trop de tentatives. Réessayez dans une heure.", HttpStatusCode.TooManyRequests);

        var response = await Start();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("Trop de tentatives", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task A_wrong_code_leaves_nothing_pending()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify("000000")).StatusCode);

        Assert.False((await PendingAsync()).Pending);
    }

    [Fact]
    public async Task A_confirmed_registration_is_remembered_but_builds_no_workspace()
    {
        Assert.Equal(HttpStatusCode.OK, (await Verify()).StatusCode);

        var pending = await PendingAsync();
        Assert.True(pending.Pending);
        Assert.Equal(Email, pending.Email);

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.ToListAsync());
    }

    [Fact]
    public async Task Activation_waits_for_approval_then_builds_the_workspace()
    {
        await Verify();

        var waiting = await Activate();
        Assert.Equal(HttpStatusCode.Forbidden, waiting.StatusCode);
        Assert.Contains("attente", (await waiting.Content.ReadFromJsonAsync<ApiError>())!.Error);
        Assert.True((await PendingAsync()).Pending);

        _licences.Approved = true;

        var activated = await Activate();
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var groupe = await db.Groupes.SingleAsync();
        Assert.Equal(GroupId, groupe.Id);
        Assert.Equal("https://licence.test", groupe.LicenceServerUrl);

        var admin = await db.Users.SingleAsync();
        Assert.Equal(Email, admin.Email);
        Assert.True(BCrypt.Net.BCrypt.Verify(Password, admin.Password));
        Assert.Equal(admin.IdUser, groupe.IdUserAdmin);

        Assert.False((await PendingAsync()).Pending);
    }

    [Fact]
    public async Task Activating_with_nothing_pending_is_refused()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Activate()).StatusCode);
    }

    [Fact]
    public async Task The_endpoints_close_once_a_workspace_exists()
    {
        await Verify();
        _licences.Approved = true;
        await Activate();

        Assert.Equal(HttpStatusCode.Conflict, (await Start()).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Verify()).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync("/api/setup/register/pending")).StatusCode);
    }

    [Fact]
    public async Task Cancelling_forgets_the_pending_registration()
    {
        await Verify();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/setup/register/pending")).StatusCode);
        Assert.False((await PendingAsync()).Pending);
    }
}
