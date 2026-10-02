using System.Text.Json;
using System.Text.Json.Serialization;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Ventes;

/// <summary>
/// "Paiement Groupé": a customer with several unpaid factures settles them together, and gets
/// one combined receipt. Ports <c>POST /ventes/groupe-payment</c>, <c>GET /ventes/groupe-payments</c>
/// and the <c>type: 'groupe'</c> branch of <c>POST /ventes/solder-avoir</c> from
/// backend/routes/ventes.js.
/// </summary>
public static class GroupePaymentEndpoints
{
    private static readonly string[] Modes =
        [ModePaiement.Cash, ModePaiement.MobileMoney, ModePaiement.Carte, ModePaiement.Virement, ModePaiement.Cheque,
         ModePaiement.GooglePay, ModePaiement.ApplePay, ModePaiement.PayPal];

    public static void MapGroupePaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var ventes = app.MapGroup("/api/ventes/groupe-payments").WithTags("Ventes");

        ventes.MapPost("/", PayAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.GroupePayment);
        ventes.MapGet("/", ListAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewGroupePaymentHistory);
        ventes.MapPut("/{id}/solder-avoir", SolderAvoirAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.SoldeAvoir);
    }

    /// <summary>The shape of one entry of <c>groupe_payments.factures_data</c>, as Lonnii
    /// Business writes it - the web app reads this column too, so the names must not change.</summary>
    private sealed record FactureLine(
        [property: JsonPropertyName("facture_id")] string FactureId,
        [property: JsonPropertyName("numero_vente")] string NumeroVente,
        [property: JsonPropertyName("montant")] decimal Montant);

    /// <summary>
    /// Settles the whole remaining balance of every listed facture, all or nothing: a facture
    /// that is missing, cancelled or already paid refuses the whole payment, because the
    /// customer has been quoted one total for the lot and silently settling fewer would leave
    /// the till and the receipt disagreeing. (Lonnii Business skips such factures quietly.)
    /// </summary>
    private static async Task<IResult> PayAsync(
        GroupePaiementRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        // Hiding the button behind can_groupe_payment on the client is not enforcement, and
        // this still records payments - the same bar as a single facture's payment.
        if (!scope.Privileges.HasGestion(Priv.Gestion.AddPayment))
            return Results.Json(
                new ApiError("Privilège insuffisant", Priv.Gestion.AddPayment),
                statusCode: StatusCodes.Status403Forbidden);

        var ids = request.FactureIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0)
            return Results.BadRequest(new ApiError("Aucune facture sélectionnée"));
        if (!Modes.Contains(request.ModePaiement))
            return Results.BadRequest(new ApiError("Mode de paiement invalide"));
        if (ids.Any(id => !Guid.TryParse(id, out _)))
            return Results.BadRequest(new ApiError("Facture invalide"));

        var ventes = await db.Ventes.Include(v => v.Paiements)
            .Where(v => v.GroupId == scope.GroupId && ids.Contains(v.Id))
            .ToListAsync(ct);

        foreach (var id in ids)
        {
            var vente = ventes.FirstOrDefault(v => v.Id == id);
            if (vente is null)
                return Results.NotFound(new ApiError("Une des factures est introuvable"));

            var statut = StatutPaiement.Normalise(vente.StatutPaiement);
            if (statut is StatutPaiement.Annule or StatutPaiement.Paye || vente.MontantRestant <= 0)
                return Results.Conflict(new ApiError(
                    $"La facture {vente.NumeroVente} est déjà payée ou annulée. Retirez-la de la liste."));
        }

        var total = ventes.Sum(v => v.MontantRestant);

        // What the customer handed over has to cover what is owed; any excess is change.
        var recu = request.MontantRecu is > 0 ? request.MontantRecu.Value : total;
        if (recu < total)
            return Results.BadRequest(new ApiError(
                "Le montant reçu doit être supérieur ou égal au montant total."));

        // Only when the till could not give all the change back does any of it become an
        // avoir; otherwise the excess is change handed over and never recorded.
        var change = recu - total;
        var remis = Math.Clamp(request.MontantRemis, 0, change);
        var avoir = request.MonnaieEnAvoir ? change - remis : 0;
        var montantPaye = total + avoir;

        if (avoir > 0 && !scope.Privileges.HasGestion(Priv.Gestion.CreateAvoir))
            return Results.Json(
                new ApiError("Privilège insuffisant", Priv.Gestion.CreateAvoir),
                statusCode: StatusCodes.Status403Forbidden);

        var userName = await VentesEndpoints.DisplayNameAsync(db, scope.UserId, ct);
        var stamp = DateTime.UtcNow;
        var reference = $"GROUPE-{new DateTimeOffset(stamp).ToUnixTimeMilliseconds()}";

        var openCaisse = await VentesEndpoints.FindOpenCaisseAsync(db, scope.GroupId, scope.UserId, ct);
        var lines = new List<FactureLine>();

        foreach (var vente in ventes)
        {
            var restant = vente.MontantRestant;

            vente.Paiements.Add(new PaiementVente
            {
                VenteId = vente.Id,
                Montant = restant,
                ModePaiement = request.ModePaiement,
                Reference = reference,
                Notes = "Paiement groupé",
                CreatedBy = scope.UserId,
            });
            vente.StatutPaiement = StatutPaiement.Paye;
            vente.UpdatedBy = scope.UserId;
            vente.UpdatedAt = stamp;

            lines.Add(new FactureLine(vente.Id, vente.NumeroVente, restant));

            // A facture from before the till opened is not one the session's reconciliation
            // counts by date, so its cash is recorded explicitly - same rule as a single
            // facture's payment (VentesEndpoints.AddPaiementAsync).
            if (openCaisse is not null && vente.DateVente < openCaisse.DateOuverture)
            {
                db.CaisseTransactions.Add(new CaisseTransaction
                {
                    CaisseId = openCaisse.Id,
                    GroupId = scope.GroupId,
                    Type = "entree",
                    Montant = restant,
                    Description = $"Paiement facture antérieure {vente.NumeroVente}",
                    Category = "facture_anterieure",
                    ModePaiement = request.ModePaiement,
                    CreatedBy = scope.UserId,
                });
            }
        }

        var record = new GroupePayment
        {
            GroupId = scope.GroupId,
            ClientName = VentesEndpoints.Blank(request.ClientName) ?? "N/A",
            CaissierName = userName,
            FactureIds = ventes.Select(v => Guid.Parse(v.Id)).ToArray(),
            FacturesData = JsonSerializer.Serialize(lines),
            TotalAmount = total,
            MontantPaye = montantPaye,
            AvoirAmount = avoir,
            PartialChangeGiven = remis,
            ModePaiement = request.ModePaiement,
            CreatedAt = stamp,
        };
        db.GroupePayments.Add(record);

        db.VentesUserActivities.Add(new VentesUserActivity
        {
            GroupId = scope.GroupId,
            UserId = scope.UserId,
            Action = "vente.paiement_groupe",
            TargetId = record.Id,
            Details = $"{ventes.Count} facture(s): {total:0.##}",
        });

        // One SaveChanges: the payments, the statuses and the receipt record land together or not at all.
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(record, ventes.ToDictionary(v => v.Id, v => v.MontantTotal), null));
    }

    /// <summary>History of group receipts, newest first, optionally within a local date range
    /// (see <see cref="VentesEndpoints.LocalRangeToUtc"/>).</summary>
    private static async Task<IResult> ListAsync(
        int? page, int? limit, DateOnly? dateDebut, DateOnly? dateFin, int? tzOffsetMinutes,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var size = Math.Clamp(limit ?? 10, 1, 100);
        var skip = (Math.Max(page ?? 1, 1) - 1) * size;
        var (start, end) = VentesEndpoints.LocalRangeToUtc(dateDebut, dateFin, tzOffsetMinutes);

        var query = db.GroupePayments.AsNoTracking().Where(g => g.GroupId == scope.GroupId);
        if (start is { } from) query = query.Where(g => g.CreatedAt >= from);
        if (end is { } to) query = query.Where(g => g.CreatedAt < to);

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(g => g.CreatedAt).Skip(skip).Take(size).ToListAsync(ct);

        // The sale's full total is not in factures_data (the web app looks it up too).
        var factureIds = rows.SelectMany(r => r.FactureIds).Select(g => g.ToString()).Distinct().ToList();
        var totals = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == scope.GroupId && factureIds.Contains(v.Id))
            .Select(v => new { v.Id, v.MontantTotal })
            .ToDictionaryAsync(v => v.Id, v => v.MontantTotal, ct);

        var soldeurIds = rows.Select(r => r.AvoirSoldedBy).Where(id => id is not null).Distinct().ToList();
        var soldeurNames = new Dictionary<string, string?>();
        foreach (var id in soldeurIds)
            soldeurNames[id!] = await VentesEndpoints.DisplayNameAsync(db, id, ct);

        return Results.Ok(new GroupePaiementsResponse(
            rows.Select(r => ToDto(r, totals,
                r.AvoirSoldedBy is not null && soldeurNames.TryGetValue(r.AvoirSoldedBy, out var n) ? n : null)).ToList(),
            total));
    }

    /// <summary>Pays the customer back the avoir a group payment created. Mirrors
    /// <c>POST /solder-avoir</c> with <c>type: 'groupe'</c>, including the cash going out of the till.</summary>
    private static async Task<IResult> SolderAvoirAsync(
        string id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var payment = await db.GroupePayments.FirstOrDefaultAsync(
            g => g.Id == id && g.GroupId == scope.GroupId, ct);
        if (payment is null) return Results.NotFound(new ApiError("Paiement groupé introuvable"));

        if (payment.AvoirAmount <= 0 || payment.IsAvoirSolded)
            return Results.BadRequest(new ApiError("Ce paiement n'a pas d'avoir à solder"));

        payment.IsAvoirSolded = true;
        payment.AvoirSoldedAt = DateTime.UtcNow;
        payment.AvoirSoldedBy = scope.UserId;

        // The refund is only the caller's own drawer's business - unlike a payment, the web
        // app does not fall back to another user's session here.
        var caisse = await db.Caisses.FirstOrDefaultAsync(
            c => c.GroupId == scope.GroupId && c.UserId == scope.UserId && c.Status == CaisseStatus.Open, ct);
        if (caisse is not null)
        {
            db.CaisseTransactions.Add(new CaisseTransaction
            {
                CaisseId = caisse.Id,
                GroupId = scope.GroupId,
                Type = "sortie",
                Montant = payment.AvoirAmount,
                Description = $"Remboursement avoir - Groupe - Client: {payment.ClientName ?? "N/A"}",
                Category = "avoir_solde",
                CreatedBy = scope.UserId,
            });
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            success = true,
            avoirSoldedByName = await VentesEndpoints.DisplayNameAsync(db, scope.UserId, ct),
        });
    }

    private static GroupePaiementDto ToDto(
        GroupePayment p, IReadOnlyDictionary<string, decimal> ventesTotals, string? soldeurName)
    {
        var lines = new List<FactureLine>();
        try
        {
            lines = JsonSerializer.Deserialize<List<FactureLine>>(p.FacturesData) ?? [];
        }
        catch (JsonException)
        {
            // A row the web app wrote that we cannot read still lists, with no factures.
        }

        return new GroupePaiementDto(
            p.Id, p.ClientName, p.CaissierName, p.CreatedAt, p.ModePaiement,
            lines.Select(l => new GroupePaiementFactureDto(
                l.FactureId, l.NumeroVente, l.Montant,
                ventesTotals.TryGetValue(l.FactureId, out var original) ? original : l.Montant)).ToList(),
            p.TotalAmount,
            p.MontantPaye > 0 ? p.MontantPaye : p.TotalAmount,
            p.AvoirAmount, p.PartialChangeGiven, p.IsAvoirSolded, p.AvoirSoldedAt, soldeurName);
    }
}
