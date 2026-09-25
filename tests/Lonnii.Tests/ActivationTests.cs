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
/// Covers licence activation end to end.
///
/// The refusals matter more than the happy path here. Activation is the whole anti-piracy
/// mechanism - a forged credentials file is stopped because the server has no record of the
/// workspace it invents - so a bug that let an unknown workspace through, or that let a
/// shop exceed the machines it paid for, would not fail loudly. It would just quietly give
/// the product away.
/// </summary>
public class ActivationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

    private const string AdminEmail = "admin@pharmacie-nord.bf";
    private const string AdminPassword = "Motdepasse123";
    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";

    public async Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Lonnii:DataDirectory", _dataDirectory));

        _client = _factory.CreateClient();

        await SeedWorkspaceAsync();
    }

    /// <summary>Creates the workspace a real installation would have been registered with.</summary>
    private async Task SeedWorkspaceAsync(int maxDevices = 2, string mode = DeploymentModes.Local)
    {
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
            Mode = mode,
            MaxDevices = maxDevices,
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        try { Directory.Delete(_dataDirectory, recursive: true); }
        catch (IOException) { }
    }

    private static ActivationRequest Request(string deviceId, string? groupId = null) =>
        new(groupId ?? GroupId, AdminEmail, AdminPassword, deviceId, $"poste-{deviceId}");

    private Task<HttpResponseMessage> ActivateAsync(ActivationRequest request) =>
        _client.PostAsJsonAsync("/api/activation", request);

    [Fact]
    public async Task A_registered_workspace_activates_and_reports_its_settings()
    {
        var response = await ActivateAsync(Request("machine-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ActivationResponse>();
        Assert.NotNull(body);
        Assert.Equal("Pharmacie Nord", body!.GroupName);
        Assert.Equal(2, body.MaxDevices);
        Assert.Equal(1, body.DevicesUsed);
        Assert.False(body.SubscriptionRequired);
    }

    /// <summary>
    /// The core of the scheme: a credentials file someone generated themselves carries a
    /// workspace id we never registered, so there is nothing to activate against.
    /// </summary>
    [Fact]
    public async Task A_workspace_we_never_registered_is_refused()
    {
        var response = await ActivateAsync(Request("machine-1", groupId: Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        var response = await ActivateAsync(
            new ActivationRequest(GroupId, AdminEmail, "mauvais-mot-de-passe", "machine-1"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// An unknown workspace and a wrong password must be indistinguishable. Otherwise
    /// someone holding a forged file could tell when they had guessed a real workspace id.
    /// </summary>
    [Fact]
    public async Task Unknown_workspace_and_wrong_password_look_identical()
    {
        var unknownWorkspace = await ActivateAsync(Request("machine-1", groupId: Guid.NewGuid().ToString()));
        var wrongPassword = await ActivateAsync(
            new ActivationRequest(GroupId, AdminEmail, "mauvais", "machine-1"));

        Assert.Equal(unknownWorkspace.StatusCode, wrongPassword.StatusCode);
        Assert.Equal(
            await unknownWorkspace.Content.ReadAsStringAsync(),
            await wrongPassword.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_machine_limit_holds()
    {
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(Request("machine-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(Request("machine-2"))).StatusCode);

        var third = await ActivateAsync(Request("machine-3"));

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        var error = await third.Content.ReadFromJsonAsync<ApiError>();
        Assert.Contains("Limite de postes", error!.Error);
    }

    /// <summary>
    /// Re-activating a machine that is already bound - a reinstall, or simply a second run -
    /// must not consume another slot, or a shop would exhaust its allowance by reinstalling.
    /// </summary>
    [Fact]
    public async Task Re_activating_the_same_machine_does_not_consume_another_slot()
    {
        await ActivateAsync(Request("machine-1"));
        await ActivateAsync(Request("machine-1"));
        var again = await ActivateAsync(Request("machine-1"));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var body = await again.Content.ReadFromJsonAsync<ActivationResponse>();
        Assert.Equal(1, body!.DevicesUsed);

        // And the freed slot is still available to a genuinely new machine.
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(Request("machine-2"))).StatusCode);
    }

    /// <summary>A replaced till frees its slot without us having to raise the limit.</summary>
    [Fact]
    public async Task A_revoked_machine_frees_its_slot()
    {
        await ActivateAsync(Request("machine-1"));
        await ActivateAsync(Request("machine-2"));
        Assert.Equal(HttpStatusCode.Conflict, (await ActivateAsync(Request("machine-3"))).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            var dead = db.Devices.First(d => d.DeviceId == "machine-1");
            dead.RevokedAt = DateTime.UtcNow;
            dead.RevokedReason = "Poste remplacé";
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(Request("machine-3"))).StatusCode);
    }

    [Fact]
    public async Task A_blocked_workspace_cannot_activate()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            var groupe = db.Groupes.First(g => g.Id == GroupId);
            groupe.IsBlocked = true;
            groupe.BlockReason = "Impayé";
            await db.SaveChangesAsync();
        }

        var response = await ActivateAsync(Request("machine-1"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A soft-deleted workspace is not blocked, so it needs its own check.</summary>
    [Fact]
    public async Task A_deleted_workspace_cannot_activate()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Groupes.First(g => g.Id == GroupId).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await ActivateAsync(Request("machine-1"))).StatusCode);
    }

    /// <summary>
    /// A user who is not in this workspace must not be able to activate against it, or one
    /// shop's admin could bring up an installation pointed at another's data.
    /// </summary>
    [Fact]
    public async Task An_account_from_another_workspace_is_refused()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Users.Add(new User
            {
                Email = "autre@boutique.bf",
                Password = BCrypt.Net.BCrypt.HashPassword(AdminPassword),
                IsVerified = true,
            });
            await db.SaveChangesAsync();
        }

        var response = await ActivateAsync(
            new ActivationRequest(GroupId, "autre@boutique.bf", AdminPassword, "machine-1"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Local_mode_never_asks_for_a_subscription()
    {
        var body = await (await ActivateAsync(Request("machine-1")))
            .Content.ReadFromJsonAsync<ActivationResponse>();

        Assert.False(body!.SubscriptionRequired);
        Assert.Null(body.SubscriptionStatus);
    }

    [Fact]
    public async Task Online_mode_without_a_subscription_is_refused()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Groupes.First(g => g.Id == GroupId).Mode = DeploymentModes.Online;
            await db.SaveChangesAsync();
        }

        var response = await ActivateAsync(Request("machine-1"));

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [Fact]
    public async Task Online_mode_with_a_live_subscription_activates()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Groupes.First(g => g.Id == GroupId).Mode = DeploymentModes.Online;
            db.DashboardSubscriptions.Add(new DashboardSubscription
            {
                GroupId = GroupId,
                GroupName = "Pharmacie Nord",
                AdminName = "Sasso",
                Montant = 20_000m,
                Statut = StatutAbonnement.Active,
                ContractStartDate = DateTime.UtcNow.AddMonths(-1),
                ContractEndDate = DateTime.UtcNow.AddMonths(11),
            });
            await db.SaveChangesAsync();
        }

        var response = await ActivateAsync(Request("machine-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ActivationResponse>();
        Assert.True(body!.SubscriptionRequired);
        Assert.Equal(StatutAbonnement.Active, body.SubscriptionStatus);
    }

    [Fact]
    public async Task An_expired_subscription_is_refused()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
            db.Groupes.First(g => g.Id == GroupId).Mode = DeploymentModes.Online;
            db.DashboardSubscriptions.Add(new DashboardSubscription
            {
                GroupId = GroupId,
                GroupName = "Pharmacie Nord",
                AdminName = "Sasso",
                Montant = 20_000m,
                Statut = StatutAbonnement.Active,
                ContractStartDate = DateTime.UtcNow.AddYears(-2),
                ContractEndDate = DateTime.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.PaymentRequired, (await ActivateAsync(Request("machine-1"))).StatusCode);
    }
}
