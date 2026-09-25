using Lonnii.Api.Security;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The Ventes module's till: creating a sale from a cart. Every stock-tracked line decrements
/// the same <see cref="Product.Quantity"/> the Stock module manages, and writes a paired
/// <see cref="StockHistory"/> row, exactly as <c>StockEndpoints.AdjustStockAsync</c> does.
/// </summary>
public static class VentesEndpoints
{
    public static void MapVentesEndpoints(this IEndpointRouteBuilder app)
    {
        var ventes = app.MapGroup("/api/ventes").WithTags("Ventes");

        ventes.MapPost("/", CreateVenteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.CreateVente);
        ventes.MapGet("/", ListVentesAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewVentes);
        ventes.MapGet("/{id}", GetVenteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewVentes);
        ventes.MapPost("/{id}/paiement", AddPaiementAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.AddPayment);
        ventes.MapPut("/{id}/annuler", CancelVenteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.CancelVente);
        ventes.MapPut("/{id}/solder-avoir", SolderAvoirAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.SoldeAvoir);
        ventes.MapPut("/{id}", EditVenteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.EditVente);
        ventes.MapGet("/stats", GetStatsAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewVentesAnalytics);
    }

    /// <summary>A filtered range spans no more than this many days uses a daily bucket for
    /// the revenue-over-time chart; a longer range buckets by month instead, so a
    /// "Cette année" view does not plot 365 nearly-invisible bars.</summary>
    private const int DailyBucketMaxDays = 62;

    /// <summary>
    /// Converts a client-local calendar date range into the UTC instants that actually bound
    /// it in the database. <see cref="Vente.DateVente"/> is stored as UTC
    /// (<c>DateTime.UtcNow</c>), but a date filter is built from the client's own local
    /// calendar day (e.g. <c>DateOnly.FromDateTime(DateTime.Now)</c> for "Aujourd'hui") - so
    /// comparing the bare local date directly against DateVente silently drops a sale made in
    /// the evening as soon as UTC has rolled into the next calendar day. Mirrors Lonnii
    /// Business's own <c>tzOffset</c> query parameter (backend/routes/ventes.js).
    /// </summary>
    /// <param name="tzOffsetMinutes">The client's local time minus UTC, in minutes - same
    /// sign convention as .NET's own <c>TimeZoneInfo.GetUtcOffset</c> (positive east of UTC,
    /// e.g. +60 for Cameroon; negative west of it, e.g. -240 for EDT). Treated as 0 (UTC)
    /// when absent, which keeps existing callers that never sent it working exactly as before.</param>
    private static (DateTime? Start, DateTime? End) LocalRangeToUtc(
        DateOnly? dateDebut, DateOnly? dateFin, int? tzOffsetMinutes)
    {
        var offset = TimeSpan.FromMinutes(tzOffsetMinutes ?? 0);
        var start = dateDebut?.ToDateTime(TimeOnly.MinValue) - offset;
        var end = dateFin?.ToDateTime(TimeOnly.MinValue).AddDays(1) - offset;
        return (start, end);
    }

    /// <summary>The client-local calendar date a UTC-stored instant falls on - the inverse
    /// half of <see cref="LocalRangeToUtc"/>, used to bucket a chart by the day the client
    /// would call it "today", not the UTC day <see cref="Vente.DateVente"/> happens to hold.</summary>
    private static DateOnly LocalDate(DateTime utc, int? tzOffsetMinutes) =>
        DateOnly.FromDateTime(utc + TimeSpan.FromMinutes(tzOffsetMinutes ?? 0));

