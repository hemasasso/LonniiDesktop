using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Api.Features.Auth;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// A phone was signed out whenever its 12-hour token ran out - noticed after server deploys, the first
/// request after a night. A token out of date (not forged, account still allowed) is now traded for a new
/// one. Also: the caisse history filters by the client's own day, not UTC's.
/// </summary>
public class SessionRenewalTests : IAsyncLifetime
{
    private const string Secret = "a-test-secret-long-enough-for-hmac-sha256-0123456789";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

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
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private static User Someone => new() { IdUser = "u1", Email = "a@b.c" };

    private static TokenService Tokens(int lifetimeHours, string secret = Secret) =>
        new(new JwtOptions { Secret = secret, LifetimeHours = lifetimeHours });

    [Fact]
    public void An_expired_token_can_still_be_renewed_within_the_window()
    {
        var (token, _) = Tokens(-24).Issue(Someone);

        var read = Tokens(12).ReadForRefresh(token, DateTime.UtcNow);

        Assert.Equal("u1", read?.UserId);
    }

    [Fact]
    public void A_token_expired_beyond_the_window_or_signed_elsewhere_is_refused()
    {
        var (old, _) = Tokens(-24 * 31).Issue(Someone);
        var (foreign, _) = Tokens(12, Secret + "-other").Issue(Someone);

        Assert.Null(Tokens(12).ReadForRefresh(old, DateTime.UtcNow));
        Assert.Null(Tokens(12).ReadForRefresh(foreign, DateTime.UtcNow));
        Assert.Null(Tokens(12).ReadForRefresh("garbage", DateTime.UtcNow));
    }

    private async Task<(string Token, string GroupSession)> SignUpAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("patron@lonnii.test", "MotDePasse123", "patron"));
        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("patron@lonnii.test", "MotDePasse123"))).Content.ReadFromJsonAsync<LoginResponse>())!;

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes") { Content = JsonContent.Create(new CreateGroupeRequest("Boutique")) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var groupe = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>())!;

        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupe.Id}/session");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = (await (await _client.SendAsync(open)).Content.ReadFromJsonAsync<GroupSessionResponse>())!;
        return (login.AccessToken, scoped.SessionToken);
    }

    [Fact]
    public async Task Refresh_issues_a_new_token_and_refuses_a_bad_one()
    {
        var (token, _) = await SignUpAsync();

        using var good = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        good.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var ok = await _client.SendAsync(good);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ok.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken));

        using var bad = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        bad.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(bad)).StatusCode);
    }

    [Fact]
    public async Task Caisse_history_filters_by_the_clients_own_day()
    {
        var (token, groupSession) = await SignUpAsync();

        async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("x-group-session", groupSession);
            if (body is not null) request.Content = JsonContent.Create(body);
            var response = await _client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return response;
        }

        await Send(HttpMethod.Post, "/api/caisse/ouvrir", new OpenCaisseRequest(MontantInitialCash: 0m));

        // A client whose calendar day differs from UTC's right now.
        var now = DateTime.UtcNow;
        var offset = now.Hour >= 12 ? 14 * 60 : -12 * 60;
        var localDay = DateOnly.FromDateTime(now.AddMinutes(offset));
        Assert.NotEqual(DateOnly.FromDateTime(now), localDay);

        async Task<int> Count(string extra) =>
            (await (await Send(HttpMethod.Get, $"/api/caisse/historique?page=1&pageSize=50&dateDebut={localDay:yyyy-MM-dd}&dateFin={localDay:yyyy-MM-dd}{extra}"))
                .Content.ReadFromJsonAsync<CaisseHistoryResponse>())!.Total;

        Assert.Equal(1, await Count($"&tzOffsetMinutes={offset}"));
        Assert.Equal(0, await Count(string.Empty));
    }
}
