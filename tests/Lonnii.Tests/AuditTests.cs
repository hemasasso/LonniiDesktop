using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Api.Endpoints;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// The attendance arithmetic (<see cref="AttendanceRules"/>) on fixed clocks, so the
/// thresholds are pinned without depending on when the suite runs.
/// </summary>
public class AttendanceRulesTests
{
    private static readonly DateTime Start = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_recent_heartbeat_is_online_even_on_a_day_off()
    {
        var status = AttendanceRules.Status(Start.AddHours(2), Start.AddHours(2).AddMinutes(-1),
            hasOpenStretch: true, firstLoginUtc: Start, scheduleType: ProgrammeEntryTypes.Repos, plannedStartUtc: null);

        Assert.Equal(AttendanceStatuses.EnLigne, status);
    }

    [Fact]
    public void A_heartbeat_gone_quiet_reads_inactive_then_offline()
    {
        var now = Start.AddHours(3);
        Assert.Equal(AttendanceStatuses.Inactif,
            AttendanceRules.Status(now, now.AddMinutes(-5), true, Start, null, null));
        Assert.Equal(AttendanceStatuses.HorsLigne,
            AttendanceRules.Status(now, now.AddMinutes(-15), true, Start, null, null));
    }

    [Fact]
    public void Arriving_within_five_minutes_is_on_time_and_after_is_late()
    {
        Assert.Null(AttendanceRules.RetardMinutes(Start.AddMinutes(5), Start));
        Assert.Equal(12, AttendanceRules.RetardMinutes(Start.AddMinutes(12), Start));

        Assert.Equal(AttendanceStatuses.Retard,
            AttendanceRules.Status(Start.AddHours(4), null, false, Start.AddMinutes(12), ProgrammeEntryTypes.Travail, Start));
    }

    [Fact]
    public void No_connection_is_absent_only_thirty_minutes_after_the_planned_start()
    {
        Assert.Equal(AttendanceStatuses.HorsLigne,
            AttendanceRules.Status(Start.AddMinutes(29), null, false, null, ProgrammeEntryTypes.Travail, Start));
        Assert.Equal(AttendanceStatuses.Absent,
            AttendanceRules.Status(Start.AddMinutes(31), null, false, null, ProgrammeEntryTypes.Travail, Start));
    }

    [Fact]
    public void Planned_minutes_skip_days_off_and_wrap_past_midnight()
    {
        var day = new ProgrammeEntry { Type = ProgrammeEntryTypes.Travail, HeureDebut = new(8, 0, 0), HeureFin = new(17, 30, 0) };
        var night = new ProgrammeEntry { Type = ProgrammeEntryTypes.Travail, HeureDebut = new(22, 0, 0), HeureFin = new(6, 0, 0) };
        var off = new ProgrammeEntry { Type = ProgrammeEntryTypes.Repos, HeureDebut = new(8, 0, 0), HeureFin = new(17, 0, 0) };

        Assert.Equal(570, AttendanceRules.PlannedMinutes(day));
        Assert.Equal(480, AttendanceRules.PlannedMinutes(night));
        Assert.Equal(0, AttendanceRules.PlannedMinutes(off));
    }

    [Fact]
    public void An_open_stretch_counts_only_up_to_its_last_heartbeat()
    {
        var row = new MemberWorkLog { LoginAt = Start, LastSeenAt = Start.AddMinutes(90) };
        Assert.Equal(90, AttendanceRules.Minutes(row));

        AttendanceRules.Close(row, Start.AddMinutes(95));
        Assert.Equal(95, row.DurationMinutes);
        Assert.Equal(95, AttendanceRules.Minutes(row));
    }

    [Fact]
    public void Productivity_is_capped_at_one_hundred_and_absent_without_a_plan()
    {
        Assert.Equal(100, AttendanceRules.Productivity(600, 480));
        Assert.Equal(50, AttendanceRules.Productivity(240, 480));
        Assert.Null(AttendanceRules.Productivity(240, 0));
    }

    [Fact]
    public void The_week_starts_on_monday()
    {
        Assert.Equal(new DateTime(2026, 9, 28), AttendanceRules.WeekStart(new DateTime(2026, 10, 4))); // Sunday
        Assert.Equal(new DateTime(2026, 9, 28), AttendanceRules.WeekStart(new DateTime(2026, 9, 28))); // Monday
    }
}

