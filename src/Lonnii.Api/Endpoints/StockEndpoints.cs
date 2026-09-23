using Lonnii.Api.Security;
using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The Gestion de Stock module: products, categories, suppliers and stock movements.
/// Every endpoint is gated by the same privilege the web app checks.
/// </summary>
public static class StockEndpoints
{
    public static void MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var stock = app.MapGroup("/api/stock").WithTags("Stock");

        stock.MapGet("/products", ListProductsAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewStock);
        stock.MapGet("/products/{id}", GetProductAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewStock);
        stock.MapPost("/products", CreateProductAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.AddProducts);
        stock.MapPut("/products/{id}", UpdateProductAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.EditProducts);
        stock.MapDelete("/products/{id}", DeleteProductAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.DeleteProducts);
        stock.MapPost("/products/{id}/adjust", AdjustStockAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.AdjustStock);
        stock.MapGet("/products/{id}/history", ProductHistoryAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewStockHistory);

        // Gated on either privilege: attaching a photo while creating a product is part
        // of adding it, and replacing one later is part of editing it. Lonnii Business
        // does not split this any finer either - both its add and edit stock routes
        // accept the same image field.
        stock.MapPost("/products/{id}/image", UploadProductImageAsync)
            .RequireGroupScope().DisableAntiforgery();
        stock.MapDelete("/products/{id}/image", DeleteProductImageAsync)
            .RequireGroupScope().DisableAntiforgery();

        stock.MapGet("/categories", ListCategoriesAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewStock);
        stock.MapPost("/categories", CreateCategoryAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageCategories);
        stock.MapPut("/categories/{id}", UpdateCategoryAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageCategories);
        stock.MapPost("/categories/{id}/image", UploadCategoryImageAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageCategories).DisableAntiforgery();
        stock.MapDelete("/categories/{id}/image", DeleteCategoryImageAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageCategories).DisableAntiforgery();

