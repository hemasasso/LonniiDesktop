using System.Net;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Pins the rule that self-service registration closes once the host has an account.
///
/// This one is worth an end-to-end test rather than a unit test: it is the difference
/// between "only the owner sets this up" and "anyone on the office Wi-Fi can create
/// themselves an account", and it lives in the endpoint rather than in a service.
/// </summary>
public class RegistrationPolicyTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    public Task InitializeAsync()
    {
        // Each run gets its own empty database directory, so the tests start from a host
        // that has never been set up.
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory));

        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public async Task FreshHost_ReportsThatItNeedsSetup()
    {
        var state = await _client.GetFromJsonAsync<SetupStateResponse>("/api/auth/setup-state");

        Assert.NotNull(state);
        Assert.False(state.HasAnyAccount);
    }

    [Fact]
    public async Task FirstRegistration_IsAllowed_AndClosesRegistration()
    {
        var first = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("patron@lonnii.test", "MotDePasse123", "patron"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var state = await _client.GetFromJsonAsync<SetupStateResponse>("/api/auth/setup-state");
        Assert.True(state!.HasAnyAccount);

        // A second person on the network cannot register themselves.
        var second = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("intrus@lonnii.test", "MotDePasse123"));

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);

        var error = await second.Content.ReadFromJsonAsync<ApiError>();
        Assert.Contains("administrateur", error!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheFirstAccount_CanSignIn()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("patron@lonnii.test", "MotDePasse123", "patron"));

        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("patron@lonnii.test", "MotDePasse123"));

        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.Equal("patron@lonnii.test", login.User.Email);
    }

    [Fact]
    public async Task SignIn_WithTheWrongPassword_IsRejected()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("patron@lonnii.test", "MotDePasse123"));

        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("patron@lonnii.test", "MauvaisMotDePasse"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SignIn_ForAnUnknownAccount_GivesTheSameMessageAsAWrongPassword()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("patron@lonnii.test", "MotDePasse123"));

        var wrongPassword = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("patron@lonnii.test", "MauvaisMotDePasse"));
        var unknownUser = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("personne@lonnii.test", "MauvaisMotDePasse"));

        // Identical replies, so a failed sign-in does not reveal which accounts exist.
        Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);

        var a = await wrongPassword.Content.ReadFromJsonAsync<ApiError>();
        var b = await unknownUser.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(a!.Error, b!.Error);
    }

    [Fact]
    public async Task GroupScopedEndpoints_RefuseAnUnauthenticatedCaller()
    {
        var response = await _client.GetAsync("/api/privileges/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
