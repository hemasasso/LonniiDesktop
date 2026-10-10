using Lonnii.Api.Features.Auth;
using Lonnii.Api.Features.Members;
using Lonnii.Api.Features.Stock;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Api.Features.Remote;

/// <summary>
/// Product changes asked for from afar, run through the very handlers the Stock screens use - the
/// same validation, duplicate checks, stock history and activity log - so a change requested from a
/// phone obeys exactly the rules of one made at the shop. Used twice: on the server, inside a
/// transaction that is rolled back, to refuse a bad request at once; on the shop's computer, for real.
/// </summary>
internal static class ProductCommands
{
    /// <summary>The privilege the matching Stock screen asks for.</summary>
    public static string RequiredPrivilege(string type) => type switch
    {
        RemoteCommandTypes.ProductCreate => Priv.Gestion.AddProducts,
        RemoteCommandTypes.ProductUpdate => Priv.Gestion.EditProducts,
        RemoteCommandTypes.ProductAdjust => Priv.Gestion.AdjustStock,
        _ => Priv.Gestion.DeleteProducts,
    };

    /// <summary>What is missing from the request for its type, or null when it is complete.</summary>
    public static string? Incomplete(string type, string? productId, SaveProductRequest? product, AdjustStockRequest? adjustment) =>
        type switch
        {
            RemoteCommandTypes.ProductCreate when product is null => "Les informations du produit sont requises.",
            RemoteCommandTypes.ProductUpdate when product is null || string.IsNullOrWhiteSpace(productId) =>
                "Le produit et ses nouvelles informations sont requis.",
            RemoteCommandTypes.ProductAdjust when adjustment is null || string.IsNullOrWhiteSpace(productId) =>
                "Le produit et la quantité à ajuster sont requis.",
            RemoteCommandTypes.ProductDeactivate when string.IsNullOrWhiteSpace(productId) => "Le produit est requis.",
            _ => null,
        };

    /// <returns>The outcome, and the product's id (the new one, for a product just added) so a photo
    /// sent with the request can be attached to it.</returns>
    public static async Task<(PrivilegeChangeResult Result, string? ProductId)> RunAsync(
        string type, string? productId, SaveProductRequest? product, AdjustStockRequest? adjustment,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (!scope.Privileges.All().TryGetValue(RequiredPrivilege(type), out var allowed) || !allowed)
            return (PrivilegeChangeResult.Refused(StatusCodes.Status403Forbidden, "Privilège insuffisant pour cette modification."), null);

        if (Incomplete(type, productId, product, adjustment) is { } missing)
            return (PrivilegeChangeResult.Refused(StatusCodes.Status400BadRequest, missing), null);

        var result = type switch
        {
            RemoteCommandTypes.ProductCreate => await StockEndpoints.CreateProductAsync(product!, scope, db, ct),
            RemoteCommandTypes.ProductUpdate => await StockEndpoints.UpdateProductAsync(productId!, product!, scope, db, ct),
            RemoteCommandTypes.ProductAdjust => await StockEndpoints.AdjustStockAsync(productId!, adjustment!, scope, db, ct),
            _ => await StockEndpoints.DeleteProductAsync(productId!, scope, db, ct),
        };

        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        var value = (result as IValueHttpResult)?.Value;

        return status is >= 200 and < 300
            ? (PrivilegeChangeResult.Done, (value as ProductDto)?.Id ?? productId)
            : (PrivilegeChangeResult.Refused(status, (value as ApiError)?.Error ?? "Modification refusée."), null);
    }

    /// <summary>What is wrong with the photo part of a request, or null. A photo only goes with a
    /// product being added or changed, and removing one only makes sense for a product that exists.</summary>
    public static string? PhotoError(string type, string? photoId, bool? removePhoto) =>
        (photoId, removePhoto) switch
        {
            (not null, true) => "Une photo ne peut pas être à la fois envoyée et supprimée.",
            (not null, _) when type is not (RemoteCommandTypes.ProductCreate or RemoteCommandTypes.ProductUpdate) =>
                "Une photo accompagne seulement l'ajout ou la modification d'un produit.",
            (_, true) when type != RemoteCommandTypes.ProductUpdate =>
                "Seule la modification d'un produit peut retirer sa photo.",
            _ => null,
        };
}
