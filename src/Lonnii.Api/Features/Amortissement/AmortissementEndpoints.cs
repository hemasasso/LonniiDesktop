using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Amortissement;

/// <summary>
/// The Amortissement module: fixed assets and their depreciation schedules. Mirrors
/// backend/routes/gestionAmortissement.js against the same <c>immobilisations</c> and
/// <c>amortissement_echeances</c> tables.
///
/// Not ported: the source's <c>POST /simulate</c>. The calculator lives in Lonnii.Shared, so
/// the client simulates - and previews the schedule while an asset is being typed in -
/// without a round trip. The list and the summary tiles come back in one response rather than
/// two, and are filtered on the client.
/// </summary>
public static class AmortissementEndpoints
{
    public static void MapAmortissementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/amortissement").WithTags("Amortissement");

        group.MapGet("/", ListAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewAmortissement);
        group.MapGet("/{id:int}", GetDetailsAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewAmortissement);
        group.MapPost("/", CreateAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.AddImmobilisation);
        group.MapPut("/{id:int}", UpdateAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.EditImmobilisation);
        group.MapPut("/{id:int}/ceder", CederAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.CederImmobilisation);
        group.MapDelete("/{id:int}", DeleteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.DeleteImmobilisation);
    }

    /// <param name="annee">The caller's current year; the server's when absent.</param>
    private static async Task<IResult> ListAsync(
        int? annee, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var year = annee ?? DateTime.UtcNow.Year;

        var immos = await db.Immobilisations.AsNoTracking()
            .Include(i => i.Echeances)
            .Where(i => i.GroupId == scope.GroupId)
            .ToListAsync(ct);

        immos = immos
            .OrderByDescending(i => i.DateAcquisition)
            .ThenByDescending(i => i.CreatedAt)
            .ToList();

        var names = await DisplayNamesAsync(db, immos.Select(i => i.CreatedBy), ct);
        var rows = immos.Select(i => (Immo: i, Echeances: AmortissementRules.Echeances(i))).ToList();
        var actifs = rows.Where(r => StatutsImmobilisation.Normalise(r.Immo.Statut) == StatutsImmobilisation.Actif).ToList();

        var stats = new AmortissementStatsDto(
            year,
            actifs.Count,
            actifs.Sum(r => r.Immo.ValeurAcquisition),
            // Every asset's dotation for the year, including one sold during it: that is the
            // year's depreciation expense, and what the compte de résultat charges.
            rows.Sum(r => AmortissementRules.DotationDe(r.Immo, r.Echeances, year)),
            actifs.Sum(r => AmortissementRules.CumuleAu(r.Immo, r.Echeances, year)),
            actifs.Sum(r => r.Immo.ValeurAcquisition - AmortissementRules.CumuleAu(r.Immo, r.Echeances, year)),
            actifs
                .GroupBy(r => r.Immo.Categorie)
                .Select(g => new AmortissementCategorieStatDto(
                    g.Key, g.Count(),
                    g.Sum(r => r.Immo.ValeurAcquisition),
                    g.Sum(r => r.Immo.ValeurAcquisition - AmortissementRules.CumuleAu(r.Immo, r.Echeances, year))))
                .OrderByDescending(c => c.ValeurBrute)
                .ToList());

        return Results.Ok(new AmortissementListResponse(
            rows.Select(r => ToDto(r.Immo, r.Echeances, year, names)).ToList(), stats));
    }

    private static async Task<IResult> GetDetailsAsync(
        int id, int? annee, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var immo = await db.Immobilisations.AsNoTracking()
            .Include(i => i.Echeances)
            .FirstOrDefaultAsync(i => i.Id == id && i.GroupId == scope.GroupId, ct);
        if (immo is null) return Results.NotFound(new ApiError("Immobilisation introuvable"));

        return Results.Ok(await DetailsAsync(immo, annee ?? DateTime.UtcNow.Year, db, ct));
    }

    private static async Task<IResult> CreateAsync(
        SaveImmobilisationRequest request, int? annee, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } error) return Results.BadRequest(new ApiError(error));

        var immo = new Immobilisation { GroupId = scope.GroupId, CreatedBy = scope.UserId };
        Apply(immo, request);
        immo.Echeances = Schedule(immo);

        db.Immobilisations.Add(immo);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/amortissement/{immo.Id}", await DetailsAsync(immo, annee ?? DateTime.UtcNow.Year, db, ct));
    }

    /// <summary>Saves the asset and recomputes its whole schedule, as the source does.</summary>
    private static async Task<IResult> UpdateAsync(
        int id, SaveImmobilisationRequest request, int? annee, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var immo = await db.Immobilisations
            .Include(i => i.Echeances)
            .FirstOrDefaultAsync(i => i.Id == id && i.GroupId == scope.GroupId, ct);
        if (immo is null) return Results.NotFound(new ApiError("Immobilisation introuvable"));

        // The source form only offers "Modifier" on an active asset; the server enforces it
        // too, since recomputing a sold asset's schedule would rewrite the years it was
        // already closed on.
        if (StatutsImmobilisation.IsSortie(immo.Statut))
            return Results.BadRequest(new ApiError("Une immobilisation cédée ou réformée ne peut plus être modifiée"));

        if (Validate(request) is { } error) return Results.BadRequest(new ApiError(error));

        Apply(immo, request);
        immo.UpdatedAt = DateTime.UtcNow;

        db.AmortissementEcheances.RemoveRange(immo.Echeances);
        immo.Echeances = Schedule(immo);

        await db.SaveChangesAsync(ct);
        return Results.Ok(await DetailsAsync(immo, annee ?? DateTime.UtcNow.Year, db, ct));
    }

    /// <summary>"Céder / Réformer": the asset leaves the books. Its schedule is kept as it
    /// was; it simply stops counting after the exit year.</summary>
    private static async Task<IResult> CederAsync(
        int id, CederImmobilisationRequest request, int? annee, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var immo = await db.Immobilisations
            .Include(i => i.Echeances)
            .FirstOrDefaultAsync(i => i.Id == id && i.GroupId == scope.GroupId, ct);
        if (immo is null) return Results.NotFound(new ApiError("Immobilisation introuvable"));

        if (request.Statut is not (StatutsImmobilisation.Cede or StatutsImmobilisation.Reforme))
            return Results.BadRequest(new ApiError("Type de sortie invalide"));
        if (StatutsImmobilisation.IsSortie(immo.Statut))
            return Results.BadRequest(new ApiError("Cette immobilisation est déjà sortie de l'actif"));
        if (request.DateCession < immo.DateAcquisition)
            return Results.BadRequest(new ApiError("La date de sortie ne peut pas précéder la date d'acquisition"));
        if (request.ValeurCession is < 0)
            return Results.BadRequest(new ApiError("La valeur de cession ne peut pas être négative"));

        immo.Statut = request.Statut;
        immo.DateCession = request.DateCession;
        // The source stores 0 when none is given, a scrapped asset included.
        immo.ValeurCession = request.Statut == StatutsImmobilisation.Cede ? request.ValeurCession ?? 0m : 0m;
        immo.MotifSortie = Blank(request.MotifSortie);
        immo.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(await DetailsAsync(immo, annee ?? DateTime.UtcNow.Year, db, ct));
    }

    private static async Task<IResult> DeleteAsync(
        int id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var immo = await db.Immobilisations
            .Include(i => i.Echeances)
            .FirstOrDefaultAsync(i => i.Id == id && i.GroupId == scope.GroupId, ct);
        if (immo is null) return Results.NotFound(new ApiError("Immobilisation introuvable"));

        db.Immobilisations.Remove(immo);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    // --- Helpers ---

    internal static string? Validate(SaveImmobilisationRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Nom))
            return "Nom, date d'acquisition, valeur, durée et méthode d'amortissement sont requis";
        if (!CategoriesImmobilisation.All.Contains(r.Categorie))
            return "Catégorie invalide";
        if (r.DateMiseEnService is { } service && service < r.DateAcquisition)
            return "La date de mise en service ne peut pas précéder la date d'acquisition";

        var coefficient = r.MethodeAmortissement == MethodesAmortissement.Degressif ? r.TauxDegressif : null;
        return AmortissementCalculator.Valider(
            r.ValeurAcquisition, r.ValeurResiduelle, r.DureeAmortissement, r.MethodeAmortissement, coefficient);
    }

    private static void Apply(Immobilisation immo, SaveImmobilisationRequest r)
    {
        immo.Nom = r.Nom.Trim();
        immo.Description = Blank(r.Description);
        immo.Categorie = r.Categorie;
        immo.DateAcquisition = r.DateAcquisition;
        immo.ValeurAcquisition = r.ValeurAcquisition;
        immo.ValeurResiduelle = r.ValeurResiduelle;
        immo.DureeAmortissement = r.DureeAmortissement;
        immo.MethodeAmortissement = r.MethodeAmortissement;
        immo.TauxDegressif = r.MethodeAmortissement == MethodesAmortissement.Degressif ? r.TauxDegressif : null;
        immo.DateMiseEnService = r.DateMiseEnService;
        immo.NumeroInventaire = Blank(r.NumeroInventaire);
        immo.Localisation = Blank(r.Localisation);
        immo.Fournisseur = Blank(r.Fournisseur);
        immo.NumeroFacture = Blank(r.NumeroFacture);
        immo.Notes = Blank(r.Notes);
    }

    private static List<AmortissementEcheance> Schedule(Immobilisation immo) =>
        AmortissementCalculator.Calculer(
                immo.ValeurAcquisition, immo.ValeurResiduelle ?? 0m, immo.DureeAmortissement,
                immo.MethodeAmortissement, immo.TauxDegressif, immo.DateAcquisition, immo.DateMiseEnService)
            .Select(e => new AmortissementEcheance
            {
                GroupId = immo.GroupId,
                Annee = e.Annee,
                NumeroAnnee = e.NumeroAnnee,
                DateDebut = e.DateDebut,
                DateFin = e.DateFin,
                ValeurDebutPeriode = e.ValeurDebutPeriode,
                DotationAnnuelle = e.DotationAnnuelle,
                AmortissementCumule = e.AmortissementCumule,
                ValeurNetteComptable = e.ValeurNetteComptable,
            })
            .ToList();

    private static async Task<ImmobilisationDetailsResponse> DetailsAsync(
        Immobilisation immo, int year, LonniiDbContext db, CancellationToken ct)
    {
        var echeances = AmortissementRules.Echeances(immo);
        var names = await DisplayNamesAsync(db, [immo.CreatedBy], ct);

        decimal? vncSortie = null, plusMoinsValue = null;
        if (AmortissementRules.DerniereAnnee(immo) is { } sortie)
        {
            vncSortie = immo.ValeurAcquisition - AmortissementRules.CumuleAu(immo, echeances, sortie);
            plusMoinsValue = (immo.ValeurCession ?? 0m) - vncSortie;
        }

        return new ImmobilisationDetailsResponse(ToDto(immo, echeances, year, names), echeances, vncSortie, plusMoinsValue);
    }

    private static ImmobilisationDto ToDto(
        Immobilisation i, IReadOnlyList<AmortissementEcheanceDto> echeances, int year, IReadOnlyDictionary<string, string> names)
    {
        var statut = StatutsImmobilisation.Normalise(i.Statut);
        var cumule = AmortissementRules.CumuleAu(i, echeances, year);
        var totalementAmorti = statut == StatutsImmobilisation.Actif
            && echeances.Count > 0 && year > echeances.Max(e => e.Annee);

        return new ImmobilisationDto(
            i.Id, i.Nom, i.Description, i.Categorie, i.DateAcquisition, i.ValeurAcquisition,
            i.ValeurResiduelle ?? 0m, i.DureeAmortissement, i.MethodeAmortissement, i.TauxDegressif,
            i.DateMiseEnService, statut, i.DateCession, i.ValeurCession, i.MotifSortie,
            i.NumeroInventaire, i.Localisation, i.Fournisseur, i.NumeroFacture, i.Notes,
            names.TryGetValue(i.CreatedBy, out var name) ? name : null,
            cumule, i.ValeurAcquisition - cumule,
            AmortissementRules.DotationDe(i, echeances, year),
            totalementAmorti);
    }

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
