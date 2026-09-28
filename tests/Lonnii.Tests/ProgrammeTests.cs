using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Covers <c>GET/POST /api/programme</c>: that saving a worker's day is an upsert rather than
/// a growing pile of duplicates, that an announcement addressed to one worker never leaks onto
/// another's board while a group-wide one reaches everyone, and that an ordinary member -
/// granted <c>can_view_programme</c> by default but nothing else - can read the board but not
/// write to it.
/// </summary>
public class ProgrammeTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string OwnerEmail = "patron@lonnii.test";
    private const string OwnerPassword = "MotDePasse123";
    private const string MemberEmail = "employe@lonnii.test";
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

        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing over */ }
    }

    private sealed record Session(string Token, string GroupSession, string GroupId, string UserId);

    private async Task<Session> SignUpOwnerAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(OwnerEmail, OwnerPassword, "patron"));
        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(OwnerEmail, OwnerPassword))).Content.ReadFromJsonAsync<LoginResponse>())!;

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
        {
            Content = JsonContent.Create(new CreateGroupeRequest("Boutique")),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var groupe = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>())!;

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupe.Id}/session");
        sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = (await (await _client.SendAsync(sessionRequest)).Content.ReadFromJsonAsync<GroupSessionResponse>())!;

        return new Session(login.AccessToken, scoped.SessionToken, groupe.Id, login.User.IdUser);
    }

    private async Task<Session> AddMemberAsync(Session owner)
    {
        await SendAsync(owner, HttpMethod.Post, "/api/groupe/members",
            new AddMemberRequest(MemberEmail, MemberPassword, "employe"));

        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(MemberEmail, MemberPassword))).Content.ReadFromJsonAsync<LoginResponse>())!;

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{owner.GroupId}/session");
        sessionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = (await (await _client.SendAsync(sessionRequest)).Content.ReadFromJsonAsync<GroupSessionResponse>())!;

        return new Session(login.AccessToken, scoped.SessionToken, owner.GroupId, login.User.IdUser);
    }

    private async Task<HttpResponseMessage> SendAsync(Session session, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<ProgrammeResponse> GetProgrammeAsync(Session session, DateOnly start, DateOnly end, string? userId = null)
    {
        var url = $"/api/programme?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}"
            + (userId is null ? string.Empty : $"&userId={userId}");
        var response = await SendAsync(session, HttpMethod.Get, url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProgrammeResponse>())!;
    }

    [Fact]
    public async Task Saving_the_same_worker_and_date_twice_replaces_the_entry_instead_of_duplicating()
    {
        var owner = await SignUpOwnerAsync();
        var date = new DateOnly(2026, 10, 5);

        var first = await SendAsync(owner, HttpMethod.Post, "/api/programme/entries",
            new SaveProgrammeEntryRequest(owner.UserId, date, "travail", new TimeSpan(8, 0, 0), new TimeSpan(17, 0, 0)));
        first.EnsureSuccessStatusCode();

        var second = await SendAsync(owner, HttpMethod.Post, "/api/programme/entries",
            new SaveProgrammeEntryRequest(owner.UserId, date, "reunion", Note: "Réunion générale"));
        second.EnsureSuccessStatusCode();

        var board = await GetProgrammeAsync(owner, date, date, owner.UserId);

        var entry = Assert.Single(board.Entries);
        Assert.Equal("reunion", entry.Type);
        Assert.Equal("Réunion générale", entry.Note);
        Assert.Null(entry.HeureDebut);
    }

    [Fact]
    public async Task Group_wide_announcement_reaches_every_worker_while_a_targeted_one_stays_private()
    {
        var owner = await SignUpOwnerAsync();
        var member = await AddMemberAsync(owner);
        var date = new DateOnly(2026, 10, 10);

        var groupWide = await SendAsync(owner, HttpMethod.Post, "/api/programme/announcements",
            new SaveProgrammeAnnouncementRequest(null, "Fermeture exceptionnelle", "Fermé le 12 pour inventaire", date));
        groupWide.EnsureSuccessStatusCode();

        var targeted = await SendAsync(owner, HttpMethod.Post, "/api/programme/announcements",
            new SaveProgrammeAnnouncementRequest(owner.UserId, "Rappel", "N'oublie pas la clé du magasin", date));
        targeted.EnsureSuccessStatusCode();

        var ownerBoard = await GetProgrammeAsync(owner, date, date, owner.UserId);
        Assert.Equal(2, ownerBoard.Announcements.Count);

        var memberBoard = await GetProgrammeAsync(member, date, date, member.UserId);
        var onlyAnnouncement = Assert.Single(memberBoard.Announcements);
        Assert.Equal("Fermeture exceptionnelle", onlyAnnouncement.Titre);
    }

    [Fact]
    public async Task An_ordinary_member_can_view_the_board_but_not_write_to_it()
    {
        var owner = await SignUpOwnerAsync();
        var member = await AddMemberAsync(owner);
        var date = new DateOnly(2026, 10, 12);

        var view = await SendAsync(member, HttpMethod.Get, $"/api/programme?start={date:yyyy-MM-dd}&end={date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);

        var write = await SendAsync(member, HttpMethod.Post, "/api/programme/entries",
            new SaveProgrammeEntryRequest(member.UserId, date, "travail"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
