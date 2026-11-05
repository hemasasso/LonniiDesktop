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

    /// <summary>The licence key for these tests: the fake server signs with it, the guard trusts it.</summary>
    private static readonly System.Security.Cryptography.ECDsa TestKey =
        System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    private static readonly string TestPublicKey = Convert.ToBase64String(TestKey.ExportSubjectPublicKeyInfo());

    private static string Sign(LicenceClaims claims) => LicenceToken.Issue(TestKey.ExportPkcs8PrivateKey(), claims);

    private sealed class FakeLicenceServer : ILicenceServer
    {
        public Exception? Refuse { get; set; }
        public int MaxOfflineDays { get; set; } = 14;
        public string? BackupToken { get; set; }
        public List<string> Devices { get; } = [Device, "till-2"];
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
            var deadline = now.AddDays(MaxOfflineDays);
            return Task.FromResult(new LicenceRefreshResponse(
                request.GroupId, "Pharmacie Nord", DeploymentModes.Online, 5, 1, "FCFA",
                true, "Active", now.AddDays(200), false, null, now, deadline, BackupToken,
                Sign(new LicenceClaims(request.GroupId, DeploymentModes.Online, deadline, now, Devices, false, null))));
        }
    }

    /// <summary>Stores a licence as the host would have it after a refresh.</summary>
    private async Task StoreLicence(string? token)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var row = await db.HostLicences.FirstOrDefaultAsync(h => h.GroupId == GroupId);
        if (row is null) db.HostLicences.Add(row = new HostLicence { GroupId = GroupId });
        row.Token = token;
        await db.SaveChangesAsync();
    }

    private Task StoreSigned(string mode, DateTime? deadline, DateTime? issuedAt = null, bool blocked = false, string[]? devices = null) =>
        StoreLicence(Sign(new LicenceClaims(GroupId, mode, deadline, issuedAt ?? DateTime.UtcNow,
            devices ?? [Device, "till-2"], blocked, blocked ? "Impayé depuis deux mois" : null)));

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
                s.RemoveAll<LicenceGuard>();
                s.AddScoped(sp => new LicenceGuard(
                    sp.GetRequiredService<Lonnii.Api.Features.Remote.ControlDb>().Db, TestPublicKey));
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

    private async Task<HttpResponseMessage> OpenSessionAsync(string device = Device)
    {
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{GroupId}/session")
        {
            Content = JsonContent.Create(new { }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Add("x-device-id", device);
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
        await StoreSigned(DeploymentModes.Online, DateTime.UtcNow.AddDays(3));

        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);

        var status = await _client.GetFromJsonAsync<LicenceStatusDto>($"/api/licence/status/{GroupId}");
        Assert.True(status!.Enforced);
        Assert.False(status.Expired);
        Assert.Equal(3, status.DaysLeft);
    }

    [Fact]
    public async Task Past_the_deadline_the_workspace_is_locked()
    {
        await StoreSigned(DeploymentModes.Online, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(-14));

        var response = await OpenSessionAsync();

        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Contains("licence", error!.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_host_with_no_signed_licence_gets_fourteen_days_to_fetch_one()
    {
        // An installation from before signed licences, or a fresh setup.
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);

        var status = await _client.GetFromJsonAsync<LicenceStatusDto>($"/api/licence/status/{GroupId}");
        Assert.True(status!.Enforced);
        Assert.InRange((status.Deadline!.Value - DateTime.UtcNow).TotalDays, 13.9, 14.1);
    }

    [Fact]
    public async Task Past_the_unsigned_window_the_host_locks_until_it_connects_once()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.HostLicences.Add(new HostLicence { GroupId = GroupId, UnsignedSince = DateTime.UtcNow.AddDays(-15) });
            await db.SaveChangesAsync();
        }
        await Mutate(g => g.Mode = DeploymentModes.Local);

        var response = await OpenSessionAsync();
        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        Assert.Contains("Internet", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);

        // Connecting once brings the signed licence and lifts it.
        Assert.Equal(HttpStatusCode.OK, (await SyncAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task Winding_the_clock_back_does_not_buy_more_days()
    {
        // The installation has already seen a moment after the deadline; the computer's own
        // clock now says it is earlier. "Now" must stay at the later of the two.
        await StoreSigned(DeploymentModes.Online, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-15));
        await Mutate(g => g.LicenceClockMark = DateTime.UtcNow.AddDays(2));

        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task Clearing_the_clock_mark_does_not_take_now_below_the_licence_issue_time()
    {
        // Clock wound back and the mark row emptied: the signature's own issue time still holds.
        await StoreSigned(DeploymentModes.Online, DateTime.UtcNow.AddDays(1), issuedAt: DateTime.UtcNow.AddDays(3));
        await Mutate(g => g.LicenceClockMark = null);

        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task A_signed_local_licence_is_never_locked_whatever_the_dates()
    {
        await StoreSigned(DeploymentModes.Local, deadline: null);
        await Mutate(g => g.LicenceDeadline = DateTime.UtcNow.AddDays(-400));

        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
    }

    // --- The database cracks the signature closes ---

    [Fact]
    public async Task Setting_the_mode_to_local_in_the_database_does_not_unlock_an_online_licence()
    {
        await StoreSigned(DeploymentModes.Online, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-15));
        await Mutate(g =>
        {
            g.Mode = DeploymentModes.Local;
            g.LicenceDeadline = DateTime.UtcNow.AddYears(10);
        });

        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task An_altered_licence_counts_as_none()
    {
        // A signed online licence, its deadline rewritten in the stored text: the signature no
        // longer matches, so the host is back on the unsigned window, not the forged date.
        var genuine = Sign(new LicenceClaims(GroupId, DeploymentModes.Local, null, DateTime.UtcNow, [Device], false, null));
        var parts = genuine.Split('.');
        var forged = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            parts[0].Replace('-', '+').Replace('_', '/').PadRight(parts[0].Length + (4 - parts[0].Length % 4) % 4, '=')))
            .Replace("\"local\"", "\"online\"");
        await StoreLicence(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(forged)).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_') + "." + parts[1]);

        var status = await _client.GetFromJsonAsync<LicenceStatusDto>($"/api/licence/status/{GroupId}");
        Assert.True(status!.Enforced);
        Assert.InRange((status.Deadline!.Value - DateTime.UtcNow).TotalDays, 13.9, 14.1);
    }

    [Fact]
    public async Task A_machine_added_to_the_devices_table_is_refused_when_the_licence_does_not_list_it()
    {
        // A copied installation: the copier adds their own machine to the local table.
        await StoreSigned(DeploymentModes.Online, DateTime.UtcNow.AddDays(10), devices: [Device]);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Devices.Add(new Device { GroupId = GroupId, DeviceId = "copied-pc", DeviceName = "Copie" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await OpenSessionAsync("copied-pc")).StatusCode);
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
        await StoreSigned(DeploymentModes.Online, DateTime.UtcNow.AddDays(-5), DateTime.UtcNow.AddDays(-19));
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
        await StoreSigned(DeploymentModes.Online, DateTime.UtcNow.AddDays(-5), DateTime.UtcNow.AddDays(-19));
        _licences.Refuse = new ActivationRefusedException("Abonnement inactif ou expiré.", HttpStatusCode.PaymentRequired);

        var sync = await SyncAsync();

        Assert.Equal(HttpStatusCode.PaymentRequired, sync.StatusCode);
        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);
    }

    // --- Blocked, or this computer removed, from the dashboard ---

    [Fact]
    public async Task A_blocked_shop_is_locked_with_its_reason_in_local_mode_too()
    {
        await Mutate(g =>
        {
            g.Mode = DeploymentModes.Local;
            g.IsBlocked = true;
            g.BlockReason = "Impayé depuis deux mois";
        });

        var response = await OpenSessionAsync();

        // A lock (with "Réessayer"), not a plain refusal: lifting the block lets the shop back in.
        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        Assert.Contains("Impayé", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task A_computer_removed_on_the_server_locks_itself_and_not_the_others()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Devices.Add(new Device { GroupId = GroupId, DeviceId = "till-2", DeviceName = "Caisse 2" });
            await db.SaveChangesAsync();
        }

        await Mutate(g => g.Mode = DeploymentModes.Local);
        _licences.Refuse = new ActivationRefusedException("Cet ordinateur a été retiré de l'espace.", HttpStatusCode.Gone);

        var sync = await SyncAsync();
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.True((await sync.Content.ReadFromJsonAsync<LicenceStatusDto>())!.Expired);

        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync("till-2")).StatusCode);

        // Re-authorised on the dashboard: the next check-in lets it back in.
        _licences.Refuse = null;
        Assert.Equal(HttpStatusCode.OK, (await SyncAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
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

    // --- A local shop checks in too ---
    // The server is the authority for a shop's mode, machine allowance and blocked flag. A shop that never
    // contacts it can never be told: found when a local shop was made online in the dashboard and the
    // desktop stayed local for ever.

    [Fact]
    public async Task A_local_shop_learns_from_the_server_that_it_is_now_online()
    {
        _licences.BackupToken = "token-for-this-till";
        await Mutate(g =>
        {
            g.Mode = DeploymentModes.Local;
            g.MaxDevices = 3;
            g.LicenceDeadline = null;
        });

        var sync = await SyncAsync();

        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(1, _licences.Calls);

        var groupe = await ReadGroupe();
        Assert.Equal(DeploymentModes.Online, groupe.Mode);
        Assert.Equal(5, groupe.MaxDevices);
        Assert.NotNull(groupe.LicenceDeadline);

        var status = (await sync.Content.ReadFromJsonAsync<LicenceStatusDto>())!;
        Assert.True(status.Enforced);
        Assert.Equal(14, status.DaysLeft);

        // And it can now back up: the key to its cloud backup arrived with the answer.
        using var scope = _factory.Services.CreateScope();
        var state = await scope.ServiceProvider.GetRequiredService<LonniiDbContext>()
            .CloudBackupStates.AsNoTracking().SingleAsync();
        Assert.Equal("token-for-this-till", state.DeviceToken);
        Assert.Equal(Device, state.DeviceId);
    }

    [Fact]
    public async Task A_local_shop_that_cannot_reach_the_server_stays_local_and_hears_nothing_about_it()
    {
        await Mutate(g => g.Mode = DeploymentModes.Local);
        _licences.Refuse = new ActivationRefusedException("Impossible de joindre le serveur Lonnii.");

        var sync = await SyncAsync();

        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var groupe = await ReadGroupe();
        Assert.Equal(DeploymentModes.Local, groupe.Mode);
        Assert.Null(groupe.LicenceDeadline);
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task A_local_shop_the_server_refuses_is_never_locked_for_it()
    {
        await Mutate(g => g.Mode = DeploymentModes.Local);
        _licences.Refuse = new ActivationRefusedException("Ce poste n'est pas autorisé.", HttpStatusCode.Forbidden);

        var sync = await SyncAsync();

        // The refusal itself locks nothing; the shop is on its window to confirm its licence.
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.False((await sync.Content.ReadFromJsonAsync<LicenceStatusDto>())!.Expired);
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task A_shop_with_no_licence_server_never_calls_one()
    {
        await Mutate(g =>
        {
            g.Mode = DeploymentModes.Local;
            g.LicenceServerUrl = null;
        });

        var sync = await SyncAsync();

        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(0, _licences.Calls);
        Assert.Equal(DeploymentModes.Local, (await ReadGroupe()).Mode);
    }
}
