using System.Collections.Concurrent;
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
/// The newest licence a computer has held, kept outside lonnii.db (<see cref="ILicenceWatermark"/>):
/// an older licence - from a copy of the database put back, or replayed by a fake server - is not
/// accepted, and a deleted one does not restart the window to fetch one.
/// </summary>
public class LicenceWatermarkTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private FakeLicenceServer _licences = null!;
    private readonly MemoryWatermark _watermark = new();

    private const string GroupId = "8d3e2a71-6c4b-4f0a-a5d9-1b7e0c3f2a64";
    private const string AdminEmail = "admin@quincaillerie-est.bf";
    private const string AdminPassword = "Motdepasse123";
    private const string Device = "till-1";

    private static readonly System.Security.Cryptography.ECDsa TestKey =
        System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    private static readonly string TestPublicKey = Convert.ToBase64String(TestKey.ExportSubjectPublicKeyInfo());

    private static string Sign(string mode, DateTime? deadline, DateTime issuedAt) =>
        LicenceToken.Issue(TestKey.ExportPkcs8PrivateKey(),
            new LicenceClaims(GroupId, mode, deadline, issuedAt, [Device], false, null));

    private sealed class MemoryWatermark : ILicenceWatermark
    {
        public ConcurrentDictionary<string, DateTime> Values { get; } = new();
        public DateTime? Floor { get; set; }
        public ConcurrentQueue<DateTime> Marks { get; } = new();
        public ConcurrentQueue<DateTime> Resets { get; } = new();

        public DateTime? NewestIssuedAt(string groupId) => Values.TryGetValue(groupId, out var at) ? at : null;
        public void Raise(string groupId, DateTime issuedAt) =>
            Values.AddOrUpdate(groupId, issuedAt, (_, old) => issuedAt > old ? issuedAt : old);
        public DateTime? ClockFloor() => Floor;
        public void MarkClock(DateTime now) => Marks.Enqueue(now);
        public void ResetClock(DateTime serverTime)
        {
            Resets.Enqueue(serverTime);
            Floor = serverTime;
        }
    }

    private sealed class FakeLicenceServer : ILicenceServer
    {
        public Exception? Refuse { get; set; }

        /// <summary>When set, the licence answered was signed then - a replay of an old one.</summary>
        public DateTime? IssuedAt { get; set; }

        public int Calls { get; private set; }
        public List<LicenceRefreshRequest> Requests { get; } = [];

        public Task<ActivationResponse> ActivateAsync(string baseUrl, ActivationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        /// <summary>Per call, in order: how long the answer takes, and how much later than now it is signed.</summary>
        public Queue<(int DelayMs, TimeSpan Later)> Plan { get; } = new();

        public async Task<LicenceRefreshResponse> RefreshAsync(string baseUrl, LicenceRefreshRequest request, CancellationToken ct)
        {
            Calls++;
            Requests.Add(request);
            if (Refuse is not null) throw Refuse;

            var now = DateTime.UtcNow;
            var issued = IssuedAt ?? now;
            if (Plan.TryDequeue(out var step))
            {
                issued = now + step.Later;
                await Task.Delay(step.DelayMs, ct);
            }
            var deadline = issued.AddDays(14);
            return new LicenceRefreshResponse(
                request.GroupId, "Quincaillerie Est", DeploymentModes.Online, 5, 1, "FCFA",
                true, "Active", now.AddDays(200), false, null, now, deadline, null,
                Sign(DeploymentModes.Online, deadline, issued));
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
                s.RemoveAll<LicenceGuard>();
                s.AddScoped(sp => new LicenceGuard(
                    sp.GetRequiredService<Lonnii.Api.Features.Remote.ControlDb>().Db, TestPublicKey, _watermark,
                    sp.GetService<HostSecurityLog>()));
            });
        });
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var admin = new User { Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword), IsVerified = true };
        db.Users.Add(admin);
        db.Groupes.Add(new Groupe
        {
            Id = GroupId,
            Nom = "Quincaillerie Est",
            IdUserAdmin = admin.IdUser,
            Mode = DeploymentModes.Online,
            LicenceServerUrl = "https://licences.lonnii.test",
        });
        db.GroupMembers.Add(new GroupMember { IdGroupe = GroupId, IdUser = admin.IdUser });
        db.UserRoles.Add(new UserRole { UserId = admin.IdUser, GroupId = GroupId, Role = GroupRoles.Admin, AssignedBy = admin.IdUser });
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

    private async Task StoreToken(string? token)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var row = await db.HostLicences.FirstOrDefaultAsync(h => h.GroupId == GroupId);
        if (row is null) db.HostLicences.Add(row = new HostLicence { GroupId = GroupId });
        row.Token = token;
        await db.SaveChangesAsync();
    }

    private async Task<string?> StoredToken()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        return await db.HostLicences.AsNoTracking().Where(h => h.GroupId == GroupId).Select(h => h.Token).FirstOrDefaultAsync();
    }

    private async Task<HttpResponseMessage> SyncAsync()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/licence/sync") { Content = JsonContent.Create(new LicenceSyncRequest(GroupId)) };
        message.Headers.Add("x-device-id", Device);
        return await _client.SendAsync(message);
    }

    private async Task<HttpResponseMessage> OpenSessionAsync()
    {
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{GroupId}/session") { Content = JsonContent.Create(new { }) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Add("x-device-id", Device);
        return await _client.SendAsync(message);
    }

    // --- Recording ---

    [Fact]
    public async Task A_refresh_records_the_licence_issue_time_outside_the_database()
    {
        Assert.Equal(HttpStatusCode.OK, (await SyncAsync()).StatusCode);

        var recorded = _watermark.NewestIssuedAt(GroupId);
        Assert.NotNull(recorded);
        Assert.Equal(LicenceToken.Verify(await StoredToken(), TestPublicKey)!.IssuedAt, recorded);
    }

    [Fact]
    public async Task An_older_licence_from_the_server_is_not_stored()
    {
        Assert.Equal(HttpStatusCode.OK, (await SyncAsync()).StatusCode);
        var current = await StoredToken();

        // Something posing as our server answers with a licence captured last month.
        _licences.IssuedAt = DateTime.UtcNow.AddDays(-30);
        await SyncAsync();

        Assert.Equal(current, await StoredToken());
    }

    // --- A copy of lonnii.db put back ---

    [Fact]
    public async Task An_older_licence_in_the_database_locks_until_the_computer_connects()
    {
        // The computer has held a licence issued today; the database put back carries a local-mode one
        // from two months ago, which never expires.
        _watermark.Raise(GroupId, DateTime.UtcNow);
        await StoreToken(Sign(DeploymentModes.Local, null, DateTime.UtcNow.AddDays(-60)));
        _licences.Refuse = new ActivationRefusedException("Impossible de joindre le serveur Lonnii.");

        var response = await OpenSessionAsync();

        Assert.Equal(HttpStatusCode.Locked, response.StatusCode);
        Assert.Contains("Internet", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task A_deleted_licence_does_not_restart_the_window_to_fetch_one()
    {
        _watermark.Raise(GroupId, DateTime.UtcNow.AddDays(-2));
        _licences.Refuse = new ActivationRefusedException("Impossible de joindre le serveur Lonnii.");

        var status = await _client.GetFromJsonAsync<LicenceStatusDto>($"/api/licence/status/{GroupId}");

        Assert.True(status!.Expired);
    }

    [Fact]
    public async Task Online_the_session_fetches_the_current_licence_and_opens()
    {
        _watermark.Raise(GroupId, DateTime.UtcNow.AddMinutes(-5));
        await StoreToken(Sign(DeploymentModes.Local, null, DateTime.UtcNow.AddDays(-60)));

        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
        Assert.Equal(1, _licences.Calls);
        Assert.Equal(DeploymentModes.Online, LicenceToken.Verify(await StoredToken(), TestPublicKey)!.Mode);
    }

    [Fact]
    public async Task A_computer_with_nothing_recorded_keeps_the_window_to_fetch_a_first_licence()
    {
        var status = await _client.GetFromJsonAsync<LicenceStatusDto>($"/api/licence/status/{GroupId}");

        Assert.False(status!.Expired);
        Assert.InRange((status.Deadline!.Value - DateTime.UtcNow).TotalDays, 13.9, 14.1);
    }

    // --- The latest moment seen ---

    [Fact]
    public async Task A_frozen_clock_still_reaches_the_deadline_through_the_recorded_moment()
    {
        // Licence issued ten days ago, valid fourteen; Windows' clock agrees, but this computer is known
        // (registry, file, uptime) to have run five days past today already.
        var issued = DateTime.UtcNow.AddDays(-10);
        await StoreToken(Sign(DeploymentModes.Online, issued.AddDays(14), issued));
        _watermark.Floor = DateTime.UtcNow.AddDays(5);

        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task Each_check_records_the_moment_reached()
    {
        await _client.GetFromJsonAsync<LicenceStatusDto>($"/api/licence/status/{GroupId}");

        Assert.True(_watermark.Marks.TryPeek(out var mark));
        Assert.InRange(mark, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task A_fresh_licence_restarts_a_clock_that_once_ran_ahead()
    {
        // The computer's clock once said next month: the recorded moment is stuck there and has locked it.
        var issued = DateTime.UtcNow.AddDays(-1);
        await StoreToken(Sign(DeploymentModes.Online, issued.AddDays(14), issued));
        _watermark.Raise(GroupId, issued);
        _watermark.Floor = DateTime.UtcNow.AddDays(30);
        await Mutate(g => g.LicenceClockMark = DateTime.UtcNow.AddDays(30));
        Assert.Equal(HttpStatusCode.Locked, (await OpenSessionAsync()).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SyncAsync()).StatusCode);

        Assert.Single(_watermark.Resets);
        Assert.Equal(HttpStatusCode.OK, (await OpenSessionAsync()).StatusCode);
    }

    [Fact]
    public async Task The_same_licence_served_again_does_not_restart_the_clock()
    {
        var issued = DateTime.UtcNow.AddHours(-2);
        await StoreToken(Sign(DeploymentModes.Online, issued.AddDays(14), issued));
        _watermark.Raise(GroupId, issued);

        // Something posing as our server answers with the licence it captured.
        _licences.IssuedAt = issued;
        Assert.Equal(HttpStatusCode.OK, (await SyncAsync()).StatusCode);

        Assert.Empty(_watermark.Resets);
    }

    private async Task Mutate(Action<Groupe> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        change(await db.Groupes.FirstAsync(g => g.Id == GroupId));
        await db.SaveChangesAsync();
    }

    // --- Reporting attempts ---

    [Fact]
    public async Task An_old_copy_of_the_database_is_reported_at_the_next_check_in()
    {
        _watermark.Raise(GroupId, DateTime.UtcNow);
        await StoreToken(Sign(DeploymentModes.Local, null, DateTime.UtcNow.AddDays(-60)));

        // Offline: noticed and kept.
        _licences.Refuse = new ActivationRefusedException("Impossible de joindre le serveur Lonnii.");
        await OpenSessionAsync();
        var log = _factory.Services.GetRequiredService<HostSecurityLog>();
        Assert.Contains(log.Pending(GroupId), e => e.Kind == SecurityEventKinds.LicenceOlder);

        // Back online: the check-in carries it, and it is not sent twice.
        _licences.Refuse = null;
        Assert.Equal(HttpStatusCode.OK, (await SyncAsync()).StatusCode);

        Assert.Contains(_licences.Requests.Last().Events!, e => e.Kind == SecurityEventKinds.LicenceOlder);
        Assert.Empty(log.Pending(GroupId));
    }

    [Fact]
    public async Task Two_check_ins_at_once_keep_the_newest_licence_and_report_nothing()
    {
        // As after an update: the host's renewal and the app's check-in start together. The first is
        // signed earlier but answers later; side by side, it used to overwrite the newer one.
        _licences.Plan.Enqueue((600, TimeSpan.Zero));
        _licences.Plan.Enqueue((0, TimeSpan.FromSeconds(5)));

        var both = await Task.WhenAll(SyncAsync(), SyncAsync());
        Assert.All(both, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var stored = LicenceToken.Verify(await StoredToken(), TestPublicKey)!;
        Assert.Equal(_watermark.NewestIssuedAt(GroupId), stored.IssuedAt);
        var log = _factory.Services.GetRequiredService<HostSecurityLog>();
        Assert.DoesNotContain(log.Pending(GroupId), e => e.Kind is SecurityEventKinds.LicenceOlder or SecurityEventKinds.LicenceReplay);
        Assert.False((await _client.GetFromJsonAsync<LicenceStatusDto>($"/api/licence/status/{GroupId}"))!.Expired);
    }

    [Fact]
    public async Task A_replayed_older_licence_is_reported()
    {
        Assert.Equal(HttpStatusCode.OK, (await SyncAsync()).StatusCode);

        _licences.IssuedAt = DateTime.UtcNow.AddDays(-30);
        await SyncAsync();

        // Sent with the check-in after the one that noticed it.
        _licences.IssuedAt = null;
        await SyncAsync();
        Assert.Contains(_licences.Requests.Last().Events!, e => e.Kind == SecurityEventKinds.LicenceReplay);
    }

    [Fact]
    public async Task A_machine_nobody_registered_is_noted_when_refused()
    {
        await StoreToken(Sign(DeploymentModes.Online, DateTime.UtcNow.AddDays(10), DateTime.UtcNow));

        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AdminEmail, AdminPassword));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        var message = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{GroupId}/session") { Content = JsonContent.Create(new { }) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Add("x-device-id", "copied-pc-0123456789");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(message)).StatusCode);

        var noted = Assert.Single(_factory.Services.GetRequiredService<HostSecurityLog>().Pending(GroupId));
        Assert.Equal(SecurityEventKinds.UnknownDevice, noted.Kind);
        Assert.Contains("copied-p", noted.Detail);
    }
}

/// <summary>
/// The real <see cref="HostLicenceWatermark"/>, pointed at a throwaway key under HKCU and a temporary
/// file instead of HKLM and the SYSTEM profile.
/// </summary>
public sealed class HostLicenceWatermarkTests : IDisposable
{
    private readonly string _keyPath = @"Software\LonniiTests\" + Guid.NewGuid().ToString("N");
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"), "host.dat");

    private HostLicenceWatermark Start() =>
        new(Microsoft.Win32.RegistryHive.CurrentUser, _keyPath, _filePath, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    private void DeleteRegistryCopy() => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    public void Dispose()
    {
        DeleteRegistryCopy();
        // The shared parent too, once the last test using it is done (refused while others still are).
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\LonniiTests", throwOnMissingSubKey: false); }
        catch (InvalidOperationException) { }
        try { Directory.Delete(Path.GetDirectoryName(_filePath)!, recursive: true); }
        catch (IOException) { } // a missing folder (DirectoryNotFoundException) included
    }

    [Fact]
    public void Nothing_recorded_reads_as_nothing()
    {
        var watermark = Start();

        Assert.Null(watermark.ClockFloor());
        Assert.Null(watermark.NewestIssuedAt("espace"));
    }

    [Fact]
    public void The_moment_reached_survives_a_restart_and_keeps_counting()
    {
        var later = DateTime.UtcNow.AddDays(10);
        Start().MarkClock(later);

        var floor = Start().ClockFloor();

        Assert.NotNull(floor);
        Assert.InRange(floor!.Value, later, later.AddMinutes(1));
    }

    [Fact]
    public void Deleting_either_copy_changes_nothing()
    {
        var issued = DateTime.UtcNow.AddHours(-1);
        Start().Raise("espace", issued);

        DeleteRegistryCopy();
        Assert.Equal(issued, Start().NewestIssuedAt("espace"));

        // That start wrote the registry copy back from the file; now lose the file instead.
        File.Delete(_filePath);
        Assert.Equal(issued, Start().NewestIssuedAt("espace"));

        // And that start wrote the file back.
        Assert.True(File.Exists(_filePath));
    }

    [Fact]
    public void A_licence_is_never_lowered()
    {
        var watermark = Start();
        var issued = DateTime.UtcNow;
        watermark.Raise("espace", issued);
        watermark.Raise("espace", issued.AddDays(-30));

        Assert.Equal(issued, Start().NewestIssuedAt("espace"));
    }

    [Fact]
    public void A_reset_lowers_the_clock_but_never_below_a_licence_held()
    {
        var watermark = Start();
        var issued = DateTime.UtcNow.AddHours(-3);
        watermark.Raise("espace", issued);
        watermark.MarkClock(DateTime.UtcNow.AddDays(100));

        watermark.ResetClock(issued.AddDays(-1));

        Assert.InRange(watermark.ClockFloor()!.Value, issued, issued.AddMinutes(1));
        Assert.InRange(Start().ClockFloor()!.Value, issued, issued.AddMinutes(1));
    }
}
