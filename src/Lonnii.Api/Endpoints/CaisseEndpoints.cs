using Lonnii.Api.Security;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The Ventes module's till reconciliation: opening/closing a cash register session and
/// reviewing past ones. Mirrors backend/routes/ventes.js's <c>/caisse/*</c> routes.
///
/// Every route here was unguarded in Lonnii Business - a session check only, no privilege
/// check at all - which the lonnii-preparer-cashier-flow memory flags as a theft route for
/// the écart ones in particular. The port gates every route on the dedicated caisse
/// privileges the catalogue already defines, rather than reusing <c>can_add_payment</c>.
/// </summary>
public static class CaisseEndpoints
{
    public static void MapCaisseEndpoints(this IEndpointRouteBuilder app)
    {
        var caisse = app.MapGroup("/api/caisse").WithTags("Caisse");

        caisse.MapGet("/status", GetStatusAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.AccessCaisse);
        caisse.MapPost("/ouvrir", OuvrirAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.OpenCaisse);
        caisse.MapPost("/fermer", FermerAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.CloseCaisse);
        caisse.MapGet("/historique", HistoriqueAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewCaisseHistory);
        caisse.MapGet("/vendeurs", VendeursAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewCaisseHistory);
        caisse.MapPost("/{id:int}/resolve-ecart", ResolveEcartAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ResolveCaisseEcart);
        // Gated on can_close_caisse rather than a dedicated privilege: taking cash out of the
        // drawer carries the same trust as reconciling it shut, and Lonnii Business's own
        // caisse_transactions table has no manual-withdrawal route to mirror a privilege from
        // (see lonnii-schema-mapping memory) - its "sortie" rows are only ever written by the
        // server itself, for avoir refunds.
        caisse.MapPost("/retrait", RetraitAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.CloseCaisse);
    }

    /// <summary>"Ouvrir Caisse": one open session per user per group, enforced by the
    /// database's own filtered unique index - this check exists to fail with a clear message
    /// rather than a raw constraint-violation 500.</summary>
    private static async Task<IResult> OuvrirAsync(
        OpenCaisseRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.MontantInitialCash < 0 || request.MontantInitialMobile < 0)
            return Results.BadRequest(new ApiError("Le montant initial ne peut pas être négatif"));

        var alreadyOpen = await db.Caisses.AnyAsync(
            c => c.GroupId == scope.GroupId && c.UserId == scope.UserId && c.Status == CaisseStatus.Open, ct);
        if (alreadyOpen)
            return Results.BadRequest(new ApiError(
                "Vous avez déjà une caisse ouverte. Veuillez la fermer avant d'en ouvrir une nouvelle."));

        var userName = await DisplayNameAsync(db, scope.UserId, ct) ?? scope.UserId;

        var caisse = new Caisse
        {
            GroupId = scope.GroupId,
            UserId = scope.UserId,
            UserName = userName,
            MontantInitial = request.MontantInitialCash + request.MontantInitialMobile,
            MontantInitialCash = request.MontantInitialCash,
            MontantInitialMobile = request.MontantInitialMobile,
            Notes = Blank(request.Notes),
        };

