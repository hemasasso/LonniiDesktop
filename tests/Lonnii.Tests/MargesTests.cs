using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Api.Endpoints;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// Covers <c>GET /api/marges</c>. The points worth pinning are the ones a wrong answer would
/// not look wrong on screen: which cost each sale line is charged at, that cancelled sales
/// (in either spelling the live database holds) never count, that a category filter narrows
/// sales but not charges, and that the period follows the till's local day rather than UTC.
/// </summary>
public class MargesTests : IAsyncLifetime
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

    private sealed record Session(string Token, string GroupSession, string GroupId);

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

        return new Session(login.AccessToken, scoped.SessionToken, groupe.Id);
    }

    private async Task<MargesResponse> GetMargesAsync(Session session, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/marges?" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        request.Headers.Add("x-group-session", session.GroupSession);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MargesResponse>())!;
    }

    private async Task SeedAsync(Action<LonniiDbContext> seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static Vente Sale(string groupId, DateTime utc, string statut, params (Product Product, int Qty, decimal Total)[] lines) => new()
    {
        GroupId = groupId,
        NumeroVente = "V-" + Guid.NewGuid().ToString("N")[..8],
        DateVente = utc,
        StatutPaiement = statut,
        MontantTotal = lines.Sum(l => l.Total),
        Items = lines.Select(l => new VenteItem
        {
            ProductId = l.Product.Id, NomProduit = l.Product.Name, Quantite = l.Qty,
            PrixUnitaire = l.Total / l.Qty, PrixTotal = l.Total,
        }).ToList(),
    };

    [Fact]
    public void Line_cost_follows_the_source_apps_three_cases()
    {
        Assert.Equal((1200m, MargeCostSources.Reel), MargesEndpoints.LineCost(600m, 1000m, venteLibre: false, quantite: 2));
        // No known cost: estimated from what the line sold for, never from today's price.
        Assert.Equal((1000m, MargeCostSources.Estime), MargesEndpoints.LineCost(null, 1300m, venteLibre: false, quantite: 1));
        Assert.Equal((1000m, MargeCostSources.Estime), MargesEndpoints.LineCost(0m, 1300m, venteLibre: false, quantite: 1));
        Assert.Equal((0m, MargeCostSources.Aucun), MargesEndpoints.LineCost(400m, 500m, venteLibre: true, quantite: 3));
    }

    [Fact]
    public void Break_even_divides_fixed_charges_by_the_contribution_margin_rate()
    {
        var (mcv, taux, seuil) = MargesEndpoints.BreakEven(revenue: 10_000m, variableCosts: 6_000m, fixedCharges: 2_000m);
        Assert.Equal(4_000m, mcv);
        Assert.Equal(40m, taux);
        Assert.Equal(5_000m, seuil);

        // Every sale loses money: no revenue ever breaks even.
        Assert.Null(MargesEndpoints.BreakEven(1_000m, 1_200m, 500m).Seuil);
    }

    [Fact]
    public async Task Totals_count_real_estimated_and_service_costs_and_skip_cancelled_sales()
    {
        var session = await SignUpOwnerAsync();
        var g = session.GroupId;

        var boissons = new Category { GroupId = g, Name = "Boissons" };
        var services = new Category { GroupId = g, Name = "Services" };
        var soda = new Product { GroupId = g, Name = "Soda", CategoryId = boissons.Id, CostPrice = 600m, Price = 1000m };
        var jus = new Product { GroupId = g, Name = "Jus", CategoryId = boissons.Id, Price = 1300m };
        var impression = new Product { GroupId = g, Name = "Impression", CategoryId = services.Id, Price = 500m, VenteLibre = true };

        var day = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db =>
        {
            db.Categories.AddRange(boissons, services);
            db.Products.AddRange(soda, jus, impression);
            db.Ventes.Add(Sale(g, day, StatutPaiement.Paye, (soda, 2, 2000m), (jus, 1, 1300m)));
            db.Ventes.Add(Sale(g, day, StatutPaiement.EnAttente, (impression, 1, 500m)));
            db.Ventes.Add(Sale(g, day, StatutPaiement.Annule, (soda, 10, 10000m)));
            db.Ventes.Add(Sale(g, day, "cancelled", (soda, 10, 10000m)));
            db.Charges.Add(new Charge { GroupId = g, Description = "Loyer", Montant = 300m, Categorie = "Loyer", TypeCharge = ChargeTypes.Fixe, Date = new DateTime(2026, 3, 1) });
            db.Charges.Add(new Charge { GroupId = g, Description = "Livraison", Montant = 100m, Categorie = "Transport", TypeCharge = ChargeTypes.Variable, Date = new DateTime(2026, 3, 12) });
            db.Charges.Add(new Charge { GroupId = g, Description = "Loyer", Montant = 999m, Categorie = "Loyer", TypeCharge = ChargeTypes.Fixe, Date = new DateTime(2026, 2, 1) });
        });

        var marges = await GetMargesAsync(session, "dateDebut=2026-03-01&dateFin=2026-03-31&tzOffsetMinutes=0");

        Assert.Equal(3800m, marges.TotalRevenue);
        Assert.Equal(2200m, marges.TotalCosts);          // 1 200 real + 1 000 estimated + 0 service
        Assert.Equal(1600m, marges.GrossProfit);
        Assert.Equal(400m, marges.TotalCharges);
        Assert.Equal(300m, marges.ChargesFixes);
        Assert.Equal(100m, marges.ChargesVariables);
        Assert.Equal(1200m, marges.NetProfit);
        Assert.Equal(2, marges.NombreVentes);
        Assert.Equal(1, marges.EstimatedLines);
        Assert.Equal(1300m, marges.EstimatedRevenue);

        // MCV = 3 800 − 2 200 − 100 variable = 1 500; seuil = 300 fixed ÷ (1 500 / 3 800) = 760.
        Assert.Equal(1500m, marges.MargeCoutsVariables);
        Assert.Equal(760m, marges.SeuilRentabilite!.Value, 6);
        // 3 800 over 31 days is ~122,58 a day: 760 is reached on day 7.
        Assert.Equal(new DateOnly(2026, 3, 7), marges.PointMortDate);

        var service = Assert.Single(marges.Products, p => p.Nom == "Impression");
        Assert.Equal(100m, service.Margin);
        Assert.Equal(MargeCostSources.Aucun, service.CostSource);

        Assert.Equal(["Boissons", "Services"], marges.Categories.Select(c => c.Categorie).Order());
    }

    /// <summary>Regression: a product renamed and reused for another item after it was sold
    /// charged its new purchase price to every past sale of the old item - a 25 F print
    /// costed at a 6 000 F bottle, driving the cost of sales past the revenue.</summary>
    [Fact]
    public async Task Past_sales_use_the_cost_at_sale_time_and_ignore_reused_products()
    {
        var session = await SignUpOwnerAsync();
        var g = session.GroupId;

        var boissons = new Category { GroupId = g, Name = "Boissons" };
        // Sold as "Impression - NB" (no stock history: it was vente libre then), since
        // renamed "Gourde" with a 6 000 purchase price.
        var gourde = new Product { GroupId = g, Name = "Gourde", CategoryId = boissons.Id, CostPrice = 6000m, Price = 7000m };
        // Sold as "Portable" at a recorded 1 200 cost, since renamed and repriced.
        var whisky = new Product { GroupId = g, Name = "Jonny Walker", CategoryId = boissons.Id, CostPrice = 9000m, Price = 12000m };

        var day = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        var impression = Sale(g, day, StatutPaiement.Paye, (gourde, 10, 250m));
        impression.Items.Single().NomProduit = "Impression - NB";
        var portable = Sale(g, day, StatutPaiement.Paye, (whisky, 1, 3500m));
        portable.Items.Single().NomProduit = "Portable";
        var jw = Sale(g, day, StatutPaiement.Paye, (whisky, 1, 12000m));

        StockHistory History(Vente v, Product p, decimal unitCost) => new()
        {
            GroupId = g, ProductId = p.Id, MovementType = StockMovementTypes.Vente,
            QuantityChanged = -1, UnitCost = unitCost, TotalCost = unitCost, ReferenceId = v.Id, ReferenceType = "sale",
        };

        await SeedAsync(db =>
        {
            db.Categories.Add(boissons);
            db.Products.AddRange(gourde, whisky);
            db.Ventes.AddRange(impression, portable, jw);
            db.StockHistories.AddRange(History(portable, whisky, 1200m), History(jw, whisky, 9000m));
        });

        var marges = await GetMargesAsync(session, "dateDebut=2026-03-01&dateFin=2026-03-31");

        Assert.Equal(250m / 1.3m + 1200m + 9000m, marges.TotalCosts, 6);
        Assert.True(marges.TotalCosts < marges.TotalRevenue);

        var print = Assert.Single(marges.Products, p => p.Nom == "Impression - NB");
        Assert.Equal(MargeCostSources.Estime, print.CostSource);
        Assert.Equal("Sans catégorie", print.Categorie);
        Assert.Null(print.ProductId);

        Assert.Equal(1200m, Assert.Single(marges.Products, p => p.Nom == "Portable").Cost);
        Assert.Equal(9000m, Assert.Single(marges.Products, p => p.Nom == "Jonny Walker").Cost);
        Assert.DoesNotContain(marges.Products, p => p.Nom == "Gourde");
    }

    [Fact]
    public async Task Category_filter_narrows_sales_but_not_charges()
    {
        var session = await SignUpOwnerAsync();
        var g = session.GroupId;

        var boissons = new Category { GroupId = g, Name = "Boissons" };
        var snacks = new Category { GroupId = g, Name = "Snacks" };
        var soda = new Product { GroupId = g, Name = "Soda", CategoryId = boissons.Id, CostPrice = 600m, Price = 1000m };
        var chips = new Product { GroupId = g, Name = "Chips", CategoryId = snacks.Id, CostPrice = 200m, Price = 500m };

        var day = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db =>
        {
            db.Categories.AddRange(boissons, snacks);
            db.Products.AddRange(soda, chips);
            db.Ventes.Add(Sale(g, day, StatutPaiement.Paye, (soda, 1, 1000m), (chips, 2, 1000m)));
            db.Charges.Add(new Charge { GroupId = g, Description = "Eau", Montant = 250m, Categorie = "Eau", Date = new DateTime(2026, 3, 5) });
        });

        var marges = await GetMargesAsync(session, $"dateDebut=2026-03-01&dateFin=2026-03-31&categoryId={snacks.Id}");

        Assert.Equal(1000m, marges.TotalRevenue);
        Assert.Equal(400m, marges.TotalCosts);
        Assert.Equal(250m, marges.TotalCharges);
        Assert.Equal("Chips", Assert.Single(marges.Products).Nom);
        Assert.Equal(2, marges.CategoryOptions.Count);
    }

    [Fact]
    public async Task Period_follows_the_tills_local_day_and_compares_with_the_previous_one()
    {
        var session = await SignUpOwnerAsync();
        var g = session.GroupId;
        var soda = new Product { GroupId = g, Name = "Soda", CostPrice = 600m, Price = 1000m };

        await SeedAsync(db =>
        {
            db.Products.Add(soda);
            // 23:30 on 10 March in UTC-4 is already 11 March in UTC.
            db.Ventes.Add(Sale(g, new DateTime(2026, 3, 11, 3, 30, 0, DateTimeKind.Utc), StatutPaiement.Paye, (soda, 1, 1000m)));
            // The day before, local: the comparison period.
            db.Ventes.Add(Sale(g, new DateTime(2026, 3, 9, 16, 0, 0, DateTimeKind.Utc), StatutPaiement.Paye, (soda, 2, 2000m)));
        });

        var marges = await GetMargesAsync(session, "dateDebut=2026-03-10&dateFin=2026-03-10&tzOffsetMinutes=-240");

        Assert.Equal(1000m, marges.TotalRevenue);
        Assert.Equal(2000m, marges.PreviousRevenue);
        Assert.Equal(800m, marges.PreviousGrossProfit);
        Assert.Equal(12, marges.Monthly.Count);
    }
}
