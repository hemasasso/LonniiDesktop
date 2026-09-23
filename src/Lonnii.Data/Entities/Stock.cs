namespace Lonnii.Data.Entities;

/// <summary>Product category. Ported from <c>categories</c> in gestion_stock_schema.sql.</summary>
public class Category
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Scopes the row to one group. The web app added group_id in add_group_id_to_gestion_tables.sql.</summary>
    public string GroupId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Hex colour used by the UI, e.g. <c>#4361ee</c>.</summary>
    public string? Color { get; set; }

    public string? Icon { get; set; }

    /// <summary>
    /// API-relative URL of the category's photo, e.g. <c>/api/images/categories/&lt;file&gt;</c>.
    /// Null when no photo has been uploaded. Not part of Lonnii Business - added for the
    /// desktop client, which stores the file on the host laptop under the data directory.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>Parent category, for sub-categories.</summary>
    public string? ParentId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Category? Parent { get; set; }
}

/// <summary>Supplier. Ported from <c>suppliers</c>.</summary>
public class Supplier
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }

    /// <summary>Free text, e.g. "Net 30".</summary>
    public string? PaymentTerms { get; set; }

    public string? Notes { get; set; }

    /// <summary>1-5, as constrained by the source schema.</summary>
    public int? Rating { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A stocked product. Ported from <c>products</c>. Monetary fields are stored as integer
/// minor units - see <c>LonniiDbContext</c> for why.
/// </summary>
public class Product
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Stock Keeping Unit. Unique within a group.</summary>
    public string? Sku { get; set; }

    public string? Barcode { get; set; }
    public string? CategoryId { get; set; }
    public string? SupplierId { get; set; }

    // Stock levels
    public int Quantity { get; set; }

    /// <summary>Low-stock alert threshold.</summary>
    public int MinimumThreshold { get; set; } = 5;

    public int? MaximumThreshold { get; set; }
    public int? ReorderQuantity { get; set; }

    // Pricing
    /// <summary>Purchase cost, used by the Marges module.</summary>
    public decimal? CostPrice { get; set; }

    public decimal Price { get; set; }
    public decimal? MarginPercentage { get; set; }

    /// <summary>Mirrors <c>prix_fixe</c>: when true the sale price cannot be edited at the till.</summary>
    public bool PrixFixe { get; set; }

    /// <summary>Sold without stock tracking (e.g. a service): quantity is never adjusted or checked.</summary>
    public bool VenteLibre { get; set; }

    /// <summary>Stock is never depleted or flagged low, regardless of <see cref="Quantity"/>.</summary>
    public bool StockIllimite { get; set; }

    /// <summary>Optional unit shown next to the quantity, e.g. "page", "service", "copie".</summary>
    public string? UniteAffichage { get; set; }

    // Physical properties
    public decimal? Weight { get; set; }
    public decimal? DimensionsLength { get; set; }
    public decimal? DimensionsWidth { get; set; }
    public decimal? DimensionsHeight { get; set; }

    // Storage
    public string? StorageLocation { get; set; }
    public int? ShelfLifeDays { get; set; }
    public DateTime? ExpiryDate { get; set; }

    // Flags
    public bool IsActive { get; set; } = true;
    public bool IsFeatured { get; set; }
    public bool IsPerishable { get; set; }
    public bool RequiresSerialNumber { get; set; }

    public string? ImageUrl { get; set; }

    /// <summary>Comma-separated tags. The source column is a PostgreSQL TEXT[].</summary>
    public string? Tags { get; set; }

    public string? Notes { get; set; }

    // Soft delete, added by run_soft_delete_migration.js
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Category? Category { get; set; }
    public Supplier? Supplier { get; set; }
}

/// <summary>Movement types recorded in <see cref="StockHistory.MovementType"/>.</summary>
public static class StockMovementTypes
{
    public const string Ajout = "ajout";
    public const string Vente = "vente";
    public const string Retour = "retour";
    public const string Adjustment = "adjustment";
    public const string Transfer = "transfer";
    public const string Damaged = "damaged";
    public const string Expired = "expired";
}

/// <summary>An audited stock movement. Ported from <c>stock_history</c>.</summary>
public class StockHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;

    /// <summary>One of <see cref="StockMovementTypes"/>.</summary>
    public string MovementType { get; set; } = string.Empty;

    public int PreviousQuantity { get; set; }

    /// <summary>Negative for outgoing movements.</summary>
    public int QuantityChanged { get; set; }

    public int NewQuantity { get; set; }

    /// <summary>The sale, purchase or adjustment this movement belongs to.</summary>
    public string? ReferenceId { get; set; }

    /// <summary>One of <c>sale</c>, <c>purchase</c>, <c>adjustment</c>, <c>transfer</c>.</summary>
    public string? ReferenceType { get; set; }

    public decimal? UnitCost { get; set; }
    public decimal? TotalCost { get; set; }

    public string? Reason { get; set; }
    public string? Notes { get; set; }
    public string? Location { get; set; }

    public string? UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Product? Product { get; set; }
}

/// <summary>Per-group stock configuration. Ported from <c>stock_settings</c>.</summary>
public class StockSetting
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = string.Empty;
    public string SettingName { get; set; } = string.Empty;
    public string SettingValue { get; set; } = string.Empty;

    /// <summary>One of <c>string</c>, <c>number</c>, <c>boolean</c>, <c>json</c>.</summary>
    public string SettingType { get; set; } = "string";

    public string? Description { get; set; }
    public string? Category { get; set; }

    /// <summary>When true, non-admin members may read this setting.</summary>
    public bool IsPublic { get; set; }

    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Who is doing what in the Stock module. Ported from <c>stock_user_activity</c>,
/// which drives the "utilisateurs en ligne" panel of the Audit screen.
/// </summary>
public class StockUserActivity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string? Action { get; set; }
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