    /// <summary>
    /// "Liste des Ventes": every sale in the group, newest first, filtered by status, search
    /// term and date range. Mirrors <c>GET /ventes</c> in backend/routes/ventes.js, including
    /// the own-sales-only default - but that own-sales rule is lifted by
    /// <see cref="Priv.Gestion.ViewAllVentes"/> rather than the web app's admin-role check
    /// (see lonnii-preparer-cashier-flow memory), so a cashier can hold it without also
    /// getting member/privilege management.
    /// </summary>
    private static async Task<IResult> ListVentesAsync(
        string? statut, string? search, string? searchType, DateOnly? dateDebut, DateOnly? dateFin,
        int? tzOffsetMinutes, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var canViewAll = scope.IsAdminGeneral || scope.IsAdmin
            || scope.Privileges.HasGestion(Priv.Gestion.ViewAllVentes);

        var query = db.Ventes.AsNoTracking()
            // Required: MontantPaye/MontantRestant are summed from this collection, and the
            // list shows both.
            .Include(v => v.Paiements)
            .Where(v => v.GroupId == scope.GroupId);

        if (!canViewAll)
            query = query.Where(v => v.CreatedBy == scope.UserId);

        var (rangeStart, rangeEnd) = LocalRangeToUtc(dateDebut, dateFin, tzOffsetMinutes);

        if (rangeStart is { } start)
            query = query.Where(v => v.DateVente >= start);

        if (rangeEnd is { } end)
            query = query.Where(v => v.DateVente < end);

        switch (statut)
        {
            case null or "" or "all":
                break;
            case "avoir":
                query = query.Where(v => v.AvoirAmount > 0 && !v.IsAvoirSolded
                    && v.StatutPaiement != StatutPaiement.Annule);
                break;
            case "cancelled":
                query = query.Where(v => v.StatutPaiement == StatutPaiement.Annule);
                break;
            case "paid":
                query = query.Where(v => v.StatutPaiement == StatutPaiement.Paye);
                break;
            case "partial":
                query = query.Where(v => v.StatutPaiement == StatutPaiement.Partiel);
                break;
            case "pending":
                query = query.Where(v => v.StatutPaiement == StatutPaiement.EnAttente);
                break;
            default:
                query = query.Where(v => v.StatutPaiement == statut);
                break;
        }

        var ventes = await query.OrderByDescending(v => v.DateVente).Take(1000).ToListAsync(ct);

        // Vendeur names, resolved once per distinct seller rather than once per row.
        var vendeurIds = ventes.Select(v => v.CreatedBy).Where(id => id is not null).Distinct().ToList();
        var vendeurNames = await db.Users.AsNoTracking()
            .Where(u => vendeurIds.Contains(u.IdUser))
            .Select(u => new { u.IdUser, u.FirstName, u.LastName, u.Username, u.Email })
            .ToDictionaryAsync(u => u.IdUser, ct);

        string? NameFor(string? userId)
        {
            if (userId is null || !vendeurNames.TryGetValue(userId, out var u)) return null;
            var full = $"{u.FirstName} {u.LastName}".Trim();
            return !string.IsNullOrWhiteSpace(full) ? full : u.Username ?? u.Email;
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            ventes = (searchType switch
            {
                "client" => ventes.Where(v => v.ClientNom?.Contains(term, StringComparison.OrdinalIgnoreCase) == true),
                "vendeur" => ventes.Where(v => NameFor(v.CreatedBy)?.Contains(term, StringComparison.OrdinalIgnoreCase) == true),
                _ => ventes.Where(v =>
                    v.NumeroVente.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || v.ClientNom?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
                    || NameFor(v.CreatedBy)?.Contains(term, StringComparison.OrdinalIgnoreCase) == true),
            }).ToList();
        }

        var items = ventes.Select(v => new VenteListItemDto(
            v.Id, v.NumeroVente, v.DateVente, v.ClientNom, NameFor(v.CreatedBy),
            v.MontantTotal, v.MontantPaye, v.MontantRestant,
            v.AvoirAmount, v.IsAvoirSolded,
            StatutPaiement.Normalise(v.StatutPaiement), v.CancellationReason, v.ModePaiement))
            .ToList();

        return Results.Ok(new VentesListResponse(items));
    }

