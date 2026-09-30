using Lonnii.Api.Security;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The Bilan module: balance sheet, compte de résultat, chart of accounts and manual écritures.
/// Mirrors backend/routes/gestionBilan.js against the same <c>bilan_comptes</c>,
/// <c>bilan_ecritures</c>, <c>resultat_comptes</c> and <c>stock_snapshots</c> tables, and
/// feeds both statements from the other modules the same way it does - with these
/// differences, each of which made the source's figures wrong:
/// <list type="bullet">
/// <item>Cost of sales is costed exactly as Marges does (the purchase price at the time of
/// sale, recorded in stock_history), not by today's <c>products.cost_price</c> - products get
/// renamed and reused, which made it several times the revenue on real data.</item>
/// <item>"Variation de stocks" is shown but not counted: the cost of goods sold already
/// accounts for what left the shelves, and adding the variation on top counted the same goods
/// twice - buying stock alone showed a profit.</item>
/// <item>Customer receivables are what was still owed at the year-end (total less payments
/// received by then), not every pending sale's full amount.</item>
/// <item>Fixed assets follow <see cref="AmortissementRules"/>: owned at the year-end, and
/// depreciated only up to their exit year. Software goes to account 20
/// (incorporelles) rather than 21.</item>
/// <item>The year's net result is carried into account 12 (Résultat de l'exercice) - the
/// link between the two statements - and trésorerie passif counts toward total passif.</item>
/// </list>
/// Not ported: prestations revenue (the desktop has no Prestations module yet). Stock is split
/// by <c>products.stock_type</c> as the source does - matières premières 31, produits finis 33,
/// marchandises 37.
/// </summary>
public static class BilanEndpoints
{
    private sealed record CompteDefaut(string Numero, string Libelle, string Type, string SousType);

    /// <summary>gestionBilan.js's <c>defaultBilanAccounts</c>, seeded for a group the first
    /// time its Bilan is opened.</summary>
    private static readonly CompteDefaut[] DefaultBilanAccounts =
    [
        new("20", "Immobilisations incorporelles", TypesCompteBilan.ActifImmobilise, "incorporelles"),
        new("21", "Immobilisations corporelles", TypesCompteBilan.ActifImmobilise, SousTypesCompte.Corporelles),
        new("22", "Immobilisations en cours", TypesCompteBilan.ActifImmobilise, "en_cours"),
        new("23", "Avances et acomptes sur immobilisations", TypesCompteBilan.ActifImmobilise, "avances"),
        new("28", "Amortissements des immobilisations", TypesCompteBilan.ActifImmobilise, SousTypesCompte.Amortissements),
        new("31", "Stocks de matières premières", TypesCompteBilan.ActifCirculant, SousTypesCompte.StocksMatieres),
        new("33", "Stocks de produits finis", TypesCompteBilan.ActifCirculant, SousTypesCompte.StocksProduits),
        new("37", "Stocks de marchandises", TypesCompteBilan.ActifCirculant, SousTypesCompte.StocksMarchandises),
        new("41", "Clients et comptes rattachés", TypesCompteBilan.ActifCirculant, SousTypesCompte.Clients),
        new("44", "État et collectivités", TypesCompteBilan.ActifCirculant, "etat"),
        new("51", "Banques", TypesCompteBilan.TresorerieActif, "banques"),
        new("53", "Caisse", TypesCompteBilan.TresorerieActif, "caisse"),
        new("10", "Capital social", TypesCompteBilan.CapitauxPropres, "capital"),
        new("11", "Réserves", TypesCompteBilan.CapitauxPropres, "reserves"),
        new("12", "Résultat de l'exercice", TypesCompteBilan.CapitauxPropres, SousTypesCompte.Resultat),
        new("13", "Report à nouveau", TypesCompteBilan.CapitauxPropres, "report"),
        new("16", "Emprunts et dettes à long terme", TypesCompteBilan.DettesLongTerme, "emprunts"),
        new("17", "Dettes de crédit-bail", TypesCompteBilan.DettesLongTerme, "credit_bail"),
        new("40", "Fournisseurs et comptes rattachés", TypesCompteBilan.DettesCourtTerme, "fournisseurs"),
        new("42", "Personnel et comptes rattachés", TypesCompteBilan.DettesCourtTerme, "personnel"),
        new("43", "Organismes sociaux", TypesCompteBilan.DettesCourtTerme, "organismes_sociaux"),
        new("44D", "État - Dettes fiscales", TypesCompteBilan.DettesCourtTerme, "etat_dettes"),
    ];

    /// <summary>gestionBilan.js's <c>defaultResultatAccounts</c>.</summary>
    private static readonly CompteDefaut[] DefaultResultatAccounts =
    [
        new("70", "Ventes de marchandises", TypesCompteResultat.ProduitExploitation, SousTypesCompte.VentesMarchandises),
        new("71", "Production vendue - Services", TypesCompteResultat.ProduitExploitation, SousTypesCompte.Services),
        new("73", "Variations de stocks", TypesCompteResultat.ProduitExploitation, SousTypesCompte.VariationsStocks),
        new("75", "Autres produits d'exploitation", TypesCompteResultat.ProduitExploitation, "autres_produits"),
        new("60", "Achats de marchandises", TypesCompteResultat.ChargeExploitation, SousTypesCompte.Achats),
        new("61", "Services extérieurs", TypesCompteResultat.ChargeExploitation, SousTypesCompte.ServicesExterieurs),
        new("62", "Autres services extérieurs", TypesCompteResultat.ChargeExploitation, SousTypesCompte.AutresServices),
        new("63", "Impôts et taxes", TypesCompteResultat.ChargeExploitation, SousTypesCompte.ImpotsTaxes),
        new("64", "Charges de personnel", TypesCompteResultat.ChargeExploitation, SousTypesCompte.Personnel),
        new("65", "Autres charges d'exploitation", TypesCompteResultat.ChargeExploitation, SousTypesCompte.AutresCharges),
        new("68", "Dotations aux amortissements", TypesCompteResultat.ChargeExploitation, SousTypesCompte.Amortissements),
        new("76", "Produits financiers", TypesCompteResultat.ProduitFinancier, "interets_recus"),
        new("66", "Charges financières", TypesCompteResultat.ChargeFinanciere, "interets_payes"),
        new("77", "Produits exceptionnels", TypesCompteResultat.ProduitExceptionnel, "exceptionnel"),
        new("67", "Charges exceptionnelles", TypesCompteResultat.ChargeExceptionnelle, "exceptionnel"),
    ];

