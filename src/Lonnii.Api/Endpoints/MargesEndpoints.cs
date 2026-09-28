using Lonnii.Api.Security;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The Marges module: revenue, cost of sales, gross and net profit over a period, broken
/// down by product, category and month. Mirrors backend/routes/gestionMarges.js.
///
/// Two parts of that route are not ported. Prestations factures are folded into its totals
/// there; the desktop has no Prestations module yet, so there is nothing to fold in. And its
/// cost formula divides by <c>products.conversion_factor</c> for bulk units, which the
/// desktop model does not carry either (see lonnii-live-schema-drift memory).
/// </summary>
public static class MargesEndpoints
{
    /// <summary>gestionMarges.js's <c>COALESCE(p.markup_percentage, 30)</c>: the markup
    /// assumed over cost when a product has no purchase price recorded.</summary>
    private const decimal DefaultMarkupPercent = 30m;

    private const int MonthlyWindow = 12;

    public static void MapMargesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/marges").WithTags("Marges")
            .MapGet("/", GetMargesAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewMarges);
    }

    private sealed record ProductInfo(string Name, string? CategoryId, decimal? CostPrice, bool VenteLibre);

    private sealed record Line(
        string VenteId, DateTime DateVente, string? ProductId, string NomProduit,
        int Quantite, decimal PrixTotal, decimal Cost, string CostSource, string? CategoryId);

    /// <summary>
    /// <paramref name="dateDebut"/>/<paramref name="dateFin"/> are client-local calendar
    /// days, converted with <paramref name="tzOffsetMinutes"/> the same way
    /// VentesEndpoints does - <see cref="Vente.DateVente"/> is stored as UTC. Both absent
    /// means every sale ever made.
    /// </summary>
    private static async Task<IResult> GetMargesAsync(
        DateOnly? dateDebut, DateOnly? dateFin, string? categoryId, int? tzOffsetMinutes,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var offset = TimeSpan.FromMinutes(tzOffsetMinutes ?? 0);
        var groupId = scope.GroupId;

        // Soft-deleted products are kept: a sale made before the deletion still cost what
        // that product cost.
        var products = await db.Products.AsNoTracking()
            .Where(p => p.GroupId == groupId)
            .Select(p => new { p.Id, p.Name, p.CategoryId, p.CostPrice, p.VenteLibre })
            .ToDictionaryAsync(p => p.Id, p => new ProductInfo(p.Name, p.CategoryId, p.CostPrice, p.VenteLibre), ct);

        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.GroupId == groupId)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);
        var categoryNames = categories.ToDictionary(c => c.Id, c => c.Name);

        async Task<List<Line>> LinesAsync(DateTime? startUtc, DateTime? endUtc)
        {
            var query = db.Ventes.AsNoTracking().Where(v => v.GroupId == groupId);
            if (startUtc is { } s) query = query.Where(v => v.DateVente >= s);
            if (endUtc is { } e) query = query.Where(v => v.DateVente < e);

            // A join rather than SelectMany over v.Items: SQLite cannot translate the latter
            // (it needs an APPLY).
            var raw = await query
                .Join(db.VenteItems, v => v.Id, i => i.VenteId, (v, i) => new
                {
                    v.Id, v.DateVente, v.StatutPaiement, i.ProductId, i.NomProduit, i.Quantite, i.PrixTotal,
                })
                .ToListAsync(ct);

            // The purchase price as it stood when each sale was rung up: VentesEndpoints
            // writes it into the sale's stock_history row. Today's cost_price is only a
            // fallback - a product edited or repurposed since would otherwise re-price every
            // past sale of it.
            var saleCosts = await query
                .Join(db.StockHistories.Where(h => h.MovementType == StockMovementTypes.Vente && h.UnitCost != null),
                    v => v.Id, h => h.ReferenceId, (v, h) => new { h.ReferenceId, h.ProductId, h.UnitCost })
                .ToListAsync(ct);
            var costAtSale = saleCosts
                .GroupBy(h => (h.ReferenceId!, h.ProductId))
                .ToDictionary(g => g.Key, g => g.First().UnitCost!.Value);

            // Filtered after Normalise rather than in SQL: the live column still holds the
            // English spellings as well as "annule".
            return raw
                .Where(r => StatutPaiement.Normalise(r.StatutPaiement) != StatutPaiement.Annule)
                .Select(r =>
                {
                    var product = r.ProductId is not null ? products.GetValueOrDefault(r.ProductId) : null;

                    // A line whose name no longer matches its product was sold as something
                    // else before the product was renamed or reused: neither today's name,
                    // category nor purchase price describes what was actually sold, so it is
                    // treated like a line whose product was deleted.
                    if (product is not null && !SameName(product.Name, r.NomProduit)) product = null;

                    decimal? unitCostAtSale = r.ProductId is not null && costAtSale.TryGetValue((r.Id, r.ProductId), out var c) ? c : null;
                    var (cost, source) = product is null && unitCostAtSale is null
                        ? Estimate(r.PrixTotal)
                        : LineCost(unitCostAtSale ?? product?.CostPrice, r.PrixTotal, product?.VenteLibre ?? false, r.Quantite);

                    return new Line(r.Id, r.DateVente, product is not null ? r.ProductId : null, r.NomProduit,
                        r.Quantite, r.PrixTotal, cost, source, product?.CategoryId);
                })
                .Where(l => categoryId is null || l.CategoryId == categoryId)
                .ToList();
        }

        async Task<List<(DateTime Date, decimal Montant, bool Fixe)>> ChargesAsync(DateOnly? debut, DateOnly? fin)
        {
            var query = db.Charges.AsNoTracking().Where(c => c.GroupId == groupId);
            if (debut is { } d) query = query.Where(c => c.Date >= d.ToDateTime(TimeOnly.MinValue));
            if (fin is { } f) query = query.Where(c => c.Date < f.ToDateTime(TimeOnly.MinValue).AddDays(1));
            var rows = await query.Select(c => new { c.Date, c.Montant, c.TypeCharge }).ToListAsync(ct);
            return rows.Select(r => (r.Date, r.Montant, r.TypeCharge == ChargeTypes.Fixe)).ToList();
        }

        DateTime? ToUtc(DateOnly? local) => local?.ToDateTime(TimeOnly.MinValue) - offset;

        // --- The period itself ---

        var lines = await LinesAsync(ToUtc(dateDebut), ToUtc(dateFin?.AddDays(1)));
        var charges = await ChargesAsync(dateDebut, dateFin);

        var totalRevenue = lines.Sum(l => l.PrixTotal);
        var totalCosts = lines.Sum(l => l.Cost);
        var totalCharges = charges.Sum(c => c.Montant);
        var chargesFixes = charges.Where(c => c.Fixe).Sum(c => c.Montant);
        var chargesVariables = totalCharges - chargesFixes;
        var grossProfit = totalRevenue - totalCosts;
        var netProfit = grossProfit - totalCharges;
        var grossMargin = Rate(grossProfit, totalRevenue);
        var netMargin = Rate(netProfit, totalRevenue);

        var breakEven = BreakEven(totalRevenue, totalCosts + chargesVariables, chargesFixes);

        // The day the break-even revenue was (or would be) reached, assuming sales spread
        // evenly over the period - only meaningful for a bounded period with sales in it.
        DateOnly? pointMortDate = null;
        if (breakEven.Seuil is { } seuil && dateDebut is { } debutPeriode && dateFin is { } finPeriode && totalRevenue > 0)
        {
            var days = finPeriode.DayNumber - debutPeriode.DayNumber + 1;
            var dayReached = (int)Math.Ceiling(seuil / (totalRevenue / days));
            if (dayReached <= days) pointMortDate = debutPeriode.AddDays(Math.Max(dayReached, 1) - 1);
        }

        var productRows = lines
            .GroupBy(l => l.ProductId is not null && products.ContainsKey(l.ProductId) ? l.ProductId : "nom:" + l.NomProduit)
            .Select(g =>
            {
                var first = g.First();
                var product = first.ProductId is not null ? products.GetValueOrDefault(first.ProductId) : null;
                var revenue = g.Sum(l => l.PrixTotal);
                var cost = g.Sum(l => l.Cost);
                return new MargeLineDto(
                    product is not null ? first.ProductId : null,
                    product?.Name ?? (string.IsNullOrWhiteSpace(first.NomProduit) ? "Produit inconnu" : first.NomProduit),
                    first.CategoryId,
                    CategoryName(first.CategoryId, categoryNames),
                    g.Sum(l => l.Quantite), revenue, cost, revenue - cost, Rate(revenue - cost, revenue),
                    first.CostSource);
            })
            .OrderByDescending(p => p.Profit)
            .ToList();

        var categoryRows = lines
            .GroupBy(l => l.CategoryId)
            .Select(g =>
            {
                var revenue = g.Sum(l => l.PrixTotal);
                var cost = g.Sum(l => l.Cost);
                return new MargeCategoryDto(
                    g.Key, CategoryName(g.Key, categoryNames), g.Sum(l => l.Quantite),
                    revenue, cost, revenue - cost, Rate(revenue - cost, revenue));
            })
            .OrderByDescending(c => c.Profit)
            .ToList();

        var estimated = lines.Where(l => l.CostSource == MargeCostSources.Estime).ToList();

        // --- The preceding period of the same length, for the trend tiles ---

        decimal? previousRevenue = null, previousGrossProfit = null;
        if (dateDebut is { } periodDebut && dateFin is { } periodFin)
        {
            var span = periodFin.DayNumber - periodDebut.DayNumber + 1;
            var previous = await LinesAsync(ToUtc(periodDebut.AddDays(-span)), ToUtc(periodDebut));
            previousRevenue = previous.Sum(l => l.PrixTotal);
            previousGrossProfit = previousRevenue - previous.Sum(l => l.Cost);
        }

        // --- Trailing twelve months, independent of the period filter (as in the source) ---

        var localToday = DateOnly.FromDateTime(DateTime.UtcNow + offset);
        var firstMonth = new DateOnly(localToday.Year, localToday.Month, 1).AddMonths(-(MonthlyWindow - 1));
        var monthLines = await LinesAsync(ToUtc(firstMonth), ToUtc(localToday.AddDays(1)));
        var monthCharges = await ChargesAsync(firstMonth, localToday);

        var monthly = Enumerable.Range(0, MonthlyWindow)
            .Select(i => firstMonth.AddMonths(i))
            .Select(month =>
            {
                bool InMonth(DateTime local) => local.Year == month.Year && local.Month == month.Month;
                var ofMonth = monthLines.Where(l => InMonth(l.DateVente + offset)).ToList();
                return new MargeMonthDto(
                    month,
                    ofMonth.Sum(l => l.PrixTotal),
                    ofMonth.Sum(l => l.Cost),
                    monthCharges.Where(c => InMonth(c.Date)).Sum(c => c.Montant),
                    monthCharges.Where(c => c.Fixe && InMonth(c.Date)).Sum(c => c.Montant));
            })
            .ToList();

        return Results.Ok(new MargesResponse(
            totalRevenue, totalCosts, totalCharges, chargesFixes, chargesVariables,
            grossProfit, netProfit, grossMargin, netMargin,
            lines.Select(l => l.VenteId).Distinct().Count(),
            breakEven.Mcv, breakEven.TauxMcv, breakEven.Seuil, pointMortDate,
            previousRevenue, previousGrossProfit,
            estimated.Count, estimated.Sum(l => l.PrixTotal),
            productRows, categoryRows, monthly,
            categories.Select(c => new MargeCategoryOptionDto(c.Id, c.Name)).ToList()));
    }

    /// <summary>
    /// The break-even analysis: marge sur coûts variables (MCV) = CA − coûts variables, its
    /// rate on revenue, and the seuil de rentabilité = charges fixes ÷ taux de MCV - the
    /// revenue at which the MCV exactly pays the fixed charges. Null seuil when the rate is
    /// not positive: no amount of revenue breaks even if each sale loses money.
    /// </summary>
    /// <param name="variableCosts">Cost of sales plus the variable charges.</param>
    public static (decimal Mcv, decimal TauxMcv, decimal? Seuil) BreakEven(decimal revenue, decimal variableCosts, decimal fixedCharges)
    {
        var mcv = revenue - variableCosts;
        var taux = Rate(mcv, revenue);
        return (mcv, taux, taux > 0 ? fixedCharges / (taux / 100m) : null);
    }

    /// <summary>
    /// The cost of <paramref name="quantite"/> units, and where it came from - gestionMarges.js's
    /// three-way CASE: nothing for a vente-libre product, the purchase price when there is
    /// one, and otherwise an estimate at <see cref="DefaultMarkupPercent"/> markup.
    /// </summary>
    /// <param name="unitCost">The purchase price at the time of the sale when recorded,
    /// otherwise the product's current one.</param>
    /// <param name="prixTotal">What the line was actually sold for, after discounts.</param>
    public static (decimal Cost, string Source) LineCost(decimal? unitCost, decimal prixTotal, bool venteLibre, int quantite)
    {
        if (venteLibre) return (0m, MargeCostSources.Aucun);
        if (unitCost is { } cost && cost > 0) return (quantite * cost, MargeCostSources.Reel);
        return Estimate(prixTotal);
    }

    /// <summary>
    /// The source app's markup fallback, applied to what the line was actually sold for
    /// rather than to the product's current price as gestionMarges.js does. A product repriced
    /// or repurposed since would otherwise give an "estimate" bearing no relation to the sale
    /// - a 25 F line estimated at a 7 000 F product's cost - and an estimate could exceed the
    /// revenue it was estimated from.
    /// </summary>
    private static (decimal Cost, string Source) Estimate(decimal prixTotal) =>
        (prixTotal / (1 + DefaultMarkupPercent / 100m), MargeCostSources.Estime);

    private static bool SameName(string productName, string nomProduit) =>
        string.IsNullOrWhiteSpace(nomProduit)
        || string.Equals(productName.Trim(), nomProduit.Trim(), StringComparison.OrdinalIgnoreCase);

    private static decimal Rate(decimal part, decimal whole) => whole > 0 ? part / whole * 100m : 0m;

    private static string CategoryName(string? id, IReadOnlyDictionary<string, string> names) =>
        id is not null && names.TryGetValue(id, out var name) ? name : "Sans catégorie";
}