    /// <summary>
    /// "Statistiques" tab: KPI tiles plus the category, top-product, payment-method and
    /// revenue-over-time breakdowns behind its charts. Extends Lonnii Business's own
    /// <c>GET /ventes/stats</c> (period-only) with optional <paramref name="categoryId"/> and
    /// <paramref name="productId"/> filters - see <see cref="VentesStatsResponse"/> for how
    /// those change what each amount means.
    /// </summary>
    private static async Task<IResult> GetStatsAsync(
        DateOnly? dateDebut, DateOnly? dateFin, string? categoryId, string? productId, int? tzOffsetMinutes,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var query = db.Ventes.AsNoTracking()
            .Include(v => v.Items)
            .Include(v => v.Paiements)
            .Where(v => v.GroupId == scope.GroupId);

        var (rangeStart, rangeEnd) = LocalRangeToUtc(dateDebut, dateFin, tzOffsetMinutes);

        if (rangeStart is { } start)
            query = query.Where(v => v.DateVente >= start);

        if (rangeEnd is { } end)
            query = query.Where(v => v.DateVente < end);

        var ventes = await query.ToListAsync(ct);

        var productCategories = await db.Products.AsNoTracking()
            .Where(p => p.GroupId == scope.GroupId)
            .Select(p => new { p.Id, p.CategoryId })
            .ToDictionaryAsync(p => p.Id, p => p.CategoryId, ct);

        var categoryNames = await db.Categories.AsNoTracking()
            .Where(c => c.GroupId == scope.GroupId)
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        string? CategoryOf(string? prodId) =>
            prodId is not null && productCategories.TryGetValue(prodId, out var catId) ? catId : null;

        var hasLineFilter = categoryId is not null || productId is not null;

        bool ItemMatches(VenteItem item) =>
            (productId is null || item.ProductId == productId)
            && (categoryId is null || CategoryOf(item.ProductId) == categoryId);

        var active = ventes.Where(v => StatutPaiement.Normalise(v.StatutPaiement) != StatutPaiement.Annule).ToList();

        // One row per matching vente: Montant is its matching-line revenue (the whole
        // MontantTotal when no category/product filter narrows it), Share is what fraction
        // of the vente that represents - used below to prorate its payments.
        var rows = new List<(Vente Vente, decimal Montant, decimal Share)>();
        foreach (var v in active)
        {
            if (!hasLineFilter)
            {
                rows.Add((v, v.MontantTotal, 1m));
                continue;
            }

            var matching = v.Items.Where(ItemMatches).ToList();
            if (matching.Count == 0) continue;

            var montant = matching.Sum(i => i.PrixTotal);
            rows.Add((v, montant, v.MontantTotal > 0 ? montant / v.MontantTotal : 0m));
        }

        var chiffreAffaires = rows.Sum(r => r.Montant);
        var montantEncaisse = rows.Sum(r => r.Vente.MontantPaye * r.Share);
        var montantRestant = rows.Sum(r => Math.Max(0, r.Vente.MontantRestant) * r.Share);
        var totalAvoir = rows.Sum(r => r.Vente.AvoirAmount * r.Share);
        var venteMoyenne = rows.Count > 0 ? chiffreAffaires / rows.Count : 0;
        var venteMax = rows.Count > 0 ? rows.Max(r => r.Montant) : 0;

        int CountByStatus(string statut) =>
            rows.Count(r => StatutPaiement.Normalise(r.Vente.StatutPaiement) == statut);

        var clientsUniques = rows.Select(r => r.Vente.ClientNom)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Count();

        decimal PaymentSum(Func<string, bool> modeMatches) => rows.Sum(r =>
            r.Vente.Paiements.Where(p => modeMatches(p.ModePaiement)).Sum(p => p.Montant) * r.Share);

        var paiementCash = PaymentSum(m => m == ModePaiement.Cash);
        var paiementMobile = PaymentSum(m => m == ModePaiement.MobileMoney);
        var paiementCarte = PaymentSum(m => m == ModePaiement.Carte);
        var paiementAutres = PaymentSum(m => m != ModePaiement.Cash && m != ModePaiement.MobileMoney && m != ModePaiement.Carte);

        // The category/top-product charts read the matching lines directly rather than the
        // prorated rows above - a pie or bar chart needs the real per-line amounts, not a
        // share of the whole sale.
        var lines = active.SelectMany(v => v.Items.Where(ItemMatches)).ToList();

        var categorySales = lines
            .GroupBy(i => CategoryOf(i.ProductId))
            .Select(g => new CategorySalesDto(
                g.Key,
                g.Key is not null && categoryNames.TryGetValue(g.Key, out var name) ? name : "Non catégorisé",
                g.Sum(i => i.PrixTotal),
                g.Sum(i => i.Quantite)))
            .Where(c => c.MontantTotal > 0)
            .OrderByDescending(c => c.MontantTotal)
            .ToList();

        var topProducts = lines
            .GroupBy(i => new { i.ProductId, i.NomProduit })
            .Select(g => new TopProductDto(
                g.Key.ProductId, g.Key.NomProduit, g.Sum(i => i.Quantite), g.Sum(i => i.PrixTotal)))
            .OrderByDescending(p => p.MontantTotal)
            .Take(8)
            .ToList();

        // Revenue-over-time chart: daily buckets for a short range, monthly ones for a long
        // one, so "Cette année" does not plot 365 nearly-invisible bars. Bucketed by the
        // client's local calendar day (LocalDate), not DateVente's own UTC one - a sale made
        // at 22:00 local should land in that local day's bar even if UTC has already rolled
        // into the next date.
        var byMonth = dateDebut is { } db1 && dateFin is { } df1 && df1.DayNumber - db1.DayNumber > DailyBucketMaxDays;
        var serie = rows
            .GroupBy(r =>
            {
                var local = LocalDate(r.Vente.DateVente, tzOffsetMinutes);
                return byMonth ? new DateOnly(local.Year, local.Month, 1) : local;
            })
            .Select(g => new VenteStatsPointDto(g.Key, g.Count(), g.Sum(r => r.Montant)))
            .OrderBy(p => p.Date)
            .ToList();

        var distinctDays = rows.Select(r => LocalDate(r.Vente.DateVente, tzOffsetMinutes)).Distinct().Count();
        var moyenneQuotidienne = distinctDays > 0 ? chiffreAffaires / distinctDays : 0;

        // Compares the filtered range against the immediately preceding range of the same
        // length, so "Croissance" stays meaningful for an arbitrary custom period, not only
        // a calendar month.
        decimal croissance = 0;
        if (dateDebut is { } periodDebut && dateFin is { } periodFin)
        {
            var spanDays = periodFin.DayNumber - periodDebut.DayNumber + 1;
            var (prevStart, prevEnd) = LocalRangeToUtc(periodDebut.AddDays(-spanDays), periodDebut.AddDays(-1), tzOffsetMinutes);

            var previousTotal = await db.Ventes.AsNoTracking()
                .Where(v => v.GroupId == scope.GroupId && v.DateVente >= prevStart && v.DateVente < prevEnd
                    && v.StatutPaiement != StatutPaiement.Annule)
                .SumAsync(v => (decimal?)v.MontantTotal, ct) ?? 0;

            if (previousTotal > 0)
                croissance = (chiffreAffaires - previousTotal) / previousTotal * 100;
        }

        return Results.Ok(new VentesStatsResponse(
            rows.Count, chiffreAffaires, montantEncaisse, montantRestant, totalAvoir,
            venteMoyenne, venteMax,
            CountByStatus(StatutPaiement.Paye), CountByStatus(StatutPaiement.Partiel),
            CountByStatus(StatutPaiement.EnAttente), ventes.Count - active.Count,
            clientsUniques, paiementCash, paiementMobile, paiementCarte, paiementAutres,
            moyenneQuotidienne, croissance, categorySales, topProducts, serie));
    }

