using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lonnii.Api.Features.Registration;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// "Mot de passe oublié" for a shop owner: the licence server emails a code, and only that code
/// changes the password - there and, through the host that relayed it, in the shop's database.
/// </summary>
public class PasswordResetTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private readonly FakeEmail _email = new();
    private readonly FakeLicenceServer _licences = new();

    private const string OwnerEmail = "owner@pharmacie-sud.bf";
    private const string OldPassword = "AncienMotDePasse1";
    private const string NewPassword = "NouveauMotDePasse1";

    private sealed class FakeEmail : IEmailSender
    {
        public bool IsConfigured => true;
        public List<(string To, string Body)> Sent { get; } = [];

        public Task SendAsync(string to, string subject, string body, CancellationToken ct)
        {
            Sent.Add((to, body));
            return Task.CompletedTask;
        }

        public string LastCode => Regex.Match(Sent[^1].Body, "class=\"code\">(\\d{6})<").Groups[1].Value;
    }

    private sealed class FakeLicenceServer : ILicenceServer
    {
        public List<PasswordResetVerifyRequest> Verified { get; } = [];

        public Task<ActivationResponse> ActivateAsync(string baseUrl, ActivationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PasswordResetStartResponse> PasswordResetStartAsync(
            string baseUrl, PasswordResetStartRequest request, CancellationToken ct) =>
            Task.FromResult(new PasswordResetStartResponse("req-1", DateTime.UtcNow.AddMinutes(15)));

        public Task<PasswordResetVerifyResponse> PasswordResetVerifyAsync(
            string baseUrl, PasswordResetVerifyRequest request, CancellationToken ct)
        {
            if (request.Code != "123456")
                throw new ActivationRefusedException("Code invalide ou expiré.", HttpStatusCode.BadRequest);

            Verified.Add(request);
            return Task.FromResult(new PasswordResetVerifyResponse(true));
        }
    }

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            // One host plays both parts: the licence server's endpoints are switched on, and the
            // host-side relay talks to the fake below rather than to them.
            builder.UseSetting("Lonnii:Registration:Enabled", "true");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Lonnii:LicenceServerUrl"] = "" }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IEmailSender>(_email);
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

    /// <summary>An owner, and a member who is not one, in a shop activated with Lonnii.</summary>
    private async Task SeedShopAsync()
    {
        await _client.GetAsync("/api/health");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var owner = new User { Email = OwnerEmail, Password = BCrypt.Net.BCrypt.HashPassword(OldPassword), IsVerified = true };
        var member = new User { Email = "caisse@pharmacie-sud.bf", Password = BCrypt.Net.BCrypt.HashPassword(OldPassword) };
        db.Users.AddRange(owner, member);
        await db.SaveChangesAsync();

        db.Groupes.Add(new Groupe
        {
            Nom = "Pharmacie Sud",
            IdUserAdmin = owner.IdUser,
            LicenceServerUrl = "https://licence.test",
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> PasswordHashOfAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        return (await db.Users.AsNoTracking().FirstAsync(u => u.Email == email)).Password!;
    }

    // --- Licence server ---

    private async Task<string> ServerStartAsync(string email = OwnerEmail)
    {
        var response = await _client.PostAsJsonAsync("/api/registration/password/start", new PasswordResetStartRequest(email));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PasswordResetStartResponse>())!.RequestId;
    }

    private Task<HttpResponseMessage> ServerVerify(string requestId, string code, string email = OwnerEmail) =>
        _client.PostAsJsonAsync("/api/registration/password/verify",
            new PasswordResetVerifyRequest(requestId, email, code, NewPassword));

    [Fact]
    public async Task The_emailed_code_changes_the_owners_password()
    {
        await SeedShopAsync();

        var id = await ServerStartAsync();
        var response = await ServerVerify(id, _email.LastCode);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OwnerEmail, _email.Sent[0].To);
        Assert.True(BCrypt.Net.BCrypt.Verify(NewPassword, await PasswordHashOfAsync(OwnerEmail)));
    }

    [Fact]
    public async Task An_address_that_owns_no_shop_gets_the_same_answer_and_no_email()
    {
        await SeedShopAsync();

        await ServerStartAsync("caisse@pharmacie-sud.bf");
        await ServerStartAsync("personne@ailleurs.bf");

        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task A_wrong_code_changes_nothing_and_the_code_dies_after_five_tries()
    {
        await SeedShopAsync();
        var id = await ServerStartAsync();
        var code = _email.LastCode;
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await ServerVerify(id, wrong)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await ServerVerify(id, code)).StatusCode);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, await PasswordHashOfAsync(OwnerEmail)));
    }

    [Fact]
    public async Task A_code_only_works_for_the_address_it_was_sent_to()
    {
        await SeedShopAsync();
        var id = await ServerStartAsync();

        var response = await ServerVerify(id, _email.LastCode, email: "caisse@pharmacie-sud.bf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, await PasswordHashOfAsync("caisse@pharmacie-sud.bf")));
    }

    // --- The shop's host ---

    [Fact]
    public async Task The_host_relays_to_the_licence_server_and_then_changes_its_own_copy()
    {
        await SeedShopAsync();

        var start = await _client.PostAsJsonAsync("/api/auth/forgot/start", new ForgotPasswordStartRequest(OwnerEmail));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal("ow***@pharmacie-sud.bf", (await start.Content.ReadFromJsonAsync<ForgotPasswordStartResponse>())!.MaskedEmail);

        var verify = await _client.PostAsJsonAsync("/api/auth/forgot/verify",
            new ForgotPasswordVerifyRequest("req-1", OwnerEmail, "123456", NewPassword));

        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
        Assert.Equal(OwnerEmail, Assert.Single(_licences.Verified).Email);
        Assert.True(BCrypt.Net.BCrypt.Verify(NewPassword, await PasswordHashOfAsync(OwnerEmail)));

        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(OwnerEmail, NewPassword));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task The_host_keeps_the_old_password_when_the_licence_server_refuses_the_code()
    {
        await SeedShopAsync();

        var verify = await _client.PostAsJsonAsync("/api/auth/forgot/verify",
            new ForgotPasswordVerifyRequest("req-1", OwnerEmail, "999999", NewPassword));

        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, await PasswordHashOfAsync(OwnerEmail)));
    }

    [Fact]
    public async Task A_member_who_is_not_the_owner_is_sent_to_their_administrator()
    {
        await SeedShopAsync();

        var start = await _client.PostAsJsonAsync("/api/auth/forgot/start", new ForgotPasswordStartRequest("caisse@pharmacie-sud.bf"));

        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Contains("administrateur", (await start.Content.ReadFromJsonAsync<ApiError>())!.Error);
    }

    // --- The shop's host, without the internet ---

    [Fact]
    public async Task The_offline_tag_is_shown_for_an_owner_even_without_a_licence_server()
    {
        await SeedShopAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            await db.Groupes.ExecuteUpdateAsync(g => g.SetProperty(x => x.LicenceServerUrl, (string?)null));
        }

        var response = await _client.PostAsJsonAsync("/api/auth/forgot/offline/tag", new ForgotPasswordStartRequest(OwnerEmail));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("^[0-9A-F]{4}(-[0-9A-F]{4}){3}$", (await response.Content.ReadFromJsonAsync<OfflineResetTagResponse>())!.AccountTag);
    }

    [Fact]
    public async Task The_offline_tag_is_not_given_for_a_member_who_is_not_the_owner()
    {
        await SeedShopAsync();

        var response = await _client.PostAsJsonAsync("/api/auth/forgot/offline/tag", new ForgotPasswordStartRequest("caisse@pharmacie-sud.bf"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_code_not_signed_by_Lonnii_changes_nothing()
    {
        await SeedShopAsync();
        var tag = (await (await _client.PostAsJsonAsync("/api/auth/forgot/offline/tag", new ForgotPasswordStartRequest(OwnerEmail)))
            .Content.ReadFromJsonAsync<OfflineResetTagResponse>())!.AccountTag;

        // Signed with a key of our own making: only Lonnii's private key is accepted.
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var forged = Lonnii.Shared.Security.ResetCode.Issue(key.ExportPkcs8PrivateKey(), tag, DateTimeOffset.UtcNow.AddHours(1));

        var response = await _client.PostAsJsonAsync("/api/auth/forgot/offline/apply",
            new OfflineResetApplyRequest(OwnerEmail, forged, NewPassword));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, await PasswordHashOfAsync(OwnerEmail)));
    }
}