        db.Caisses.Add(caisse);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/caisse/{caisse.Id}", await ToDtoAsync(db, caisse, ct));
    }

    /// <summary>The caller's own open session, with stats computed live from sales rung up and
    /// payments taken since it opened. Null when nothing is open.</summary>
    private static async Task<IResult> GetStatusAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var caisse = await db.Caisses.FirstOrDefaultAsync(
            c => c.GroupId == scope.GroupId && c.UserId == scope.UserId && c.Status == CaisseStatus.Open, ct);

        return Results.Ok(new CaisseStatusResponse(caisse is null ? null : await ToDtoAsync(db, caisse, ct)));
    }

    /// <summary>"Fermer Caisse": stamps final live stats onto the session and records the
    /// écart between what the drawer should hold in cash and what was actually counted.</summary>
    private static async Task<IResult> FermerAsync(
        CloseCaisseRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.MontantFinal < 0)
            return Results.BadRequest(new ApiError("Le montant compté ne peut pas être négatif"));

        var caisse = await db.Caisses.FirstOrDefaultAsync(
            c => c.GroupId == scope.GroupId && c.UserId == scope.UserId && c.Status == CaisseStatus.Open, ct);
        if (caisse is null) return Results.NotFound(new ApiError("Aucune caisse ouverte trouvée"));

        var closingAt = DateTime.UtcNow;
        var stats = await ComputeLiveStatsAsync(db, caisse, closingAt, ct);

        caisse.DateFermeture = closingAt;
        caisse.MontantFinal = request.MontantFinal;
        caisse.TotalVentes = stats.TotalVentes;
        caisse.TotalChiffreAffaires = stats.ChiffreAffaires;
        caisse.TotalAvoir = stats.TotalAvoir;
        caisse.PaiementCash = stats.PaiementCash;
        caisse.PaiementMobile = stats.PaiementMobile;
        caisse.PaiementCarte = stats.PaiementCarte;
        caisse.PaiementAutres = stats.PaiementAutres;
        caisse.TotalEncaisse = stats.TotalEncaisse;
        caisse.TotalRestant = Math.Max(0, stats.ChiffreAffaires - stats.TotalEncaisse);
        caisse.Ecart = request.MontantFinal - stats.ExpectedCash(caisse);
        caisse.Notes = Blank(request.Notes) ?? caisse.Notes;
        caisse.Status = CaisseStatus.Closed;
        caisse.UpdatedAt = closingAt;

        await db.SaveChangesAsync(ct);

        return Results.Ok(await ToDtoAsync(db, caisse, ct));
    }

    /// <summary>"Historique Caisse": every session in the group, newest first. A user without
    /// <see cref="Priv.Gestion.ViewAllVentes"/>-equivalent admin standing sees only their own -
    /// Lonnii Business decides this by admin role alone (ventes.js:1352), which this keeps,
    /// since <c>can_view_caisse_history</c> is what a manager who reconciles several cashiers'
    /// tills holds instead.</summary>
    private static async Task<IResult> HistoriqueAsync(
        DateOnly? dateDebut, DateOnly? dateFin, string? userId, int page, int pageSize,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize is <= 0 or > 200 ? 50 : pageSize;

        var query = db.Caisses.AsNoTracking().Where(c => c.GroupId == scope.GroupId);

        if (!scope.IsAdmin && !scope.IsAdminGeneral)
            query = query.Where(c => c.UserId == scope.UserId);
        else if (!string.IsNullOrWhiteSpace(userId))
            query = query.Where(c => c.UserId == userId);

        if (dateDebut is { } start)
            query = query.Where(c => c.DateOuverture >= start.ToDateTime(TimeOnly.MinValue));
        if (dateFin is { } end)
            query = query.Where(c => c.DateOuverture < end.ToDateTime(TimeOnly.MinValue).AddDays(1));

        var total = await query.CountAsync(ct);
        var caisses = await query.OrderByDescending(c => c.DateOuverture)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        var retraits = await LoadRetraitsAsync(db, caisses.Select(c => c.Id).ToList(), ct);

        var dtos = new List<CaisseDto>(caisses.Count);
        foreach (var caisse in caisses)
        {
            var own = retraits.GetValueOrDefault(caisse.Id);
            // An open session's totals are stale the instant they're read, so it gets the same
            // live recompute as GetStatusAsync; a closed one already has its final figures.
            dtos.Add(caisse.Status == CaisseStatus.Open
                ? await ToDtoAsync(db, caisse, ct, own)
                : ToDto(caisse, own));
        }

        return Results.Ok(new CaisseHistoryResponse(dtos, total));
    }

    /// <summary>Distinct list of users who have opened a caisse in this group, for the
    /// history screen's vendeur filter. Grouped client-side rather than via a translated
    /// <c>GroupBy(...).Select(g => g.OrderBy(...).First())</c> - SQLite's EF Core provider
    /// cannot translate a nested ordered <c>First()</c> inside a group projection and throws
    /// InvalidOperationException at query-compile time, which crashed "Historique Caisse" for
    /// every caller (the client's error handling only expects a JSON error body, not a raw
    /// developer exception page). The per-group row count here is one row per session ever
    /// opened, small enough that pulling it into memory is not a real cost.</summary>
    private static async Task<IResult> VendeursAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var rows = await db.Caisses.AsNoTracking()
            .Where(c => c.GroupId == scope.GroupId)
            .Select(c => new { c.UserId, c.UserName, c.DateOuverture })
            .ToListAsync(ct);

        var vendeurs = rows
            .GroupBy(c => c.UserId)
            .Select(g => new CaisseVendeurDto(g.Key, g.OrderByDescending(c => c.DateOuverture).First().UserName))
            .OrderBy(v => v.UserName)
            .ToList();

        return Results.Ok(vendeurs);
    }

    /// <summary>Resolves a closed session's écart. <c>Adjusted</c> corrects
    /// <see cref="Caisse.MontantFinal"/> to the expected cash figure so the écart becomes
    /// zero; the other two resolution types keep the recorded écart and only annotate it, so
    /// the discrepancy stays visible in the history rather than being quietly erased.</summary>
    private static async Task<IResult> ResolveEcartAsync(
        int id, ResolveEcartRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var caisse = await db.Caisses.FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
        if (caisse is null) return Results.NotFound(new ApiError("Caisse introuvable"));

        if (caisse.Status != CaisseStatus.Closed)
            return Results.BadRequest(new ApiError("Seules les caisses fermées peuvent être résolues"));

        if (request.ResolutionType is not (EcartResolutionTypes.Justified
            or EcartResolutionTypes.WrittenOff or EcartResolutionTypes.Adjusted))
        {
            return Results.BadRequest(new ApiError("Type de résolution invalide"));
        }

        if (request.ResolutionType == EcartResolutionTypes.Adjusted)
        {
            caisse.MontantFinal -= caisse.Ecart;
            caisse.Ecart = 0;
        }

        caisse.EcartResolved = true;
        caisse.EcartResolvedBy = scope.UserId;
        caisse.EcartResolvedAt = DateTime.UtcNow;
        caisse.EcartResolutionNote = Blank(request.Notes);
        caisse.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.Ok(await ToDtoAsync(db, caisse, ct));
    }

    /// <summary>"Retirer de la caisse": cash out for something other than a sale refund - the
    /// motif is what makes it auditable later in the history, so it is required, not
    /// optional like <see cref="CloseCaisseRequest.Notes"/>. Capped at what the drawer is
    /// currently expected to hold, the same figure <see cref="LiveStats.ExpectedCash"/>
    /// reports at close time, so a withdrawal can never push the session into a manufactured
    /// shortfall.</summary>
    private static async Task<IResult> RetraitAsync(
        WithdrawCaisseRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.Montant <= 0)
            return Results.BadRequest(new ApiError("Le montant du retrait doit être supérieur à zéro"));

        var motif = Blank(request.Motif);
        if (motif is null)
            return Results.BadRequest(new ApiError("Le motif du retrait est requis"));

        var caisse = await db.Caisses.FirstOrDefaultAsync(
            c => c.GroupId == scope.GroupId && c.UserId == scope.UserId && c.Status == CaisseStatus.Open, ct);
        if (caisse is null) return Results.NotFound(new ApiError("Aucune caisse ouverte trouvée"));

        var stats = await ComputeLiveStatsAsync(db, caisse, DateTime.UtcNow, ct);
        if (request.Montant > stats.ExpectedCash(caisse))
            return Results.BadRequest(new ApiError("Le montant dépasse les espèces disponibles en caisse"));

        db.CaisseTransactions.Add(new CaisseTransaction
        {
            CaisseId = caisse.Id,
            GroupId = scope.GroupId,
            Type = "sortie",
            Montant = request.Montant,
            Description = motif,
            Category = RetraitCategory,
            ModePaiement = ModePaiement.Cash,
            CreatedBy = scope.UserId,
        });

        caisse.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(await ToDtoAsync(db, caisse, ct));
    }

    /// <summary>Everything <see cref="ComputeLiveStatsAsync"/> needs to also report an
    /// écart, without making every caller repeat the cash-expectation formula.</summary>
    private readonly record struct LiveStats(
        int TotalVentes, decimal ChiffreAffaires, decimal TotalAvoir,
        decimal PaiementCash, decimal PaiementMobile, decimal PaiementCarte, decimal PaiementAutres,
        decimal TotalEncaisse)
    {
        /// <summary>Cash the drawer should hold: the float's cash portion (not
        /// <see cref="Caisse.MontantInitial"/>, which also carries the mobile-money float -
        /// nobody miscounts a mobile balance), plus cash sale payments, plus cash
        /// <see cref="CaisseTransaction"/> movements (old-facture payments in, avoir refunds
        /// out) - see add_caisse_transactions_table.sql, which scopes that table to exactly
        /// these cash movements.</summary>
        public decimal ExpectedCash(Caisse caisse) => caisse.MontantInitialCash + PaiementCash;
    }

    /// <summary>
    /// Recomputes a session's figures from the sales this user rang up and the payments taken
    /// group-wide since it opened, up to <paramref name="asOf"/> - the session's closing
    /// instant once closed, or "now" while it is still open. Mirrors the recomputation
    /// backend/routes/ventes.js performs on every read of an open caisse, rather than trusting
    /// stale counters, since a sale or payment can land at any moment while the till is open.
    /// </summary>
    private static async Task<LiveStats> ComputeLiveStatsAsync(
        LonniiDbContext db, Caisse caisse, DateTime asOf, CancellationToken ct)
    {
        var ventes = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == caisse.GroupId && v.CreatedBy == caisse.UserId
                && v.DateVente >= caisse.DateOuverture && v.DateVente <= asOf
                && v.StatutPaiement != StatutPaiement.Annule)
            .Select(v => new { v.MontantTotal, v.AvoirAmount, v.IsAvoirSolded })
            .ToListAsync(ct);

        var totalVentes = ventes.Count;
        var chiffreAffaires = ventes.Sum(v => v.MontantTotal);
        var totalAvoir = ventes.Where(v => v.AvoirAmount > 0 && !v.IsAvoirSolded).Sum(v => v.AvoirAmount);

        var paiements = await db.PaiementsVentes.AsNoTracking()
            .Where(p => p.Vente!.GroupId == caisse.GroupId
                && p.DatePaiement >= caisse.DateOuverture && p.DatePaiement <= asOf)
            .Select(p => new { p.Montant, p.ModePaiement })
            .ToListAsync(ct);

        decimal cash = 0, mobile = 0, carte = 0, autres = 0;
        foreach (var p in paiements)
        {
            switch (p.ModePaiement)
            {
                case ModePaiement.Cash: cash += p.Montant; break;
                case ModePaiement.MobileMoney: mobile += p.Montant; break;
                case ModePaiement.Carte: carte += p.Montant; break;
                default: autres += p.Montant; break;
            }
        }

        var transactions = await db.CaisseTransactions.AsNoTracking()
            .Where(t => t.CaisseId == caisse.Id).ToListAsync(ct);
        var transactionsIn = transactions.Where(t => t.Type == "entree").Sum(t => t.Montant);
        var transactionsOut = transactions.Where(t => t.Type == "sortie").Sum(t => t.Montant);

        // Transactions are cash-drawer movements (see the LiveStats.ExpectedCash doc comment),
        // so they fold into PaiementCash rather than a separate figure.
        cash += transactionsIn - transactionsOut;

        var totalEncaisse = cash + mobile + carte + autres;

        return new LiveStats(totalVentes, chiffreAffaires, totalAvoir, cash, mobile, carte, autres, totalEncaisse);
    }

    /// <param name="retraits">Already-loaded withdrawals for this session, when the caller
    /// batch-loaded them for a whole page (see <see cref="HistoriqueAsync"/>); null to load
    /// them here.</param>
    private static async Task<CaisseDto> ToDtoAsync(
        LonniiDbContext db, Caisse caisse, CancellationToken ct, List<CaisseRetraitDto>? retraits = null)
    {
        retraits ??= (await LoadRetraitsAsync(db, [caisse.Id], ct)).GetValueOrDefault(caisse.Id);

        if (caisse.Status == CaisseStatus.Closed) return ToDto(caisse, retraits);

        var stats = await ComputeLiveStatsAsync(db, caisse, DateTime.UtcNow, ct);
        return new CaisseDto(
            caisse.Id, caisse.UserId, caisse.UserName, caisse.DateOuverture,
            caisse.MontantInitial, caisse.MontantInitialCash, caisse.MontantInitialMobile,
            caisse.DateFermeture, caisse.MontantFinal,
            stats.TotalVentes, stats.ChiffreAffaires, stats.TotalEncaisse, stats.TotalAvoir,
            stats.PaiementCash, stats.PaiementMobile, stats.PaiementCarte, stats.PaiementAutres,
            Ecart: 0, EcartResolved: false, EcartResolutionNote: null,
            caisse.Status, caisse.Notes, retraits ?? []);
    }

    private static CaisseDto ToDto(Caisse caisse, List<CaisseRetraitDto>? retraits) => new(
        caisse.Id, caisse.UserId, caisse.UserName, caisse.DateOuverture,
        caisse.MontantInitial, caisse.MontantInitialCash, caisse.MontantInitialMobile,
        caisse.DateFermeture, caisse.MontantFinal,
        caisse.TotalVentes, caisse.TotalChiffreAffaires, caisse.TotalEncaisse, caisse.TotalAvoir,
        caisse.PaiementCash, caisse.PaiementMobile, caisse.PaiementCarte, caisse.PaiementAutres,
        caisse.Ecart, caisse.EcartResolved, caisse.EcartResolutionNote,
        caisse.Status, caisse.Notes, retraits ?? []);

    /// <summary>Manual withdrawals only - not the avoir refunds that share the same
    /// <c>sortie</c> type, which already show up as their own avoir figures.</summary>
    private static async Task<Dictionary<int, List<CaisseRetraitDto>>> LoadRetraitsAsync(
        LonniiDbContext db, List<int> caisseIds, CancellationToken ct)
    {
        if (caisseIds.Count == 0) return [];

        var rows = await db.CaisseTransactions.AsNoTracking()
            .Where(t => caisseIds.Contains(t.CaisseId) && t.Type == "sortie" && t.Category == RetraitCategory)
            .Select(t => new { t.CaisseId, t.Montant, t.Description, t.CreatedAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.CaisseId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.CreatedAt)
                    .Select(r => new CaisseRetraitDto(r.Montant, r.Description ?? string.Empty, r.CreatedAt))
                    .ToList());
    }

    private const string RetraitCategory = "retrait_manuel";

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Duplicated from VentesEndpoints rather than shared: the two endpoint classes
    /// keep no internals in common, same as StockEndpoints and VentesEndpoints today.</summary>
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
}
