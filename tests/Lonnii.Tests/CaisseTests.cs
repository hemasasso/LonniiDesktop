using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// Covers the caisse session math in <c>CaisseEndpoints.ComputeLiveStatsAsync</c>. Regression
/// for a bug where a manual retrait (<c>RetraitAsync</c>) - cash pulled from the drawer for
/// something unrelated to a sale, e.g. buying food - was folded into PaiementCash/TotalEncaisse
/// the same way an avoir refund already is. The session's final "Espèces attendues" total came
/// out right either way (addition is associative), but "Total encaissé" then read short of its
/// own "Chiffre d'affaires" for an otherwise fully-paid cash sale, looking like a shortfall that
/// was never there.
/// </summary>
public class CaisseTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dataDirectory = null!;

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
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("patron@lonnii.test", "MotDePasse123", "patron"));
        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("patron@lonnii.test", "MotDePasse123"))).Content.ReadFromJsonAsync<LoginResponse>())!;

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

    private async Task<HttpResponseMessage> SendAsync(Session session, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        if (body is not null) request.Content = JsonContent.Create(body);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task SeedAsync(Action<LonniiDbContext> seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Manual_retrait_reduces_expected_cash_without_touching_encaissements()
    {
        var session = await SignUpOwnerAsync();

        var opened = (await (await SendAsync(session, HttpMethod.Post, "/api/caisse/ouvrir",
            new OpenCaisseRequest(MontantInitialCash: 10_000m, MontantInitialMobile: 2_000m)))
            .Content.ReadFromJsonAsync<CaisseDto>())!;

        // A single sale, fully paid in cash - nothing here should ever make "Total encaissé"
        // disagree with "Chiffre d'affaires".
        var saleTime = DateTime.UtcNow;
        await SeedAsync(db =>
        {
            var vente = new Vente
            {
                GroupId = session.GroupId, NumeroVente = "V-TEST-0001", DateVente = saleTime,
                StatutPaiement = StatutPaiement.Paye, MontantTotal = 14_000m, CreatedBy = session.UserId,
            };
            vente.Paiements.Add(new PaiementVente
            {
                VenteId = vente.Id, Montant = 14_000m, ModePaiement = ModePaiement.Cash, DatePaiement = saleTime,
            });
            db.Ventes.Add(vente);
        });

        // Cash out for something unrelated to the sale.
        await SendAsync(session, HttpMethod.Post, "/api/caisse/retrait",
            new WithdrawCaisseRequest(Montant: 1_000m, Motif: "Nourriture"));

        var status = (await (await SendAsync(session, HttpMethod.Get, "/api/caisse/status"))
            .Content.ReadFromJsonAsync<CaisseStatusResponse>())!.Caisse!;

        Assert.Equal(14_000m, status.TotalChiffreAffaires);
        // The bug: this used to read 13 000 (14 000 sale minus the 1 000 retrait), making the
        // sale look 1 000 short of paid in full.
        Assert.Equal(14_000m, status.PaiementCash);
        Assert.Equal(14_000m, status.TotalEncaisse);
        Assert.Equal(1_000m, status.TotalRetraits);

        // The retrait must still come off what the drawer is expected to hold.
        var expectedCash = status.MontantInitialCash + status.PaiementCash - status.TotalRetraits;
        Assert.Equal(23_000m, expectedCash);

        var closed = (await (await SendAsync(session, HttpMethod.Post, "/api/caisse/fermer",
            new CloseCaisseRequest(MontantFinal: 23_000m)))
            .Content.ReadFromJsonAsync<CaisseDto>())!;

        Assert.Equal(0m, closed.Ecart);
        Assert.Equal(14_000m, closed.TotalEncaisse);
    }
}
