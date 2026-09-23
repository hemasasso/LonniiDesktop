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
        ventes.MapGet("/{id}", GetVenteAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewVentes);
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

            var lineTotal = line.DiscountType == "amount"
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
        vente.MontantTotal = total;
        vente.MontantPaye = montantPaye;
        vente.MontantRestant = total - montantPaye;
        vente.StatutPaiement = vente.MontantRestant <= 0
            ? StatutPaiement.Paye
            : montantPaye > 0 ? StatutPaiement.Partiel : StatutPaiement.EnAttente;

        db.Ventes.Add(vente);

        if (montantPaye > 0)
        {
            db.PaiementsVentes.Add(new PaiementVente
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

        return Results.Created($"/api/ventes/{vente.Id}", ToDto(vente));
    }

    private static async Task<IResult> GetVenteAsync(
        string id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var vente = await db.Ventes
            .Include(v => v.Items)
            .FirstOrDefaultAsync(v => v.Id == id && v.GroupId == scope.GroupId, ct);

        return vente is null ? Results.NotFound(new ApiError("Vente introuvable")) : Results.Ok(ToDto(vente));
    }

    /// <summary>Next human-readable reference for this group this year, e.g. <c>V2026-00043</c>.</summary>
    private static async Task<string> NextNumeroVenteAsync(LonniiDbContext db, string groupId, CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var count = await db.Ventes.CountAsync(v => v.GroupId == groupId && v.DateVente >= yearStart, ct);
        return $"V{year}-{count + 1:D5}";
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static VenteDto ToDto(Vente v) => new(
        v.Id, v.NumeroVente, v.DateVente, v.ClientNom, v.ClientTelephone, v.ClientEmail,
        v.MontantTotal, v.MontantPaye, v.MontantRestant, v.StatutPaiement, v.ModePaiement, v.Notes,
        v.Items.Select(i => new VenteItemDto(
            i.Id, i.ProductId, i.NomProduit, i.Quantite, i.PrixUnitaire, i.PrixTotal, i.Discount, i.DiscountType))
            .ToList());
}
