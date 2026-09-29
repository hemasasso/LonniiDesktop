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

    /// <summary>Opens a session with a 10 000 cash + 2 000 mobile float and one sale paid
    /// 5 000 cash and 3 000 mobile: 15 000 cash and 5 000 mobile expected at closing.</summary>
    private async Task<Session> OpenWithMixedSaleAsync()
    {
        var session = await SignUpOwnerAsync();
        await SendAsync(session, HttpMethod.Post, "/api/caisse/ouvrir",
            new OpenCaisseRequest(MontantInitialCash: 10_000m, MontantInitialMobile: 2_000m));

        var saleTime = DateTime.UtcNow;
        await SeedAsync(db =>
        {
            var vente = new Vente
            {
                GroupId = session.GroupId, NumeroVente = "V-TEST-0002", DateVente = saleTime,
                StatutPaiement = StatutPaiement.Paye, MontantTotal = 8_000m, CreatedBy = session.UserId,
            };
            vente.Paiements.Add(new PaiementVente
            {
                VenteId = vente.Id, Montant = 5_000m, ModePaiement = ModePaiement.Cash, DatePaiement = saleTime,
            });
            vente.Paiements.Add(new PaiementVente
            {
                VenteId = vente.Id, Montant = 3_000m, ModePaiement = ModePaiement.MobileMoney, DatePaiement = saleTime,
            });
            db.Ventes.Add(vente);
        });

        return session;
    }

    [Fact]
    public async Task Closing_with_a_mobile_count_puts_both_differences_in_the_ecart()
    {
        var session = await OpenWithMixedSaleAsync();

        // 500 cash missing, 200 mobile extra: -300 overall, each side still readable.
        var closed = (await (await SendAsync(session, HttpMethod.Post, "/api/caisse/fermer",
            new CloseCaisseRequest(MontantFinal: 14_500m, MontantFinalMobile: 5_200m)))
            .Content.ReadFromJsonAsync<CaisseDto>())!;

        Assert.Equal(15_000m, closed.ExpectedCash);
        Assert.Equal(5_000m, closed.ExpectedMobile);
        Assert.Equal(-300m, closed.Ecart);
        Assert.Equal(-500m, closed.EcartCash);
        Assert.Equal(200m, closed.EcartMobile);
        Assert.Equal(20_000m, closed.ExpectedTotal);
        Assert.Equal(19_700m, closed.CountedTotal);

        // "Ajusté" corrects each count by its own share.
        var resolved = (await (await SendAsync(session, HttpMethod.Post, $"/api/caisse/{closed.Id}/resolve-ecart",
            new ResolveEcartRequest(EcartResolutionTypes.Adjusted)))
            .Content.ReadFromJsonAsync<CaisseDto>())!;

        Assert.Equal(0m, resolved.Ecart);
        Assert.Equal(15_000m, resolved.MontantFinal);
        Assert.Equal(5_000m, resolved.MontantFinalMobile);
    }

    [Fact]
    public async Task Closing_with_cash_alone_keeps_a_cash_only_ecart()
    {
        var session = await OpenWithMixedSaleAsync();

        var closed = (await (await SendAsync(session, HttpMethod.Post, "/api/caisse/fermer",
            new CloseCaisseRequest(MontantFinal: 15_000m)))
            .Content.ReadFromJsonAsync<CaisseDto>())!;

        Assert.Equal(0m, closed.Ecart);
        Assert.False(closed.MobileCounted);
        Assert.Equal(15_000m, closed.ExpectedTotal);
    }

    [Fact]
    public async Task A_mobile_retrait_comes_out_of_mobile_not_cash()
    {
        var session = await OpenWithMixedSaleAsync();

        // 5 000 mobile expected before any withdrawal.
        var beforeStatus = (await (await SendAsync(session, HttpMethod.Get, "/api/caisse/status"))
            .Content.ReadFromJsonAsync<CaisseStatusResponse>())!.Caisse!;
        Assert.Equal(5_000m, beforeStatus.ExpectedMobile);
        Assert.Equal(15_000m, beforeStatus.ExpectedCash);

        // Pulling more mobile money than is there is refused - built by hand since SendAsync
        // throws on a non-2xx response.
        using var overdrawnRequest = new HttpRequestMessage(HttpMethod.Post, "/api/caisse/retrait")
        {
            Content = JsonContent.Create(new WithdrawCaisseRequest(Montant: 6_000m, Motif: "Test", ModePaiement: "mobile_money")),
        };
        overdrawnRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        overdrawnRequest.Headers.Add("x-group-session", session.GroupSession);
        var overdrawn = await _client.SendAsync(overdrawnRequest);
        Assert.False(overdrawn.IsSuccessStatusCode);

        // ...but a valid mobile retrait leaves cash untouched.
        var afterRetrait = (await (await SendAsync(session, HttpMethod.Post, "/api/caisse/retrait",
            new WithdrawCaisseRequest(Montant: 2_000m, Motif: "Recharge fournisseur", ModePaiement: "mobile_money")))
            .Content.ReadFromJsonAsync<CaisseDto>())!;

        Assert.Equal(3_000m, afterRetrait.ExpectedMobile);
        Assert.Equal(15_000m, afterRetrait.ExpectedCash);
        Assert.Equal(2_000m, afterRetrait.TotalRetraits);
        Assert.Equal("mobile_money", Assert.Single(afterRetrait.Retraits!).ModePaiement);

        var closed = (await (await SendAsync(session, HttpMethod.Post, "/api/caisse/fermer",
            new CloseCaisseRequest(MontantFinal: 15_000m, MontantFinalMobile: 3_000m)))
            .Content.ReadFromJsonAsync<CaisseDto>())!;

        Assert.Equal(0m, closed.Ecart);
    }
}
