using Lonnii.Api.Features.Registration;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// Self-registration on the licence server. What matters is the order of events: nothing
/// exists until the emailed code comes back, and what then exists cannot be used until we
/// approve it - otherwise the point (knowing, and agreeing to, everyone who uses the app) is lost.
/// </summary>
public class RegistrationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private readonly FakeEmail _email = new();

    private const string Email = "owner@pharmacie-sud.bf";
    private const string Password = "MotDePasse123";

    private sealed class FakeEmail : IEmailSender
    {
        public bool IsConfigured { get; set; } = true;
        public bool Fail { get; set; }
        public List<(string To, string Body)> Sent { get; } = [];

        public Task SendAsync(string to, string subject, string body, CancellationToken ct)
        {
            if (Fail) throw new EmailSendException("boom");
            Sent.Add((to, body));
            return Task.CompletedTask;
        }

        public string LastCode => Regex.Match(Sent[^1].Body, "class=\"code\">(\\d{6})<").Groups[1].Value;
    }

    public Task InitializeAsync() => StartAsync(enabled: true);

    private Task StartAsync(bool enabled)
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.UseSetting("Lonnii:Registration:Enabled", enabled ? "true" : "false");
            builder.ConfigureServices(services => services.AddSingleton<IEmailSender>(_email));
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

    private Task<HttpResponseMessage> Start(string email = Email, string password = Password) =>
        _client.PostAsJsonAsync("/api/registration/start",
            new RegistrationStartRequest("Pharmacie Sud", email, password, "machine-1", "poste"));

    private async Task<string> StartOkAsync()
    {
        var response = await Start();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RegistrationStartResponse>())!.RequestId;
    }

    private Task<HttpResponseMessage> Verify(string requestId, string code) =>
        _client.PostAsJsonAsync("/api/registration/verify", new RegistrationVerifyRequest(requestId, code));

    private async Task<RegistrationVerifyResponse> RegisterAsync()
    {
        var id = await StartOkAsync();
        var response = await Verify(id, _email.LastCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RegistrationVerifyResponse>())!;
    }

    private Task<HttpResponseMessage> Activate(string groupId) =>
        _client.PostAsJsonAsync("/api/activation",
            new ActivationRequest(groupId, Email, Password, "machine-1", "poste"));

    [Fact]
    public async Task Registration_is_not_there_unless_switched_on()
    {
        await DisposeAsync();
        await StartAsync(enabled: false);

        Assert.Equal(HttpStatusCode.NotFound, (await Start()).StatusCode);
    }

    [Fact]
    public async Task Nothing_exists_until_the_code_comes_back()
    {
        await StartOkAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.Groupes.ToListAsync());
        Assert.Single(_email.Sent);
        Assert.Equal(Email, _email.Sent[0].To);
    }

    [Fact]
    public async Task Verifying_creates_a_pending_shop_with_its_admin()
    {
        var result = await RegisterAsync();

        Assert.Equal(ApprovalStatuses.Pending, result.ApprovalStatus);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var groupe = await db.Groupes.SingleAsync();
        var admin = await db.Users.SingleAsync();
        Assert.Equal("Pharmacie Sud", groupe.Nom);
        Assert.Equal(admin.IdUser, groupe.IdUserAdmin);
        Assert.Equal(DeploymentModes.Local, groupe.Mode);
        Assert.True(BCrypt.Net.BCrypt.Verify(Password, admin.Password));
        Assert.Single(await db.UserRoles.Where(r => r.Role == GroupRoles.Admin).ToListAsync());
        Assert.Empty(await db.RegistrationRequests.ToListAsync());
    }

    [Fact]
    public async Task A_pending_shop_cannot_activate_until_it_is_approved()
    {
        var result = await RegisterAsync();

        var refused = await Activate(result.GroupId);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("attente", (await refused.Content.ReadFromJsonAsync<ApiError>())!.Error);

        await SetStatusAsync(result.GroupId, ApprovalStatuses.Approved);

        Assert.Equal(HttpStatusCode.OK, (await Activate(result.GroupId)).StatusCode);
    }

    [Fact]
    public async Task A_rejected_shop_is_told_so()
    {
        var result = await RegisterAsync();
        await SetStatusAsync(result.GroupId, ApprovalStatuses.Rejected);

        var refused = await Activate(result.GroupId);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("pas été acceptée", (await refused.Content.ReadFromJsonAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task A_wrong_password_learns_nothing_about_approval()
    {
        var result = await RegisterAsync();

        var response = await _client.PostAsJsonAsync("/api/activation",
            new ActivationRequest(result.GroupId, Email, "wrong-password", "machine-1"));

        Assert.DoesNotContain("attente", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_five_of_them_burn_the_request()
    {
        var id = await StartOkAsync();
        var real = _email.LastCode;
        var wrong = real == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await Verify(id, wrong)).StatusCode);

        // Even the right code no longer works: six digits are only safe because guesses are capped.
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(id, real)).StatusCode);

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.ToListAsync());
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var id = await StartOkAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            (await db.RegistrationRequests.SingleAsync()).CodeExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(id, _email.LastCode)).StatusCode);
    }

    [Fact]
    public async Task An_address_that_already_has_an_account_is_refused()
    {
        await RegisterAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await Start()).StatusCode);
    }

    [Fact]
    public async Task Too_many_requests_for_one_address_are_limited()
    {
        for (var i = 0; i < RegistrationLimiter.PerEmailPerHour; i++)
            Assert.Equal(HttpStatusCode.OK, (await Start()).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Start()).StatusCode);
    }

    [Fact]
    public async Task Without_email_configured_registration_says_so_instead_of_accepting()
    {
        _email.IsConfigured = false;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Start()).StatusCode);
    }

    [Fact]
    public async Task A_failed_email_leaves_no_registration_behind()
    {
        _email.Fail = true;

        Assert.Equal(HttpStatusCode.BadGateway, (await Start()).StatusCode);

        using var scope = _factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<LonniiDbContext>().RegistrationRequests.ToListAsync());
    }

    [Theory]
    [InlineData("pas-un-email", "MotDePasse123")]
    [InlineData("a@b.bf", "court")]
    public async Task Bad_input_is_refused_before_anything_is_stored(string email, string password)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(email, password)).StatusCode);
        Assert.Empty(_email.Sent);
    }

    private async Task SetStatusAsync(string groupId, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        (await db.Groupes.SingleAsync(g => g.Id == groupId)).ApprovalStatus = status;
        await db.SaveChangesAsync();
    }
}
