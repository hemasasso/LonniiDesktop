using Lonnii.Api.Security;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The Charges module: business expenses, their categories, recurring "fixe" charges that
/// re-create themselves every month, and a year's worth of analytics. Mirrors
/// backend/routes/gestionCharges.js.
///
/// Two of that file's routes had no privilege check at all (GET categories, and the never-
/// wired can_approve_charges) - the port gates every route regardless, same decision
/// CaisseEndpoints already made for Lonnii Business's unguarded caisse routes.
/// can_approve_charges itself is not enforced anywhere here: nothing in the source app
/// checks it either (confirmed against gestionCharges.js and gestion.js) - it is a
/// catalogued privilege with no route behind it, like can_process_returns in the Ventes
/// module, not a feature this port left out.
/// </summary>
public static class ChargesEndpoints
{
    /// <summary>The twelve categories Lonnii Business seeds for every group in
    /// create_charges_system.sql. Seeded here lazily, on first read of a group's category
    /// list, rather than at migration time - the source seeds by iterating every existing
    /// <c>groupes</c> row, which a fresh desktop install has none of yet.</summary>
    private static readonly (string Nom, string Description, string Color)[] DefaultCategories =
    [
        ("Loyer", "Charges de location et loyer", "#3b82f6"),
        ("Électricité", "Factures d'électricité", "#facc15"),
        ("Eau", "Factures d'eau", "#06b6d4"),
        ("Internet", "Abonnements internet et télécom", "#8b5cf6"),
        ("Salaires", "Rémunération du personnel", "#10b981"),
        ("Fournitures", "Fournitures de bureau et matériel", "#f59e0b"),
        ("Marketing", "Dépenses marketing et publicité", "#ec4899"),
        ("Transport", "Frais de transport et déplacement", "#f97316"),
        ("Assurances", "Primes d'assurance", "#ef4444"),
        ("Maintenance", "Entretien et réparations", "#6366f1"),
        ("Taxes", "Taxes et impôts", "#dc2626"),
        ("Autres", "Autres charges diverses", "#64748b"),
    ];

    public static void MapChargesEndpoints(this IEndpointRouteBuilder app)
    {
        var charges = app.MapGroup("/api/charges").WithTags("Charges");

        charges.MapGet("/", ListAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewCharges);
        charges.MapGet("/{id:int}", GetDetailsAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewCharges);
        charges.MapPost("/", CreateAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.AddCharges);
        charges.MapPut("/{id:int}", UpdateAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.EditCharges);
        charges.MapDelete("/{id:int}", DeleteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.DeleteCharges);

        charges.MapGet("/categories", ListCategoriesAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewCharges);
        charges.MapPost("/categories", SaveCategoryAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageChargesCategories);
        charges.MapDelete("/categories/{id:int}", DeleteCategoryAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageChargesCategories);

        charges.MapGet("/recurring", ListRecurringAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewCharges);
        charges.MapPut("/{id:int}/stop-recurring", StopRecurringAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.EditCharges);
        charges.MapPut("/{id:int}/reactivate-recurring", ReactivateRecurringAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.EditCharges);

        charges.MapGet("/stats", GetStatsAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewChargesAnalytics);
    }

    /// <summary>"Charges": every expense in range, newest first. Auto-creates this month's
    /// occurrence of any due recurring charge first - see <see cref="ProcessRecurringAsync"/>
    /// - the same "recompute live on read" approach CaisseEndpoints uses for an open session,
    /// rather than a background service the desktop has no persistent process to run.</summary>
    private static async Task<IResult> ListAsync(
        DateOnly? dateDebut, DateOnly? dateFin, string? categorie,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        await ProcessRecurringAsync(db, scope.GroupId, ct);

        var query = db.Charges.AsNoTracking().Where(c => c.GroupId == scope.GroupId);

        if (dateDebut is { } start) query = query.Where(c => c.Date >= start.ToDateTime(TimeOnly.MinValue));
        if (dateFin is { } end) query = query.Where(c => c.Date < end.ToDateTime(TimeOnly.MinValue).AddDays(1));
        if (!string.IsNullOrWhiteSpace(categorie)) query = query.Where(c => c.Categorie == categorie);

        var rows = await query.OrderByDescending(c => c.Date).ThenByDescending(c => c.CreatedAt).ToListAsync(ct);
        var names = await DisplayNamesAsync(db, rows.Select(c => c.CreatedBy), ct);

        return Results.Ok(new ChargesListResponse(rows.Select(c => ToDto(c, names)).ToList()));
    }

