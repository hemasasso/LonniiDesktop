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

    private Task<string> DeviceRowId(string fingerprint)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        return Task.FromResult(db.Devices.Single(d => d.DeviceId == fingerprint).Id);
    }
}
