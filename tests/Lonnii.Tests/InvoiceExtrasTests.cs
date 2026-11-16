using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lonnii.Tests;

/// <summary>
/// Prélèvement, effort de guerre, consigne, livraison and fractions of a unit: the server
/// charges each exactly as the till shows it, prints-ready on the sale, and a fraction sale
/// opens a unit without ever making the stock a decimal.
/// </summary>
public class InvoiceExtrasTests : IAsyncLifetime
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

    private sealed record Session(string Token, string GroupSession);

    private async Task<Session> SignUpOwnerAsync()
    {
        await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("patron@lonnii.test", "MotDePasse123", "patron"));
        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("patron@lonnii.test", "MotDePasse123"))).Content.ReadFromJsonAsync<LoginResponse>())!;

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/groupes")
        {
            Content = JsonContent.Create(new CreateGroupeRequest("Boutique")),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var groupe = (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<GroupeDto>())!;

        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupe.Id}/session");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = (await (await _client.SendAsync(open)).Content.ReadFromJsonAsync<GroupSessionResponse>())!;

        return new Session(login.AccessToken, scoped.SessionToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Session session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        return await _client.SendAsync(request);
    }

    private async Task<ProductDto> CreateProductAsync(Session owner, SaveProductRequest request)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/stock/products", owner, request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private async Task SaveSettingsAsync(Session owner, decimal? prelevement = null, bool? livraison = null,
        bool? fractions = null, decimal? tvaRate = null, string? tvaMode = null)
    {
        var current = (await (await SendAsync(HttpMethod.Get, "/api/parametres/recu", owner))
            .Content.ReadFromJsonAsync<ReceiptSettingsDto>())!;
        var request = new UpdateReceiptSettingsRequest(
            current.CompanyName, current.NoteUnderQr, current.ReceiptTitle, current.FactureTitle,
            current.FactureNoticeTitle, current.FactureNoticeText, current.FactureFooterText, current.ReceiptFooterText,
            current.SellerLabel, current.AvoirNoticeTitle, current.AvoirNoticeText, current.FontFamily, current.FontSize,
            current.ReceiptTitleFontSize, current.FactureTitleFontSize,
            TvaRate: tvaRate ?? current.TvaRate, TvaMode: tvaMode ?? current.TvaMode,
            PrelevementRate: prelevement, LivraisonEnabled: livraison, FractionsEnabled: fractions,
            EffortGuerreEnabled: true);
        var response = await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner, request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<ProductDto> GetProductAsync(Session owner, string id) =>
        (await (await SendAsync(HttpMethod.Get, $"/api/stock/products/{id}", owner))
            .Content.ReadFromJsonAsync<ProductDto>())!;

    [Fact]
    public async Task Charges_AreAddedAfterTheTva_InTheRightAmounts()
    {
        var owner = await SignUpOwnerAsync();
        // HT prices with 18 % TVA added, and a 2 % prélèvement on the HT amount.
        await SaveSettingsAsync(owner, prelevement: 2, livraison: true, tvaRate: 18, tvaMode: TvaModes.Ajoutee);

        var biere = await CreateProductAsync(owner, new SaveProductRequest(
            "Bière", 1000, Quantity: 50, PrixFixe: true,
            EffortGuerre: 25, EmballageRecuperable: true, ConsigneMontant: 100));

        var response = await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(biere.Id, 4)], "cash", 100000, AvecConsigne: true, Livraison: 500));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var vente = (await response.Content.ReadFromJsonAsync<VenteDto>())!;

        // 4 000 HT + 720 TVA + 80 prélèvement + 100 effort + 400 consigne + 500 livraison.
        Assert.Equal(720m, vente.TvaAmount);
        Assert.Equal(80m, vente.PrelevementAmount);
        Assert.Equal(2m, vente.PrelevementRate);
        Assert.Equal(100m, vente.EffortGuerreAmount);
        Assert.Equal(400m, vente.ConsigneAmount);
        Assert.Equal(500m, vente.LivraisonAmount);
        Assert.Equal(5800m, vente.MontantTotal);
    }

    [Fact]
    public async Task Empties_beyond_the_sale_first_pay_off_old_ones_and_leave_no_false_credit()
    {
        var owner = await SignUpOwnerAsync();
        var current = (await (await SendAsync(HttpMethod.Get, "/api/parametres/recu", owner))
            .Content.ReadFromJsonAsync<ReceiptSettingsDto>())!;
        var settings = await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner, new UpdateReceiptSettingsRequest(
            current.CompanyName, current.NoteUnderQr, current.ReceiptTitle, current.FactureTitle,
            current.FactureNoticeTitle, current.FactureNoticeText, current.FactureFooterText, current.ReceiptFooterText,
            current.SellerLabel, current.AvoirNoticeTitle, current.AvoirNoticeText, current.FontFamily, current.FontSize,
            current.ReceiptTitleFontSize, current.FactureTitleFontSize, EmballagesSuivi: true));
        Assert.True(settings.IsSuccessStatusCode, await settings.Content.ReadAsStringAsync());

        var biere = await CreateProductAsync(owner, new SaveProductRequest(
            "Brakina", 1000, Quantity: 50, PrixFixe: true, EmballageRecuperable: true));

        // First visit: takes 3, brings nothing back - owes 3.
        var first = await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(biere.Id, 3)], "cash", 100000, ClientNom: "Sasso Soma"));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        // Second visit: takes 3 more and brings back all 6 - the 3 owed and today's 3.
        var second = await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(biere.Id, 3)], "cash", 100000, ClientNom: "Sasso Soma",
            Retours: [new EmballageRetourLigne(biere.Id, 6)]));
        Assert.True(second.IsSuccessStatusCode, await second.Content.ReadAsStringAsync());
        var vente = (await second.Content.ReadFromJsonAsync<VenteDto>())!;

        Assert.Null(vente.EmballagesCreditRestant); // nothing left over: no credit
        Assert.Equal(3m, vente.EmballagesDusAvant); // the facture knows the client owed 3 before
        var soldes = await (await SendAsync(HttpMethod.Get, "/api/ventes/clients/consignes?nom=Sasso%20Soma", owner))
            .Content.ReadFromJsonAsync<List<ConsigneSoldeDto>>();
        Assert.Empty(soldes!); // and nothing owed
    }

    [Fact]
    public async Task Emballages_tab_counts_pleins_vides_and_chez_les_clients()
    {
        var owner = await SignUpOwnerAsync();
        (await SendAsync(HttpMethod.Post, "/api/caisse/ouvrir", owner, new OpenCaisseRequest(MontantInitialCash: 10_000m))).EnsureSuccessStatusCode();
        var biere = await CreateProductAsync(owner, new SaveProductRequest(
            "Brakina", 1000, Quantity: 50, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "caisse"));

        // Shop counts 20 empties, client A takes 5 and brings 2 back, client B takes 1 and brings 3 (2 in advance).
        var inv = await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(biere.Id, EmballageMouvementTypes.Inventaire, 20));
        Assert.True(inv.IsSuccessStatusCode, await inv.Content.ReadAsStringAsync());
        (await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(biere.Id, 5)], "cash", 100000, ClientNom: "Client A"))).EnsureSuccessStatusCode();
        (await SendAsync(HttpMethod.Post, "/api/ventes/clients/emballages-retours", owner,
            new EmballageRetourRequest("Client A", null, null, 2, [new EmballageRetourLigne(biere.Id, 2)], "cash"))).EnsureSuccessStatusCode();

        // 6 go back to the supplier, who pays 1 500 back into the caisse; 1 broken.
        var retour = await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(biere.Id, EmballageMouvementTypes.RetourFournisseur, 6, 1500, "cash"));
        Assert.True(retour.IsSuccessStatusCode, await retour.Content.ReadAsStringAsync());
        (await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(biere.Id, EmballageMouvementTypes.Casse, 1))).EnsureSuccessStatusCode();
        // More than in the shop is refused.
        var tooMany = await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(biere.Id, EmballageMouvementTypes.Casse, 100));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        var rows = await (await SendAsync(HttpMethod.Get, "/api/stock/emballages", owner)).Content.ReadFromJsonAsync<List<EmballageStockDto>>();
        var row = Assert.Single(rows!);
        Assert.Equal(45m, row.Pleins);      // 50 - 5 sold
        Assert.Equal(15m, row.Vides);       // 20 + 2 back - 6 supplier - 1 broken
        Assert.Equal(3m, row.ChezClients);  // 5 - 2
        Assert.Equal(63m, row.Total);
        Assert.Equal("caisse", row.Unite);

        // The clients list adds up to the "Chez les clients" total, with the product and its unit.
        var holders = await (await SendAsync(HttpMethod.Get, "/api/stock/emballages/clients", owner)).Content.ReadFromJsonAsync<List<EmballageClientDto>>();
        var clientA = Assert.Single(holders!);
        Assert.Equal("Client A", clientA.ClientNom);
        Assert.Equal(3m, clientA.TotalDus);
        var ligne = Assert.Single(clientA.Lignes);
        Assert.Equal("Brakina", ligne.NomProduit);
        Assert.Equal("caisse", ligne.Unite);

        // Inventaire sets the count, whatever it was.
        var recount = await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(biere.Id, EmballageMouvementTypes.Inventaire, 12.5m));
        Assert.Equal(12.5m, (await recount.Content.ReadFromJsonAsync<EmballageStockDto>())!.Vides);

        var history = await (await SendAsync(HttpMethod.Get, "/api/stock/emballages/mouvements", owner)).Content.ReadFromJsonAsync<List<EmballageMouvementDto>>();
        Assert.Contains(history!, m => m.Type == "retour_client" && m.Quantite == 2 && m.ClientNom == "Client A");
        Assert.Contains(history!, m => m.Type == EmballageMouvementTypes.RetourFournisseur && m.Quantite == -6 && m.Montant == 1500);

        // The period's flux: today has everything, an old period nothing.
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var flux = Assert.Single((await (await SendAsync(HttpMethod.Get,
            $"/api/stock/emballages/flux?dateDebut={today}&dateFin={today}&tzOffsetMinutes=0", owner)).Content.ReadFromJsonAsync<List<EmballageFluxDto>>())!);
        Assert.Equal(5m, flux.PrisClients);
        Assert.Equal(2m, flux.RapportesClients);
        Assert.Equal(6m, flux.RetourFournisseur);
        Assert.Equal(1m, flux.Casse);
        Assert.Equal(17.5m, flux.Inventaire); // 20 counted at first, then 12.5 where 15 were expected
        Assert.Equal(1500m, flux.ConsigneFournisseur);
        var old = Assert.Single((await (await SendAsync(HttpMethod.Get,
            "/api/stock/emballages/flux?dateDebut=2020-01-01&dateFin=2020-01-31&tzOffsetMinutes=0", owner)).Content.ReadFromJsonAsync<List<EmballageFluxDto>>())!);
        Assert.Equal(0m, old.PrisClients + old.RapportesClients + old.RetourFournisseur + old.Casse);
        var oldHistory = await (await SendAsync(HttpMethod.Get,
            "/api/stock/emballages/mouvements?dateDebut=2020-01-01&dateFin=2020-01-31&tzOffsetMinutes=0", owner)).Content.ReadFromJsonAsync<List<EmballageMouvementDto>>();
        Assert.Empty(oldHistory!);

        // The supplier's consigne is in the drawer.
        var caisse = await (await SendAsync(HttpMethod.Get, "/api/caisse/status", owner)).Content.ReadAsStringAsync();
        Assert.Contains("\"consigneEncaissee\":1500", caisse);
    }

    [Fact]
    public async Task Interchangeable_crates_share_one_count_per_emballage_type()
    {
        var owner = await SignUpOwnerAsync();
        var brafaso = await CreateProductAsync(owner, new SaveProductRequest(
            "Brafaso", 1000, Quantity: 30, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "caisse", EmballageContenance: 12));
        var brafort = await CreateProductAsync(owner, new SaveProductRequest(
            "Brafort", 1000, Quantity: 20, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "caisse", EmballageContenance: 12));
        var grand = await CreateProductAsync(owner, new SaveProductRequest(
            "Brafaso grand", 1500, Quantity: 5, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "caisse", EmballageContenance: 16));
        var eau = await CreateProductAsync(owner, new SaveProductRequest("Eau", 300, Quantity: 10, PrixFixe: true));

        // Counted apart before grouping: 4 Brafaso empties, 6 Brafort.
        (await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(brafaso.Id, EmballageMouvementTypes.Inventaire, 4))).EnsureSuccessStatusCode();
        (await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(brafort.Id, EmballageMouvementTypes.Inventaire, 6))).EnsureSuccessStatusCode();
        // Client A takes 3 Brafaso and 1 Brafort, brings 1 Brafaso back.
        (await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(brafaso.Id, 3), new CartItemRequest(brafort.Id, 1)], "cash", 100000, ClientNom: "Client A"))).EnsureSuccessStatusCode();
        (await SendAsync(HttpMethod.Post, "/api/ventes/clients/emballages-retours", owner,
            new EmballageRetourRequest("Client A", null, null, 1, [new EmballageRetourLigne(brafaso.Id, 1)], "cash"))).EnsureSuccessStatusCode();
        Assert.Equal(3, (await (await SendAsync(HttpMethod.Get, "/api/stock/emballages", owner)).Content.ReadFromJsonAsync<List<EmballageStockDto>>())!.Count);

        // A product without a returnable emballage can't join, nor a crate of another size (16 is
        // not 12); the name must be free.
        var refused = await SendAsync(HttpMethod.Post, "/api/stock/emballages/types", owner,
            new SaveEmballageTypeRequest("Caisse Brafaso/Brafort", [brafaso.Id, eau.Id]));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var otherSize = await SendAsync(HttpMethod.Post, "/api/stock/emballages/types", owner,
            new SaveEmballageTypeRequest("Caisse Brafaso/Brafort", [brafaso.Id, brafort.Id, grand.Id]));
        Assert.Equal(HttpStatusCode.BadRequest, otherSize.StatusCode);
        Assert.Contains("même contenance", await otherSize.Content.ReadAsStringAsync());
        var created = await SendAsync(HttpMethod.Post, "/api/stock/emballages/types", owner,
            new SaveEmballageTypeRequest("Caisse Brafaso/Brafort", [brafaso.Id, brafort.Id]));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var type = (await created.Content.ReadFromJsonAsync<EmballageTypeDto>())!;
        Assert.Equal(2, type.ProductIds.Count);
        var duplicate = await SendAsync(HttpMethod.Post, "/api/stock/emballages/types", owner,
            new SaveEmballageTypeRequest("caisse brafaso/brafort", []));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        // One row for both: everything already recorded is regrouped.
        var row = (await (await SendAsync(HttpMethod.Get, "/api/stock/emballages", owner)).Content.ReadFromJsonAsync<List<EmballageStockDto>>())!
            .Single(r => r.TypeId is not null);
        Assert.Equal(type.Id, row.ProductId);
        Assert.Equal(type.Id, row.TypeId);
        Assert.Equal("Caisse Brafaso/Brafort", row.NomProduit);
        Assert.Equal("caisse", row.Unite);
        Assert.Equal(46m, row.Pleins);      // 27 + 19
        Assert.Equal(11m, row.Vides);       // 4 + 6 + 1 brought back
        Assert.Equal(3m, row.ChezClients);  // 2 Brafaso + 1 Brafort
        Assert.Equal(["Brafaso", "Brafort"], row.Produits!.Select(p => p.NomProduit));

        var clientA = Assert.Single((await (await SendAsync(HttpMethod.Get, "/api/stock/emballages/clients", owner)).Content.ReadFromJsonAsync<List<EmballageClientDto>>())!);
        var ligne = Assert.Single(clientA.Lignes);
        Assert.Equal(3m, ligne.Dus);
        Assert.Equal("Caisse Brafaso/Brafort", ligne.NomProduit);

        // A casse recorded on one of its products counts for the type.
        var casse = await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(brafort.Id, EmballageMouvementTypes.Casse, 2));
        Assert.Equal(9m, (await casse.Content.ReadFromJsonAsync<EmballageStockDto>())!.Vides);
        var history = (await (await SendAsync(HttpMethod.Get, $"/api/stock/emballages/mouvements?productId={brafaso.Id}", owner))
            .Content.ReadFromJsonAsync<List<EmballageMouvementDto>>())!;
        Assert.All(history, m => Assert.Equal(type.Id, m.ProductId));
        Assert.Contains(history, m => m.Type == EmballageMouvementTypes.Casse && m.Quantite == -2);
        Assert.Contains(history, m => m.Type == "retour_client" && m.NomProduit == "Brafaso");
        Assert.Equal(4, history.Count); // 2 inventaires, the client's return, the casse
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var flux = (await (await SendAsync(HttpMethod.Get,
            $"/api/stock/emballages/flux?dateDebut={today}&dateFin={today}&tzOffsetMinutes=0", owner)).Content.ReadFromJsonAsync<List<EmballageFluxDto>>())!
            .Single(f => f.ProductId == type.Id);
        Assert.Equal(4m, flux.PrisClients);
        Assert.Equal(2m, flux.Casse);

        // Ventes: the client owes the type as one line, and may bring back any of its crates -
        // 2 Brafort crates although they owed 2 Brafaso and 1 Brafort.
        var soldes = (await (await SendAsync(HttpMethod.Get, "/api/ventes/clients/consignes?nom=Client%20A", owner))
            .Content.ReadFromJsonAsync<List<ConsigneSoldeDto>>())!;
        var solde = Assert.Single(soldes);
        Assert.Equal(type.Id, solde.ProductId);
        Assert.Equal(3m, solde.Dus);
        Assert.True(solde.Covers(brafaso.Id) && solde.Covers(brafort.Id));
        Assert.Equal("Brafaso, Brafort", solde.Produits);
        var brought = await SendAsync(HttpMethod.Post, "/api/ventes/clients/emballages-retours", owner,
            new EmballageRetourRequest("Client A", null, null, 2, [new EmballageRetourLigne(brafort.Id, 2)], "cash"));
        Assert.True(brought.IsSuccessStatusCode, await brought.Content.ReadAsStringAsync());
        var after = (await (await SendAsync(HttpMethod.Get, "/api/ventes/clients/consignes?nom=Client%20A", owner))
            .Content.ReadFromJsonAsync<List<ConsigneSoldeDto>>())!;
        Assert.Equal(1m, Assert.Single(after).Dus);
        // More than owed on the type is still refused.
        var tooMuch = await SendAsync(HttpMethod.Post, "/api/ventes/clients/emballages-retours", owner,
            new EmballageRetourRequest("Client A", null, null, 2, [new EmballageRetourLigne(type.Id, 2)], "cash"));
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);

        // A product in the type can't change to another crate size.
        var resize = await SendAsync(HttpMethod.Put, $"/api/stock/products/{brafort.Id}", owner, new SaveProductRequest(
            "Brafort", 1000, Quantity: 19, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "caisse", EmballageContenance: 16));
        Assert.Equal(HttpStatusCode.BadRequest, resize.StatusCode);

        // Taking Brafort out puts it back on its own; deleting the type splits the rest.
        (await SendAsync(HttpMethod.Put, $"/api/stock/emballages/types/{type.Id}", owner,
            new SaveEmballageTypeRequest("Caisse Brafaso", [brafaso.Id]))).EnsureSuccessStatusCode();
        var split = (await (await SendAsync(HttpMethod.Get, "/api/stock/emballages", owner)).Content.ReadFromJsonAsync<List<EmballageStockDto>>())!
            .Where(r => r.ProductId != grand.Id).ToList();
        Assert.Equal(["Brafort", "Caisse Brafaso"], split.Select(r => r.NomProduit));
        Assert.Equal(8m, split[0].Vides);   // 6 counted + the 2 crates brought back as Brafort; the casse stays with the type
        Assert.Equal(3m, split[1].Vides);   // 4 + 1 - 2 broken
        (await SendAsync(HttpMethod.Delete, $"/api/stock/emballages/types/{type.Id}", owner)).EnsureSuccessStatusCode();
        var apart = (await (await SendAsync(HttpMethod.Get, "/api/stock/emballages", owner)).Content.ReadFromJsonAsync<List<EmballageStockDto>>())!
            .Where(r => r.ProductId != grand.Id).ToList();
        Assert.Equal(["Brafaso", "Brafort"], apart.Select(r => r.NomProduit));
        Assert.All(apart, r => Assert.Null(r.TypeId));
        Assert.Empty((await (await SendAsync(HttpMethod.Get, "/api/stock/emballages/types", owner)).Content.ReadFromJsonAsync<List<EmballageTypeDto>>())!);
    }

    [Fact]
    public async Task Crates_received_from_a_supplier_are_tracked_against_what_is_returned_to_them()
    {
        var owner = await SignUpOwnerAsync();
        var castel = await CreateProductAsync(owner, new SaveProductRequest(
            "Castel", 1000, Quantity: 10, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "casier"));
        var brakina = (await (await SendAsync(HttpMethod.Post, "/api/stock/suppliers", owner, new SaveSupplierRequest("Brakina")))
            .Content.ReadFromJsonAsync<SupplierDto>())!;
        (await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(castel.Id, EmballageMouvementTypes.Inventaire, 6))).EnsureSuccessStatusCode();

        // The supplier is required, and full crates in do not touch the empties.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(castel.Id, EmballageMouvementTypes.RecuFournisseur, 20))).StatusCode);
        var recu = await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(castel.Id, EmballageMouvementTypes.RecuFournisseur, 20, SupplierId: brakina.Id));
        Assert.True(recu.IsSuccessStatusCode, await recu.Content.ReadAsStringAsync());
        Assert.Equal(6m, (await recu.Content.ReadFromJsonAsync<EmballageStockDto>())!.Vides);
        (await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", owner,
            new EmballageMouvementRequest(castel.Id, EmballageMouvementTypes.RetourFournisseur, 5, SupplierId: brakina.Id))).EnsureSuccessStatusCode();

        var fournisseur = Assert.Single((await (await SendAsync(HttpMethod.Get, "/api/stock/emballages/fournisseurs", owner))
            .Content.ReadFromJsonAsync<List<EmballageFournisseurDto>>())!);
        Assert.Equal("Brakina", fournisseur.Nom);
        Assert.Equal(20m, fournisseur.Recus);
        Assert.Equal(5m, fournisseur.Rendus);
        Assert.Equal(15m, fournisseur.ARendre);
        Assert.Equal("Castel", Assert.Single(fournisseur.Lignes).NomProduit);

        // The Fournisseurs window shows the same balance; the empties went down by the 5 returned.
        var supplier = (await (await SendAsync(HttpMethod.Get, "/api/stock/suppliers", owner)).Content.ReadFromJsonAsync<List<SupplierDto>>())!.Single();
        Assert.Equal(15m, supplier.EmballagesARendre);
        var row = (await (await SendAsync(HttpMethod.Get, "/api/stock/emballages", owner)).Content.ReadFromJsonAsync<List<EmballageStockDto>>())!.Single();
        Assert.Equal(1m, row.Vides);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var flux = (await (await SendAsync(HttpMethod.Get, $"/api/stock/emballages/flux?dateDebut={today}&dateFin={today}&tzOffsetMinutes=0", owner))
            .Content.ReadFromJsonAsync<List<EmballageFluxDto>>())!.Single();
        Assert.Equal(20m, flux.RecusFournisseur);
        Assert.Equal(5m, flux.RetourFournisseur);
    }

    [Fact]
    public async Task A_stock_entry_naming_its_supplier_counts_the_crates_as_owed_to_them()
    {
        var owner = await SignUpOwnerAsync();
        var castel = await CreateProductAsync(owner, new SaveProductRequest(
            "Castel", 1000, Quantity: 2, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "casier"));
        var eau = await CreateProductAsync(owner, new SaveProductRequest("Eau", 300, Quantity: 2, PrixFixe: true));
        var brakina = (await (await SendAsync(HttpMethod.Post, "/api/stock/suppliers", owner, new SaveSupplierRequest("Brakina")))
            .Content.ReadFromJsonAsync<SupplierDto>())!;

        (await SendAsync(HttpMethod.Post, $"/api/stock/products/{castel.Id}/adjust", owner,
            new AdjustStockRequest(12, "ajout", SupplierId: brakina.Id))).EnsureSuccessStatusCode();
        // Without a supplier, or for a product without returnable crates, nothing is counted.
        (await SendAsync(HttpMethod.Post, $"/api/stock/products/{castel.Id}/adjust", owner,
            new AdjustStockRequest(3, "ajout"))).EnsureSuccessStatusCode();
        (await SendAsync(HttpMethod.Post, $"/api/stock/products/{eau.Id}/adjust", owner,
            new AdjustStockRequest(5, "ajout", SupplierId: brakina.Id))).EnsureSuccessStatusCode();

        Assert.Equal(17, (await GetProductAsync(owner, castel.Id)).Quantity);
        var fournisseur = Assert.Single((await (await SendAsync(HttpMethod.Get, "/api/stock/emballages/fournisseurs", owner))
            .Content.ReadFromJsonAsync<List<EmballageFournisseurDto>>())!);
        Assert.Equal(12m, fournisseur.Recus);
        Assert.Equal(12m, fournisseur.ARendre);
        Assert.Equal(0m, (await (await SendAsync(HttpMethod.Get, "/api/stock/emballages", owner)).Content.ReadFromJsonAsync<List<EmballageStockDto>>())!.Single().Vides);
    }

    [Fact]
    public async Task The_emballages_tab_needs_its_own_privilege_not_just_view_stock()
    {
        var owner = await SignUpOwnerAsync();
        var add = await SendAsync(HttpMethod.Post, "/api/groupe/members", owner,
            new AddMemberRequest("caisse@lonnii.test", "MotDePasse123", "vendeur"));
        add.EnsureSuccessStatusCode();
        var memberId = (await add.Content.ReadFromJsonAsync<UserDto>())!.IdUser;
        (await SendAsync(HttpMethod.Post, "/api/privileges/gestion", owner,
            new SetPrivilegeRequest(memberId, Priv.Gestion.ViewStock, true))).EnsureSuccessStatusCode();

        var login = (await (await _client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("caisse@lonnii.test", "MotDePasse123"))).Content.ReadFromJsonAsync<LoginResponse>())!;
        var groupId = (await (await SendAsync(HttpMethod.Get, "/api/groupes", owner)).Content.ReadFromJsonAsync<List<GroupeDto>>())!.Single().Id;
        using var open = new HttpRequestMessage(HttpMethod.Post, $"/api/groupes/{groupId}/session");
        open.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        var scoped = (await (await _client.SendAsync(open)).Content.ReadFromJsonAsync<GroupSessionResponse>())!;
        var member = new Session(login.AccessToken, scoped.SessionToken);

        // Seeing the stock is not enough; the product form still lists the groups.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/stock/emballages", member)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/stock/emballages/clients", member)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/stock/emballages/types", member)).StatusCode);

        (await SendAsync(HttpMethod.Post, "/api/privileges/gestion", owner,
            new SetPrivilegeRequest(memberId, Priv.Gestion.ViewEmballages, true))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/api/stock/emballages", member)).StatusCode);
        // Recording a movement still needs Ajuster Stock.
        var product = await CreateProductAsync(owner, new SaveProductRequest("Castel", 1000, PrixFixe: true, EmballageRecuperable: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/stock/emballages/mouvements", member,
            new EmballageMouvementRequest(product.Id, EmballageMouvementTypes.Inventaire, 3))).StatusCode);
    }

    [Fact]
    public async Task A_new_product_can_join_an_interchangeable_group_from_the_product_form()
    {
        var owner = await SignUpOwnerAsync();
        var sodibo = await CreateProductAsync(owner, new SaveProductRequest(
            "Sodibo Orange", 900, Quantity: 10, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "caisse", EmballageContenance: 12));
        var created = await SendAsync(HttpMethod.Post, "/api/stock/emballages/types", owner,
            new SaveEmballageTypeRequest("Caisse Sodibo", [sodibo.Id]));
        var type = (await created.Content.ReadFromJsonAsync<EmballageTypeDto>())!;

        // A new crate of the same size joins straight from the form, and the product says so.
        var cola = await CreateProductAsync(owner, new SaveProductRequest(
            "Sodibo Cola", 900, Quantity: 4, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "caisse",
            EmballageContenance: 12, EmballageTypeId: type.Id));
        Assert.Equal(type.Id, cola.EmballageTypeId);
        var row = (await (await SendAsync(HttpMethod.Get, "/api/stock/emballages", owner)).Content.ReadFromJsonAsync<List<EmballageStockDto>>())!.Single();
        Assert.Equal(type.Id, row.ProductId);
        Assert.Equal(14m, row.Pleins);

        // Another size, or a group that doesn't exist, is refused.
        var big = await SendAsync(HttpMethod.Post, "/api/stock/products", owner, new SaveProductRequest(
            "Sodibo grand", 1200, Quantity: 2, PrixFixe: true, EmballageRecuperable: true, EmballageContenance: 16, EmballageTypeId: type.Id));
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
        Assert.Contains("même contenance", await big.Content.ReadAsStringAsync());
        var unknown = await SendAsync(HttpMethod.Post, "/api/stock/products", owner, new SaveProductRequest(
            "Sodibo citron", 900, PrixFixe: true, EmballageRecuperable: true, EmballageContenance: 12, EmballageTypeId: Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Saving without the field (the phone, an older desktop) keeps it; "" takes it out.
        (await SendAsync(HttpMethod.Put, $"/api/stock/products/{cola.Id}", owner, new SaveProductRequest(
            "Sodibo Cola", 950, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "caisse"))).EnsureSuccessStatusCode();
        Assert.Equal(type.Id, (await GetProductAsync(owner, cola.Id)).EmballageTypeId);
        (await SendAsync(HttpMethod.Put, $"/api/stock/products/{cola.Id}", owner, new SaveProductRequest(
            "Sodibo Cola", 950, PrixFixe: true, EmballageRecuperable: true, UniteAffichage: "caisse", EmballageTypeId: ""))).EnsureSuccessStatusCode();
        Assert.Null((await GetProductAsync(owner, cola.Id)).EmballageTypeId);
    }

    [Fact]
    public async Task Returning_half_a_caisse_from_Clients_counts_empties_not_the_consigne_refunded()
    {
        var owner = await SignUpOwnerAsync();
        var current = (await (await SendAsync(HttpMethod.Get, "/api/parametres/recu", owner))
            .Content.ReadFromJsonAsync<ReceiptSettingsDto>())!;
        (await SendAsync(HttpMethod.Put, "/api/parametres/recu", owner, new UpdateReceiptSettingsRequest(
            current.CompanyName, current.NoteUnderQr, current.ReceiptTitle, current.FactureTitle,
            current.FactureNoticeTitle, current.FactureNoticeText, current.FactureFooterText, current.ReceiptFooterText,
            current.SellerLabel, current.AvoirNoticeTitle, current.AvoirNoticeText, current.FontFamily, current.FontSize,
            current.ReceiptTitleFontSize, current.FactureTitleFontSize, EmballagesSuivi: true))).EnsureSuccessStatusCode();
        (await SendAsync(HttpMethod.Post, "/api/caisse/ouvrir", owner, new OpenCaisseRequest(MontantInitialCash: 50_000m))).EnsureSuccessStatusCode();

        var biere = await CreateProductAsync(owner, new SaveProductRequest(
            "Brakina", 11200, Quantity: 50, PrixFixe: true, EmballageRecuperable: true, ConsigneMontant: 3000));
        var sale = await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(biere.Id, 1)], "cash", 100000, ClientNom: "Michel Soulama", AvecConsigne: true));
        Assert.True(sale.IsSuccessStatusCode, await sale.Content.ReadAsStringAsync());

        var retour = await SendAsync(HttpMethod.Post, "/api/ventes/clients/emballages-retours", owner,
            new EmballageRetourRequest("Michel Soulama", null, null, 0.5m, [new EmballageRetourLigne(biere.Id, 0.5m)], "cash"));
        Assert.True(retour.IsSuccessStatusCode, await retour.Content.ReadAsStringAsync());

        var clients = await (await SendAsync(HttpMethod.Get, "/api/ventes/clients/", owner)).Content.ReadFromJsonAsync<List<ClientDto>>();
        var michel = Assert.Single(clients!, c => c.Nom == "Michel Soulama");
        Assert.Equal(0.5m, michel.EmballagesDus); // 1 taken - 1/2 back, not 1 - 1 500 FCFA
        Assert.Equal(1500m, michel.ConsigneVersee); // 3 000 paid - 1 500 refunded

        // Statistiques: the consigne is a deposit - out of the chiffre d'affaires, shown apart per client.
        var stats = (await (await SendAsync(HttpMethod.Get, "/api/ventes/stats", owner)).Content.ReadFromJsonAsync<VentesStatsResponse>())!;
        Assert.Equal(11200m, stats.ChiffreAffaires); // the beer only, not 11 200 + 3 000
        Assert.Equal(3000m, stats.ConsigneEncaissee);
        Assert.Equal(1500m, stats.ConsigneRemboursee);
        var row = Assert.Single(stats.ConsigneClients!);
        Assert.Equal("Michel Soulama", row.Client);
        Assert.Equal(1500m, row.Nette);
    }

    [Fact]
    public async Task Consigne_IsChargedOnlyWhenTheTillAsks()
    {
        var owner = await SignUpOwnerAsync();
        var biere = await CreateProductAsync(owner, new SaveProductRequest(
            "Bière", 1000, Quantity: 50, PrixFixe: true, EmballageRecuperable: true, ConsigneMontant: 100));

        var vente = (await (await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(biere.Id, 2)], "cash", 100000))).Content.ReadFromJsonAsync<VenteDto>())!;

        Assert.Null(vente.ConsigneAmount);
        Assert.Equal(2000m, vente.MontantTotal);
    }

    [Fact]
    public async Task Livraison_IsRefused_WhenTheShopHasItOff()
    {
        var owner = await SignUpOwnerAsync();
        var p = await CreateProductAsync(owner, new SaveProductRequest("Riz", 500, Quantity: 10, PrixFixe: true));

        var response = await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(p.Id, 1)], "cash", 10000, Livraison: 300));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Fractions_SellAtTheirOwnPrice_AndOpenAUnit()
    {
        var owner = await SignUpOwnerAsync();
        await SaveSettingsAsync(owner, fractions: true);
        var casier = await CreateProductAsync(owner, new SaveProductRequest(
            "Casier", 12000, Quantity: 10, PrixFixe: true, PrixDemi: 6500, PrixQuart: 3300));

        // 1 + 1/2 caisse: 12 000 + 6 500.
        var vente = (await (await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(casier.Id, 1, Quarts: 2)], "cash", 100000))).Content.ReadFromJsonAsync<VenteDto>())!;
        Assert.Equal(18500m, vente.MontantTotal);
        Assert.Equal("1 + 1/2", vente.Items[0].QuantiteDisplay);

        var after = await GetProductAsync(owner, casier.Id);
        Assert.Equal(8, after.Quantity);
        Assert.Equal(2, after.FractionOuverte);
        Assert.Equal("8 + 1/2", after.QuantityDisplay);

        // A quarter alone comes out of the opened unit, not a new one.
        var quart = await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(casier.Id, 0, Quarts: 1)], "cash", 100000));
        Assert.True(quart.IsSuccessStatusCode, await quart.Content.ReadAsStringAsync());
        after = await GetProductAsync(owner, casier.Id);
        Assert.Equal((8, 1), (after.Quantity, after.FractionOuverte));

        // Cancelling the first sale puts the 1 + 1/2 back: 8 + 1/4 + 1 + 1/2 = 9 + 3/4.
        var cancel = await SendAsync(HttpMethod.Put, $"/api/ventes/{vente.Id}/annuler", owner, new CancelVenteRequest("Erreur"));
        Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        after = await GetProductAsync(owner, casier.Id);
        Assert.Equal((9, 3), (after.Quantity, after.FractionOuverte));
    }

    [Fact]
    public async Task Fractions_AreRefused_WithoutAPriceOrWithTheOptionOff()
    {
        var owner = await SignUpOwnerAsync();
        var casier = await CreateProductAsync(owner, new SaveProductRequest(
            "Casier", 12000, Quantity: 10, PrixFixe: true, PrixDemi: 6500));

        var optionOff = await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(casier.Id, 0, Quarts: 2)], "cash", 100000));
        Assert.Equal(HttpStatusCode.BadRequest, optionOff.StatusCode);

        await SaveSettingsAsync(owner, fractions: true);
        var noQuarterPrice = await SendAsync(HttpMethod.Post, "/api/ventes/", owner, new CreateVenteRequest(
            [new CartItemRequest(casier.Id, 0, Quarts: 1)], "cash", 100000));
        Assert.Equal(HttpStatusCode.BadRequest, noQuarterPrice.StatusCode);
    }

    [Fact]
    public async Task UpdatingAProduct_WithoutTheNewFields_KeepsThem()
    {
        var owner = await SignUpOwnerAsync();
        var p = await CreateProductAsync(owner, new SaveProductRequest(
            "Bière", 1000, Quantity: 5, PrixFixe: true, EffortGuerre: 25, EmballageRecuperable: true,
            ConsigneMontant: 100, PrixDemi: 600));

        // What the phone or an older desktop sends: none of the new fields.
        var update = await SendAsync(HttpMethod.Put, $"/api/stock/products/{p.Id}", owner,
            new SaveProductRequest("Bière blonde", 1100, PrixFixe: true));
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());

        var after = await GetProductAsync(owner, p.Id);
        Assert.Equal(25m, after.EffortGuerre);
        Assert.True(after.EmballageRecuperable);
        Assert.Equal(100m, after.ConsigneMontant);
        Assert.Equal(600m, after.PrixDemi);
    }

    [Theory]
    [InlineData("2", 2, 0)]
    [InlineData("1.5", 1, 2)]
    [InlineData("1,5", 1, 2)]
    [InlineData("1 1/2", 1, 2)]
    [InlineData("1+1/2", 1, 2)]
    [InlineData("3/4", 0, 3)]
    [InlineData("0.25", 0, 1)]
    [InlineData("2 3/4", 2, 3)]
    public void TypedQuantities_AreReadAsUnitsAndQuarters(string typed, int quantity, int quarts)
    {
        Assert.True(SaleCharges.TryParseQuantity(typed, out var q, out var r));
        Assert.Equal((quantity, quarts), (q, r));
    }

    [Theory]
    [InlineData("1.3")]
    [InlineData("1/3")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("")]
    public void OtherQuantities_AreRefused(string typed) =>
        Assert.False(SaleCharges.TryParseQuantity(typed, out _, out _));

    [Theory]
    [InlineData(3, 0, "3")]
    [InlineData(1, 2, "1 + 1/2")]
    [InlineData(0, 2, "1/2")]
    [InlineData(2, 3, "2 + 3/4")]
    public void Quantities_PrintAsUnitsPlusAFraction(int quantity, int quarts, string expected) =>
        Assert.Equal(expected, SaleCharges.QuantityLabel(quantity, quarts));

    [Fact]
    public void Prelevement_IsTakenOnTheHtAmount_WhenPricesIncludeTva()
    {
        // 11 800 TTC at 18 % is 10 000 HT; 2 % of that is 200.
        Assert.Equal(200m, SaleCharges.Prelevement(11800, 2, 18, TvaModes.Incluse));
        Assert.Equal(236m, SaleCharges.Prelevement(11800, 2, 18, TvaModes.Ajoutee));
        // After TVA: on the TTC amount.
        Assert.Equal(236m, SaleCharges.Prelevement(11800, 2, 18, TvaModes.Incluse, afterTva: true));
        Assert.Equal(278m, SaleCharges.Prelevement(11800, 2, 18, TvaModes.Ajoutee, afterTva: true));
    }
}
