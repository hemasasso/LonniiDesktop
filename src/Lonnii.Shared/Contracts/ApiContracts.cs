namespace Lonnii.Shared.Contracts;

// --- Authentication ---

/// <summary>Sign-in request. <paramref name="Identifier"/> accepts an email or a username.</summary>
public sealed record LoginRequest(string Identifier, string Password, string? DeviceName = null);

/// <summary>Issued on a successful sign-in.</summary>
public sealed record LoginResponse(string AccessToken, DateTime ExpiresAt, UserDto User);

/// <summary>Account creation request.</summary>
public sealed record RegisterRequest(
    string Email,
    string Password,
    string? Username = null,
    string? FirstName = null,
    string? LastName = null,
    string? Phone = null);

/// <summary>
/// Changes your own password. The current one is required, so someone who walks up to an
/// unlocked till cannot lock the owner out of their own account.
/// </summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Resets another member's password. Only an administrator may do this, and the current
/// password is not required - the point is that nobody knows it any more.
/// </summary>
public sealed record ResetMemberPasswordRequest(string NewPassword);

/// <summary>
/// Whether this host has been set up yet. Lets the sign-in window offer to create the
/// first administrator account on a brand-new installation, instead of showing a
/// sign-in form that nobody can possibly satisfy.
/// </summary>
public sealed record SetupStateResponse(bool HasAnyAccount);

/// <summary>A user, as returned to the client. Never carries a password hash.</summary>
public sealed record UserDto(
    string IdUser,
    string Email,
    string? Username,
    string? FirstName,
    string? LastName,
    string? Phone,
    string? Poste,
    bool IsVerified,
    DateTime? LastLogin);

// --- Groups and sessions ---

/// <summary>A group the signed-in user belongs to.</summary>
public sealed record GroupeDto(
    string Id,
    string Nom,
    bool IsAdminGeneral,
    string Role,
    bool GestionAccess,
    bool PrestationsEnabled,
    string? PrestationsLocation,
    int MemberCount,
    DateTime CreatedAt,
    string CurrencyLabel);

/// <summary>Request to create a group. The caller becomes its Admin Général.</summary>
public sealed record CreateGroupeRequest(string Nom, bool GestionAccess = true);

/// <summary>Changes the currency label shown after every amount in this workspace.</summary>
public sealed record UpdateCurrencyRequest(string CurrencyLabel);

/// <summary>
/// A scoped session for one group. The client sends <paramref name="SessionToken"/>
/// in the <c>x-group-session</c> header, matching the web app's convention.
/// </summary>
public sealed record GroupSessionResponse(string SessionToken, DateTime ExpiresAt, GroupeDto Groupe);

/// <summary>
/// Adds someone to the current group.
///
/// When an account already matches <paramref name="Identifier"/> it is simply added and
/// the other fields are ignored. When none does, supplying <paramref name="Password"/>
/// creates the account and adds it in one step - which is how a shop onboards a till
/// operator who has never used Lonnii before.
/// </summary>
public sealed record AddMemberRequest(
    string Identifier,
    string? Password = null,
    string? Username = null,
    string? FirstName = null,
    string? LastName = null,
    string? Phone = null);

/// <summary>A member of a group, with their resolved role.</summary>
public sealed record GroupMemberDto(
    string IdUser,
    string Email,
    string? Username,
    string? FirstName,
    string? LastName,
    string Role,
    bool IsAdminGeneral,
    DateTime JoinedAt,
    DateTime? LastSeen);

// --- Privileges ---

/// <summary>
/// The signed-in user's effective privileges in the current group. Mirrors the shape
/// returned by the web app's <c>GET /gestion/:sessionToken/my-privileges</c>.
/// </summary>
public sealed record MyPrivilegesResponse(
    string Role,
    bool IsAdmin,
    bool IsAdminGeneral,
    IReadOnlyDictionary<string, bool> Option,
    IReadOnlyDictionary<string, bool> Gestion);

/// <summary>A privilege definition, for the privilege-management screens.</summary>
public sealed record PrivilegeDto(
    int Id,
    string Name,
    string DisplayName,
    string? Description,
    string Module,
    bool IsAdminOnly,
    bool IsGranted);

/// <summary>
/// One member's privileges across both catalogues, with <c>IsGranted</c> resolved.
/// Backs the privilege-management dialog.
/// </summary>
public sealed record MemberPrivilegesResponse(
    string Role,
    bool IsAdminGeneral,
    IReadOnlyList<PrivilegeDto> Gestion,
    IReadOnlyList<PrivilegeDto> Option);

