using Lonnii.Api.Security;
using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>Serves the product and category photos <see cref="ImageStorageService"/> stores.</summary>
public static class ImageEndpoints
{
    public static void MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/images/{folder}/{fileName}", ServeAsync)
            .RequireGroupScope()
            .WithTags("Images");
    }

    /// <summary>
    /// Streams a stored photo back to the caller, after checking it belongs to the
    /// caller's own group.
    ///
    /// The filename alone (an entity id plus a timestamp) is not a secret worth trusting:
    /// ids are visible in every product and category listing a group's own members
    /// already see. Requiring group scope and then re-checking that the entity the
    /// filename names actually belongs to this group is what stops a member of one
    /// workspace fetching another workspace's photos by reusing the same URL shape.
    /// </summary>
    private static async Task<IResult> ServeAsync(
        string folder, string fileName, GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct)
    {
        var entityId = ImageStorageService.EntityIdFromFileName(fileName);
        if (entityId is null) return Results.NotFound();

        var ownedByThisGroup = folder switch
        {
            ImageStorageService.Folders.Products =>
                await db.Products.AnyAsync(p => p.Id == entityId && p.GroupId == scope.GroupId, ct),
            ImageStorageService.Folders.Categories =>
                await db.Categories.AnyAsync(c => c.Id == entityId && c.GroupId == scope.GroupId, ct),
            _ => false,
        };

        // Same response either way: a photo that exists in another group and one that
        // never existed at all should look identical to a caller probing for either.
        if (!ownedByThisGroup) return Results.NotFound(new ApiError("Image introuvable"));

        var stream = images.OpenRead(folder, fileName);
        if (stream is null) return Results.NotFound(new ApiError("Image introuvable"));

        return Results.File(stream, "image/jpeg");
    }
}