    private static async Task<IResult> GetDetailsAsync(
        int id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var charge = await db.Charges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
        if (charge is null) return Results.NotFound(new ApiError("Charge introuvable"));

        var source = charge.RecurringSourceId is { } sourceId
            ? await db.Charges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sourceId && c.GroupId == scope.GroupId, ct)
              ?? charge
            : charge;

        var generated = await db.Charges.AsNoTracking()
            .Where(c => c.RecurringSourceId == source.Id && c.GroupId == scope.GroupId)
            .OrderByDescending(c => c.Date).ThenByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        var names = await DisplayNamesAsync(
            db, new[] { charge.CreatedBy, source.CreatedBy }.Concat(generated.Select(g => g.CreatedBy)), ct);

        return Results.Ok(new ChargeDetailsResponse(
            ToDto(charge, names),
            ToDto(source, names),
            $"Création automatique {RecurringDay.Describe(source.RecurringDay)}",
            NextRecurringDate(source, generated),
            generated.Select(g => ToDto(g, names)).ToList()));
    }

    /// <summary>"Ajouter une charge". <see cref="SaveChargeRequest.IsRecurring"/> only takes
    /// effect for a <c>fixe</c> charge, and requires an end date covering at least the month
    /// after <see cref="SaveChargeRequest.Date"/> - same validation as
    /// backend/routes/gestionCharges.js's own <c>shouldRecur</c> block.</summary>
    private static async Task<IResult> CreateAsync(
        SaveChargeRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || request.Montant <= 0
            || string.IsNullOrWhiteSpace(request.Categorie))
        {
            return Results.BadRequest(new ApiError(
                "Description, montant, type de charge et catégorie sont requis"));
        }

        if (request.TypeCharge is not (ChargeTypes.Fixe or ChargeTypes.Variable))
            return Results.BadRequest(new ApiError("Type de charge invalide"));

        var shouldRecur = request.IsRecurring && request.TypeCharge == ChargeTypes.Fixe;
        var (error, normalizedEnd) = ValidateRecurrence(shouldRecur, request.Date, request.RecurringEndDate);
        if (error is not null) return error;

        var charge = new Charge
        {
            GroupId = scope.GroupId,
            Description = request.Description.Trim(),
            Montant = request.Montant,
            TypeCharge = request.TypeCharge,
            Categorie = request.Categorie,
            Date = request.Date.Date,
            CreatedBy = scope.UserId,
            IsRecurring = shouldRecur,
            RecurringEndDate = normalizedEnd,
            RecurringActive = shouldRecur,
            RecurringDay = shouldRecur ? RecurringDay.Normalise(request.RecurringDay) : RecurringDay.Fin,
        };

        db.Charges.Add(charge);
        await db.SaveChangesAsync(ct);

        var names = await DisplayNamesAsync(db, [charge.CreatedBy], ct);
        return Results.Created($"/api/charges/{charge.Id}", ToDto(charge, names));
    }

    private static async Task<IResult> UpdateAsync(
        int id, SaveChargeRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var charge = await db.Charges.FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
        if (charge is null) return Results.NotFound(new ApiError("Charge introuvable"));

        if (string.IsNullOrWhiteSpace(request.Description) || request.Montant <= 0
            || string.IsNullOrWhiteSpace(request.Categorie))
        {
            return Results.BadRequest(new ApiError(
                "Description, montant, type de charge et catégorie sont requis"));
        }

        if (request.TypeCharge is not (ChargeTypes.Fixe or ChargeTypes.Variable))
            return Results.BadRequest(new ApiError("Type de charge invalide"));

        // A charge already generated FROM a recurring source can never become a recurring
        // source itself - same guard as the source app's own PUT route.
        if (charge.RecurringSourceId is not null && request.IsRecurring)
            return Results.BadRequest(new ApiError(
                "Une charge générée automatiquement ne peut pas devenir une charge récurrente source"));

        var shouldRecur = request.IsRecurring && request.TypeCharge == ChargeTypes.Fixe
            && charge.RecurringSourceId is null;
        var (error, normalizedEnd) = ValidateRecurrence(shouldRecur, request.Date, request.RecurringEndDate);
        if (error is not null) return error;

        charge.Description = request.Description.Trim();
        charge.Montant = request.Montant;
        charge.TypeCharge = request.TypeCharge;
        charge.Categorie = request.Categorie;
        charge.Date = request.Date.Date;
        charge.IsRecurring = shouldRecur;
        charge.RecurringEndDate = shouldRecur ? normalizedEnd : null;
        charge.RecurringActive = shouldRecur;
        charge.RecurringDay = shouldRecur
            ? RecurringDay.Normalise(request.RecurringDay)
            : (charge.RecurringDay ?? RecurringDay.Fin);
        charge.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        var names = await DisplayNamesAsync(db, [charge.CreatedBy], ct);
        return Results.Ok(ToDto(charge, names));
    }

    private static async Task<IResult> DeleteAsync(
        int id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var charge = await db.Charges.FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
        if (charge is null) return Results.NotFound(new ApiError("Charge introuvable"));

        db.Charges.Remove(charge);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    private static async Task<IResult> ListCategoriesAsync(
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var categories = await db.ChargeCategories.AsNoTracking()
            .Where(c => c.GroupId == scope.GroupId).OrderBy(c => c.Nom).ToListAsync(ct);

        if (categories.Count == 0)
        {
            foreach (var (nom, description, color) in DefaultCategories)
            {
                db.ChargeCategories.Add(new ChargeCategory
                {
                    GroupId = scope.GroupId, Nom = nom, Description = description, Color = color,
                });
            }
            await db.SaveChangesAsync(ct);

            categories = await db.ChargeCategories.AsNoTracking()
                .Where(c => c.GroupId == scope.GroupId).OrderBy(c => c.Nom).ToListAsync(ct);
        }

        return Results.Ok(categories.Select(ToDto).ToList());
    }

    /// <summary>Creates a category, or edits one by name - same upsert-by-name behaviour as
    /// the source app's <c>ON CONFLICT (groupe_id, nom) DO UPDATE</c>.</summary>
    private static async Task<IResult> SaveCategoryAsync(
        SaveChargeCategoryRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Nom))
            return Results.BadRequest(new ApiError("Le nom de la catégorie est requis"));

        var existing = await db.ChargeCategories
            .FirstOrDefaultAsync(c => c.GroupId == scope.GroupId && c.Nom == request.Nom, ct);

        if (existing is null)
        {
            existing = new ChargeCategory { GroupId = scope.GroupId, Nom = request.Nom.Trim() };
            db.ChargeCategories.Add(existing);
        }

        existing.Description = Blank(request.Description);
        existing.Color = string.IsNullOrWhiteSpace(request.Color) ? "#6366f1" : request.Color;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(existing));
    }

    private static async Task<IResult> DeleteCategoryAsync(
        int id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var category = await db.ChargeCategories.FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
        if (category is null) return Results.Ok();

        db.ChargeCategories.Remove(category);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }

    /// <summary>"Charges Récurrentes": every recurring source (not the occurrences it has
    /// generated), active ones first.</summary>
    private static async Task<IResult> ListRecurringAsync(
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        await ProcessRecurringAsync(db, scope.GroupId, ct);

        var rows = await db.Charges.AsNoTracking()
            .Where(c => c.GroupId == scope.GroupId && c.IsRecurring && c.RecurringSourceId == null)
            .OrderByDescending(c => c.RecurringActive).ThenByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        var names = await DisplayNamesAsync(db, rows.Select(c => c.CreatedBy), ct);
        return Results.Ok(new ChargesListResponse(rows.Select(c => ToDto(c, names)).ToList()));
    }

    private static async Task<IResult> StopRecurringAsync(
        int id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var charge = await db.Charges.FirstOrDefaultAsync(
            c => c.Id == id && c.GroupId == scope.GroupId && c.IsRecurring, ct);
        if (charge is null) return Results.NotFound(new ApiError("Charge récurrente introuvable"));

        charge.RecurringActive = false;
        charge.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var names = await DisplayNamesAsync(db, [charge.CreatedBy], ct);
        return Results.Ok(ToDto(charge, names));
    }

    private static async Task<IResult> ReactivateRecurringAsync(
        int id, ReactivateRecurringRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var charge = await db.Charges.FirstOrDefaultAsync(
            c => c.Id == id && c.GroupId == scope.GroupId && c.IsRecurring, ct);
        if (charge is null) return Results.NotFound(new ApiError("Charge récurrente introuvable"));

        charge.RecurringActive = true;
        charge.RecurringEndDate = request.RecurringEndDate?.Date ?? charge.RecurringEndDate;
        charge.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var names = await DisplayNamesAsync(db, [charge.CreatedBy], ct);
        return Results.Ok(ToDto(charge, names));
    }

    /// <summary>"Analyses": totals, monthly average, the year's heaviest category, and three
    /// breakdowns (by month, by quarter, by category) for one calendar year.</summary>
    private static async Task<IResult> GetStatsAsync(
        int? annee, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var year = annee ?? DateTime.UtcNow.Year;

        var all = await db.Charges.AsNoTracking()
            .Where(c => c.GroupId == scope.GroupId)
            .Select(c => new { c.Montant, c.Date, c.Categorie })
            .ToListAsync(ct);

        var total = all.Sum(c => c.Montant);

        var moyenne = all.GroupBy(c => new { c.Date.Year, c.Date.Month })
            .Select(g => g.Sum(c => c.Montant))
            .DefaultIfEmpty(0)
            .Average();

        var categoriePrincipale = all
            .GroupBy(c => c.Categorie)
            .OrderByDescending(g => g.Sum(c => c.Montant))
            .Select(g => g.Key)
            .FirstOrDefault() ?? "-";

        var ofYear = all.Where(c => c.Date.Year == year).ToList();

        var parMois = ofYear.GroupBy(c => c.Date.Month)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Montant));
        var parTrimestre = ofYear.GroupBy(c => (c.Date.Month - 1) / 3 + 1)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Montant));
        var parCategorie = ofYear.GroupBy(c => c.Categorie)
            .OrderByDescending(g => g.Sum(c => c.Montant))
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Montant));

        return Results.Ok(new ChargesStatsResponse(
            total, moyenne, categoriePrincipale, parMois, parTrimestre, parCategorie));
    }

    // --- Recurrence ---

    /// <summary>Auto-creates this month's occurrence of every active recurring charge in the
    /// group whose configured day has arrived, and deactivates any whose end date has passed
    /// - the same idempotent check (by year+month) as
    /// backend/utils/recurringChargesScheduler.js's <c>processRecurringCharges</c>, just run
    /// on read instead of a 6-hourly timer.</summary>
    private static async Task ProcessRecurringAsync(LonniiDbContext db, string groupId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var sources = await db.Charges
            .Where(c => c.GroupId == groupId && c.IsRecurring && c.RecurringActive && c.RecurringSourceId == null)
            .ToListAsync(ct);

        foreach (var source in sources)
        {
            var targetDay = RecurringDay.Resolve(source.RecurringDay, now.Year, now.Month);
            if (targetDay != now.Day) continue;

            var targetDate = new DateTime(now.Year, now.Month, targetDay);
            if (source.RecurringEndDate is { } end && targetDate > end.Date)
            {
                source.RecurringActive = false;
                continue;
            }

            // Never for the source's own first month, and never twice for the same month.
            if (source.Date.Year == now.Year && source.Date.Month == now.Month) continue;

            var alreadyCreated = await db.Charges.AnyAsync(c =>
                c.RecurringSourceId == source.Id && c.Date.Year == now.Year && c.Date.Month == now.Month, ct);
            if (alreadyCreated) continue;

            db.Charges.Add(new Charge
            {
                GroupId = source.GroupId,
                Description = source.Description,
                Montant = source.Montant,
                TypeCharge = source.TypeCharge,
                Categorie = source.Categorie,
                Date = targetDate,
                CreatedBy = source.CreatedBy,
                RecurringSourceId = source.Id,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>When a recurring source is next due, or null once it is inactive/exhausted -
    /// mirrors gestionCharges.js's <c>computeNextRecurringChargeDate</c>.</summary>
    private static DateTime? NextRecurringDate(Charge source, IReadOnlyList<Charge> generated)
    {
        if (!source.IsRecurring || !source.RecurringActive || source.RecurringSourceId is not null) return null;

        var latest = generated.Select(g => g.Date).Append(source.Date).Max();
        var nextMonth = new DateTime(latest.Year, latest.Month, 1).AddMonths(1);
        var targetDay = RecurringDay.Resolve(source.RecurringDay, nextMonth.Year, nextMonth.Month);
        var next = new DateTime(nextMonth.Year, nextMonth.Month, targetDay);

        if (source.RecurringEndDate is { } end && next.Date > end.Date) return null;
        return next;
    }

    /// <summary>The last day of <paramref name="date"/>'s own month - mirrors
    /// gestionCharges.js's <c>getMonthEndDate</c>, which both validates against this and (for
    /// <see cref="Charge.RecurringEndDate"/> itself) stores it, so a date picked mid-month
    /// still covers that whole month rather than cutting it short partway through.</summary>
    private static DateTime MonthEnd(DateTime date) => new DateTime(date.Year, date.Month, 1).AddMonths(1).AddDays(-1);

    /// <summary>Validates a recurring charge's end date and, when valid, returns it normalised
    /// to <see cref="MonthEnd"/> - the value that must actually be stored, not the raw date
    /// the user picked. Returns <c>(Error: not null, NormalizedEnd: null)</c> on failure.</summary>
    private static (IResult? Error, DateTime? NormalizedEnd) ValidateRecurrence(
        bool shouldRecur, DateTime date, DateTime? recurringEndDate)
    {
        if (!shouldRecur) return (null, null);

        if (recurringEndDate is not { } end)
        {
            return (Results.BadRequest(new ApiError(
                "Veuillez préciser jusqu'à quand cette charge fixe doit être créée automatiquement")), null);
        }

        var normalizedEnd = MonthEnd(end);
        if (normalizedEnd <= MonthEnd(date))
        {
            return (Results.BadRequest(new ApiError(
                "La date de fin doit couvrir au moins le mois suivant pour qu'une charge mensuelle soit créée automatiquement")), null);
        }

        return (null, normalizedEnd);
    }

    // --- Mapping ---

    private static ChargeDto ToDto(Charge c, IReadOnlyDictionary<string, string> names) => new(
        c.Id, c.Description, c.Montant, c.TypeCharge, c.Categorie, c.Date,
        c.CreatedBy is { } id && names.TryGetValue(id, out var name) ? name : null,
        c.IsRecurring, c.RecurringEndDate, c.RecurringActive, c.RecurringSourceId, c.RecurringDay);

    private static ChargeCategoryDto ToDto(ChargeCategory c) => new(c.Id, c.Nom, c.Description, c.Color);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Duplicated from other endpoint classes rather than shared - see
    /// CaisseEndpoints's own copy for why.</summary>
    private static async Task<Dictionary<string, string>> DisplayNamesAsync(
        LonniiDbContext db, IEnumerable<string?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => id is not null).Distinct().ToList();
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
