using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Lonnii.Api.Features.Admin;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Lonnii.Tests;

/// <summary>
/// The admin dashboard's "Lonnii Desktop" routes: only the dashboard's own tokens open them, each
/// action needs its permission, and only desktop shops - not the web app's workspaces sharing the
/// database - are listed.
/// </summary>
public class DesktopAdminTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;
    private ECDsa _dashboardKey = null!;

    private const string WebWorkspace = "0b6f1d1e-1a7e-4d55-9a51-2f2a7f0c0001";
    private const string LocalShop = "0b6f1d1e-1a7e-4d55-9a51-2f2a7f0c0002";
    private const string OnlineShopOffline = "0b6f1d1e-1a7e-4d55-9a51-2f2a7f0c0003";
    private const string PendingShop = "0b6f1d1e-1a7e-4d55-9a51-2f2a7f0c0004";

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _dashboardKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyFile = Path.Combine(_dataDirectory, "dashboard-public.pem");
        await File.WriteAllTextAsync(publicKeyFile, _dashboardKey.ExportSubjectPublicKeyInfoPem());

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory);
            builder.UseSetting("DashboardAuth:PublicKeyFile", publicKeyFile);
        });
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var owner = new User { Email = "owner@shop.bf", FirstName = "Awa", LastName = "Ouedraogo" };
        db.Users.Add(owner);

        // A Lonnii Business workspace: local by default, never touched by the desktop app.
        db.Groupes.Add(new Groupe { Id = WebWorkspace, Nom = "Boutique Web", IdUserAdmin = owner.IdUser });

        db.Groupes.Add(new Groupe
        {
            Id = LocalShop, Nom = "Pharmacie Locale", IdUserAdmin = owner.IdUser,
            MaxDevices = 2, LastLicenceCheckAt = DateTime.UtcNow.AddHours(-2),
        });
        db.Devices.Add(new Device { GroupId = LocalShop, DeviceId = "till-a", DeviceName = "Caisse 1", AppVersion = "1.4.0" });
        db.Devices.Add(new Device { GroupId = LocalShop, DeviceId = "till-b", DeviceName = "Caisse 2", AppVersion = "1.5.0" });
        db.Devices.Add(new Device
        {
            GroupId = LocalShop, DeviceId = "till-old", DeviceName = "Ancienne caisse",
            RevokedAt = DateTime.UtcNow.AddDays(-30), RevokedReason = "Panne",
        });

        db.Groupes.Add(new Groupe
        {
            Id = OnlineShopOffline, Nom = "Super Marché En Ligne", IdUserAdmin = owner.IdUser,
            Mode = DeploymentModes.Online, MaxOfflineDays = 14,
            LastLicenceCheckAt = DateTime.UtcNow.AddDays(-20),
        });
        db.Devices.Add(new Device { GroupId = OnlineShopOffline, DeviceId = "pos-1" });

        db.Groupes.Add(new Groupe
        {
            Id = PendingShop, Nom = "Boutique En Attente", IdUserAdmin = owner.IdUser,
            ApprovalStatus = ApprovalStatuses.Pending,
        });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        _dashboardKey.Dispose();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    // --- Silent shops ----------------------------------------------------------------------

    private async Task AddShop(Groupe groupe, bool withComputer)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        groupe.IdUserAdmin = db.Users.Select(u => u.IdUser).First();
        db.Groupes.Add(groupe);
        if (withComputer) db.Devices.Add(new Device { GroupId = groupe.Id, DeviceId = "pc-" + groupe.Id });
        await db.SaveChangesAsync();
    }

    private async Task<DesktopOverviewDto> Overview()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/overview", Viewer()));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DesktopOverviewDto>())!;
    }

    [Fact]
    public async Task A_phone_only_shop_is_listed_and_never_flagged_as_silent()
    {
        // No computer, so no devices row and no check-in: it used to vanish from the list once approved,
        // and would have been flagged as "never connected".
        const string phoneShop = "0b6f1d1e-1a7e-4d55-9a51-2f2a7f0c0010";
        await AddShop(new Groupe { Id = phoneShop, Nom = "Boutique Téléphone", Mode = DeploymentModes.Online, PhoneOnly = true },
            withComputer: false);

        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/shops", Viewer()));
        var shops = (await response.Content.ReadFromJsonAsync<List<ShopSummaryDto>>())!;

        Assert.Contains(shops, s => s.Id == phoneShop && s.PhoneOnly);
        Assert.DoesNotContain((await Overview()).Attention, a => a.ShopId == phoneShop && a.Kind == "offline");
    }

    [Fact]
    public async Task An_online_shop_silent_for_a_week_is_flagged_before_it_would_lock()
    {
        const string quietShop = "0b6f1d1e-1a7e-4d55-9a51-2f2a7f0c0011";
        await AddShop(new Groupe
        {
            Id = quietShop, Nom = "Boutique Silencieuse", Mode = DeploymentModes.Online, MaxOfflineDays = 14,
            LastLicenceCheckAt = DateTime.UtcNow.AddDays(-8).AddHours(-1),
        }, withComputer: true);

        var item = Assert.Single((await Overview()).Attention, a => a.ShopId == quietShop && a.Kind == "offline");

        Assert.Equal("warning", item.Severity);
        Assert.Contains("Silencieuse depuis 8 jours", item.Message);
    }

    [Fact]
    public async Task Past_its_offline_days_a_silent_shop_is_not_assumed_locked()
    {
        // An old version with the internet cut keeps selling: the dashboard must not say it is blocked.
        var item = Assert.Single((await Overview()).Attention, a => a.ShopId == OnlineShopOffline && a.Kind == "offline");

        Assert.Contains("utilisée sans se connecter", item.Message);
    }

    // --- Attempts to get round the licence ------------------------------------------------

    [Fact]
    public async Task Attempts_reported_at_check_in_reach_the_dashboard_even_when_the_check_in_is_refused()
    {
        // The shop's computer reports that an old copy of its database was put back. Its subscription has
        // lapsed, so the check-in itself is refused - the report is kept all the same.
        var refresh = await _client.PostAsJsonAsync("/api/licence/refresh", new Lonnii.Shared.Contracts.LicenceRefreshRequest(
            OnlineShopOffline, "pos-1", "1.0.21",
            [new Lonnii.Shared.Contracts.SecurityEventDto(SecurityEventKinds.LicenceOlder, DateTime.UtcNow.AddHours(-1), "Licence du 01/09/2026")]));
        Assert.Equal(HttpStatusCode.PaymentRequired, refresh.StatusCode);

        var item = Assert.Single((await Overview()).Attention, a => a.ShopId == OnlineShopOffline && a.Kind == "security");
        Assert.Equal("critical", item.Severity);
        Assert.Contains("Ancienne copie de la base", item.Message);
        Assert.Equal(1, (await Overview()).SecurityAlerts);

        var response = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{OnlineShopOffline}", Viewer()));
        var detail = (await response.Content.ReadFromJsonAsync<ShopDetailDto>())!;
        var attempt = Assert.Single(detail.SecurityEvents!);
        Assert.Equal("pos-1", attempt.DeviceId);
        Assert.Equal("1.0.21", attempt.AppVersion);
        Assert.Equal("Licence du 01/09/2026", attempt.Detail);
        Assert.False(attempt.FromServer);
    }

    [Fact]
    public async Task A_machine_nobody_registered_asking_for_a_licence_is_noted_once_a_day()
    {
        for (var i = 0; i < 3; i++)
            await _client.PostAsJsonAsync("/api/licence/refresh",
                new Lonnii.Shared.Contracts.LicenceRefreshRequest(LocalShop, "copied-pc", "1.0.20"));

        var response = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{LocalShop}", Viewer()));
        var detail = (await response.Content.ReadFromJsonAsync<ShopDetailDto>())!;

        var attempt = Assert.Single(detail.SecurityEvents!);
        Assert.Equal(SecurityEventKinds.UnboundDevice, attempt.Kind);
        Assert.True(attempt.FromServer);

        var shops = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/shops?filter=security", Viewer()));
        Assert.Equal([LocalShop], (await shops.Content.ReadFromJsonAsync<List<ShopSummaryDto>>())!.Select(s => s.Id));
    }

    [Fact]
    public async Task A_changed_hardware_signature_on_the_same_device_is_flagged_as_a_possible_clone()
    {
        async Task Refresh(string hardware) =>
            (await _client.PostAsJsonAsync("/api/licence/refresh",
                new Lonnii.Shared.Contracts.LicenceRefreshRequest(LocalShop, "till-a", "1.0.21", HardwareSignature: hardware)))
                .EnsureSuccessStatusCode();

        // First check-in sets the baseline; a copy of the MachineGuid on other hardware checks in next.
        await Refresh("hw-sig-real-machine");
        await Refresh("hw-sig-cloned-pc");

        var response = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{LocalShop}", Viewer()));
        var detail = (await response.Content.ReadFromJsonAsync<ShopDetailDto>())!;

        var attempt = Assert.Single(detail.SecurityEvents!, e => e.Kind == SecurityEventKinds.HardwareChanged);
        Assert.True(attempt.FromServer);
        Assert.Equal("till-a", attempt.DeviceId);

        // The same signature checking in again is not a change.
        await Refresh("hw-sig-cloned-pc");
        var after = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{LocalShop}", Viewer()));
        var detail2 = (await after.Content.ReadFromJsonAsync<ShopDetailDto>())!;
        Assert.Single(detail2.SecurityEvents!, e => e.Kind == SecurityEventKinds.HardwareChanged);
    }

    [Fact]
    public async Task Changed_program_files_on_the_same_version_are_flagged_but_a_version_change_is_not()
    {
        async Task Refresh(string version, string buildHash) =>
            (await _client.PostAsJsonAsync("/api/licence/refresh",
                new Lonnii.Shared.Contracts.LicenceRefreshRequest(LocalShop, "till-b", version, BuildHash: buildHash)))
                .EnsureSuccessStatusCode();

        async Task<ShopDetailDto> Detail()
        {
            var r = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{LocalShop}", Viewer()));
            return (await r.Content.ReadFromJsonAsync<ShopDetailDto>())!;
        }

        // Baseline, then the published build updates (new version, new files) - not tampering.
        await Refresh("1.0.21", "build-aaa");
        await Refresh("1.0.22", "build-bbb");
        Assert.DoesNotContain((await Detail()).SecurityEvents!, e => e.Kind == SecurityEventKinds.BinariesModified);

        // Same version, different files: a patched build.
        await Refresh("1.0.22", "build-ccc-patched");
        Assert.Single((await Detail()).SecurityEvents!, e => e.Kind == SecurityEventKinds.BinariesModified);
    }

    // --- Tokens like the ones dashboard-auth issues -----------------------------------------

    private static string Token(ECDsa key, string role, object permissions, string audience = "lonnii-dashboard") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "lonnii-dashboard-auth",
            Audience = audience,
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object>
            {
                ["adminId"] = "admin-1",
                ["email"] = "admin@lonnii.com",
                ["role"] = role,
                ["permissions"] = permissions,
            },
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256),
        });

    private HttpRequestMessage Request(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private string Viewer() => Token(_dashboardKey, "admin", new[] { DesktopAdminEndpoints.ViewPermission });

    private string DeviceManager() => Token(_dashboardKey, "admin",
        new[] { DesktopAdminEndpoints.ViewPermission, DesktopAdminEndpoints.ManageDevicesPermission });

    private string SuperAdmin() => Token(_dashboardKey, "super_admin", "all");

    // --- Who gets in ---------------------------------------------------------------------

    [Fact]
    public async Task No_token_is_refused()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/overview", null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_signed_by_another_key_is_refused()
    {
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var response = await _client.SendAsync(
            Request(HttpMethod.Get, "/api/admin/desktop/overview", Token(forger, "owner", "all")));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Password_confirmation_token_is_not_a_login()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/overview",
            Token(_dashboardKey, "owner", "all", audience: "lonnii-dashboard-confirm")));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_without_desktop_permission_is_forbidden()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/overview",
            Token(_dashboardKey, "admin", new[] { "users.view" })));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_origin_passes_the_browser_preflight_and_others_do_not()
    {
        async Task<string?> AllowedOrigin(string origin)
        {
            var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/admin/desktop/overview");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization");
            var response = await _client.SendAsync(preflight);
            return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;
        }

        Assert.Equal("https://dashboard-lonniib.com", await AllowedOrigin("https://dashboard-lonniib.com"));
        Assert.Null(await AllowedOrigin("https://evil.example"));
    }

    // --- What it shows -------------------------------------------------------------------

    [Fact]
    public async Task Overview_counts_desktop_shops_only_and_flags_what_needs_attention()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/overview", Viewer()));
        response.EnsureSuccessStatusCode();
        var overview = (await response.Content.ReadFromJsonAsync<DesktopOverviewDto>())!;

        Assert.Equal(3, overview.TotalShops); // the web workspace is not one of them
        Assert.Equal(1, overview.OnlineShops);
        Assert.Equal(1, overview.PendingRegistrations);
        Assert.Equal(1, overview.OfflineBeyondGrace);
        Assert.Equal(1, overview.ShopsAtDeviceLimit); // 2/2, the revoked till does not count
        Assert.Equal(3, overview.ActiveDevices);
        Assert.DoesNotContain(overview.Attention, a => a.ShopId == WebWorkspace);
        Assert.Contains(overview.Attention, a => a.ShopId == OnlineShopOffline && a.Kind == "offline" && a.Severity == "critical");
        Assert.Contains(overview.Attention, a => a.ShopId == OnlineShopOffline && a.Kind == "subscription");
        Assert.Equal("critical", overview.Attention[0].Severity); // worst first
    }

    [Fact]
    public async Task Shop_list_filters_and_puts_problems_first()
    {
        var all = await GetShops(null);
        Assert.Equal(OnlineShopOffline, all[0].Id);
        Assert.DoesNotContain(all, s => s.Id == WebWorkspace);

        var local = await GetShops("local");
        Assert.All(local, s => Assert.Equal(DeploymentModes.Local, s.Mode));

        var summary = all.Single(s => s.Id == LocalShop);
        Assert.Equal(2, summary.ActiveDevices);
        Assert.Equal("1.5.0", summary.AppVersion); // newest version among the shop's tills

        async Task<List<ShopSummaryDto>> GetShops(string? filter)
        {
            var url = "/api/admin/desktop/shops" + (filter is null ? "" : $"?filter={filter}");
            var response = await _client.SendAsync(Request(HttpMethod.Get, url, Viewer()));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<List<ShopSummaryDto>>())!;
        }
    }

    [Fact]
    public async Task A_deleted_shop_is_listed_only_under_the_deleted_filter()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Groupes.Single(g => g.Id == LocalShop).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        var all = await Get<List<ShopSummaryDto>>("/api/admin/desktop/shops");
        Assert.DoesNotContain(all, s => s.Id == LocalShop);

        var deleted = await Get<List<ShopSummaryDto>>("/api/admin/desktop/shops?filter=deleted");
        var shop = Assert.Single(deleted);
        Assert.Equal(LocalShop, shop.Id);
        Assert.True(shop.IsDeleted);
        Assert.Equal(0, shop.AttentionLevel);

        var detail = await Get<ShopDetailDto>($"/api/admin/desktop/shops/{LocalShop}");
        Assert.True(detail.Shop.IsDeleted);

        async Task<T> Get<T>(string url)
        {
            var response = await _client.SendAsync(Request(HttpMethod.Get, url, Viewer()));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
    }

    [Fact]
    public async Task Shop_detail_lists_every_device_including_revoked_ones()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{LocalShop}", Viewer()));
        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<ShopDetailDto>())!;

        Assert.Equal(3, detail.Devices.Count);
        Assert.Null(detail.Devices[0].RevokedAt); // active ones first
        Assert.Equal("Panne", detail.Devices[^1].RevokedReason);
        Assert.Equal("owner@shop.bf", detail.Owner!.Email);
    }

    [Fact]
    public async Task Web_workspace_has_no_desktop_detail()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{WebWorkspace}", Viewer()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- What it changes -----------------------------------------------------------------

    [Fact]
    public async Task Removing_a_device_needs_the_manage_permission_and_frees_its_slot()
    {
        var deviceRowId = await DeviceRowId("till-a");
        var url = $"/api/admin/desktop/shops/{LocalShop}/devices/{deviceRowId}?reason=Vol%C3%A9e";

        var asViewer = await _client.SendAsync(Request(HttpMethod.Delete, url, Viewer()));
        Assert.Equal(HttpStatusCode.Forbidden, asViewer.StatusCode);

        var asManager = await _client.SendAsync(Request(HttpMethod.Delete, url, DeviceManager()));
        Assert.Equal(HttpStatusCode.NoContent, asManager.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var device = scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Devices.Single(d => d.Id == deviceRowId);
        Assert.NotNull(device.RevokedAt);
        Assert.Equal("Volée", device.RevokedReason);
    }

    [Fact]
    public async Task Device_of_another_shop_cannot_be_removed_through_this_one()
    {
        var deviceRowId = await DeviceRowId("pos-1"); // belongs to the online shop
        var response = await _client.SendAsync(Request(HttpMethod.Delete,
            $"/api/admin/desktop/shops/{LocalShop}/devices/{deviceRowId}", DeviceManager()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_owner_or_super_admin_changes_the_device_limit()
    {
        HttpRequestMessage Put(string token, int value)
        {
            var request = Request(HttpMethod.Put, $"/api/admin/desktop/shops/{LocalShop}/max-devices", token);
            request.Content = JsonContent.Create(new SetMaxDevicesRequest(value));
            return request;
        }

        // Even an admin holding every listed permission is not enough: this is what shops pay for.
        var asAdmin = await _client.SendAsync(Put(DeviceManager(), 10));
        Assert.Equal(HttpStatusCode.Forbidden, asAdmin.StatusCode);

        var invalid = await _client.SendAsync(Put(SuperAdmin(), 0));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var asSuperAdmin = await _client.SendAsync(Put(SuperAdmin(), 6));
        asSuperAdmin.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var groupe = scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.Single(g => g.Id == LocalShop);
        Assert.Equal(6, groupe.MaxDevices);
    }

    [Fact]
    public async Task Only_owner_or_super_admin_blocks_a_shop_and_a_reason_is_required()
    {
        var url = $"/api/admin/desktop/shops/{LocalShop}/block";
        HttpRequestMessage Put(string token, object body)
        {
            var request = Request(HttpMethod.Put, url, token);
            request.Content = JsonContent.Create(body);
            return request;
        }

        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.SendAsync(Put(DeviceManager(), new { blocked = true, reason = "Impayé" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.SendAsync(Put(SuperAdmin(), new { blocked = true, reason = " " }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.SendAsync(Put(SuperAdmin(), new { blocked = true, reason = "Impayé" }))).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var groupe = scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.Single(g => g.Id == LocalShop);
            Assert.True(groupe.IsBlocked);
            Assert.Equal("Impayé", groupe.BlockReason);
        }

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.SendAsync(Put(SuperAdmin(), new { blocked = false }))).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var groupe = scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.Single(g => g.Id == LocalShop);
            Assert.False(groupe.IsBlocked);
            Assert.Null(groupe.BlockReason);
        }
    }

    [Fact]
    public async Task Database_download_is_for_super_admins_and_needs_a_backup()
    {
        var url = $"/api/admin/desktop/shops/{OnlineShopOffline}/database";
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(Request(HttpMethod.Get, url, Viewer()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Request(HttpMethod.Get, url, SuperAdmin()))).StatusCode);

        var database = "SQLite format 3 " + new string('x', 200);
        using (var packed = new MemoryStream())
        {
            using (var gzip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(System.Text.Encoding.ASCII.GetBytes(database));
            var dir = Path.Combine(_dataDirectory, "backups", OnlineShopOffline, "snapshots");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "snapshot-20261027120000000.db.gz"), packed.ToArray());
        }

        var response = await _client.SendAsync(Request(HttpMethod.Get, url, SuperAdmin()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(database, System.Text.Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Shop_token_cannot_reach_admin_routes()
    {
        // A shop's own JWT (HS256, the host's secret) is a different scheme entirely.
        using var scope = _factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<Lonnii.Api.Features.Auth.TokenService>();
        var (shopToken, _) = tokens.Issue(new User { Email = "caissier@shop.bf" });

        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/overview", shopToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Bringing a removed machine back -----------------------------------------------------

    private Device ReadDevice(string fingerprint)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<LonniiDbContext>().Devices.AsNoTracking().Single(d => d.DeviceId == fingerprint);
    }

    [Fact]
    public async Task A_removed_machine_is_brought_back_within_the_limit()
    {
        var url = $"/api/admin/desktop/shops/{LocalShop}/devices/{await DeviceRowId("till-old")}/restore";

        // Viewing is not managing.
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(Request(HttpMethod.Post, url, Viewer()))).StatusCode);

        // 2/2 in use: no room.
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(Request(HttpMethod.Post, url, DeviceManager()))).StatusCode);
        Assert.NotNull(ReadDevice("till-old").RevokedAt);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Groupes.Single(g => g.Id == LocalShop).MaxDevices = 3;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(Request(HttpMethod.Post, url, DeviceManager()))).StatusCode);
        var device = ReadDevice("till-old");
        Assert.Null(device.RevokedAt);
        Assert.Null(device.RevokedReason);
    }

    [Fact]
    public async Task Unticking_phone_only_can_bring_back_the_computers_it_removed()
    {
        var url = $"/api/admin/desktop/shops/{OnlineShopOffline}/phone-only";
        HttpRequestMessage Put(object body)
        {
            var request = Request(HttpMethod.Put, url, SuperAdmin());
            request.Content = JsonContent.Create(body);
            return request;
        }

        (await _client.SendAsync(Put(new { phoneOnly = true }))).EnsureSuccessStatusCode();
        Assert.Equal(DesktopAdminEndpoints.PhoneOnlyRevokedReason, ReadDevice("pos-1").RevokedReason);

        // While phone-only, a single machine cannot be brought back: it would be refused at once.
        var restore = $"/api/admin/desktop/shops/{OnlineShopOffline}/devices/{await DeviceRowId("pos-1")}/restore";
        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(Request(HttpMethod.Post, restore, DeviceManager()))).StatusCode);

        var response = await _client.SendAsync(Put(new { phoneOnly = false, restoreComputers = true }));
        response.EnsureSuccessStatusCode();

        Assert.Null(ReadDevice("pos-1").RevokedAt);
        Assert.Contains("\"restoredDevices\":1", await response.Content.ReadAsStringAsync());
    }

    // --- Gestion: Lonnii Desktop's own switch -------------------------------------------

    [Fact]
    public async Task Gestion_switched_from_the_desktop_dashboard_leaves_the_web_apps_column_alone()
    {
        // The React dashboard switches groupes.gestion_access for the web app. Turning Gestion off there
        // used to turn it off in the shop's desktop app too: the two must not touch each other.
        bool webBefore;
        using (var before = _factory.Services.CreateScope())
            webBefore = before.ServiceProvider.GetRequiredService<LonniiDbContext>().Groupes.AsNoTracking().Single(g => g.Id == LocalShop).GestionAccess;

        var request = Request(HttpMethod.Put, $"/api/admin/desktop/shops/{LocalShop}/gestion", SuperAdmin());
        request.Content = JsonContent.Create(new { enabled = false });
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var groupe = db.Groupes.AsNoTracking().Single(g => g.Id == LocalShop);
        Assert.False(groupe.DesktopGestionAccess);
        Assert.Equal(webBefore, groupe.GestionAccess);

        var detail = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{LocalShop}", Viewer()));
        Assert.False((await detail.Content.ReadFromJsonAsync<ShopDetailDto>())!.Shop.GestionAccess);
    }

    // --- Sign-ins through our server ----------------------------------------------------

    [Fact]
    public async Task A_phone_signing_in_from_afar_shows_on_the_shops_page_the_connections_page_and_the_overview()
    {
        var log = _factory.Services.GetRequiredService<ShopConnectionLog>();
        log.Record(OnlineShopOffline, new StoredShopConnection(
            ShopConnectionKinds.Remote, DateTime.UtcNow.AddMinutes(-5), "user-1", "awa@shop.bf", "Awa Ouedraogo",
            "phone-1", "Samsung A14", "android", "41.1.1.1", "1.0.11"));
        // The app re-opening its session a minute later is the same visit, not a second one.
        log.Record(OnlineShopOffline, new StoredShopConnection(
            ShopConnectionKinds.Remote, DateTime.UtcNow.AddMinutes(-4), "user-1", "awa@shop.bf", "Awa Ouedraogo",
            "phone-1", "Samsung A14", "android", "41.1.1.1", "1.0.11"));

        var detail = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{OnlineShopOffline}", Viewer()));
        var shop = (await detail.Content.ReadFromJsonAsync<ShopDetailDto>())!;
        var connection = Assert.Single(shop.Connections!);
        Assert.Equal("android", connection.Platform);
        Assert.Equal("Awa Ouedraogo", connection.UserName);
        Assert.Equal(1, shop.Shop.RemoteConnections7d);
        Assert.NotNull(shop.Shop.LastActivityAt);

        var all = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/connections?days=1", Viewer()));
        var rows = (await all.Content.ReadFromJsonAsync<List<AdminConnectionDto>>())!;
        Assert.Equal("Super Marché En Ligne", Assert.Single(rows).ShopName);

        var overview = await Overview();
        Assert.Equal(1, overview.RemoteConnections24h);
        Assert.Equal(1, overview.ShopsConnectedRemotely24h);
        Assert.Single(overview.RecentConnections!);

        var filtered = await _client.SendAsync(Request(HttpMethod.Get, "/api/admin/desktop/shops?filter=remote", Viewer()));
        Assert.Single((await filtered.Content.ReadFromJsonAsync<List<ShopSummaryDto>>())!);
    }

    [Fact]
    public async Task A_check_in_carries_when_the_shops_other_devices_were_last_seen()
    {
        // A phone on the shop's Wi-Fi signs in to the shop's computer, never to us: its host reports
        // when it last saw it, so the dashboard stops showing the day it was added.
        // Later than when the test data was seeded (a device's clock only moves forward), not in the future.
        var seenAt = DateTime.UtcNow.AddMinutes(5);
        seenAt = new DateTime(seenAt.Ticks - seenAt.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var refresh = await _client.PostAsJsonAsync("/api/licence/refresh", new Lonnii.Shared.Contracts.LicenceRefreshRequest(
            LocalShop, "till-a", "1.5.0",
            Devices: [
                new Lonnii.Shared.Contracts.DeviceSeenDto("till-b", seenAt, "1.5.0", "Caisse 2"),
                // Far in the future by the host's clock: not believed.
                new Lonnii.Shared.Contracts.DeviceSeenDto("till-old", DateTime.UtcNow.AddDays(3)),
            ]));
        refresh.EnsureSuccessStatusCode();

        Assert.Equal(seenAt, ReadDevice("till-b").LastSeenAt);
        Assert.True(ReadDevice("till-old").LastSeenAt < DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task An_alert_marked_as_dealt_with_leaves_the_attention_list_until_a_new_one()
    {
        var store = _factory.Services.GetRequiredService<SecurityEventStore>();
        store.AddFromServer(LocalShop, SecurityEventKinds.BinariesModified, "till-a", null, "1.5.0");
        Assert.Contains((await Overview()).Attention, a => a.ShopId == LocalShop && a.Kind == "security");

        var ack = await _client.SendAsync(Request(HttpMethod.Post, $"/api/admin/desktop/shops/{LocalShop}/security/acknowledge", SuperAdmin()));
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);

        Assert.DoesNotContain((await Overview()).Attention, a => a.ShopId == LocalShop && a.Kind == "security");
        var detail = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{LocalShop}", Viewer()));
        var shop = (await detail.Content.ReadFromJsonAsync<ShopDetailDto>())!;
        Assert.NotEmpty(shop.SecurityEvents!);       // still listed on the shop's page
        Assert.NotNull(shop.SecurityAcknowledgedAt);
        Assert.Equal(0, shop.Shop.SecurityEvents);   // no badge

        // A viewer may not dismiss alerts.
        var refused = await _client.SendAsync(Request(HttpMethod.Post, $"/api/admin/desktop/shops/{LocalShop}/security/acknowledge", Viewer()));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Activity_adds_up_sales_on_a_sqlite_database()
    {
        // A shop's cloud copy is SQLite, which cannot sum or sort decimals in a query: this returned a 500.
        // The test database is SQLite too, and a phone-only shop is read from it directly.
        const string phoneShop = "0b6f1d1e-1a7e-4d55-9a51-2f2a7f0c0020";
        await AddShop(new Groupe { Id = phoneShop, Nom = "Boutique Ventes", Mode = DeploymentModes.Online, PhoneOnly = true },
            withComputer: false);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            var vente = new Vente { GroupId = phoneShop, NumeroVente = "V1", MontantTotal = 1500m, ServeurNom = "Ali" };
            vente.Items.Add(new VenteItem { NomProduit = "Coca", Quantite = 3, PrixUnitaire = 500m, PrixTotal = 1500m });
            db.Ventes.Add(vente);
            db.Ventes.Add(new Vente { GroupId = phoneShop, NumeroVente = "V2", MontantTotal = 500m });
            await db.SaveChangesAsync();
        }

        var response = await _client.SendAsync(Request(HttpMethod.Get, $"/api/admin/desktop/shops/{phoneShop}/activity", Viewer()));
        response.EnsureSuccessStatusCode();
        var activity = (await response.Content.ReadFromJsonAsync<ShopActivityDto>())!;

        Assert.Equal("live", activity.Source);
        Assert.Equal(2000m, activity.Today.Amount);
        Assert.Equal(2, activity.Last7Days.Sales);
        Assert.Equal("Coca", Assert.Single(activity.TopProducts).Name);
        Assert.Equal("Ali", activity.Sellers[0].Name);
    }

    private Task<string> DeviceRowId(string fingerprint)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        return Task.FromResult(db.Devices.Single(d => d.DeviceId == fingerprint).Id);
    }
}
