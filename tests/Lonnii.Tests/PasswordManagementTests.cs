using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Covers changing and resetting passwords.
///
/// The point that needs proving is not that the hash changes - it is that the change
/// ends the account's existing access. A reset that leaves an old token working for the
/// rest of the day would be no use for the case it exists to handle: someone who should
/// no longer be able to get in.
/// </summary>
public class PasswordManagementTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string AdminEmail = "patron@lonnii.test";
    private const string AdminPassword = "MotDePasse123";
    private const string MemberEmail = "vendeur@lonnii.test";
    private const string MemberPassword = "Comptoir2026";

    public Task InitializeAsync()
    {
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

    /// <summary>A signed-in caller, scoped to a group.</summary>
    private sealed record Session(string Token, string GroupSession, string UserId);

    /// <summary>Creates the owner account, a workspace, and returns a scoped session.</summary>
    private async Task<Session> SignUpOwnerAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(AdminEmail, AdminPassword, "patron"));

        return await SignInAsync(AdminEmail, AdminPassword, createGroup: true);
    }

    private async Task<Session> SignInAsync(string identifier, string password, bool createGroup = false)
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(identifier, password));
        loginResponse.EnsureSuccessStatusCode();

        var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/groupes");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var groupes = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<List<GroupeDto>>();

        string groupId;
        if (createGroup && groupes!.Count == 0)
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
            {
                Content = JsonContent.Create(new CreateGroupeRequest("Boutique")),
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
            var created = await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>();
            groupId = created!.Id;
        }
        else
        {
            groupId = groupes![0].Id;
        }

        using var session = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupId}/session");
        session.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = await (await _client.SendAsync(session)).Content.ReadFromJsonAsync<GroupSessionResponse>();

        return new Session(login.AccessToken, scoped!.SessionToken, login.User.IdUser);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, Session session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    /// <summary>Adds a member with a known password and returns their user id.</summary>
    private async Task<string> AddMemberAsync(Session owner)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/groupe/members", owner,
            new AddMemberRequest(MemberEmail, MemberPassword, "vendeur", "Ami"));

        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<UserDto>();
        return user!.IdUser;
    }

    // --- Admin resetting a member's password ---

    [Fact]
    public async Task Admin_CanResetAMembersPassword()
    {
        var owner = await SignUpOwnerAsync();
        var memberId = await AddMemberAsync(owner);

        var response = await SendAsync(
            HttpMethod.Post, $"/api/groupe/members/{memberId}/password", owner,
            new ResetMemberPasswordRequest("NouveauMotDePasse2026"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The old password stops working and the new one takes over.
        var oldPassword = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(MemberEmail, MemberPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);

        var newPassword = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(MemberEmail, "NouveauMotDePasse2026"));
        newPassword.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ResettingAPassword_EndsTheMembersExistingSession()
    {
        var owner = await SignUpOwnerAsync();
        var memberId = await AddMemberAsync(owner);

        // The member is signed in and working.
        var member = await SignInAsync(MemberEmail, MemberPassword);
        var before = await SendAsync(HttpMethod.Get, "/api/privileges/me", member);
        before.EnsureSuccessStatusCode();

        await SendAsync(HttpMethod.Post, $"/api/groupe/members/{memberId}/password", owner,
            new ResetMemberPasswordRequest("NouveauMotDePasse2026"));

        // The token they are holding stops working straight away.
        var after = await SendAsync(HttpMethod.Get, "/api/privileges/me", member);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task ResettingAPassword_RejectsOneThatIsTooShort()
    {
        var owner = await SignUpOwnerAsync();
        var memberId = await AddMemberAsync(owner);

        var response = await SendAsync(
            HttpMethod.Post, $"/api/groupe/members/{memberId}/password", owner,
            new ResetMemberPasswordRequest("court"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AMember_CannotResetAnyonesPassword()
    {
        var owner = await SignUpOwnerAsync();
        var memberId = await AddMemberAsync(owner);
        var member = await SignInAsync(MemberEmail, MemberPassword);

        var response = await SendAsync(
            HttpMethod.Post, $"/api/groupe/members/{memberId}/password", member,
            new ResetMemberPasswordRequest("NouveauMotDePasse2026"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAdmin_CannotResetTheGroupCreatorsPassword()
    {
        var owner = await SignUpOwnerAsync();
        var memberId = await AddMemberAsync(owner);

        // Promote the member to sub_admin.
        await SendAsync(HttpMethod.Post, "/api/privileges/role", owner,
            new SetRoleRequest(memberId, "sub_admin"));

        var subAdmin = await SignInAsync(MemberEmail, MemberPassword);

        // Taking over the owner's account is the escalation this guard exists to stop.
        var response = await SendAsync(
            HttpMethod.Post, $"/api/groupe/members/{owner.UserId}/password", subAdmin,
            new ResetMemberPasswordRequest("JeSuisLePatron2026"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The owner's password is untouched.
        var stillWorks = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(AdminEmail, AdminPassword));
        stillWorks.EnsureSuccessStatusCode();
    }

    // --- Changing your own password ---

    [Fact]
    public async Task AUser_CanChangeTheirOwnPassword()
    {
        var owner = await SignUpOwnerAsync();
        await AddMemberAsync(owner);
        var member = await SignInAsync(MemberEmail, MemberPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password")
        {
            Content = JsonContent.Create(new ChangePasswordRequest(MemberPassword, "MonNouveauMotDePasse")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", member.Token);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // A fresh token comes back, and it works despite the change that just happened.
        var refreshed = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(refreshed!.AccessToken));

        var signIn = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(MemberEmail, "MonNouveauMotDePasse"));
        signIn.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ChangingYourPassword_RequiresTheCurrentOne()
    {
        var owner = await SignUpOwnerAsync();
        await AddMemberAsync(owner);
        var member = await SignInAsync(MemberEmail, MemberPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password")
        {
            Content = JsonContent.Create(new ChangePasswordRequest("PasLeBon", "MonNouveauMotDePasse")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", member.Token);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The old password still works, so nothing was changed.
        var signIn = await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(MemberEmail, MemberPassword));
        signIn.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ANewlyIssuedToken_SurvivesTheChangeThatProducedIt()
    {
        // The iat claim has whole-second resolution, so password_changed_at is truncated
        // to seconds. Without that, a token issued in the same second as the change would
        // look older than it and reject itself.
        var owner = await SignUpOwnerAsync();
        await AddMemberAsync(owner);
        var member = await SignInAsync(MemberEmail, MemberPassword);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password")
        {
            Content = JsonContent.Create(new ChangePasswordRequest(MemberPassword, "MonNouveauMotDePasse")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", member.Token);

        var refreshed = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<LoginResponse>();

        // Re-enter the group with the new token and confirm it is accepted.
        using var session = new HttpRequestMessage(HttpMethod.Get, "/api/groupes");
        session.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed!.AccessToken);
        var groupes = await (await _client.SendAsync(session)).Content.ReadFromJsonAsync<List<GroupeDto>>();

        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupes![0].Id}/session");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        var scoped = await _client.SendAsync(open);

        scoped.EnsureSuccessStatusCode();
    }
}