    /// <summary>
    /// Records a payment against an existing sale - settling a facture, or a further
    /// instalment on a partial one. Overpaying turns the excess into an avoir rather than
    /// being capped, same as backend/routes/ventes.js's <c>POST /:id/paiement</c>.
    /// </summary>
    private static async Task<IResult> AddPaiementAsync(
        string id, AddPaiementRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.Montant <= 0)
            return Results.BadRequest(new ApiError("Le montant doit être positif"));

        var vente = await db.Ventes.Include(v => v.Paiements)
            .FirstOrDefaultAsync(v => v.Id == id && v.GroupId == scope.GroupId, ct);
        if (vente is null) return Results.NotFound(new ApiError("Vente introuvable"));

        if (StatutPaiement.Normalise(vente.StatutPaiement) == StatutPaiement.Annule)
            return Results.BadRequest(new ApiError("Cette vente est annulée"));

        vente.Paiements.Add(new PaiementVente
        {
            VenteId = vente.Id,
            Montant = request.Montant,
            ModePaiement = request.ModePaiement,
            Reference = Blank(request.Reference),
            Notes = Blank(request.Notes),
            CreatedBy = scope.UserId,
        });

        var restant = vente.MontantTotal - vente.MontantPaye;
        if (restant < 0)
        {
            vente.AvoirAmount += -restant;
            vente.IsAvoir = vente.AvoirAmount > 0;
            restant = 0;
        }

