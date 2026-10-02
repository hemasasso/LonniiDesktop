using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// "Paiement Groupé": several unpaid factures settled in one request, all or nothing, with the
/// change/avoir rules of Lonnii Business's <c>POST /ventes/groupe-payment</c>.
/// </summary>
public class GroupePaymentTests : IAsyncLifetime
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

    private Task<HttpResponseMessage> SendAsync(Session session, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        if (body is not null) request.Content = JsonContent.Create(body);
        return _client.SendAsync(request);
    }

    private async Task<T> Db<T>(Func<LonniiDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<LonniiDbContext>());
    }

    /// <summary>An unpaid sale of <paramref name="total"/>, with <paramref name="alreadyPaid"/> already received.</summary>
    private async Task<string> SeedFactureAsync(Session session, string numero, decimal total, decimal alreadyPaid = 0)
    {
        var id = Guid.NewGuid().ToString();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        var vente = new Vente
        {
            Id = id, GroupId = session.GroupId, NumeroVente = numero, MontantTotal = total,
            StatutPaiement = alreadyPaid > 0 ? StatutPaiement.Partiel : StatutPaiement.EnAttente,
            CreatedBy = session.UserId,
        };
        if (alreadyPaid > 0)
            vente.Paiements.Add(new PaiementVente { VenteId = id, Montant = alreadyPaid, ModePaiement = ModePaiement.Cash });
        db.Ventes.Add(vente);
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Pays_the_remaining_balance_of_every_facture_and_records_the_receipt()
    {
        var session = await SignUpOwnerAsync();
        var a = await SeedFactureAsync(session, "V-G-0001", 10_000m);
        var b = await SeedFactureAsync(session, "V-G-0002", 6_000m, alreadyPaid: 2_500m);

        var response = await SendAsync(session, HttpMethod.Post, "/api/ventes/groupe-payments",
            new GroupePaiementRequest([a, b], ModePaiement.Cash, "Awa"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payment = (await response.Content.ReadFromJsonAsync<GroupePaiementDto>())!;
        Assert.Equal(13_500m, payment.Total);
        Assert.Equal(13_500m, payment.MontantPaye);
        Assert.Equal(0m, payment.AvoirAmount);
        Assert.Equal("Awa", payment.ClientName);
        Assert.Equal([10_000m, 3_500m], payment.Factures.Select(f => f.Montant).Order().Reverse().ToArray());

        var ventes = await Db(db => db.Ventes.Include(v => v.Paiements).ToListAsync());
        Assert.All(ventes, v =>
        {
            Assert.Equal(StatutPaiement.Paye, v.StatutPaiement);
            Assert.Equal(v.MontantTotal, v.MontantPaye);
        });
        Assert.Equal(1, await Db(db => db.GroupePayments.CountAsync()));
    }

    [Fact]
    public async Task Refuses_the_whole_payment_when_one_facture_is_already_paid()
    {
        var session = await SignUpOwnerAsync();
        var open = await SeedFactureAsync(session, "V-G-0003", 4_000m);
        var paid = await SeedFactureAsync(session, "V-G-0004", 3_000m, alreadyPaid: 3_000m);
        await Db(async db =>
        {
            (await db.Ventes.FindAsync(paid))!.StatutPaiement = StatutPaiement.Paye;
            return await db.SaveChangesAsync();
        });

        var response = await SendAsync(session, HttpMethod.Post, "/api/ventes/groupe-payments",
            new GroupePaiementRequest([open, paid], ModePaiement.Cash));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // Nothing was settled, not even the facture that was payable.
        Assert.Equal(StatutPaiement.EnAttente, (await Db(db => db.Ventes.FindAsync(open).AsTask()))!.StatutPaiement);
        Assert.Equal(0, await Db(db => db.GroupePayments.CountAsync()));
    }

    [Fact]
    public async Task Refuses_a_received_amount_below_the_total()
    {
        var session = await SignUpOwnerAsync();
        var id = await SeedFactureAsync(session, "V-G-0005", 5_000m);

        var response = await SendAsync(session, HttpMethod.Post, "/api/ventes/groupe-payments",
            new GroupePaiementRequest([id], ModePaiement.Cash, MontantRecu: 4_000m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(StatutPaiement.EnAttente, (await Db(db => db.Ventes.FindAsync(id).AsTask()))!.StatutPaiement);
    }

    [Fact]
    public async Task Change_not_handed_back_becomes_an_avoir_that_can_be_settled_once()
    {
        var session = await SignUpOwnerAsync();
        var id = await SeedFactureAsync(session, "V-G-0006", 7_000m);

        // 10 000 handed over for 7 000: 3 000 of change, 1 000 given now, so 2 000 is kept as an avoir.
        var response = await SendAsync(session, HttpMethod.Post, "/api/ventes/groupe-payments",
            new GroupePaiementRequest([id], ModePaiement.Cash, MontantRecu: 10_000m, MontantRemis: 1_000m, MonnaieEnAvoir: true));
        var payment = (await response.Content.ReadFromJsonAsync<GroupePaiementDto>())!;

        Assert.Equal(7_000m, payment.Total);
        Assert.Equal(9_000m, payment.MontantPaye);
        Assert.Equal(2_000m, payment.AvoirAmount);
        Assert.Equal(1_000m, payment.PartialChangeGiven);

        var solder = await SendAsync(session, HttpMethod.Put, $"/api/ventes/groupe-payments/{payment.Id}/solder-avoir");
        Assert.Equal(HttpStatusCode.OK, solder.StatusCode);
        var again = await SendAsync(session, HttpMethod.Put, $"/api/ventes/groupe-payments/{payment.Id}/solder-avoir");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        var history = (await (await SendAsync(session, HttpMethod.Get, "/api/ventes/groupe-payments?page=1&limit=10"))
            .Content.ReadFromJsonAsync<GroupePaiementsResponse>())!;
        var listed = Assert.Single(history.Payments);
        Assert.True(listed.IsAvoirSolded);
        Assert.Equal(7_000m, Assert.Single(listed.Factures).MontantOriginal);
    }

    [Fact]
    public async Task Change_is_not_recorded_when_the_till_gave_it_all_back()
    {
        var session = await SignUpOwnerAsync();
        var id = await SeedFactureAsync(session, "V-G-0007", 7_000m);

        var response = await SendAsync(session, HttpMethod.Post, "/api/ventes/groupe-payments",
            new GroupePaiementRequest([id], ModePaiement.Cash, MontantRecu: 10_000m));
        var payment = (await response.Content.ReadFromJsonAsync<GroupePaiementDto>())!;

        Assert.Equal(7_000m, payment.MontantPaye);
        Assert.Equal(0m, payment.AvoirAmount);
    }
}
