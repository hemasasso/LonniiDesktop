using System.Net;
using System.Net.Http.Json;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// Covers licence refresh - the call that carries our changes down to a shop and resets its
/// offline clock.
///
/// The commercial stake: online mode is sold cheaply against a yearly fee, offline mode for
/// far more up front. Nothing physical stops an online shop installing and then cutting the
/// internet to avoid the fee for ever. This endpoint is what makes that stop working, so
/// the cases where it must refuse are tested harder than the case where it succeeds.
/// </summary>
public class LicenceRefreshTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";
    private const string DeviceId = "machine-1";

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory));

        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var admin = new User { Email = "admin@pharmacie-nord.bf", IsVerified = true };
        db.Users.Add(admin);
        db.Groupes.Add(new Groupe
        {
            Id = GroupId,
            Nom = "Pharmacie Nord",
            IdUserAdmin = admin.IdUser,
            Mode = DeploymentModes.Local,
            MaxDevices = 5,
            MaxOfflineDays = 7,
        });
        db.Devices.Add(new Device { GroupId = GroupId, DeviceId = DeviceId });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private Task<HttpResponseMessage> RefreshAsync(string deviceId = DeviceId, string? groupId = null) =>
        _client.PostAsJsonAsync("/api/licence/refresh",
            new LicenceRefreshRequest(groupId ?? GroupId, deviceId, "1.0.0"));

    private void Mutate(Action<LonniiDbContext, Groupe> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        change(db, db.Groupes.First(g => g.Id == GroupId));
        db.SaveChanges();
    }

    [Fact]
    public async Task A_bound_machine_gets_the_current_settings()
    {
        var response = await RefreshAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LicenceRefreshResponse>();
        Assert.Equal("Pharmacie Nord", body!.GroupName);
        Assert.Equal(5, body.MaxDevices);
        Assert.False(body.IsBlocked);
    }

    /// <summary>The point of the call: a change we make reaches the shop when it reconnects.</summary>
    [Fact]
    public async Task Raising_the_machine_limit_reaches_the_shop()
    {
        Mutate((_, g) => g.MaxDevices = 8);

        var body = await (await RefreshAsync()).Content.ReadFromJsonAsync<LicenceRefreshResponse>();

        Assert.Equal(8, body!.MaxDevices);
    }

    /// <summary>
    /// The deadline is computed from the server's clock and sent down, so putting a till's
    /// clock back cannot buy extra offline days.
    /// </summary>
    /// <summary>Puts an online subscription in good standing on the workspace.</summary>
    private void GoOnline() => Mutate((db, g) =>
    {
        g.Mode = DeploymentModes.Online;
        db.DashboardSubscriptions.Add(new DashboardSubscription
        {
            GroupId = GroupId,
            GroupName = "Pharmacie Nord",
            AdminName = "Sasso",
            Montant = 60_000m,
            Statut = StatutAbonnement.Active,
            ContractStartDate = DateTime.UtcNow.AddMonths(-2),
            ContractEndDate = DateTime.UtcNow.AddMonths(10),
        });
    });

    /// <summary>
    /// The deadline is computed from the server's clock and sent down, so putting a till's
    /// clock back cannot buy extra offline days.
    /// </summary>
    [Fact]
    public async Task The_deadline_comes_from_the_server_clock()
    {
        GoOnline();

        var body = await (await RefreshAsync()).Content.ReadFromJsonAsync<LicenceRefreshResponse>();

        Assert.NotNull(body!.MustReconnectBy);
        Assert.Equal(7, (body.MustReconnectBy!.Value - body.ServerTime).Days);
        Assert.True(Math.Abs((body.ServerTime - DateTime.UtcNow).TotalMinutes) < 5);
    }

    /// <summary>
    /// An offline licence is paid in full with no recurring fee, and is sold on needing no
    /// internet. Imposing a deadline there would punish the customer who paid the most.
    /// </summary>
    [Fact]
    public async Task A_local_shop_has_no_deadline_at_all()
    {
        var body = await (await RefreshAsync()).Content.ReadFromJsonAsync<LicenceRefreshResponse>();

        Assert.Equal(DeploymentModes.Local, body!.Mode);
        Assert.Null(body.MustReconnectBy);
    }

    [Fact]
    public async Task A_longer_grace_period_can_be_granted_per_shop()
    {
        GoOnline();
        Mutate((_, g) => g.MaxOfflineDays = 30);

        var body = await (await RefreshAsync()).Content.ReadFromJsonAsync<LicenceRefreshResponse>();

        Assert.Equal(30, (body!.MustReconnectBy!.Value - body.ServerTime).Days);
    }

    [Fact]
    public async Task The_check_in_time_is_recorded_so_quiet_shops_are_visible()
    {
        await RefreshAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var groupe = db.Groupes.First(g => g.Id == GroupId);
        Assert.NotNull(groupe.LastLicenceCheckAt);
        Assert.Equal(DeviceId, db.Devices.First().DeviceId);
        Assert.True(db.Devices.First().LastSeenAt > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task A_machine_that_was_never_bound_is_refused()
    {
        var response = await RefreshAsync(deviceId: "machine-inconnue");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Revoking a stolen laptop has to actually reach it, and this is how.</summary>
    [Fact]
    public async Task A_revoked_machine_cannot_keep_itself_alive()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Devices.First().RevokedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await RefreshAsync()).StatusCode);
    }

    [Fact]
    public async Task An_unknown_workspace_is_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await RefreshAsync(groupId: Guid.NewGuid().ToString())).StatusCode);
    }

    /// <summary>
    /// Blocking is reported rather than refused, so the shop can be told why instead of the
    /// application simply failing.
    /// </summary>
    [Fact]
    public async Task A_blocked_workspace_is_told_why()
    {
        Mutate((_, g) => { g.IsBlocked = true; g.BlockReason = "Facture impayée"; });

        var body = await (await RefreshAsync()).Content.ReadFromJsonAsync<LicenceRefreshResponse>();

        Assert.True(body!.IsBlocked);
        Assert.Equal("Facture impayée", body.BlockReason);
    }

    [Fact]
    public async Task An_online_shop_with_an_expired_subscription_is_refused()
    {
        Mutate((db, g) =>
        {
            g.Mode = DeploymentModes.Online;
            db.DashboardSubscriptions.Add(new DashboardSubscription
            {
                GroupId = GroupId,
                GroupName = "Pharmacie Nord",
                AdminName = "Sasso",
                Montant = 60_000m,
                Statut = StatutAbonnement.Active,
                ContractStartDate = DateTime.UtcNow.AddYears(-2),
                ContractEndDate = DateTime.UtcNow.AddDays(-1),
            });
        });

        Assert.Equal(HttpStatusCode.PaymentRequired, (await RefreshAsync()).StatusCode);
    }

    /// <summary>
    /// A shop that is refused must not have its offline clock reset, or an unpaid workspace
    /// could buy itself another week simply by asking and being told no.
    /// </summary>
    [Fact]
    public async Task A_refused_refresh_does_not_extend_the_offline_clock()
    {
        Mutate((db, g) =>
        {
            g.Mode = DeploymentModes.Online;
            g.LastLicenceCheckAt = null;
        });

        Assert.Equal(HttpStatusCode.PaymentRequired, (await RefreshAsync()).StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        Assert.Null(db.Groupes.First(g => g.Id == GroupId).LastLicenceCheckAt);
    }

    [Fact]
    public async Task An_online_shop_in_good_standing_refreshes_normally()
    {
        Mutate((db, g) =>
        {
            g.Mode = DeploymentModes.Online;
            db.DashboardSubscriptions.Add(new DashboardSubscription
            {
                GroupId = GroupId,
                GroupName = "Pharmacie Nord",
                AdminName = "Sasso",
                Montant = 60_000m,
                Statut = StatutAbonnement.Active,
                ContractStartDate = DateTime.UtcNow.AddMonths(-2),
                ContractEndDate = DateTime.UtcNow.AddMonths(10),
            });
        });

        var response = await RefreshAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LicenceRefreshResponse>();
        Assert.True(body!.SubscriptionRequired);
        Assert.Equal(StatutAbonnement.Active, body.SubscriptionStatus);
    }
}