        vente.StatutPaiement = restant < 1
            ? StatutPaiement.Paye
            : vente.MontantPaye > 0 ? StatutPaiement.Partiel : StatutPaiement.EnAttente;
        vente.UpdatedBy = scope.UserId;
        vente.UpdatedAt = DateTime.UtcNow;

        db.VentesUserActivities.Add(new VentesUserActivity
        {
            GroupId = scope.GroupId,
            UserId = scope.UserId,
            Action = "vente.paiement",
            TargetId = vente.Id,
            Details = $"{vente.NumeroVente}: +{request.Montant:0.##}",
        });

        await db.SaveChangesAsync(ct);

        return Results.Ok(await ToDtoAsync(db, vente, ct));
    }

    /// <summary>
    /// Cancels a sale and restores stock for every tracked line, same as
    /// backend/routes/ventes.js's <c>PUT /:id/annuler</c>. A motif is mandatory.
    /// </summary>
    private static async Task<IResult> CancelVenteAsync(
        string id, CancelVenteRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Motif))
            return Results.BadRequest(new ApiError("Le motif d'annulation est requis"));

        var vente = await db.Ventes
            .Include(v => v.Items)
            .Include(v => v.Paiements)
            .FirstOrDefaultAsync(v => v.Id == id && v.GroupId == scope.GroupId, ct);
        if (vente is null) return Results.NotFound(new ApiError("Vente introuvable"));

        if (StatutPaiement.Normalise(vente.StatutPaiement) == StatutPaiement.Annule)
            return Results.BadRequest(new ApiError("Cette vente est déjà annulée"));

        var productIds = vente.Items.Where(i => i.ProductId is not null)
            .Select(i => i.ProductId!).Distinct().ToList();
        var products = await db.Products
            .Where(p => p.GroupId == scope.GroupId && productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        foreach (var item in vente.Items)
        {
            if (item.ProductId is null || !products.TryGetValue(item.ProductId, out var product)) continue;
            if (product.VenteLibre && !product.StockIllimite && product.Quantity == 0) continue;

            product.Quantity += item.Quantite;
            product.UpdatedBy = scope.UserId;
            product.UpdatedAt = DateTime.UtcNow;
        }

        vente.StatutPaiement = StatutPaiement.Annule;
        vente.CancellationReason = request.Motif.Trim();
        vente.CancelledAt = DateTime.UtcNow;
        vente.CancelledBy = scope.UserId;
        vente.AvoirAmount = 0;
        vente.IsAvoir = false;
        vente.IsAvoirSolded = false;
        vente.UpdatedBy = scope.UserId;
        vente.UpdatedAt = DateTime.UtcNow;

        db.VentesUserActivities.Add(new VentesUserActivity
        {
            GroupId = scope.GroupId,
            UserId = scope.UserId,
            Action = "vente.cancel",
            TargetId = vente.Id,
            Details = $"{vente.NumeroVente}: {vente.CancellationReason}",
        });

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { success = true });
    }

    /// <summary>Settles an avoir: marks it paid out, keeping the amount for traceability
    /// (the money is now the client's, not owed by the till). Mirrors <c>PUT
    /// /:id/solder-avoir</c>; the caisse "sortie" transaction it also records there has no
    /// equivalent yet - the caisse module itself is not built on this port.</summary>
    private static async Task<IResult> SolderAvoirAsync(
        string id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var vente = await db.Ventes.FirstOrDefaultAsync(v => v.Id == id && v.GroupId == scope.GroupId, ct);
        if (vente is null) return Results.NotFound(new ApiError("Vente introuvable"));

        if (vente.AvoirAmount <= 0 || vente.IsAvoirSolded)
            return Results.BadRequest(new ApiError("Cette vente n'a pas d'avoir à solder"));

        vente.IsAvoirSolded = true;
        vente.AvoirSoldedAt = DateTime.UtcNow;
        vente.AvoirSoldedBy = scope.UserId;

        await db.SaveChangesAsync(ct);

        var soldeurNom = await DisplayNameAsync(db, scope.UserId, ct);
        return Results.Ok(new { success = true, avoirSoldedByName = soldeurNom });
    }

    /// <summary>Edits a sale's client name and/or date - fields sometimes forgotten at sale
    /// time, which otherwise skews analytics. Mirrors <c>PUT /:id</c>.</summary>
    private static async Task<IResult> EditVenteAsync(
        string id, EditVenteRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.ClientNom is null && request.DateVente is null)
            return Results.BadRequest(new ApiError("Aucune modification fournie"));

        var vente = await db.Ventes.FirstOrDefaultAsync(v => v.Id == id && v.GroupId == scope.GroupId, ct);
        if (vente is null) return Results.NotFound(new ApiError("Vente introuvable"));

        if (request.ClientNom is not null) vente.ClientNom = Blank(request.ClientNom);
        if (request.DateVente is { } date) vente.DateVente = date;
        vente.UpdatedBy = scope.UserId;
        vente.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { success = true });
    }

    /// <summary>
    /// Rings up a sale: resolves each line's price and product, decrements tracked stock,
    /// records the payment, and returns the completed sale.
    /// </summary>
    private static async Task<IResult> CreateVenteAsync(
        CreateVenteRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.Items is not { Count: > 0 })
            return Results.BadRequest(new ApiError("Le panier est vide"));

        if (request.Items.Any(i => i.Quantity <= 0))
            return Results.BadRequest(new ApiError("Chaque ligne doit avoir une quantité positive"));

        if (request.MontantPaye < 0)
            return Results.BadRequest(new ApiError("Le montant payé ne peut pas être négatif"));

        var productIds = request.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await db.Products
            .Where(p => p.GroupId == scope.GroupId && p.DeletedAt == null && productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        if (productIds.Any(id => !products.ContainsKey(id)))
            return Results.BadRequest(new ApiError("Un des produits du panier est introuvable"));

        var vente = new Vente
        {
            GroupId = scope.GroupId,
            NumeroVente = await NextNumeroVenteAsync(db, scope.GroupId, ct),
            ClientNom = Blank(request.ClientNom),
            ClientTelephone = Blank(request.ClientTelephone),
            ClientEmail = Blank(request.ClientEmail),
            ModePaiement = request.ModePaiement,
            Notes = Blank(request.Notes),
            IdempotencyKey = Blank(request.IdempotencyKey),
            CreatedBy = scope.UserId,
            UpdatedBy = scope.UserId,
        };

        decimal total = 0;

        foreach (var line in request.Items)
        {
            var product = products[line.ProductId];

            decimal unitPrice;
            if (product.PrixFixe)
            {
                unitPrice = product.Price;
            }
            else if (line.UnitPrice is { } manual && manual >= 0)
            {
                unitPrice = manual;
            }
            else
            {
                return Results.BadRequest(new ApiError(
                    $"Le prix de « {product.Name} » doit être précisé pour cette vente"));
            }

            var lineTotal = line.DiscountType == DiscountTypes.Amount
                ? unitPrice * line.Quantity - line.Discount
                : unitPrice * line.Quantity * (1 - line.Discount / 100m);
            lineTotal = Math.Max(0, lineTotal);
            total += lineTotal;

            vente.Items.Add(new VenteItem
            {
                VenteId = vente.Id,
                ProductId = product.Id,
                NomProduit = product.Name,
                Quantite = line.Quantity,
                PrixUnitaire = unitPrice,
                PrixTotal = lineTotal,
                Discount = line.Discount,
                DiscountType = line.DiscountType,
            });

            if (product.VenteLibre || product.StockIllimite) continue;

            var newQuantity = product.Quantity - line.Quantity;
            if (newQuantity < 0)
                return Results.BadRequest(new ApiError(
                    $"Stock insuffisant pour « {product.Name} » : {product.Quantity} en stock, {line.Quantity} demandés"));

            var previous = product.Quantity;
            product.Quantity = newQuantity;
            product.UpdatedBy = scope.UserId;
            product.UpdatedAt = DateTime.UtcNow;

            db.StockHistories.Add(new StockHistory
            {
                GroupId = scope.GroupId,
                ProductId = product.Id,
                MovementType = StockMovementTypes.Vente,
                PreviousQuantity = previous,
                QuantityChanged = -line.Quantity,
                NewQuantity = newQuantity,
                UnitCost = product.CostPrice,
                TotalCost = product.CostPrice * line.Quantity,
                ReferenceId = vente.Id,
                ReferenceType = "sale",
                UserId = scope.UserId,
            });
        }

        var remiseGlobale = Math.Clamp(request.RemiseGlobale, 0, total);
        total -= remiseGlobale;

        var montantPaye = Math.Min(request.MontantPaye, total);

        // Hiding "Payer & Valider" behind can_add_payment on the client is not enforcement -
        // this is: a preparer's cart can only ever produce an unpaid facture, no matter what
        // the request claims montant_paye is.
        if (montantPaye > 0 && !scope.Privileges.HasGestion(Priv.Gestion.AddPayment))
            return Results.Json(
                new ApiError("Privilège insuffisant", Priv.Gestion.AddPayment),
                statusCode: StatusCodes.Status403Forbidden);

        vente.MontantTotal = total;
        vente.StatutPaiement = total - montantPaye <= 0
            ? StatutPaiement.Paye
            : montantPaye > 0 ? StatutPaiement.Partiel : StatutPaiement.EnAttente;

        db.Ventes.Add(vente);

        if (montantPaye > 0)
        {
            // Added through the navigation, not db.PaiementsVentes, so vente.MontantPaye -
            // which sums this collection - is already right when the response is built below.
            vente.Paiements.Add(new PaiementVente
            {
                VenteId = vente.Id,
                Montant = montantPaye,
                ModePaiement = request.ModePaiement,
                CreatedBy = scope.UserId,
            });
        }

        db.VentesUserActivities.Add(new VentesUserActivity
        {
            GroupId = scope.GroupId,
            UserId = scope.UserId,
            Action = "vente.create",
            TargetId = vente.Id,
            Details = $"{vente.NumeroVente}: {total:0.##}",
        });

        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/ventes/{vente.Id}", await ToDtoAsync(db, vente, ct));
    }

    private static async Task<IResult> GetVenteAsync(
        string id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var vente = await db.Ventes
            .Include(v => v.Items)
            // Required: MontantPaye and MontantRestant are summed from this collection.
            .Include(v => v.Paiements)
            .FirstOrDefaultAsync(v => v.Id == id && v.GroupId == scope.GroupId, ct);

        if (vente is null) return Results.NotFound(new ApiError("Vente introuvable"));

        return Results.Ok(await ToDtoAsync(db, vente, ct));
    }

    /// <summary>Next human-readable reference for this group this month, e.g.
    /// <c>VNT-202609-00000002</c> - same format and monthly reset as Lonnii Business
    /// (backend/routes/ventes.js), so a receipt printed from either looks consistent.</summary>
    private static async Task<string> NextNumeroVenteAsync(LonniiDbContext db, string groupId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var count = await db.Ventes.CountAsync(v => v.GroupId == groupId && v.DateVente >= monthStart, ct);
        return $"VNT-{now:yyyyMM}-{count + 1:D8}";
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The name a receipt prints for "Caissier" - first/last name, falling back
    /// through username and email, same order the shell's own DisplayName uses.</summary>
    private static async Task<string?> DisplayNameAsync(LonniiDbContext db, string? userId, CancellationToken ct)
    {
        if (userId is null) return null;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.IdUser == userId)
            .Select(u => new { u.FirstName, u.LastName, u.Username, u.Email })
            .FirstOrDefaultAsync(ct);
        if (user is null) return null;

        var full = $"{user.FirstName} {user.LastName}".Trim();
        return !string.IsNullOrWhiteSpace(full) ? full : user.Username ?? user.Email;
    }

    /// <summary>Builds the full sale DTO, resolving every name it needs (vendeur, avoir
    /// soldeur, and each payment's recorder) in one pass. Used for both the receipt/print
    /// view and the "Détails" view - <see cref="Paiements"/> is what tells them apart: the
    /// receipt shows one summed "Total Payé" line, the detail view a per-payment history
    /// (Lonnii Business's ListeVentes.jsx "Détails" modal, not its print template).</summary>
    private static async Task<VenteDto> ToDtoAsync(LonniiDbContext db, Vente v, CancellationToken ct)
    {
        var vendeurNom = await DisplayNameAsync(db, v.CreatedBy, ct);
        var avoirSoldedByName = await DisplayNameAsync(db, v.AvoirSoldedBy, ct);
        var cancelledByName = await DisplayNameAsync(db, v.CancelledBy, ct);

        var payerIds = v.Paiements.Select(p => p.CreatedBy).Where(id => id is not null).Distinct().ToList();
        var payerNames = await db.Users.AsNoTracking()
            .Where(u => payerIds.Contains(u.IdUser))
            .Select(u => new { u.IdUser, u.FirstName, u.LastName, u.Username, u.Email })
            .ToDictionaryAsync(u => u.IdUser, ct);

        string? PayerName(string? userId)
        {
            if (userId is null || !payerNames.TryGetValue(userId, out var u)) return null;
            var full = $"{u.FirstName} {u.LastName}".Trim();
            return !string.IsNullOrWhiteSpace(full) ? full : u.Username ?? u.Email;
        }

        var paiements = v.Paiements.OrderBy(p => p.DatePaiement)
            .Select(p => new PaiementDto(p.Id, p.Montant, p.ModePaiement, p.DatePaiement, PayerName(p.CreatedBy)))
            .ToList();

        return new(
            v.Id, v.NumeroVente, v.DateVente, v.ClientNom, v.ClientTelephone, v.ClientEmail,
            v.MontantTotal, v.MontantPaye, v.MontantRestant,
            // Normalised here rather than trusted as-is: rows read from the live Postgres can
            // still hold the legacy English/mixed values (see StatutPaiement's remarks),
            // and a receipt switching template on this value needs the real answer, not
            // "paid" for everything by default.
            StatutPaiement.Normalise(v.StatutPaiement), v.ModePaiement, v.Notes,
            vendeurNom,
            v.Items.Select(i => new VenteItemDto(
                i.Id, i.ProductId, i.NomProduit, i.Quantite, i.PrixUnitaire, i.PrixTotal, i.Discount, i.DiscountType))
                .ToList(),
            v.AvoirAmount, v.IsAvoirSolded, avoirSoldedByName, v.AvoirSoldedAt,
            v.CancellationReason, cancelledByName, v.CancelledAt, paiements);
    }
}
