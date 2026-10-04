using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Api.Features.Licensing;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lonnii.Tests;

/// <summary>
/// Covers the offline deadline: day <c>max_offline_days</c> is a hard stop for an online-mode
/// workspace that answers to a licence server, and only for that.
///
/// The failure these guard against is the cheap online tier being used as if it were the
/// expensive offline one - internet cut, software kept for ever. The tests that matter most
/// are the two where the deadline must <em>not</em> move: a refused refresh, and a clock
/// wound back.
/// </summary>
public class LicenceEnforcementTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private FakeLicenceServer _licences = null!;

    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";
    private const string AdminEmail = "admin@pharmacie-nord.bf";
    private const string AdminPassword = "Motdepasse123";
    private const string Device = "till-1";

    private sealed class FakeLicenceServer : ILicenceServer
    {
        public Exception? Refuse { get; set; }
        public int MaxOfflineDays { get; set; } = 14;
        public int Calls { get; private set; }

        public Task<ActivationResponse> ActivateAsync(
            string baseUrl, ActivationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LicenceRefreshResponse> RefreshAsync(
            string baseUrl, LicenceRefreshRequest request, CancellationToken ct)
        {
            Calls++;
            if (Refuse is not null) throw Refuse;

            var now = DateTime.UtcNow;
            return Task.FromResult(new LicenceRefreshResponse(
                request.GroupId, "Pharmacie Nord", DeploymentModes.Online, 5, 1, "FCFA",
                true, "Active", now.AddDays(200), false, null, now, now.AddDays(MaxOfflineDays)));
        }
    }

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        _licences = new FakeLicenceServer();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<ILicenceServer>();
                s.AddSingleton<ILicenceServer>(_licences);
            });
        });

        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var admin = new User
        {
            Email = AdminEmail,
            Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword),
            IsVerified = true,
        };
        db.Users.Add(admin);
        db.Groupes.Add(new Groupe
        {
            Id = GroupId,
            Nom = "Pharmacie Nord",
            IdUserAdmin = admin.IdUser,
            Mode = DeploymentModes.Online,
            LicenceServerUrl = "https://licences.lonnii.test",
        });
        db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = admin.IdUser });
        db.UserRoles.Add(new UserRole
        {
            UserId = admin.IdUser,
            GroupId = GroupId,
            Role = GroupRoles.Admin,
            AssignedBy = admin.IdUser,
        });
        db.Devices.Add(new Device { GroupId = GroupId, DeviceId = Device, DeviceName = "Caisse 1" });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    // --- Plumbing ---

    private async Task Mutate(Action<Groupe> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var groupe = await db.Groupes.FirstAsync(g => g.Id == GroupId);
        change(groupe);
        await db.SaveChangesAsync();
    }

    private async Task<Groupe> ReadGroupe()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        return await db.Groupes.AsNoTracking().FirstAsync(g => g.Id == GroupId);
    }

    private async Task<HttpResponseMessage> OpenSessionAsync()
    {
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{GroupId}/session")
        {
            Content = JsonContent.Create(new { }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Add("x-device-id", Device);
        return await _client.SendAsync(message);
    }

    private async Task<HttpResponseMessage> SyncAsync()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/licence/sync")
        {
            Content = JsonContent.Create(new LicenceSyncRequest(GroupId)),
        };
        message.Headers.Add("x-device-id", Device);
        return await _client.SendAsync(message);
    }

    // --- The hard stop ---

    [Fact]
    public async Task Before_the_deadline_the_workspace_opens_and_counts_down()
    {
        await Mutate(g => g.LicenceDeadline = DateTime.UtcNow.AddDays(3));

        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);

        var status = await _client.GetFromJsonAsync<LicenceStatusDto>($"/api/licence/status/{GroupId}");
        Assert.True(status!.Enforced);
        Assert.False(status.Expired);
        Assert.Equal(3, status.DaysLeft);
    }

    [Fact]
    public async Task Past_the_deadline_the_workspace_is_locked()
    {
        await Mutate(g => g.LicenceDeadline = DateTime.UtcNow.AddMinutes(-1));

        var response = await OpenSessionAsync();

        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Contains("licence", error!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_workspace_with_no_deadline_yet_starts_a_full_period_instead_of_locking()
    {
        // An installation from before the deadline existed, or a fresh setup.
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);

        var groupe = await ReadGroupe();
        Assert.NotNull(groupe.LicenceDeadline);
        Assert.InRange((groupe.LicenceDeadline!.Value - DateTime.UtcNow).TotalDays, 13.9, 14.1);
    }

    [Fact]
    public async Task Winding_the_clock_back_does_not_buy_more_days()
    {
        // The installation has already seen a moment after the deadline; the computer's own
        // clock now says it is earlier. "Now" must stay at the later of the two.
        await Mutate(g =>
        {
            g.LicenceDeadline = DateTime.UtcNow.AddDays(-1);
            g.LicenceClockMark = DateTime.UtcNow.AddDays(2);
        });

        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task Local_mode_is_never_locked_whatever_the_dates()
    {
        await Mutate(g =>
        {
            g.Mode = DeploymentModes.Local;
            g.LicenceDeadline = DateTime.UtcNow.AddDays(-400);
        });

        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task The_server_itself_with_no_licence_server_url_is_never_locked()
    {
        await Mutate(g =>
        {
            g.LicenceServerUrl = null;
            g.LicenceDeadline = DateTime.UtcNow.AddDays(-400);
        });

        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
    }

    // --- Renewing ---

    [Fact]
    public async Task Reaching_the_server_lifts_the_lock_and_sets_a_fresh_deadline()
    {
        await Mutate(g => g.LicenceDeadline = DateTime.UtcNow.AddDays(-5));
        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);

        var sync = await SyncAsync();

        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var status = (await sync.Content.ReadFromJsonAsync<LicenceStatusDto>())!;
        Assert.False(status.Expired);
        Assert.Equal(14, status.DaysLeft);

        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task A_refused_refresh_leaves_the_lock_in_place()
    {
        await Mutate(g => g.LicenceDeadline = DateTime.UtcNow.AddDays(-5));
        _licences.Refuse = new ActivationRefusedException("Abonnement inactif ou expiré.", HttpStatusCode.PaymentRequired);

        var sync = await SyncAsync();

        Assert.Equal(HttpStatusCode.PaymentRequired, sync.StatusCode);
        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task An_unreachable_server_answers_503_and_changes_nothing()
    {
        var before = DateTime.UtcNow.AddDays(2);
        await Mutate(g => g.LicenceDeadline = before);
        _licences.Refuse = new ActivationRefusedException("Impossible de joindre le serveur Lonnii.");

        var sync = await SyncAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, sync.StatusCode);
        Assert.Equal(before, (await ReadGroupe()).LicenceDeadline);
    }
}