    /// <summary>
    /// gestionBilan.js's <c>categorieToSousType</c>: which résultat account a charge lands on,
    /// from its category name (lower-cased and trimmed). Any other category goes to 65 -
    /// Autres charges d'exploitation.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> CategorieVersSousType = BuildCategorieMap();

    private static Dictionary<string, string> BuildCategorieMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string sousType, params string[] categories)
        {
            foreach (var c in categories) map[c] = sousType;
        }

        Add(SousTypesCompte.Achats,
            "marchandises", "marchandise", "achat", "achats", "achat de marchandises", "achats de marchandises",
            "matières premières", "matieres premieres", "matière première", "matiere premiere",
            "consomable", "consommable", "consommables", "approvisionnement", "approvisionnements",
            "stock", "réapprovisionnement");
        Add(SousTypesCompte.ServicesExterieurs,
            "loyer", "loyers", "électricité", "electricité", "electricite", "éléctricité",
            "eau", "facture eau", "internet", "wifi", "entretien", "nettoyage",
            "réparation", "reparation", "réparations", "reparations", "téléphone", "telephone",
            "location", "abonnement", "abonnements", "énergie", "energie", "gaz");
        Add(SousTypesCompte.AutresServices,
            "marketing", "publicité", "publicite", "pub", "service", "services", "consulting", "conseil",
            "honoraires", "comptabilité", "comptabilite", "avocat", "formation", "déplacement",
            "deplacement", "mission");
        Add(SousTypesCompte.ImpotsTaxes,
            "taxes", "taxe", "impôts", "impots", "impôt", "impot", "tva", "patente",
            "fiscalité", "fiscalite", "droits", "douane");
        Add(SousTypesCompte.Personnel,
            "salaires", "salaire", "personnel", "paie", "paye", "main d'oeuvre", "employés", "employes",
            "cotisations", "cotisation", "cnss", "charges sociales", "masse salariale", "rémunération",
            "remuneration", "prime", "primes", "bonus", "indemnité", "indemnite", "indemnités");
        Add(SousTypesCompte.AutresCharges,
            "fournitures", "fourniture", "transport", "transports", "livraison", "livraisons",
            "assurances", "assurance", "maintenance", "machine", "machines", "matériel", "materiel",
            "matériel électrique", "materiel electrique", "équipement", "equipement", "divers",
            "autre", "autres", "dépense", "depense", "dépenses", "depenses", "maison", "bureau",
            "display", "aménagement", "amenagement");
        return map;
    }

    internal static string SousTypePourCategorie(string? categorie) =>
        CategorieVersSousType.GetValueOrDefault((categorie ?? string.Empty).Trim().ToLowerInvariant(), SousTypesCompte.AutresCharges);

    public static void MapBilanEndpoints(this IEndpointRouteBuilder app)
    {
        var bilan = app.MapGroup("/api/bilan").WithTags("Bilan");

        bilan.MapGet("/", GetBilanAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewBilan);
        bilan.MapGet("/resultat", GetResultatAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewResultat);

        bilan.MapPut("/stock-snapshot", SetStockSnapshotAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.EditResultatDonnees);

        bilan.MapGet("/parametres", GetParametresAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewBilan);
        bilan.MapPut("/parametres", SaveParametresAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.EditResultatDonnees);

        bilan.MapGet("/comptes", ListComptesAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewBilan);
        bilan.MapPost("/comptes", CreateCompteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageBilanComptes);
        bilan.MapPut("/comptes/{id:int}", UpdateCompteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageBilanComptes);
        bilan.MapDelete("/comptes/{id:int}", DeleteCompteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageBilanComptes);

        bilan.MapGet("/ecritures", ListEcrituresAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewBilan);
        bilan.MapPost("/ecritures", CreateEcritureAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.AddBilanEcriture);
        bilan.MapPut("/ecritures/{id:int}", UpdateEcritureAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.EditBilanEcriture);
        bilan.MapDelete("/ecritures/{id:int}", DeleteEcritureAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.DeleteBilanEcriture);
    }

    // --- Parametres ---

    private static async Task<IResult> GetParametresAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct) =>
        Results.Ok(new ComptabiliteParametresDto(await LoadCalculAutomatiqueAsync(db, scope.GroupId, ct)));

    private static async Task<IResult> SaveParametresAsync(
        SaveComptabiliteParametresRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var parametres = await db.ComptabiliteParametres.FirstOrDefaultAsync(p => p.GroupId == scope.GroupId, ct);
        if (parametres is null)
        {
            parametres = new ComptabiliteParametres { GroupId = scope.GroupId };
            db.ComptabiliteParametres.Add(parametres);
        }

        parametres.CalculAutomatique = request.CalculAutomatique;
        parametres.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new ComptabiliteParametresDto(parametres.CalculAutomatique));
    }

    /// <summary>True (the default) until a group turns automatic calculation off - see
    /// <see cref="ComptabiliteParametres"/>.</summary>
    private static async Task<bool> LoadCalculAutomatiqueAsync(LonniiDbContext db, string groupId, CancellationToken ct) =>
        (await db.ComptabiliteParametres.AsNoTracking().FirstOrDefaultAsync(p => p.GroupId == groupId, ct))
            ?.CalculAutomatique ?? true;

    // --- Year boundaries ---

    /// <summary>A calendar year of the caller's own clock, and the UTC instants it covers -
    /// sales are stored in UTC, as MargesEndpoints handles them.</summary>
    private sealed record Exercice(int Annee, bool EnCours, DateOnly Fin, DateTime DebutUtc, DateTime FinUtc);

    private static Exercice ExerciceDe(int? annee, int? tzOffsetMinutes)
    {
        var offset = TimeSpan.FromMinutes(tzOffsetMinutes ?? 0);
        var today = DateOnly.FromDateTime(DateTime.UtcNow + offset);
        var year = annee ?? today.Year;

        return new Exercice(
            year, year == today.Year, new DateOnly(year, 12, 31),
            new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc) - offset,
            new DateTime(year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc) - offset);
    }

    // --- Bilan ---

    private static async Task<IResult> GetBilanAsync(
        int? annee, int? tzOffsetMinutes, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var groupId = scope.GroupId;
        var ex = ExerciceDe(annee, tzOffsetMinutes);
        await EnsureDefaultAccountsAsync(db, groupId, scope.UserId, ct);

        var calculAutomatique = await LoadCalculAutomatiqueAsync(db, groupId, ct);
        var comptes = await db.BilanComptes.AsNoTracking().Where(c => c.GroupId == groupId).ToListAsync(ct);

        // Écritures re-summed up to the year-end rather than the stored running solde, as the
        // source does - that is what lets a closed year be viewed as it stood.
        var ecritures = await db.BilanEcritures.AsNoTracking()
            .Where(e => e.GroupId == groupId && e.TableType == TablesCompte.Bilan && e.DateEcriture <= ex.Fin)
            .Select(e => new { e.CompteId, e.MontantDebit, e.MontantCredit })
            .ToListAsync(ct);
        var mouvements = ecritures
            .GroupBy(e => e.CompteId)
            .ToDictionary(g => g.Key, g => (Debit: g.Sum(e => e.MontantDebit ?? 0m), Credit: g.Sum(e => e.MontantCredit ?? 0m)));

        var auto = new Dictionary<int, decimal>();
        void Inject(string type, string sousType, decimal montant)
        {
            // Off, every figure below is posted by hand instead - see ComptabiliteParametres.
            if (!calculAutomatique || montant == 0) return;
            if (comptes.FirstOrDefault(c => c.TypeCompte == type && c.SousType == sousType) is { } compte)
                auto[compte.Id] = auto.GetValueOrDefault(compte.Id) + montant;
        }

        // Fixed assets owned at the year-end.
        var immos = await db.Immobilisations.AsNoTracking()
            .Include(i => i.Echeances)
            .Where(i => i.GroupId == groupId)
            .ToListAsync(ct);
        var auBilan = immos.Where(i => AmortissementRules.AuBilan(i, ex.Fin)).ToList();
        var brutCorporelles = auBilan.Where(i => i.Categorie != CategoriesImmobilisation.Logiciel).Sum(i => i.ValeurAcquisition);
        var brutIncorporelles = auBilan.Where(i => i.Categorie == CategoriesImmobilisation.Logiciel).Sum(i => i.ValeurAcquisition);
        var amortissements = auBilan.Sum(i => AmortissementRules.CumuleAu(i, AmortissementRules.Echeances(i), ex.Annee));

        Inject(TypesCompteBilan.ActifImmobilise, SousTypesCompte.Corporelles, brutCorporelles);
        Inject(TypesCompteBilan.ActifImmobilise, "incorporelles", brutIncorporelles);
        Inject(TypesCompteBilan.ActifImmobilise, SousTypesCompte.Amortissements, -amortissements);

        // Stock: live for the year in progress, split by product type the way Lonnii Business's
        // gestionBilan.js does (31 / 33 / 37). A closed year only has the next year's opening
        // total in its snapshot, with no split, so it stays under marchandises.
        decimal stock;
        if (ex.EnCours)
        {
            var parType = await StockParTypeAsync(db, groupId, ct);
            stock = parType.Values.Sum();
            Inject(TypesCompteBilan.ActifCirculant, SousTypesCompte.StocksMatieres,
                parType.GetValueOrDefault(SousTypesCompte.StocksMatieres));
            Inject(TypesCompteBilan.ActifCirculant, SousTypesCompte.StocksProduits,
                parType.GetValueOrDefault(SousTypesCompte.StocksProduits));
            Inject(TypesCompteBilan.ActifCirculant, SousTypesCompte.StocksMarchandises,
                parType.GetValueOrDefault(SousTypesCompte.StocksMarchandises));
        }
        else
        {
            stock = (await db.StockSnapshots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.GroupId == groupId && s.Annee == ex.Annee + 1, ct))?.StockValueDebut ?? 0m;
            Inject(TypesCompteBilan.ActifCirculant, SousTypesCompte.StocksMarchandises, stock);
        }

        var creances = await CreancesClientsAsync(db, groupId, ex.FinUtc, ct);
        Inject(TypesCompteBilan.ActifCirculant, SousTypesCompte.Clients, creances);

        var resultat = await ComputeResultatAsync(db, groupId, ex, persistSnapshot: false, calculAutomatique, ct);
        Inject(TypesCompteBilan.CapitauxPropres, SousTypesCompte.Resultat, resultat.ResultatNet);

        BilanCompteDto Row(BilanCompte c)
        {
            var (debit, credit) = mouvements.GetValueOrDefault(c.Id);
            var manuel = TypesCompteBilan.IsActif(c.TypeCompte) ? debit - credit : credit - debit;
            var montantAuto = auto.GetValueOrDefault(c.Id);
            return ToDto(c, manuel, montantAuto);
        }

        List<BilanCompteDto> Section(string type) => comptes
            .Where(c => c.TypeCompte == type)
            .OrderBy(c => c.NumeroCompte, StringComparer.Ordinal)
            .Select(Row)
            .ToList();

        var actifImmobilise = Section(TypesCompteBilan.ActifImmobilise);
        var actifCirculant = Section(TypesCompteBilan.ActifCirculant);
        var tresorerieActif = Section(TypesCompteBilan.TresorerieActif);
        var capitauxPropres = Section(TypesCompteBilan.CapitauxPropres);
        var dettesLongTerme = Section(TypesCompteBilan.DettesLongTerme);
        var dettesCourtTerme = Section(TypesCompteBilan.DettesCourtTerme);
        var tresoreriePassif = Section(TypesCompteBilan.TresoreriePassif);

        var totalActif = R(actifImmobilise.Concat(actifCirculant).Concat(tresorerieActif).Sum(c => c.Solde));
        var totalPassif = R(capitauxPropres.Concat(dettesLongTerme).Concat(dettesCourtTerme).Concat(tresoreriePassif).Sum(c => c.Solde));

        return Results.Ok(new BilanResponse(
            ex.Annee, ex.EnCours,
            actifImmobilise, actifCirculant, tresorerieActif,
            capitauxPropres, dettesLongTerme, dettesCourtTerme, tresoreriePassif,
            new BilanImmobilisationsDto(brutCorporelles + brutIncorporelles, amortissements,
                brutCorporelles + brutIncorporelles - amortissements),
            stock, creances, resultat.ResultatNet,
            totalActif, totalPassif, R(totalActif - totalPassif)));
    }

    /// <summary>Stock at cost: every active product's purchase price × quantity on hand, as
    /// the source values it. Products with no purchase price, or none in stock, count nothing.</summary>
    private static async Task<decimal> StockActuelAsync(LonniiDbContext db, string groupId, CancellationToken ct) =>
        (await StockParTypeAsync(db, groupId, ct)).Values.Sum();

    /// <summary>The same valuation, keyed by the stock sub-account each product belongs to.
    /// Internal-use items (autre) have no account of their own here and stay with the
    /// marchandises, as all stock did before the split.</summary>
    private static async Task<Dictionary<string, decimal>> StockParTypeAsync(
        LonniiDbContext db, string groupId, CancellationToken ct)
    {
        // Multiplied in C#: under SQLite both columns go through the money converter, and a
        // product of two converted columns would be scaled twice (see LonniiDbContext).
        var rows = await db.Products.AsNoTracking()
            .Where(p => p.GroupId == groupId && p.IsActive && p.DeletedAt == null && !p.StockIllimite
                        && p.Quantity > 0 && p.CostPrice != null)
            .Select(p => new { p.Quantity, p.CostPrice, p.TypeProduit })
            .ToListAsync(ct);

        return rows
            .Where(r => r.CostPrice > 0)
            .GroupBy(r => r.TypeProduit switch
            {
                ProductTypes.MatierePremiere => SousTypesCompte.StocksMatieres,
                ProductTypes.ProduitFini => SousTypesCompte.StocksProduits,
                _ => SousTypesCompte.StocksMarchandises,
            })
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity * r.CostPrice!.Value));
    }

    /// <summary>What customers still owed at <paramref name="finUtc"/>: every non-cancelled
    /// sale made before then, less the payments received before then.</summary>
    private static async Task<decimal> CreancesClientsAsync(
        LonniiDbContext db, string groupId, DateTime finUtc, CancellationToken ct)
    {
        var ventes = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && v.DateVente < finUtc)
            .Select(v => new { v.Id, v.MontantTotal, v.StatutPaiement })
            .ToListAsync(ct);

        var paiements = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && v.DateVente < finUtc)
            .Join(db.PaiementsVentes.Where(p => p.DatePaiement < finUtc), v => v.Id, p => p.VenteId,
                (v, p) => new { p.VenteId, p.Montant })
            .ToListAsync(ct);
        var paye = paiements.GroupBy(p => p.VenteId).ToDictionary(g => g.Key, g => g.Sum(p => p.Montant));

        return ventes
            .Where(v => StatutPaiement.Normalise(v.StatutPaiement) != StatutPaiement.Annule)
            .Sum(v => Math.Max(v.MontantTotal - paye.GetValueOrDefault(v.Id), 0m));
    }

    // --- Compte de résultat ---

    private static async Task<IResult> GetResultatAsync(
        int? annee, int? tzOffsetMinutes, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        await EnsureDefaultAccountsAsync(db, scope.GroupId, scope.UserId, ct);
        var ex = ExerciceDe(annee, tzOffsetMinutes);
        var calculAutomatique = await LoadCalculAutomatiqueAsync(db, scope.GroupId, ct);
        return Results.Ok(await ComputeResultatAsync(db, scope.GroupId, ex, persistSnapshot: true, calculAutomatique, ct, scope.UserId));
    }

    private static async Task<ResultatResponse> ComputeResultatAsync(
        LonniiDbContext db, string groupId, Exercice ex, bool persistSnapshot, bool calculAutomatique,
        CancellationToken ct, string? userId = null)
    {
        var comptes = await db.ResultatComptes.AsNoTracking().Where(c => c.GroupId == groupId).ToListAsync(ct);

        // Financier/exceptionnel écritures, dated within the year - a compte de résultat is a
        // flow for the year alone, unlike the bilan's cumulative-to-year-end balance. Every
        // other résultat account only takes one once CalculAutomatique is off (see Inject
        // below), at which point its own case reaches here too.
        var debutExercice = new DateOnly(ex.Annee, 1, 1);
        var resultatEcritures = await db.BilanEcritures.AsNoTracking()
            .Where(e => e.GroupId == groupId && e.TableType == TablesCompte.Resultat
                        && e.DateEcriture >= debutExercice && e.DateEcriture <= ex.Fin)
            .Select(e => new { e.CompteId, e.MontantDebit, e.MontantCredit })
            .ToListAsync(ct);
        var resultatMouvements = resultatEcritures
            .GroupBy(e => e.CompteId)
            .ToDictionary(g => g.Key, g => (Debit: g.Sum(e => e.MontantDebit ?? 0m), Credit: g.Sum(e => e.MontantCredit ?? 0m)));

        // Sales and their cost, costed the same way as the Marges screen.
        var ventes = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && v.DateVente >= ex.DebutUtc && v.DateVente < ex.FinUtc)
            .Select(v => new { v.MontantTotal, v.StatutPaiement })
            .ToListAsync(ct);
        var ventesTotal = ventes
            .Where(v => StatutPaiement.Normalise(v.StatutPaiement) != StatutPaiement.Annule)
            .Sum(v => v.MontantTotal);

        var products = await MargesEndpoints.LoadProductsAsync(db, groupId, ct);
        var lines = await MargesEndpoints.SaleLinesAsync(db, groupId, products, ex.DebutUtc, ex.FinUtc, ct);
        var coutVentes = lines.Sum(l => l.Cost);
        var estimees = lines.Count(l => l.CostSource == MargeCostSources.Estime);

        // Charges, onto the account their category maps to. Charge.Date is already a
        // calendar day of the shop's own clock.
        var debutAnnee = new DateTime(ex.Annee, 1, 1);
        var charges = await db.Charges.AsNoTracking()
            .Where(c => c.GroupId == groupId && c.Date >= debutAnnee && c.Date < debutAnnee.AddYears(1))
            .Select(c => new { c.Categorie, c.Montant })
            .ToListAsync(ct);
        var chargesParSousType = charges
            .GroupBy(c => SousTypePourCategorie(c.Categorie))
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Montant));

        // Depreciation charged for the year.
        var immos = await db.Immobilisations.AsNoTracking()
            .Include(i => i.Echeances)
            .Where(i => i.GroupId == groupId)
            .ToListAsync(ct);
        var dotation = immos.Sum(i => AmortissementRules.DotationDe(i, AmortissementRules.Echeances(i), ex.Annee));

        // Stock at 1 January and 31 December - information only, see the class summary.
        var stockActuel = await StockActuelAsync(db, groupId, ct);
        var snapshots = await db.StockSnapshots.AsNoTracking()
            .Where(s => s.GroupId == groupId && (s.Annee == ex.Annee || s.Annee == ex.Annee + 1))
            .ToListAsync(ct);
        var snapshotDebut = snapshots.FirstOrDefault(s => s.Annee == ex.Annee);
        var snapshotFin = snapshots.FirstOrDefault(s => s.Annee == ex.Annee + 1);

        decimal stockDebut;
        if (snapshotDebut is not null)
        {
            stockDebut = snapshotDebut.StockValueDebut ?? 0m;
        }
        else if (ex.EnCours && persistSnapshot && userId is not null)
        {
            // The first time the current year is opened, today's stock is the best available
            // opening value - as the source does. It can be corrected by hand.
            stockDebut = stockActuel;
            db.StockSnapshots.Add(new StockSnapshot { GroupId = groupId, Annee = ex.Annee, StockValueDebut = stockActuel, CreatedBy = userId });
            await db.SaveChangesAsync(ct);
        }
        else
        {
            stockDebut = ex.EnCours ? stockActuel : 0m;
        }

        var stockFin = ex.EnCours ? stockActuel : snapshotFin?.StockValueDebut ?? 0m;

        // Each automatic amount goes to the first account carrying its sous-type; a charge
        // category whose account no longer exists falls back to 65, then to any charge account.
        var auto = new Dictionary<int, decimal>();
        void Inject(string type, string sousType, decimal montant)
        {
            // Off, every account below - not just the four financial/exceptional ones -
            // instead takes its figure from an écriture, the same way those four already did.
            if (!calculAutomatique || montant == 0) return;
            var compte = comptes.Where(c => c.TypeCompte == type).OrderBy(c => c.NumeroCompte, StringComparer.Ordinal)
                             .FirstOrDefault(c => c.SousType == sousType)
                         ?? (type == TypesCompteResultat.ChargeExploitation
                             ? comptes.FirstOrDefault(c => c.TypeCompte == type && c.SousType == SousTypesCompte.AutresCharges)
                               ?? comptes.FirstOrDefault(c => c.TypeCompte == type)
                             : null);
            if (compte is not null) auto[compte.Id] = auto.GetValueOrDefault(compte.Id) + montant;
        }

        Inject(TypesCompteResultat.ProduitExploitation, SousTypesCompte.VentesMarchandises, ventesTotal);
        Inject(TypesCompteResultat.ChargeExploitation, SousTypesCompte.Achats, coutVentes);
        foreach (var (sousType, montant) in chargesParSousType)
            Inject(TypesCompteResultat.ChargeExploitation, sousType, montant);
        Inject(TypesCompteResultat.ChargeExploitation, SousTypesCompte.Amortissements, dotation);

        // Produits are credit-normal, charges debit-normal - same branch GetBilanAsync's Row()
        // makes for actif/passif, just on TypesCompteResultat instead of TypesCompteBilan.
        List<BilanCompteDto> Section(string type) => comptes
            .Where(c => c.TypeCompte == type)
            .OrderBy(c => c.NumeroCompte, StringComparer.Ordinal)
            .Select(c =>
            {
                var (debit, credit) = resultatMouvements.GetValueOrDefault(c.Id);
                var manuel = TypesCompteResultat.IsProduit(c.TypeCompte) ? credit - debit : debit - credit;
                return ToDto(c, manuel, auto.GetValueOrDefault(c.Id));
            })
            .ToList();

        var pe = Section(TypesCompteResultat.ProduitExploitation);
        var ce = Section(TypesCompteResultat.ChargeExploitation);
        var pf = Section(TypesCompteResultat.ProduitFinancier);
        var cf = Section(TypesCompteResultat.ChargeFinanciere);
        var px = Section(TypesCompteResultat.ProduitExceptionnel);
        var cx = Section(TypesCompteResultat.ChargeExceptionnelle);

        static decimal Sum(IEnumerable<BilanCompteDto> rows) => rows.Sum(r => r.Solde);

        var exploitation = Sum(pe) - Sum(ce);
        var financier = Sum(pf) - Sum(cf);
        var exceptionnel = Sum(px) - Sum(cx);

        return new ResultatResponse(
            ex.Annee, pe, ce, pf, cf, px, cx,
            new ResultatIntegrationDto(
                ventesTotal, coutVentes, chargesParSousType.GetValueOrDefault(SousTypesCompte.Achats),
                charges.Sum(c => c.Montant), dotation,
                stockDebut, stockFin, stockFin - stockDebut, snapshotDebut is not null, estimees),
            R(Sum(pe) + Sum(pf) + Sum(px)),
            R(Sum(ce) + Sum(cf) + Sum(cx)),
            R(exploitation), R(financier), R(exceptionnel),
            R(exploitation + financier + exceptionnel),
            calculAutomatique);
    }

    private static async Task<IResult> SetStockSnapshotAsync(
        StockSnapshotRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.Annee is < 2000 or > 2100) return Results.BadRequest(new ApiError("Année invalide"));
        if (request.StockValueDebut < 0) return Results.BadRequest(new ApiError("Valeur du stock invalide"));

        var snapshot = await db.StockSnapshots
            .FirstOrDefaultAsync(s => s.GroupId == scope.GroupId && s.Annee == request.Annee, ct);
        if (snapshot is null)
        {
            snapshot = new StockSnapshot { GroupId = scope.GroupId, Annee = request.Annee };
            db.StockSnapshots.Add(snapshot);
        }

        snapshot.StockValueDebut = request.StockValueDebut;
        snapshot.SnapshotDate = DateTime.UtcNow;
        snapshot.CreatedBy = scope.UserId;

        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    /// <summary>Whether a résultat account may take an écriture: always the four
    /// financial/exceptional ones, or - once CalculAutomatique is off and nothing feeds any
    /// résultat account automatically any more - any of them.</summary>
    private static bool EcritureEligible(ResultatCompte compte, bool calculAutomatique) =>
        TypesCompteResultat.IsManuel(compte.TypeCompte) || !calculAutomatique;

    // --- Comptes ---

    private static async Task<IResult> ListComptesAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        await EnsureDefaultAccountsAsync(db, scope.GroupId, scope.UserId, ct);

        var bilan = await db.BilanComptes.AsNoTracking().Where(c => c.GroupId == scope.GroupId).ToListAsync(ct);
        var resultat = await db.ResultatComptes.AsNoTracking().Where(c => c.GroupId == scope.GroupId).ToListAsync(ct);

        return Results.Ok(new BilanComptesResponse(
            bilan.OrderBy(c => c.NumeroCompte, StringComparer.Ordinal).Select(c => ToDto(c, c.Solde ?? 0m, 0m)).ToList(),
            resultat.OrderBy(c => c.NumeroCompte, StringComparer.Ordinal).Select(c => ToDto(c, c.Solde ?? 0m, 0m)).ToList()));
    }

    private static async Task<IResult> CreateCompteAsync(
        SaveBilanCompteRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (await ValidateCompteAsync(request, null, scope.GroupId, db, ct) is { } error)
            return Results.BadRequest(new ApiError(error));

        if (request.TableType == TablesCompte.Resultat)
        {
            var compte = new ResultatCompte { GroupId = scope.GroupId, CreatedBy = scope.UserId, IsSystem = false, Solde = 0m };
            ApplyCompte(compte, request);
            db.ResultatComptes.Add(compte);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(compte, 0m, 0m));
        }
        else
        {
            var compte = new BilanCompte { GroupId = scope.GroupId, CreatedBy = scope.UserId, IsSystem = false, Solde = 0m };
            ApplyCompte(compte, request);
            db.BilanComptes.Add(compte);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(compte, 0m, 0m));
        }
    }

    /// <summary>A default account keeps its type and sous-type - they are what the automatic
    /// integrations find it by - but can be renumbered, renamed and described.</summary>
    private static async Task<IResult> UpdateCompteAsync(
        int id, SaveBilanCompteRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.TableType == TablesCompte.Resultat)
        {
            var compte = await db.ResultatComptes.FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
            if (compte is null) return Results.NotFound(new ApiError("Compte introuvable"));

            var effective = compte.IsSystem == true
                ? request with { TypeCompte = compte.TypeCompte, SousType = compte.SousType }
                : request;
            if (await ValidateCompteAsync(effective, id, scope.GroupId, db, ct) is { } error)
                return Results.BadRequest(new ApiError(error));

            ApplyCompte(compte, effective);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(compte, compte.Solde ?? 0m, 0m));
        }
        else
        {
            var compte = await db.BilanComptes.FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
            if (compte is null) return Results.NotFound(new ApiError("Compte introuvable"));

            var effective = compte.IsSystem == true
                ? request with { TypeCompte = compte.TypeCompte, SousType = compte.SousType }
                : request;
            if (await ValidateCompteAsync(effective, id, scope.GroupId, db, ct) is { } error)
                return Results.BadRequest(new ApiError(error));

            ApplyCompte(compte, effective);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(compte, compte.Solde ?? 0m, 0m));
        }
    }

    /// <summary>Not in the source, which has no way to remove an account at all. Default
    /// accounts cannot be deleted (their <c>is_system</c> flag says so in the source schema),
    /// nor can one that still carries écritures.</summary>
    private static async Task<IResult> DeleteCompteAsync(
        int id, string? tableType, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (tableType == TablesCompte.Resultat)
        {
            var compte = await db.ResultatComptes.FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
            if (compte is null) return Results.NotFound(new ApiError("Compte introuvable"));
            if (compte.IsSystem == true) return Results.BadRequest(new ApiError("Un compte par défaut ne peut pas être supprimé"));

            db.ResultatComptes.Remove(compte);
        }
        else
        {
            var compte = await db.BilanComptes.FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
            if (compte is null) return Results.NotFound(new ApiError("Compte introuvable"));
            if (compte.IsSystem == true) return Results.BadRequest(new ApiError("Un compte par défaut ne peut pas être supprimé"));
            if (await db.BilanEcritures.AnyAsync(e => e.CompteId == id, ct))
                return Results.BadRequest(new ApiError("Ce compte porte des écritures : supprimez-les d'abord"));

            db.BilanComptes.Remove(compte);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    private static async Task<string?> ValidateCompteAsync(
        SaveBilanCompteRequest r, int? id, string groupId, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.NumeroCompte) || string.IsNullOrWhiteSpace(r.Libelle) || string.IsNullOrWhiteSpace(r.TypeCompte))
            return "Numéro, libellé et type de compte requis";
        if (r.NumeroCompte.Trim().Length > 20)
            return "Le numéro de compte ne peut pas dépasser 20 caractères";

        var numero = r.NumeroCompte.Trim();
        if (r.TableType == TablesCompte.Resultat)
        {
            if (!TypesCompteResultat.All.Contains(r.TypeCompte)) return "Type de compte invalide";
            if (await db.ResultatComptes.AnyAsync(c => c.GroupId == groupId && c.NumeroCompte == numero && c.Id != id, ct))
                return $"Le compte {numero} existe déjà";
        }
        else if (r.TableType == TablesCompte.Bilan)
        {
            if (!TypesCompteBilan.All.Contains(r.TypeCompte)) return "Type de compte invalide";
            if (await db.BilanComptes.AnyAsync(c => c.GroupId == groupId && c.NumeroCompte == numero && c.Id != id, ct))
                return $"Le compte {numero} existe déjà";
        }
        else
        {
            return "Table de compte invalide";
        }

        return null;
    }

    private static void ApplyCompte(BilanCompte c, SaveBilanCompteRequest r)
    {
        c.NumeroCompte = r.NumeroCompte.Trim();
        c.Libelle = r.Libelle.Trim();
        c.TypeCompte = r.TypeCompte;
        c.SousType = Blank(r.SousType);
        c.Description = Blank(r.Description);
        c.UpdatedAt = DateTime.UtcNow;
    }

    private static void ApplyCompte(ResultatCompte c, SaveBilanCompteRequest r)
    {
        c.NumeroCompte = r.NumeroCompte.Trim();
        c.Libelle = r.Libelle.Trim();
        c.TypeCompte = r.TypeCompte;
        c.SousType = Blank(r.SousType);
        c.Description = Blank(r.Description);
        c.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Seeds the default accounts - each table on its own, where the source only
    /// checked bilan_comptes and so never seeded résultat accounts for a group whose bilan
    /// accounts already existed.</summary>
    internal static async Task EnsureDefaultAccountsAsync(LonniiDbContext db, string groupId, string userId, CancellationToken ct)
    {
        var changed = false;

        if (!await db.BilanComptes.AnyAsync(c => c.GroupId == groupId, ct))
        {
            db.BilanComptes.AddRange(DefaultBilanAccounts.Select(d => new BilanCompte
            {
                GroupId = groupId, NumeroCompte = d.Numero, Libelle = d.Libelle, TypeCompte = d.Type,
                SousType = d.SousType, IsSystem = true, Solde = 0m, CreatedBy = userId,
            }));
            changed = true;
        }

        if (!await db.ResultatComptes.AnyAsync(c => c.GroupId == groupId, ct))
        {
            db.ResultatComptes.AddRange(DefaultResultatAccounts.Select(d => new ResultatCompte
            {
                GroupId = groupId, NumeroCompte = d.Numero, Libelle = d.Libelle, TypeCompte = d.Type,
                SousType = d.SousType, IsSystem = true, Solde = 0m, CreatedBy = userId,
            }));
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(ct);
    }

    // --- Écritures ---

    private static async Task<IResult> ListEcrituresAsync(
        int? compteId, string? tableType, DateOnly? dateDebut, DateOnly? dateFin,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var query = db.BilanEcritures.AsNoTracking().Where(e => e.GroupId == scope.GroupId);
        if (compteId is { } c) query = query.Where(e => e.CompteId == c);
        if (tableType is { Length: > 0 }) query = query.Where(e => e.TableType == tableType);
        if (dateDebut is { } debut) query = query.Where(e => e.DateEcriture >= debut);
        if (dateFin is { } fin) query = query.Where(e => e.DateEcriture <= fin);

        var rows = await query.ToListAsync(ct);
        rows = rows.OrderByDescending(e => e.DateEcriture).ThenByDescending(e => e.CreatedAt).ToList();

        // CompteId is a row of one of two tables depending on TableType - batch-loaded into
        // its own dictionary each, since there is no single EF navigation across both.
        var bilanIds = rows.Where(e => e.TableType != TablesCompte.Resultat).Select(e => e.CompteId).Distinct().ToList();
        var resultatIds = rows.Where(e => e.TableType == TablesCompte.Resultat).Select(e => e.CompteId).Distinct().ToList();
        var bilanComptes = await db.BilanComptes.AsNoTracking()
            .Where(c => c.GroupId == scope.GroupId && bilanIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var resultatComptes = await db.ResultatComptes.AsNoTracking()
            .Where(c => c.GroupId == scope.GroupId && resultatIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);

        var names = await DisplayNamesAsync(db, rows.Select(e => e.CreatedBy), ct);
        return Results.Ok(rows.Select(e =>
        {
            var (numero, libelle) = e.TableType == TablesCompte.Resultat
                ? resultatComptes.TryGetValue(e.CompteId, out var rc) ? (rc.NumeroCompte, rc.Libelle) : (string.Empty, string.Empty)
                : bilanComptes.TryGetValue(e.CompteId, out var bc) ? (bc.NumeroCompte, bc.Libelle) : (string.Empty, string.Empty);
            return ToDto(e, numero, libelle, names);
        }).ToList());
    }

    private static async Task<IResult> CreateEcritureAsync(
        SaveBilanEcritureRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (ValidateEcriture(request) is { } error) return Results.BadRequest(new ApiError(error));

        string numero, libelle;
        if (request.TableType == TablesCompte.Resultat)
        {
            var compte = await db.ResultatComptes.FirstOrDefaultAsync(c => c.Id == request.CompteId && c.GroupId == scope.GroupId, ct);
            if (compte is null) return Results.NotFound(new ApiError("Compte introuvable"));

            var calculAutomatique = await LoadCalculAutomatiqueAsync(db, scope.GroupId, ct);
            if (!EcritureEligible(compte, calculAutomatique))
                return Results.BadRequest(new ApiError("Ce compte est alimenté automatiquement et ne prend pas d'écriture"));

            compte.Solde = (compte.Solde ?? 0m) + request.MontantDebit - request.MontantCredit;
            compte.UpdatedAt = DateTime.UtcNow;
            (numero, libelle) = (compte.NumeroCompte, compte.Libelle);
        }
        else
        {
            var compte = await db.BilanComptes.FirstOrDefaultAsync(c => c.Id == request.CompteId && c.GroupId == scope.GroupId, ct);
            if (compte is null) return Results.NotFound(new ApiError("Compte introuvable"));

            // The stored running balance, kept in step as the source does.
            compte.Solde = (compte.Solde ?? 0m) + request.MontantDebit - request.MontantCredit;
            compte.UpdatedAt = DateTime.UtcNow;
            (numero, libelle) = (compte.NumeroCompte, compte.Libelle);
        }

        var ecriture = new BilanEcriture
        {
            GroupId = scope.GroupId,
            CompteId = request.CompteId,
            TableType = request.TableType,
            DateEcriture = request.DateEcriture,
            Libelle = request.Libelle.Trim(),
            MontantDebit = request.MontantDebit,
            MontantCredit = request.MontantCredit,
            Reference = Blank(request.Reference),
            Notes = Blank(request.Notes),
            CreatedBy = scope.UserId,
        };
        db.BilanEcritures.Add(ecriture);

        await db.SaveChangesAsync(ct);

        var names = await DisplayNamesAsync(db, [ecriture.CreatedBy], ct);
        return Results.Ok(ToDto(ecriture, numero, libelle, names));
    }

    private static async Task<IResult> UpdateEcritureAsync(
        int id, SaveBilanEcritureRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (ValidateEcriture(request) is { } error) return Results.BadRequest(new ApiError(error));

        var ecriture = await db.BilanEcritures.FirstOrDefaultAsync(e => e.Id == id && e.GroupId == scope.GroupId, ct);
        if (ecriture is null) return Results.NotFound(new ApiError("Écriture introuvable"));

        // Reverse the old entry on its old account - same table as it was posted to, which may
        // not be the one the edit is moving it to.
        if (ecriture.TableType == TablesCompte.Resultat)
        {
            var ancien = await db.ResultatComptes.FirstOrDefaultAsync(c => c.Id == ecriture.CompteId && c.GroupId == scope.GroupId, ct);
            if (ancien is not null)
            {
                ancien.Solde = (ancien.Solde ?? 0m) - (ecriture.MontantDebit ?? 0m) + (ecriture.MontantCredit ?? 0m);
                ancien.UpdatedAt = DateTime.UtcNow;
            }
        }
        else
        {
            var ancien = await db.BilanComptes.FirstOrDefaultAsync(c => c.Id == ecriture.CompteId && c.GroupId == scope.GroupId, ct);
            if (ancien is not null)
            {
                ancien.Solde = (ancien.Solde ?? 0m) - (ecriture.MontantDebit ?? 0m) + (ecriture.MontantCredit ?? 0m);
                ancien.UpdatedAt = DateTime.UtcNow;
            }
        }

        string numero, libelle;
        if (request.TableType == TablesCompte.Resultat)
        {
            var nouveau = await db.ResultatComptes.FirstOrDefaultAsync(c => c.Id == request.CompteId && c.GroupId == scope.GroupId, ct);
            if (nouveau is null) return Results.NotFound(new ApiError("Compte introuvable"));

            var calculAutomatique = await LoadCalculAutomatiqueAsync(db, scope.GroupId, ct);
            if (!EcritureEligible(nouveau, calculAutomatique))
                return Results.BadRequest(new ApiError("Ce compte est alimenté automatiquement et ne prend pas d'écriture"));

            nouveau.Solde = (nouveau.Solde ?? 0m) + request.MontantDebit - request.MontantCredit;
            nouveau.UpdatedAt = DateTime.UtcNow;
            (numero, libelle) = (nouveau.NumeroCompte, nouveau.Libelle);
        }
        else
        {
            var nouveau = await db.BilanComptes.FirstOrDefaultAsync(c => c.Id == request.CompteId && c.GroupId == scope.GroupId, ct);
            if (nouveau is null) return Results.NotFound(new ApiError("Compte introuvable"));

            nouveau.Solde = (nouveau.Solde ?? 0m) + request.MontantDebit - request.MontantCredit;
            nouveau.UpdatedAt = DateTime.UtcNow;
            (numero, libelle) = (nouveau.NumeroCompte, nouveau.Libelle);
        }

        ecriture.CompteId = request.CompteId;
        ecriture.TableType = request.TableType;
        ecriture.DateEcriture = request.DateEcriture;
        ecriture.Libelle = request.Libelle.Trim();
        ecriture.MontantDebit = request.MontantDebit;
        ecriture.MontantCredit = request.MontantCredit;
        ecriture.Reference = Blank(request.Reference);
        ecriture.Notes = Blank(request.Notes);

        await db.SaveChangesAsync(ct);

        var names = await DisplayNamesAsync(db, [ecriture.CreatedBy], ct);
        return Results.Ok(ToDto(ecriture, numero, libelle, names));
    }

    private static async Task<IResult> DeleteEcritureAsync(
        int id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var ecriture = await db.BilanEcritures.FirstOrDefaultAsync(e => e.Id == id && e.GroupId == scope.GroupId, ct);
        if (ecriture is null) return Results.NotFound(new ApiError("Écriture introuvable"));

        if (ecriture.TableType == TablesCompte.Resultat)
        {
            var compte = await db.ResultatComptes.FirstOrDefaultAsync(c => c.Id == ecriture.CompteId && c.GroupId == scope.GroupId, ct);
            if (compte is not null)
            {
                compte.Solde = (compte.Solde ?? 0m) - (ecriture.MontantDebit ?? 0m) + (ecriture.MontantCredit ?? 0m);
                compte.UpdatedAt = DateTime.UtcNow;
            }
        }
        else
        {
            var compte = await db.BilanComptes.FirstOrDefaultAsync(c => c.Id == ecriture.CompteId && c.GroupId == scope.GroupId, ct);
            if (compte is not null)
            {
                compte.Solde = (compte.Solde ?? 0m) - (ecriture.MontantDebit ?? 0m) + (ecriture.MontantCredit ?? 0m);
                compte.UpdatedAt = DateTime.UtcNow;
            }
        }

        db.BilanEcritures.Remove(ecriture);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    private static string? ValidateEcriture(SaveBilanEcritureRequest r)
    {
        if (r.CompteId <= 0 || string.IsNullOrWhiteSpace(r.Libelle) || (r.MontantDebit == 0 && r.MontantCredit == 0))
            return "Compte, libellé et montant requis";
        if (r.MontantDebit < 0 || r.MontantCredit < 0)
            return "Les montants ne peuvent pas être négatifs";
        if (r.TableType is not (TablesCompte.Bilan or TablesCompte.Resultat))
            return "Type de table invalide";
        return null;
    }

    // --- Mapping ---

    private static BilanCompteDto ToDto(BilanCompte c, decimal manuel, decimal auto) => new(
        c.Id, c.NumeroCompte, c.Libelle, c.TypeCompte, c.SousType, c.Description, c.IsSystem == true,
        R(manuel), R(auto), R(manuel + auto));

    private static BilanCompteDto ToDto(ResultatCompte c, decimal manuel, decimal auto) => new(
        c.Id, c.NumeroCompte, c.Libelle, c.TypeCompte, c.SousType, c.Description, c.IsSystem == true,
        R(manuel), R(auto), R(manuel + auto));

    private static BilanEcritureDto ToDto(
        BilanEcriture e, string numeroCompte, string compteLibelle, IReadOnlyDictionary<string, string> names) => new(
        e.Id, e.CompteId, numeroCompte, compteLibelle,
        e.DateEcriture, e.Libelle, e.MontantDebit ?? 0m, e.MontantCredit ?? 0m, e.Reference, e.Notes,
        names.TryGetValue(e.CreatedBy, out var name) ? name : null, e.TableType);

    private static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Duplicated from other endpoint classes rather than shared - see
    /// CaisseEndpoints's own copy for why.</summary>
    private static async Task<Dictionary<string, string>> DisplayNamesAsync(
        LonniiDbContext db, IEnumerable<string?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (ids.Count == 0) return [];

        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.IdUser))
            .Select(u => new { u.IdUser, u.FirstName, u.LastName, u.Username, u.Email })
            .ToListAsync(ct);

        return users.ToDictionary(u => u.IdUser!, u =>
        {
            var full = $"{u.FirstName} {u.LastName}".Trim();
            return !string.IsNullOrWhiteSpace(full) ? full : u.Username ?? u.Email;
        })!;
    }
}