/// <summary>
/// Covers <c>/api/audit</c>, <c>/api/parametres/consommation</c> and the currency position:
/// a member's heartbeat shows on the admin's board and closes on sign-out, a plain member can
/// report presence but not read anyone's, and the currency position round-trips.
/// </summary>
public class AuditTests : IAsyncLifetime
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

    private async Task<AttendanceMemberDto> AttendanceOfAsync(Session admin, string userId)
    {
        var response = await SendAsync(admin, HttpMethod.Get, "/api/audit/attendance?offset=0");
        response.EnsureSuccessStatusCode();
        var board = (await response.Content.ReadFromJsonAsync<AttendanceResponse>())!;
        return board.Members.Single(m => m.UserId == userId);
    }

    [Fact]
    public async Task A_member_heartbeat_shows_them_online_and_signing_out_takes_them_offline()
    {
        var owner = await SignUpOwnerAsync();
        var member = await AddMemberAsync(owner);

        (await SendAsync(member, HttpMethod.Post, "/api/audit/presence",
            new PresenceHeartbeatRequest("Ventes", "CAISSE-1", 0))).EnsureSuccessStatusCode();

        var online = await AttendanceOfAsync(owner, member.UserId);
        Assert.Equal(AttendanceStatuses.EnLigne, online.Status);
        Assert.Equal("Ventes", online.CurrentModule);
        Assert.Equal("CAISSE-1", online.DeviceName);
        Assert.NotNull(online.FirstLoginAt);

        // A second beat extends the same stretch rather than opening another.
        (await SendAsync(member, HttpMethod.Post, "/api/audit/presence",
            new PresenceHeartbeatRequest("Stock", "CAISSE-1", 0))).EnsureSuccessStatusCode();
        var history = (await (await SendAsync(owner, HttpMethod.Get,
            $"/api/audit/attendance/{member.UserId}/history?offset=0")).Content.ReadFromJsonAsync<MemberWorkHistoryResponse>())!;
        Assert.Single(Assert.Single(history.Days).Sessions);

        (await SendAsync(member, HttpMethod.Post, "/api/audit/presence/end")).EnsureSuccessStatusCode();

        var offline = await AttendanceOfAsync(owner, member.UserId);
        Assert.Equal(AttendanceStatuses.HorsLigne, offline.Status);
        Assert.Null(offline.CurrentModule);
    }

    [Fact]
    public async Task A_plain_member_can_report_presence_but_not_read_the_board()
    {
        var owner = await SignUpOwnerAsync();
        var member = await AddMemberAsync(owner);

        (await SendAsync(member, HttpMethod.Post, "/api/audit/presence",
            new PresenceHeartbeatRequest(null, null, 0))).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(member, HttpMethod.Get, "/api/audit/attendance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(member, HttpMethod.Get, $"/api/audit/attendance/{owner.UserId}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(member, HttpMethod.Get, "/api/parametres/consommation")).StatusCode);
    }

    [Fact]
    public async Task The_programme_plan_for_today_appears_on_the_board()
    {
        var owner = await SignUpOwnerAsync();
        var member = await AddMemberAsync(owner);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        (await SendAsync(owner, HttpMethod.Post, "/api/programme/entries",
            new SaveProgrammeEntryRequest(member.UserId, today, "travail", new TimeSpan(8, 0, 0), new TimeSpan(16, 0, 0))))
            .EnsureSuccessStatusCode();

        var row = await AttendanceOfAsync(owner, member.UserId);
        Assert.Equal("travail", row.ScheduleType);
        Assert.Equal(new TimeSpan(8, 0, 0), row.ScheduleStart);
        Assert.True(row.WeekMinutesScheduled >= 480);
    }

    [Fact]
    public async Task The_currency_position_round_trips()
    {
        var owner = await SignUpOwnerAsync();

        var response = await SendAsync(owner, HttpMethod.Put, "/api/groupe/currency", new UpdateCurrencyRequest("$", true));
        response.EnsureSuccessStatusCode();
        var groupe = (await response.Content.ReadFromJsonAsync<GroupeDto>())!;

        Assert.Equal("$", groupe.CurrencyLabel);
        Assert.True(groupe.CurrencyBefore);
    }

    [Fact]
    public async Task Data_consumption_counts_the_workspace_records()
    {
        var owner = await SignUpOwnerAsync();
        (await SendAsync(owner, HttpMethod.Post, "/api/audit/presence",
            new PresenceHeartbeatRequest(null, null, 0))).EnsureSuccessStatusCode();

        var response = await SendAsync(owner, HttpMethod.Get, "/api/parametres/consommation");
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<DataConsumptionResponse>())!;

        Assert.Equal(1, data.Rows.Single(r => r.Key == "work_log").Count);
        Assert.Equal(data.Rows.Sum(r => r.Count), data.TotalRecords);
        Assert.Equal(1, data.TotalMembers);
    }
}
