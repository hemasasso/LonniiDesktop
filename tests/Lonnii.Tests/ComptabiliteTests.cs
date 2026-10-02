using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lonnii.Tests;

/// <summary>
/// Covers the Amortissement and Bilan modules. The calculator tests pin schedules figure for
/// figure, because Lonnii Business writes the same <c>amortissement_echeances</c> rows and the
/// two apps must agree on them. The endpoint tests pin what a wrong answer would not look
/// wrong on screen: an asset's figures after it is sold, which account each automatic amount
/// lands on, and that the net result is the same on both statements.
/// </summary>
public class ComptabiliteTests : IAsyncLifetime
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

    // --- Calculator ---

    private static List<decimal> Dotations(IEnumerable<AmortissementEcheanceDto> echeances) =>
        echeances.Select(e => e.DotationAnnuelle).ToList();

    [Theory]
    [InlineData(2, 10, 11)]  // before the 15th: February counts
    [InlineData(2, 15, 10)]  // on the 15th: it does not
    [InlineData(1, 3, 12)]   // a full first year
    [InlineData(12, 20, 0)]  // nothing left of the year
    public void First_year_months_follow_the_fifteenth_rule(int month, int day, int expected) =>
        Assert.Equal(expected, AmortissementCalculator.MoisPremiereAnnee(new DateOnly(2025, month, day)));

    [Fact]
    public void Linear_without_prorata_charges_the_same_amount_every_year()
    {
        var schedule = AmortissementCalculator.Calculer(
            1_000_000m, 0m, 5, MethodesAmortissement.Lineaire, null, new DateOnly(2025, 1, 10), null);

        Assert.Equal([2025, 2026, 2027, 2028, 2029], schedule.Select(e => e.Annee));
        Assert.All(schedule, e => Assert.Equal(200_000m, e.DotationAnnuelle));
        Assert.Equal(0m, schedule[^1].ValeurNetteComptable);
        Assert.Equal(new DateOnly(2025, 1, 1), schedule[0].DateDebut);
    }

    /// <summary>As the source: a prorated first year, and its shortfall absorbed by the last
    /// one, so the schedule still spans exactly N years.</summary>
    [Fact]
    public void Linear_with_prorata_leaves_the_shortfall_to_the_last_year()
    {
        var schedule = AmortissementCalculator.Calculer(
            1_100_000m, 100_000m, 5, MethodesAmortissement.Lineaire, null, new DateOnly(2025, 3, 20), null);

        // 20 March: March does not count, 9 months of 200 000.
        Assert.Equal([150_000m, 200_000m, 200_000m, 200_000m, 250_000m], Dotations(schedule));
        Assert.Equal(1_000_000m, schedule[^1].AmortissementCumule);
        Assert.Equal(100_000m, schedule[^1].ValeurNetteComptable);
        Assert.Equal(1_100_000m, schedule[0].ValeurDebutPeriode);
    }

    [Fact]
    public void Mise_en_service_date_drives_the_schedule_rather_than_acquisition()
    {
        var schedule = AmortissementCalculator.Calculer(
            1_200_000m, 0m, 3, MethodesAmortissement.Lineaire, null, new DateOnly(2024, 11, 2), new DateOnly(2025, 1, 5));

        Assert.Equal(2025, schedule[0].Annee);
        Assert.Equal([400_000m, 400_000m, 400_000m], Dotations(schedule));
    }

    /// <summary>Regression against the source, which gave a one-year asset only its prorated
    /// share and never depreciated the rest.</summary>
    [Fact]
    public void A_one_year_asset_is_fully_depreciated_even_with_prorata()
    {
        var schedule = AmortissementCalculator.Calculer(
            600_000m, 0m, 1, MethodesAmortissement.Lineaire, null, new DateOnly(2025, 7, 1), null);

        var only = Assert.Single(schedule);
        Assert.Equal(600_000m, only.DotationAnnuelle);
        Assert.Equal(0m, only.ValeurNetteComptable);
    }

    /// <summary>Regression against the source, which read "0 months left" as "no prorata" and
    /// charged a full year for an asset bought on 20 December.</summary>
    [Fact]
    public void An_asset_put_in_service_late_december_starts_the_next_year()
    {
        var schedule = AmortissementCalculator.Calculer(
            500_000m, 0m, 5, MethodesAmortissement.Lineaire, null, new DateOnly(2025, 12, 20), null);

        Assert.Equal(2026, schedule[0].Annee);
        Assert.All(schedule, e => Assert.Equal(100_000m, e.DotationAnnuelle));
    }

    [Fact]
    public void Degressive_switches_to_linear_and_ends_at_zero()
    {
        // 5 years: default coefficient 1.75, rate 35 %.
        var schedule = AmortissementCalculator.Calculer(
            1_000_000m, 0m, 5, MethodesAmortissement.Degressif, null, new DateOnly(2025, 1, 5), null);

        Assert.Equal([350_000m, 227_500m, 147_875m, 137_312.50m, 137_312.50m], Dotations(schedule));
        Assert.Equal(1_000_000m, schedule.Sum(e => e.DotationAnnuelle));
        Assert.Equal(0m, schedule[^1].ValeurNetteComptable);
    }

    [Theory]
    [InlineData(3, 1.25)]
    [InlineData(4, 1.25)]
    [InlineData(5, 1.75)]
    [InlineData(6, 1.75)]
    [InlineData(7, 2.25)]
    public void Degressive_coefficient_defaults_by_duration(int duree, decimal expected) =>
        Assert.Equal(expected, AmortissementCalculator.CoefficientDegressif(duree));

    [Fact]
    public void Sum_of_years_digits_uses_anniversary_periods()
    {
        var schedule = AmortissementCalculator.Calculer(
            150_000m, 0m, 5, MethodesAmortissement.SommeAnnees, null, new DateOnly(2025, 4, 1), null);

        Assert.Equal([50_000m, 40_000m, 30_000m, 20_000m, 10_000m], Dotations(schedule));
        Assert.Equal(new DateOnly(2025, 4, 1), schedule[0].DateDebut);
        Assert.Equal(new DateOnly(2026, 3, 31), schedule[0].DateFin);
    }

    [Fact]
    public void Validation_rejects_impossible_assets()
    {
        Assert.NotNull(AmortissementCalculator.Valider(0m, 0m, 5, MethodesAmortissement.Lineaire, null));
        Assert.NotNull(AmortissementCalculator.Valider(100m, 100m, 5, MethodesAmortissement.Lineaire, null));
        Assert.NotNull(AmortissementCalculator.Valider(100m, 0m, 0, MethodesAmortissement.Lineaire, null));
        Assert.NotNull(AmortissementCalculator.Valider(100m, 0m, 5, "unite_production", null));
        Assert.NotNull(AmortissementCalculator.Valider(100m, 0m, 5, MethodesAmortissement.Degressif, 4m));
        Assert.Null(AmortissementCalculator.Valider(100m, 10m, 5, MethodesAmortissement.Degressif, 2m));
    }

    [Theory]
    [InlineData("Loyer", SousTypesCompte.ServicesExterieurs)]
    [InlineData("  Électricité ", SousTypesCompte.ServicesExterieurs)]
    [InlineData("Salaires", SousTypesCompte.Personnel)]
    [InlineData("Taxes", SousTypesCompte.ImpotsTaxes)]
    [InlineData("Marketing", SousTypesCompte.AutresServices)]
    [InlineData("Achats", SousTypesCompte.Achats)]
    [InlineData("Quelque chose d'autre", SousTypesCompte.AutresCharges)]
    public void Charge_categories_map_to_the_source_apps_accounts(string categorie, string sousType) =>
        Assert.Equal(sousType, Lonnii.Api.Features.Bilan.BilanEndpoints.SousTypePourCategorie(categorie));

    // --- Endpoints ---

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

    private async Task<HttpResponseMessage> SendAsync(Session s, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Token);
        request.Headers.Add("x-group-session", s.GroupSession);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private async Task<T> SendAsync<T>(Session s, HttpMethod method, string url, object? body = null)
    {
        var response = await SendAsync(s, method, url, body);
        Assert.True(response.IsSuccessStatusCode, $"{method} {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task SeedAsync(Action<LonniiDbContext> seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static SaveImmobilisationRequest Machine(DateOnly acquisition, decimal valeur = 1_000_000m, int duree = 5) =>
        new("Machine X200", CategoriesImmobilisation.Materiel, acquisition, valeur, 0m, duree, MethodesAmortissement.Lineaire);

    [Fact]
    public async Task Creating_an_asset_stores_its_schedule_and_feeds_the_summary()
    {
        var s = await SignUpOwnerAsync();

        var created = await SendAsync<ImmobilisationDetailsResponse>(s, HttpMethod.Post, "/api/amortissement?annee=2026",
            Machine(new DateOnly(2025, 1, 5)));
        Assert.Equal(5, created.Echeances.Count);

        var list = await SendAsync<AmortissementListResponse>(s, HttpMethod.Get, "/api/amortissement?annee=2026");
        var immo = Assert.Single(list.Immobilisations);
        Assert.Equal(400_000m, immo.AmortissementCumule);   // 2025 and 2026
        Assert.Equal(600_000m, immo.ValeurNetteComptable);
        Assert.Equal(200_000m, immo.DotationAnneeCourante);
        Assert.False(immo.TotalementAmorti);

        Assert.Equal(1, list.Stats.TotalImmobilisations);
        Assert.Equal(1_000_000m, list.Stats.TotalValeurAcquisition);
        Assert.Equal(200_000m, list.Stats.DotationAnneeCourante);
        Assert.Equal(600_000m, list.Stats.ValeurNetteComptable);

        // Once its schedule has run out, still active but flagged fully depreciated.
        var later = await SendAsync<AmortissementListResponse>(s, HttpMethod.Get, "/api/amortissement?annee=2031");
        Assert.True(Assert.Single(later.Immobilisations).TotalementAmorti);
        Assert.Equal(0m, later.Stats.DotationAnneeCourante);
    }

    [Fact]
    public async Task Editing_an_asset_recomputes_its_schedule()
    {
        var s = await SignUpOwnerAsync();
        var created = await SendAsync<ImmobilisationDetailsResponse>(s, HttpMethod.Post, "/api/amortissement",
            Machine(new DateOnly(2025, 1, 5)));

        var edited = await SendAsync<ImmobilisationDetailsResponse>(s, HttpMethod.Put,
            $"/api/amortissement/{created.Immobilisation.Id}", Machine(new DateOnly(2025, 1, 5), duree: 4));

        Assert.Equal(4, edited.Echeances.Count);
        Assert.All(edited.Echeances, e => Assert.Equal(250_000m, e.DotationAnnuelle));

        var details = await SendAsync<ImmobilisationDetailsResponse>(s, HttpMethod.Get, $"/api/amortissement/{created.Immobilisation.Id}");
        Assert.Equal(4, details.Echeances.Count);
    }

    [Fact]
    public async Task Invalid_assets_are_refused()
    {
        var s = await SignUpOwnerAsync();

        var response = await SendAsync(s, HttpMethod.Post, "/api/amortissement",
            Machine(new DateOnly(2025, 1, 5)) with { ValeurResiduelle = 2_000_000m });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        response = await SendAsync(s, HttpMethod.Post, "/api/amortissement",
            Machine(new DateOnly(2025, 6, 1)) with { DateMiseEnService = new DateOnly(2025, 5, 1) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Regression against the source, whose SQL kept charging a sold asset's
    /// remaining dotations and kept it on every later balance sheet.</summary>
    [Fact]
    public async Task A_sold_asset_stops_depreciating_and_leaves_the_balance_sheet()
    {
        var s = await SignUpOwnerAsync();
        var created = await SendAsync<ImmobilisationDetailsResponse>(s, HttpMethod.Post, "/api/amortissement",
            Machine(new DateOnly(2023, 1, 5)));
        var id = created.Immobilisation.Id;

        var ceded = await SendAsync<ImmobilisationDetailsResponse>(s, HttpMethod.Put, $"/api/amortissement/{id}/ceder",
            new CederImmobilisationRequest(StatutsImmobilisation.Cede, new DateOnly(2025, 6, 30), 500_000m, "Remplacée"));

        // Depreciated 2023-2025: 600 000, VNC 400 000, sold 500 000.
        Assert.Equal(400_000m, ceded.VncALaSortie);
        Assert.Equal(100_000m, ceded.PlusMoinsValue);

        var list = await SendAsync<AmortissementListResponse>(s, HttpMethod.Get, "/api/amortissement?annee=2026");
        var immo = Assert.Single(list.Immobilisations);
        Assert.Equal(0m, immo.DotationAnneeCourante);
        Assert.Equal(600_000m, immo.AmortissementCumule);
        Assert.Equal(0, list.Stats.TotalImmobilisations);

        var bilan2024 = await SendAsync<BilanResponse>(s, HttpMethod.Get, "/api/bilan?annee=2024");
        Assert.Equal(1_000_000m, bilan2024.Immobilisations.ValeurBrute);
        Assert.Equal(400_000m, bilan2024.Immobilisations.Amortissements);

        var bilan2025 = await SendAsync<BilanResponse>(s, HttpMethod.Get, "/api/bilan?annee=2025");
        Assert.Equal(0m, bilan2025.Immobilisations.ValeurBrute);

        // Sold assets can no longer be edited, nor sold twice.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(s, HttpMethod.Put, $"/api/amortissement/{id}", Machine(new DateOnly(2023, 1, 5)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(s, HttpMethod.Put, $"/api/amortissement/{id}/ceder",
                new CederImmobilisationRequest(StatutsImmobilisation.Reforme, new DateOnly(2025, 7, 1), null, null))).StatusCode);
    }

    [Fact]
    public async Task An_asset_bought_after_a_year_end_is_not_on_that_years_balance_sheet()
    {
        var s = await SignUpOwnerAsync();
        await SendAsync<ImmobilisationDetailsResponse>(s, HttpMethod.Post, "/api/amortissement", Machine(new DateOnly(2026, 2, 1)));

        var bilan2025 = await SendAsync<BilanResponse>(s, HttpMethod.Get, "/api/bilan?annee=2025");
        Assert.Equal(0m, bilan2025.Immobilisations.ValeurBrute);

        var bilan2026 = await SendAsync<BilanResponse>(s, HttpMethod.Get, "/api/bilan?annee=2026");
        var corporelles = Assert.Single(bilan2026.ActifImmobilise, c => c.SousType == SousTypesCompte.Corporelles);
        Assert.Equal(1_000_000m, corporelles.SoldeAuto);
        var amortissements = Assert.Single(bilan2026.ActifImmobilise, c => c.SousType == SousTypesCompte.Amortissements);
        Assert.Equal(-183_333.33m, amortissements.Solde);   // 11 months of 200 000
    }

    [Fact]
    public async Task Default_accounts_are_seeded_once_per_table()
    {
        var s = await SignUpOwnerAsync();

        var first = await SendAsync<BilanComptesResponse>(s, HttpMethod.Get, "/api/bilan/comptes");
        var second = await SendAsync<BilanComptesResponse>(s, HttpMethod.Get, "/api/bilan/comptes");

        Assert.Equal(22, first.BilanComptes.Count);
        Assert.Equal(15, first.ResultatComptes.Count);
        Assert.Equal(22, second.BilanComptes.Count);
        Assert.All(first.BilanComptes, c => Assert.True(c.IsSystem));
    }

    [Fact]
    public async Task Ecritures_balance_the_sheet_and_follow_the_year_end()
    {
        var s = await SignUpOwnerAsync();
        var comptes = await SendAsync<BilanComptesResponse>(s, HttpMethod.Get, "/api/bilan/comptes");
        var capital = comptes.BilanComptes.Single(c => c.NumeroCompte == "10");
        var banque = comptes.BilanComptes.Single(c => c.NumeroCompte == "51");

        await SendAsync<BilanEcritureDto>(s, HttpMethod.Post, "/api/bilan/ecritures",
            new SaveBilanEcritureRequest(capital.Id, new DateOnly(2025, 1, 2), "Apport en capital", 0m, 500_000m));
        var depot = await SendAsync<BilanEcritureDto>(s, HttpMethod.Post, "/api/bilan/ecritures",
            new SaveBilanEcritureRequest(banque.Id, new DateOnly(2025, 1, 2), "Dépôt du capital", 500_000m, 0m));

        var bilan = await SendAsync<BilanResponse>(s, HttpMethod.Get, "/api/bilan?annee=2025");
        Assert.Equal(500_000m, bilan.CapitauxPropres.Single(c => c.Id == capital.Id).Solde);
        Assert.Equal(500_000m, bilan.TresorerieActif.Single(c => c.Id == banque.Id).Solde);
        Assert.Equal(0m, bilan.Ecart);

        // An entry dated after the year-end does not show on that year's sheet.
        await SendAsync<BilanEcritureDto>(s, HttpMethod.Post, "/api/bilan/ecritures",
            new SaveBilanEcritureRequest(banque.Id, new DateOnly(2026, 2, 1), "Retrait", 0m, 100_000m));
        bilan = await SendAsync<BilanResponse>(s, HttpMethod.Get, "/api/bilan?annee=2025");
        Assert.Equal(500_000m, bilan.TresorerieActif.Single(c => c.Id == banque.Id).Solde);

        // Editing moves the amount; deleting takes it back out.
        await SendAsync<BilanEcritureDto>(s, HttpMethod.Put, $"/api/bilan/ecritures/{depot.Id}",
            new SaveBilanEcritureRequest(banque.Id, new DateOnly(2025, 1, 2), "Dépôt du capital", 450_000m, 0m));
        bilan = await SendAsync<BilanResponse>(s, HttpMethod.Get, "/api/bilan?annee=2025");
        Assert.Equal(50_000m, bilan.Ecart * -1);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s, HttpMethod.Delete, $"/api/bilan/ecritures/{depot.Id}")).StatusCode);
        var ecritures = await SendAsync<List<BilanEcritureDto>>(s, HttpMethod.Get, $"/api/bilan/ecritures?compteId={banque.Id}");
        Assert.Single(ecritures);

        // The stored running balance is kept in step, as the source does.
        var stored = (await SendAsync<BilanComptesResponse>(s, HttpMethod.Get, "/api/bilan/comptes")).BilanComptes.Single(c => c.Id == banque.Id);
        Assert.Equal(-100_000m, stored.SoldeManuel);
    }

    [Fact]
    public async Task Accounts_protect_defaults_and_their_ecritures()
    {
        var s = await SignUpOwnerAsync();
        var comptes = await SendAsync<BilanComptesResponse>(s, HttpMethod.Get, "/api/bilan/comptes");
        var capital = comptes.BilanComptes.Single(c => c.NumeroCompte == "10");

        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(s, HttpMethod.Delete, $"/api/bilan/comptes/{capital.Id}?tableType=bilan")).StatusCode);

        // A default account keeps its type and sous-type whatever the request says.
        var renamed = await SendAsync<BilanCompteDto>(s, HttpMethod.Put, $"/api/bilan/comptes/{capital.Id}",
            new SaveBilanCompteRequest(TablesCompte.Bilan, "101", "Capital", TypesCompteBilan.DettesCourtTerme, "autre"));
        Assert.Equal("101", renamed.NumeroCompte);
        Assert.Equal(TypesCompteBilan.CapitauxPropres, renamed.TypeCompte);

        var caisse2 = await SendAsync<BilanCompteDto>(s, HttpMethod.Post, "/api/bilan/comptes",
            new SaveBilanCompteRequest(TablesCompte.Bilan, "531", "Caisse secondaire", TypesCompteBilan.TresorerieActif));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(s, HttpMethod.Post, "/api/bilan/comptes",
            new SaveBilanCompteRequest(TablesCompte.Bilan, "531", "Doublon", TypesCompteBilan.TresorerieActif))).StatusCode);

        await SendAsync<BilanEcritureDto>(s, HttpMethod.Post, "/api/bilan/ecritures",
            new SaveBilanEcritureRequest(caisse2.Id, new DateOnly(2025, 3, 1), "Fonds de caisse", 20_000m, 0m));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SendAsync(s, HttpMethod.Delete, $"/api/bilan/comptes/{caisse2.Id}?tableType=bilan")).StatusCode);
    }

    [Fact]
    public async Task Resultat_takes_sales_costs_charges_and_depreciation_and_carries_the_net_to_the_bilan()
    {
        var s = await SignUpOwnerAsync();
        var g = s.GroupId;

        var soda = new Product { GroupId = g, Name = "Soda", CostPrice = 9_999m, Price = 1000m, Quantity = 10 };
        var day = new DateTime(2025, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        var vente = new Vente
        {
            GroupId = g, NumeroVente = "V-1", DateVente = day, StatutPaiement = StatutPaiement.Partiel, MontantTotal = 10_000m,
            Items = [new VenteItem { ProductId = soda.Id, NomProduit = "Soda", Quantite = 10, PrixUnitaire = 1000m, PrixTotal = 10_000m }],
            Paiements = [new PaiementVente { Montant = 4_000m, DatePaiement = day }],
        };
        var annulee = new Vente
        {
            GroupId = g, NumeroVente = "V-2", DateVente = day, StatutPaiement = StatutPaiement.Annule, MontantTotal = 50_000m,
            Items = [new VenteItem { ProductId = soda.Id, NomProduit = "Soda", Quantite = 50, PrixUnitaire = 1000m, PrixTotal = 50_000m }],
        };

        await SeedAsync(db =>
        {
            db.Products.Add(soda);
            db.Ventes.AddRange(vente, annulee);
            // Cost at sale time 600, not today's 9 999.
            db.StockHistories.Add(new StockHistory
            {
                GroupId = g, ProductId = soda.Id, MovementType = StockMovementTypes.Vente, QuantityChanged = -10,
                UnitCost = 600m, TotalCost = 6_000m, ReferenceId = vente.Id, ReferenceType = "sale",
            });
            db.Charges.Add(new Charge { GroupId = g, Description = "Loyer", Montant = 1_000m, Categorie = "Loyer", Date = new DateTime(2025, 3, 1) });
            db.Charges.Add(new Charge { GroupId = g, Description = "Paie", Montant = 500m, Categorie = "Salaires", Date = new DateTime(2025, 3, 31) });
            db.Charges.Add(new Charge { GroupId = g, Description = "Divers", Montant = 200m, Categorie = "Bricoles", Date = new DateTime(2025, 4, 1) });
            db.Charges.Add(new Charge { GroupId = g, Description = "Loyer 2024", Montant = 7_777m, Categorie = "Loyer", Date = new DateTime(2024, 12, 1) });
        });

        await SendAsync<ImmobilisationDetailsResponse>(s, HttpMethod.Post, "/api/amortissement",
            Machine(new DateOnly(2025, 1, 5), valeur: 5_000m));

        var r = await SendAsync<ResultatResponse>(s, HttpMethod.Get, "/api/bilan/resultat?annee=2025&tzOffsetMinutes=0");

        Assert.Equal(10_000m, r.Integration.Ventes);
        Assert.Equal(6_000m, r.Integration.CoutMarchandisesVendues);
        Assert.Equal(1_700m, r.Integration.Charges);
        Assert.Equal(1_000m, r.Integration.DotationAmortissement);

        decimal Auto(IEnumerable<BilanCompteDto> comptes, string sousType) => comptes.Single(c => c.SousType == sousType).SoldeAuto;
        Assert.Equal(10_000m, Auto(r.ProduitsExploitation, SousTypesCompte.VentesMarchandises));
        Assert.Equal(0m, Auto(r.ProduitsExploitation, SousTypesCompte.VariationsStocks));
        Assert.Equal(6_000m, Auto(r.ChargesExploitation, SousTypesCompte.Achats));
        Assert.Equal(1_000m, Auto(r.ChargesExploitation, SousTypesCompte.ServicesExterieurs));
        Assert.Equal(500m, Auto(r.ChargesExploitation, SousTypesCompte.Personnel));
        Assert.Equal(200m, Auto(r.ChargesExploitation, SousTypesCompte.AutresCharges));
        Assert.Equal(1_000m, Auto(r.ChargesExploitation, SousTypesCompte.Amortissements));

        // 10 000 − 6 000 − 1 700 − 1 000.
        Assert.Equal(1_300m, r.ResultatExploitation);
        Assert.Equal(1_300m, r.ResultatNet);

        // An écriture on a hand-fed financial product joins the net; a credit increases a
        // produit account. An automatic account refuses one while CalculAutomatique is on.
        var interets = r.ProduitsFinanciers.Single();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s, HttpMethod.Post, "/api/bilan/ecritures",
            new SaveBilanEcritureRequest(interets.Id, new DateOnly(2025, 6, 1), "Intérêts reçus",
                MontantDebit: 0, MontantCredit: 200m, TableType: TablesCompte.Resultat))).StatusCode);
        var ventes70 = r.ProduitsExploitation.Single(c => c.SousType == SousTypesCompte.VentesMarchandises);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(s, HttpMethod.Post, "/api/bilan/ecritures",
            new SaveBilanEcritureRequest(ventes70.Id, new DateOnly(2025, 6, 1), "Tentative",
                MontantDebit: 0, MontantCredit: 1m, TableType: TablesCompte.Resultat))).StatusCode);

        r = await SendAsync<ResultatResponse>(s, HttpMethod.Get, "/api/bilan/resultat?annee=2025&tzOffsetMinutes=0");
        Assert.Equal(200m, r.ResultatFinancier);
        Assert.Equal(1_500m, r.ResultatNet);

        // The bilan carries the same net into account 12, and what the customer still owes.
        var bilan = await SendAsync<BilanResponse>(s, HttpMethod.Get, "/api/bilan?annee=2025&tzOffsetMinutes=0");
        Assert.Equal(1_500m, bilan.ResultatExercice);
        Assert.Equal(1_500m, bilan.CapitauxPropres.Single(c => c.SousType == SousTypesCompte.Resultat).SoldeAuto);
        Assert.Equal(6_000m, bilan.CreancesClients);
    }

    [Fact]
    public async Task Parametres_default_to_automatic_and_round_trip()
    {
        var s = await SignUpOwnerAsync();

        var initial = await SendAsync<ComptabiliteParametresDto>(s, HttpMethod.Get, "/api/bilan/parametres");
        Assert.True(initial.CalculAutomatique);

        var saved = await SendAsync<ComptabiliteParametresDto>(s, HttpMethod.Put, "/api/bilan/parametres",
            new SaveComptabiliteParametresRequest(false));
        Assert.False(saved.CalculAutomatique);

        var reread = await SendAsync<ComptabiliteParametresDto>(s, HttpMethod.Get, "/api/bilan/parametres");
        Assert.False(reread.CalculAutomatique);
    }

    /// <summary>With CalculAutomatique off, a sale no longer feeds "Ventes de marchandises" -
    /// the account only shows what was posted to it by hand, and now accepts an écriture at
    /// all, exactly like the four financial/exceptional accounts always could.</summary>
    [Fact]
    public async Task Turning_off_CalculAutomatique_stops_the_auto_feed_and_opens_every_account_to_ecritures()
    {
        var s = await SignUpOwnerAsync();
        var g = s.GroupId;

        var day = new DateTime(2025, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        await SeedAsync(db =>
        {
            var soda = new Product { GroupId = g, Name = "Soda", CostPrice = 500m, Price = 1000m, Quantity = 10 };
            var vente = new Vente
            {
                GroupId = g, NumeroVente = "V-AUTO-1", DateVente = day, StatutPaiement = StatutPaiement.Paye, MontantTotal = 10_000m,
                Items = [new VenteItem { ProductId = soda.Id, NomProduit = "Soda", Quantite = 10, PrixUnitaire = 1000m, PrixTotal = 10_000m }],
            };
            db.Products.Add(soda);
            db.Ventes.Add(vente);
        });

        await SendAsync(s, HttpMethod.Put, "/api/bilan/parametres", new SaveComptabiliteParametresRequest(false));

        var r = await SendAsync<ResultatResponse>(s, HttpMethod.Get, "/api/bilan/resultat?annee=2025&tzOffsetMinutes=0");
        Assert.False(r.CalculAutomatique);
        var ventes70 = r.ProduitsExploitation.Single(c => c.SousType == SousTypesCompte.VentesMarchandises);
        Assert.Equal(0m, ventes70.SoldeAuto);
        Assert.Equal(0m, ventes70.Solde);

        // The same account that refused an écriture in the previous test now accepts one.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s, HttpMethod.Post, "/api/bilan/ecritures",
            new SaveBilanEcritureRequest(ventes70.Id, new DateOnly(2025, 5, 10), "Vente comptoir",
                MontantDebit: 0, MontantCredit: 4_000m, TableType: TablesCompte.Resultat))).StatusCode);

        r = await SendAsync<ResultatResponse>(s, HttpMethod.Get, "/api/bilan/resultat?annee=2025&tzOffsetMinutes=0");
        Assert.Equal(4_000m, r.ProduitsExploitation.Single(c => c.SousType == SousTypesCompte.VentesMarchandises).Solde);
        Assert.Equal(4_000m, r.ResultatNet);
    }

    [Fact]
    public async Task A_closed_year_takes_its_closing_stock_from_the_next_years_opening_value()
    {
        var s = await SignUpOwnerAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s, HttpMethod.Put, "/api/bilan/stock-snapshot",
            new StockSnapshotRequest(2025, 30_000m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(s, HttpMethod.Put, "/api/bilan/stock-snapshot",
            new StockSnapshotRequest(2025, -1m))).StatusCode);

        var bilan2024 = await SendAsync<BilanResponse>(s, HttpMethod.Get, "/api/bilan?annee=2024");
        Assert.Equal(30_000m, bilan2024.Stocks);

        var r = await SendAsync<ResultatResponse>(s, HttpMethod.Get, "/api/bilan/resultat?annee=2025");
        Assert.Equal(30_000m, r.Integration.StockDebut);
        Assert.True(r.Integration.StockDebutSaisi);
    }
}