/// <summary>Grants or revokes one privilege for one member.</summary>
public sealed record SetPrivilegeRequest(string UserId, string PrivilegeName, bool Granted, string? Reason = null);

/// <summary>Changes a member's group role.</summary>
public sealed record SetRoleRequest(string UserId, string Role, string? Reason = null);

// --- Menu ---

/// <summary>A menu entry the signed-in user is allowed to see.</summary>
public sealed record MenuEntryDto(string Key, string Label, string Description, string Accent);

/// <summary>A titled group of menu entries.</summary>
public sealed record MenuSectionDto(string Id, string Label, string Description, string Accent, IReadOnlyList<MenuEntryDto> Entries);

/// <summary>The menu as resolved for the signed-in user in the current group.</summary>
public sealed record MenuResponse(
    IReadOnlyList<MenuEntryDto> Espace,
    IReadOnlyList<MenuEntryDto> Gestion,
    IReadOnlyList<MenuSectionDto> GestionSections);

// --- Stock ---

/// <summary>A product row, as shown in the Gestion de Stock grid.</summary>
public sealed record ProductDto(
    string Id,
    string Name,
    string? Description,
    string? Sku,
    string? Barcode,
    string? CategoryId,
    string? CategoryName,
    string? SupplierId,
    string? SupplierName,
    int Quantity,
    int MinimumThreshold,
    decimal? CostPrice,
    decimal Price,
    bool PrixFixe,
    bool VenteLibre,
    bool StockIllimite,
    string? UniteAffichage,
    bool IsActive,
    string? StorageLocation,
    DateTime? ExpiryDate,
    string? ImageUrl,
    DateTime UpdatedAt)
{
    /// <summary>True when stock has fallen to or below the alert threshold. A product sold
    /// without stock tracking or with unlimited stock is never low.</summary>
    public bool IsLowStock => !VenteLibre && !StockIllimite && Quantity <= MinimumThreshold;

    /// <summary>"∞" for a product with no stock tracking, otherwise the quantity with its
    /// display unit (e.g. "12 page") when one is set.</summary>
    public string QuantityDisplay =>
        VenteLibre || StockIllimite
            ? "∞"
            : string.IsNullOrEmpty(UniteAffichage) ? Quantity.ToString() : $"{Quantity} {UniteAffichage}";
}

/// <summary>Creates or updates a product.</summary>
public sealed record SaveProductRequest(
    string Name,
    decimal Price,
    string? Description = null,
    string? Sku = null,
    string? Barcode = null,
    string? CategoryId = null,
    string? SupplierId = null,
    int Quantity = 0,
    int MinimumThreshold = 5,
    decimal? CostPrice = null,
    bool PrixFixe = false,
    bool VenteLibre = false,
    bool StockIllimite = false,
    string? UniteAffichage = null,
    string? StorageLocation = null,
    DateTime? ExpiryDate = null);

/// <summary>Adjusts stock by a signed amount, recording why.</summary>
public sealed record AdjustStockRequest(int QuantityChanged, string MovementType, string? Reason = null);

/// <summary>One stock movement, as shown in the product history.</summary>
public sealed record StockHistoryDto(
    string Id,
    string ProductId,
    string ProductName,
    string MovementType,
    int PreviousQuantity,
    int QuantityChanged,
    int NewQuantity,
    string? Reason,
    string? UserName,
    DateTime CreatedAt);

/// <summary>A product category.</summary>
public sealed record CategoryDto(
    string Id,
    string Name,
    string? Description,
    string? Color,
    string? Icon,
    string? ImageUrl,
    bool IsActive,
    int ProductCount);

/// <summary>Creates or updates a category.</summary>
public sealed record SaveCategoryRequest(
    string Name,
    string? Description = null,
    string? Color = null,
    string? Icon = null,
    bool IsActive = true);

/// <summary>A supplier.</summary>
public sealed record SupplierDto(string Id, string Name, string? ContactPerson, string? Email, string? Phone, string? City, int? Rating, bool IsActive);

// --- Images ---

/// <summary>Returned after a photo upload, so the caller can show it immediately.</summary>
public sealed record ImageUploadResponse(string ImageUrl);

// --- Errors ---

/// <summary>A failure response. <paramref name="Required"/> names the missing privilege on a 403.</summary>
public sealed record ApiError(string Error, string? Required = null);