        stock.MapGet("/suppliers", ListSuppliersAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewStock);
        stock.MapPost("/suppliers", CreateSupplierAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageSuppliers);
    }

    /// <summary>
    /// Products in the current group. <paramref name="search"/> matches name, SKU or barcode;
    /// <paramref name="lowStockOnly"/> narrows to items at or below their alert threshold.
    /// </summary>
    private static async Task<IResult> ListProductsAsync(
        GroupScope scope,
        LonniiDbContext db,
        CancellationToken ct,
        string? search = null,
        string? categoryId = null,
        bool lowStockOnly = false,
        bool includeInactive = false)
    {
        var query = db.Products
            .Where(p => p.GroupId == scope.GroupId && p.DeletedAt == null);

        if (!includeInactive) query = query.Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(categoryId)) query = query.Where(p => p.CategoryId == categoryId);
        if (lowStockOnly) query = query.Where(p => p.Quantity <= p.MinimumThreshold);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(p =>
                EF.Functions.Like(p.Name, term) ||
                (p.Sku != null && EF.Functions.Like(p.Sku, term)) ||
                (p.Barcode != null && EF.Functions.Like(p.Barcode, term)));
        }

        var products = await query
            .OrderBy(p => p.Name)
            .Select(p => ToDto(p))
            .ToListAsync(ct);

        return Results.Ok(products);
    }

    private static async Task<IResult> GetProductAsync(
        string id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var product = await db.Products
            .Where(p => p.Id == id && p.GroupId == scope.GroupId && p.DeletedAt == null)
            .Select(p => ToDto(p))
            .FirstOrDefaultAsync(ct);

        return product is null ? Results.NotFound(new ApiError("Produit introuvable")) : Results.Ok(product);
    }

    /// <summary>
    /// Creates a product. An opening quantity is written to the stock history as an
    /// <c>ajout</c> movement so the ledger explains every unit on hand.
    /// </summary>
    private static async Task<IResult> CreateProductAsync(
        SaveProductRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new ApiError("Le nom du produit est requis"));
        if (request.Price < 0)
            return Results.BadRequest(new ApiError("Le prix ne peut pas être négatif"));
        if (request.Quantity < 0)
            return Results.BadRequest(new ApiError("La quantité ne peut pas être négative"));

        if (await IsDuplicateAsync(db, scope.GroupId, request.Name, request.Sku, request.Barcode, excludeId: null, ct) is { } conflict)
            return Results.Conflict(new ApiError(conflict));

        var product = new Product
        {
            GroupId = scope.GroupId,
            Name = request.Name.Trim(),
            Description = request.Description,
            Sku = Blank(request.Sku),
            Barcode = Blank(request.Barcode),
            CategoryId = Blank(request.CategoryId),
            SupplierId = Blank(request.SupplierId),
            Quantity = request.Quantity,
            MinimumThreshold = request.MinimumThreshold,
            CostPrice = request.CostPrice,
            Price = request.Price,
            PrixFixe = request.PrixFixe,
            VenteLibre = request.VenteLibre,
            StockIllimite = request.StockIllimite,
            UniteAffichage = Blank(request.UniteAffichage),
            StorageLocation = request.StorageLocation,
            ExpiryDate = request.ExpiryDate,
            CreatedBy = scope.UserId,
            UpdatedBy = scope.UserId,
        };

        db.Products.Add(product);

        if (request.Quantity > 0)
        {
            db.StockHistories.Add(new StockHistory
            {
                GroupId = scope.GroupId,
                ProductId = product.Id,
                MovementType = StockMovementTypes.Ajout,
                PreviousQuantity = 0,
                QuantityChanged = request.Quantity,
                NewQuantity = request.Quantity,
                UnitCost = request.CostPrice,
                TotalCost = request.CostPrice * request.Quantity,
                Reason = "Stock initial",
                UserId = scope.UserId,
            });
        }

        await db.SaveChangesAsync(ct);
        await LogActivityAsync(db, scope, "product.create", product.Id, product.Name, ct);

        return Results.Created($"/api/stock/products/{product.Id}", ToDto(product));
    }

    /// <summary>
    /// Updates a product's details. Quantity is deliberately not editable here - it moves
    /// only through the adjust endpoint, so every change leaves a stock-history entry.
    /// </summary>
    private static async Task<IResult> UpdateProductAsync(
        string id, SaveProductRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var product = await db.Products
            .FirstOrDefaultAsync(p => p.Id == id && p.GroupId == scope.GroupId && p.DeletedAt == null, ct);

        if (product is null) return Results.NotFound(new ApiError("Produit introuvable"));

        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new ApiError("Le nom du produit est requis"));
        if (request.Price < 0)
            return Results.BadRequest(new ApiError("Le prix ne peut pas être négatif"));

        if (await IsDuplicateAsync(db, scope.GroupId, request.Name, request.Sku, request.Barcode, id, ct) is { } conflict)
            return Results.Conflict(new ApiError(conflict));

        product.Name = request.Name.Trim();
        product.Description = request.Description;
        product.Sku = Blank(request.Sku);
        product.Barcode = Blank(request.Barcode);
        product.CategoryId = Blank(request.CategoryId);
        product.SupplierId = Blank(request.SupplierId);
        product.MinimumThreshold = request.MinimumThreshold;
        product.CostPrice = request.CostPrice;
        product.Price = request.Price;
        product.PrixFixe = request.PrixFixe;
        product.VenteLibre = request.VenteLibre;
        product.StockIllimite = request.StockIllimite;
        product.UniteAffichage = Blank(request.UniteAffichage);
        product.StorageLocation = request.StorageLocation;
        product.ExpiryDate = request.ExpiryDate;
        product.UpdatedBy = scope.UserId;
        product.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        await LogActivityAsync(db, scope, "product.update", product.Id, product.Name, ct);

        return Results.Ok(ToDto(product));
    }

    /// <summary>
    /// Soft-deletes a product, matching run_soft_delete_migration.js. Sales history
    /// references products by id, so a hard delete would orphan past receipts.
    /// </summary>
    private static async Task<IResult> DeleteProductAsync(
        string id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var product = await db.Products
            .FirstOrDefaultAsync(p => p.Id == id && p.GroupId == scope.GroupId && p.DeletedAt == null, ct);

        if (product is null) return Results.NotFound(new ApiError("Produit introuvable"));

        product.DeletedAt = DateTime.UtcNow;
        product.DeletedBy = scope.UserId;
        product.IsActive = false;

        await db.SaveChangesAsync(ct);
        await LogActivityAsync(db, scope, "product.delete", product.Id, product.Name, ct);

        return Results.NoContent();
    }

    /// <summary>Moves stock by a signed amount and records the movement.</summary>
    private static async Task<IResult> AdjustStockAsync(
        string id, AdjustStockRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.QuantityChanged == 0)
            return Results.BadRequest(new ApiError("La quantité doit être différente de zéro"));

        var product = await db.Products
            .FirstOrDefaultAsync(p => p.Id == id && p.GroupId == scope.GroupId && p.DeletedAt == null, ct);

        if (product is null) return Results.NotFound(new ApiError("Produit introuvable"));

        if (product.VenteLibre || product.StockIllimite)
            return Results.BadRequest(new ApiError("Ce produit n'a pas de suivi de stock"));

        var newQuantity = product.Quantity + request.QuantityChanged;
        if (newQuantity < 0)
            return Results.BadRequest(new ApiError(
                $"Stock insuffisant: {product.Quantity} en stock, {-request.QuantityChanged} demandés"));

        var previous = product.Quantity;
        product.Quantity = newQuantity;
        product.UpdatedBy = scope.UserId;
        product.UpdatedAt = DateTime.UtcNow;

        db.StockHistories.Add(new StockHistory
        {
            GroupId = scope.GroupId,
            ProductId = product.Id,
            MovementType = request.MovementType,
            PreviousQuantity = previous,
            QuantityChanged = request.QuantityChanged,
            NewQuantity = newQuantity,
            UnitCost = product.CostPrice,
            TotalCost = product.CostPrice * Math.Abs(request.QuantityChanged),
            Reason = request.Reason,
            ReferenceType = "adjustment",
            UserId = scope.UserId,
        });

        await db.SaveChangesAsync(ct);
        await LogActivityAsync(db, scope, "stock.adjust", product.Id,
            $"{product.Name}: {previous} -> {newQuantity}", ct);

        return Results.Ok(ToDto(product));
    }

    /// <summary>Movement history for one product, newest first.</summary>
    private static async Task<IResult> ProductHistoryAsync(
        string id, GroupScope scope, LonniiDbContext db, CancellationToken ct, int limit = 200)
    {
        var history = await db.StockHistories
            .Where(h => h.ProductId == id && h.GroupId == scope.GroupId)
            .OrderByDescending(h => h.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(h => new StockHistoryDto(
                h.Id,
                h.ProductId,
                h.Product!.Name,
                h.MovementType,
                h.PreviousQuantity,
                h.QuantityChanged,
                h.NewQuantity,
                h.Reason,
                db.Users.Where(u => u.IdUser == h.UserId)
                    .Select(u => u.Username ?? u.Email).FirstOrDefault(),
                h.CreatedAt))
            .ToListAsync(ct);

        return Results.Ok(history);
    }

    /// <summary>
    /// Sets or replaces a product's photo. Requires can_add_products or can_edit_products -
    /// see the route registration for why both are accepted.
    /// </summary>
    private static async Task<IResult> UploadProductImageAsync(
        string id, IFormFile file, GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct)
    {
        if (!scope.Privileges.HasGestion(Priv.Gestion.AddProducts) &&
            !scope.Privileges.HasGestion(Priv.Gestion.EditProducts))
        {
            return Results.Json(new ApiError("Privilège insuffisant", Priv.Gestion.EditProducts),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var product = await db.Products
            .FirstOrDefaultAsync(p => p.Id == id && p.GroupId == scope.GroupId && p.DeletedAt == null, ct);
        if (product is null) return Results.NotFound(new ApiError("Produit introuvable"));

        var validation = ValidateUpload(file);
        if (validation is not null) return validation;

        string url;
        await using (var stream = file.OpenReadStream())
        {
            try
            {
                url = images.Save(ImageStorageService.Folders.Products, product.Id, stream, product.ImageUrl);
            }
            catch (InvalidImageException)
            {
                return Results.BadRequest(new ApiError("Le fichier n'est pas une image valide"));
            }
        }

        product.ImageUrl = url;
        product.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new ImageUploadResponse(url));
    }

    /// <summary>Removes a product's photo, leaving it with none.</summary>
    private static async Task<IResult> DeleteProductImageAsync(
        string id, GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct)
    {
        if (!scope.Privileges.HasGestion(Priv.Gestion.AddProducts) &&
            !scope.Privileges.HasGestion(Priv.Gestion.EditProducts))
        {
            return Results.Json(new ApiError("Privilège insuffisant", Priv.Gestion.EditProducts),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var product = await db.Products
            .FirstOrDefaultAsync(p => p.Id == id && p.GroupId == scope.GroupId && p.DeletedAt == null, ct);
        if (product is null) return Results.NotFound(new ApiError("Produit introuvable"));

        images.DeleteIfOwned(product.ImageUrl);
        product.ImageUrl = null;
        product.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// Every category, active or not. The management dialog needs the inactive ones too,
    /// so a deactivated category can be reactivated; the product-filter dropdown filters
    /// them back out on the client side rather than here, since it also needs the ones
    /// with zero products still shown.
    /// </summary>
    private static async Task<IResult> ListCategoriesAsync(
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var categories = await db.Categories
            .Where(c => c.GroupId == scope.GroupId)
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto(
                c.Id, c.Name, c.Description, c.Color, c.Icon, c.ImageUrl, c.IsActive,
                db.Products.Count(p => p.CategoryId == c.Id && p.DeletedAt == null)))
            .ToListAsync(ct);

        return Results.Ok(categories);
    }

    private static async Task<IResult> CreateCategoryAsync(
        SaveCategoryRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new ApiError("Le nom de la catégorie est requis"));

        var name = request.Name.Trim();
        if (await db.Categories.AnyAsync(c => c.GroupId == scope.GroupId && c.Name == name, ct))
            return Results.Conflict(new ApiError("Cette catégorie existe déjà"));

        var category = new Category
        {
            GroupId = scope.GroupId,
            Name = name,
            Description = Blank(request.Description),
            Color = Blank(request.Color),
            Icon = Blank(request.Icon),
            IsActive = request.IsActive,
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/stock/categories/{category.Id}", ToDto(category, productCount: 0));
    }

    /// <summary>Renames or restyles a category, and can reactivate one that was deactivated.</summary>
    private static async Task<IResult> UpdateCategoryAsync(
        string id, SaveCategoryRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var category = await db.Categories
            .FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
        if (category is null) return Results.NotFound(new ApiError("Catégorie introuvable"));

        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new ApiError("Le nom de la catégorie est requis"));

        var name = request.Name.Trim();
        if (await db.Categories.AnyAsync(c => c.GroupId == scope.GroupId && c.Name == name && c.Id != id, ct))
            return Results.Conflict(new ApiError("Cette catégorie existe déjà"));

        category.Name = name;
        category.Description = Blank(request.Description);
        category.Color = Blank(request.Color);
        category.Icon = Blank(request.Icon);
        category.IsActive = request.IsActive;
        category.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        var productCount = await db.Products.CountAsync(p => p.CategoryId == id && p.DeletedAt == null, ct);
        return Results.Ok(ToDto(category, productCount));
    }

    /// <summary>
    /// Sets or replaces a category's photo. There is no can_add_categories in the
    /// privilege catalogue (Lonnii Business gates all of category creation, editing and
    /// deletion on the single can_manage_categories privilege), so the image follows suit.
    /// </summary>
    private static async Task<IResult> UploadCategoryImageAsync(
        string id, IFormFile file, GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct)
    {
        var category = await db.Categories
            .FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
        if (category is null) return Results.NotFound(new ApiError("Catégorie introuvable"));

        var validation = ValidateUpload(file);
        if (validation is not null) return validation;

        string url;
        await using (var stream = file.OpenReadStream())
        {
            try
            {
                url = images.Save(ImageStorageService.Folders.Categories, category.Id, stream, category.ImageUrl);
            }
            catch (InvalidImageException)
            {
                return Results.BadRequest(new ApiError("Le fichier n'est pas une image valide"));
            }
        }

        category.ImageUrl = url;
        category.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new ImageUploadResponse(url));
    }

    /// <summary>Removes a category's photo, leaving it with none.</summary>
    private static async Task<IResult> DeleteCategoryImageAsync(
        string id, GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct)
    {
        var category = await db.Categories
            .FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
        if (category is null) return Results.NotFound(new ApiError("Catégorie introuvable"));

        images.DeleteIfOwned(category.ImageUrl);
        category.ImageUrl = null;
        category.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListSuppliersAsync(
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var suppliers = await db.Suppliers
            .Where(s => s.GroupId == scope.GroupId)
            .OrderBy(s => s.Name)
            .Select(s => new SupplierDto(s.Id, s.Name, s.ContactPerson, s.Email, s.Phone, s.City, s.Rating, s.IsActive))
            .ToListAsync(ct);

        return Results.Ok(suppliers);
    }

    private static async Task<IResult> CreateSupplierAsync(
        SupplierDto request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.BadRequest(new ApiError("Le nom du fournisseur est requis"));

        if (request.Rating is { } rating && rating is < 1 or > 5)
            return Results.BadRequest(new ApiError("La note doit être comprise entre 1 et 5"));

        var supplier = new Supplier
        {
            GroupId = scope.GroupId,
            Name = request.Name.Trim(),
            ContactPerson = request.ContactPerson,
            Email = request.Email,
            Phone = request.Phone,
            City = request.City,
            Rating = request.Rating,
        };

        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/stock/suppliers/{supplier.Id}",
            new SupplierDto(supplier.Id, supplier.Name, supplier.ContactPerson, supplier.Email,
                supplier.Phone, supplier.City, supplier.Rating, supplier.IsActive));
    }

    /// <summary>
    /// Checks SKU and barcode uniqueness within the group before the database does, so the
    /// client gets a readable message instead of a constraint violation.
    /// </summary>
    private static async Task<string?> IsDuplicateAsync(
        LonniiDbContext db, string groupId, string name, string? sku, string? barcode, string? excludeId, CancellationToken ct)
    {
        var normalizedName = name.Trim().ToLowerInvariant();
        if (await db.Products.AnyAsync(
                p => p.GroupId == groupId && p.Id != excludeId && p.DeletedAt == null &&
                     p.Name.ToLower() == normalizedName, ct))
            return $"Un produit nommé \"{name.Trim()}\" existe déjà";

        sku = Blank(sku);
        barcode = Blank(barcode);

        if (sku is not null && await db.Products.AnyAsync(
                p => p.GroupId == groupId && p.Sku == sku && p.Id != excludeId && p.DeletedAt == null, ct))
            return $"Le SKU \"{sku}\" est déjà utilisé par un autre produit";

        if (barcode is not null && await db.Products.AnyAsync(
                p => p.GroupId == groupId && p.Barcode == barcode && p.Id != excludeId && p.DeletedAt == null, ct))
            return $"Le code-barres \"{barcode}\" est déjà utilisé par un autre produit";

        return null;
    }

    /// <summary>Records an entry in the Stock activity feed the Audit screen reads.</summary>
    private static async Task LogActivityAsync(
        LonniiDbContext db, GroupScope scope, string action, string targetId, string details, CancellationToken ct)
    {
        db.StockUserActivities.Add(new StockUserActivity
        {
            GroupId = scope.GroupId,
            UserId = scope.UserId,
            Action = action,
            TargetId = targetId,
            Details = details,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Treats an empty or whitespace string as absent, so blanks do not collide on unique indexes.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Checks an uploaded file before it reaches <see cref="ImageStorageService"/>: present,
    /// not empty, not absurdly large, and declaring an image content type. The service
    /// itself still verifies the bytes actually decode as an image - a renamed .exe with
    /// an image/jpeg header would sail through this check but fail that one.
    /// </summary>
    private static IResult? ValidateUpload(IFormFile file)
    {
        if (file.Length == 0)
            return Results.BadRequest(new ApiError("Aucun fichier reçu"));

        if (file.Length > ImageStorageService.MaxUploadBytes)
        {
            var maxMb = ImageStorageService.MaxUploadBytes / (1024 * 1024);
            return Results.BadRequest(new ApiError($"L'image dépasse la taille maximale de {maxMb} Mo"));
        }

        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ApiError("Le fichier doit être une image"));

        return null;
    }

    private static CategoryDto ToDto(Category c, int productCount) => new(
        c.Id, c.Name, c.Description, c.Color, c.Icon, c.ImageUrl, c.IsActive, productCount);

    private static ProductDto ToDto(Product p) => new(
        p.Id, p.Name, p.Description, p.Sku, p.Barcode,
        p.CategoryId, p.Category != null ? p.Category.Name : null,
        p.SupplierId, p.Supplier != null ? p.Supplier.Name : null,
        p.Quantity, p.MinimumThreshold, p.CostPrice, p.Price, p.PrixFixe,
        p.VenteLibre, p.StockIllimite, p.UniteAffichage,
        p.IsActive, p.StorageLocation, p.ExpiryDate, p.ImageUrl, p.UpdatedAt);
}
