using System.Net.Http.Json;
using Lonnii.Api.Features.Backup;
using Lonnii.Api.Features.Print;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// The host relaying print jobs from our server to its postes. Two in-process apps - our server and
/// the shop's host - because each keeps its own print queue in memory; the host's backup client is
/// wired straight to the server, so the real protocol runs without a network.
/// </summary>
[Collection(BackupGateCollection.Name)]
public class PrintRelayTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _oci = null!;
    private WebApplicationFactory<Program> _host = null!;
    private readonly List<string> _dirs = [];

    private const string AdminEmail = "admin@pharmacie-nord.bf";
    private const string Password = "Motdepasse123";
    private const string GroupId = "e126374f-922c-47c7-a3ac-d36ecdb8b499";
    private const string Device = "machine-1";
    private string _venteId = null!;

    private WebApplicationFactory<Program> App(Action<IServiceCollection>? services = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lonnii-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Lonnii:DataDirectory", dir);
            if (services is not null) b.ConfigureServices(services);
        });
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> app, Action<LonniiDbContext>? more = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var admin = new User { Email = AdminEmail, Password = BCrypt.Net.BCrypt.HashPassword(Password), IsVerified = true };
        db.Users.Add(admin);
        db.Groupes.Add(new Groupe
        {
            Id = GroupId, Nom = "Pharmacie Nord", IdUserAdmin = admin.IdUser,
            Mode = DeploymentModes.Online, MaxDevices = 3, LicenceServerUrl = "http://localhost",
        });
        db.DashboardSubscriptions.Add(new DashboardSubscription
        {
            GroupId = GroupId, GroupName = "Pharmacie Nord", AdminName = "Sasso", Montant = 60_000m,
            Statut = StatutAbonnement.Active,
            ContractStartDate = DateTime.UtcNow.AddMonths(-2), ContractEndDate = DateTime.UtcNow.AddMonths(10),
        });
        more?.Invoke(db);
        await db.SaveChangesAsync();
    }

    public async Task InitializeAsync()
    {
        _oci = App();
        _host = App(s => s.AddHttpClient(CloudBackupClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _oci.Server.CreateHandler()));

        await SeedAsync(_oci);
        await SeedAsync(_host, db =>
        {
            var vente = new Vente
            {
                GroupId = GroupId, NumeroVente = "V-TEST-0001", DateVente = DateTime.UtcNow,
                StatutPaiement = StatutPaiement.Paye, MontantTotal = 1_000m, IdempotencyKey = "phone-key-1",
            };
            _venteId = vente.Id;
            db.Ventes.Add(vente);
        });

        var response = await _oci.CreateClient().PostAsJsonAsync("/api/activation",
            new ActivationRequest(GroupId, AdminEmail, Password, Device, "poste"));
        var token = (await response.Content.ReadFromJsonAsync<ActivationResponse>())!.BackupToken;

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        LicenceGuard.StoreBackupToken(db, GroupId, Device, token);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _oci.DisposeAsync();
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    private async Task PassAsync()
    {
        using var scope = _host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PrintRelay>().PassAsync(GroupId, CancellationToken.None);
    }

    private PrintQueue HostQueue => _host.Services.GetRequiredService<PrintQueue>();
    private PrintQueue OciQueue => _oci.Services.GetRequiredService<PrintQueue>();

    [Fact]
    public async Task A_job_left_on_our_server_prints_on_the_shop_poste_and_its_result_goes_back()
    {
        HostQueue.Seen(GroupId, "till-1", "Caisse 1", isHost: false);

        // The host tells our server its till is open.
        await PassAsync();
        Assert.Equal("till-1", Assert.Single(OciQueue.Open(GroupId)).DeviceId);

        // A phone online sends a job there; the host takes it into its own queue under the same id.
        var job = OciQueue.Add(GroupId, "till-1", _venteId, "ticket", "u1");
        await PassAsync();
        var local = HostQueue.Next(GroupId, "till-1");
        Assert.Equal((job.Id, _venteId), (local!.Id, local.VenteId));
        Assert.Equal("sent", OciQueue.Get(GroupId, job.Id)!.Status);

        // The till prints; the next pass reports it.
        Assert.True(HostQueue.Finish(GroupId, "till-1", job.Id, true, null));
        await PassAsync();
        Assert.Equal("done", OciQueue.Get(GroupId, job.Id)!.Status);
    }

    [Fact]
    public async Task A_sale_made_online_is_found_by_the_key_the_phone_sent_it_with()
    {
        HostQueue.Seen(GroupId, "till-1", "Caisse 1", isHost: false);
        await PassAsync();

        var job = OciQueue.Add(GroupId, "till-1", "", "ticket", "u1", saleKey: "phone-key-1");
        await PassAsync();

        var local = HostQueue.Next(GroupId, "till-1");
        Assert.Equal((job.Id, _venteId), (local!.Id, local.VenteId));
    }

    [Fact]
    public async Task A_sale_the_shop_does_not_have_fails_with_a_reason_instead_of_waiting()
    {
        HostQueue.Seen(GroupId, "till-1", "Caisse 1", isHost: false);
        await PassAsync();

        var job = OciQueue.Add(GroupId, "till-1", "unknown-sale", "ticket", "u1");
        await PassAsync();

        Assert.Null(HostQueue.Next(GroupId, "till-1"));
        var followed = OciQueue.Get(GroupId, job.Id)!;
        Assert.Equal("failed", followed.Status);
        Assert.Contains("Vente introuvable", followed.Message);
    }

    [Fact]
    public async Task A_closed_shop_does_not_call_our_server()
    {
        await PassAsync();
        Assert.Empty(OciQueue.Open(GroupId));
    }
}
