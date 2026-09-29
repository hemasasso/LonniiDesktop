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
    string CurrencyLabel,
    bool CurrencyBefore = false);

/// <summary>Request to create a group. The caller becomes its Admin Général.</summary>
public sealed record CreateGroupeRequest(string Nom, bool GestionAccess = true);

/// <summary>Changes the currency label shown with every amount in this workspace, and
/// whether it goes before the amount (<c>$ 1 000</c>) or after it (<c>1 000 FCFA</c>).</summary>
public sealed record UpdateCurrencyRequest(string CurrencyLabel, bool CurrencyBefore = false);

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

/// <summary>
/// Values of <see cref="ProductDto.TypeProduit"/>. Only a <see cref="ProduitFini"/> appears in
/// the Ventes catalogue; the other two are stocked, adjusted and valued like any product but
/// never offered for sale - flour at a bakery, packaging, cleaning supplies.
/// </summary>
public static class ProductTypes
{
    public const string ProduitFini = "produit_fini";
    public const string MatierePremiere = "matiere_premiere";
    public const string Autre = "autre";

    public static readonly IReadOnlyList<string> All = [ProduitFini, MatierePremiere, Autre];

    public static string DisplayName(string? type) => type switch
    {
        MatierePremiere => "Matière première",
        Autre => "Autre (usage interne)",
        _ => "Produit fini",
    };

    /// <summary>A missing or unknown value counts as sellable, which is what every product
    /// created before this column existed - and every product Lonnii Business creates - is.</summary>
    public static bool IsSellable(string? type) => type is null or ProduitFini || !All.Contains(type);
}

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
    DateTime UpdatedAt,
    string TypeProduit = ProductTypes.ProduitFini)
{
    public string TypeProduitDisplay => ProductTypes.DisplayName(TypeProduit);

    /// <summary>True when stock has fallen to or below the alert threshold. A product sold
    /// without stock tracking or with unlimited stock is never low.</summary>
    public bool IsLowStock => !VenteLibre && !StockIllimite && Quantity <= MinimumThreshold;

    /// <summary>"∞" for a product with no quantity concept at all, otherwise the quantity with
    /// its display unit (e.g. "12 page") when one is set. A Vente Libre product without Stock
    /// indéfini still carries a real reference quantity - only Stock indéfini itself (which
    /// can only be set alongside Vente Libre) means there is no number to show.</summary>
    public string QuantityDisplay =>
        StockIllimite
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
    DateTime? ExpiryDate = null,
    string TypeProduit = ProductTypes.ProduitFini);

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

/// <summary>
/// One movement type's totals for the Stock module's Analyse tab - "how many transfers, how
/// much did they cost" rather than the per-product list <see cref="StockHistoryDto"/> gives.
/// <paramref name="MovementType"/> is one of the constants in
/// <c>Lonnii.Data.Entities.StockMovementTypes</c> (ajout, vente, retour, adjustment, transfer,
/// damaged, expired); the client owns the French label the same way it already does for
/// <see cref="AdjustStockRequest.MovementType"/> in StockAdjustDialog.
/// </summary>
public sealed record StockMovementStatDto(string MovementType, int Count, int TotalQuantity, decimal TotalCost);

/// <summary>Response of <c>GET /api/stock/movements/stats</c>.</summary>
public sealed record StockMovementStatsResponse(IReadOnlyList<StockMovementStatDto> Movements);

/// <summary>
/// One stock movement, newest first, for the Analyse tab's movement log - unlike
/// <see cref="StockMovementStatDto"/>'s per-type totals, this says which product (and its
/// category) each movement actually happened to.
/// </summary>
public sealed record StockMovementDetailDto(
    string Id, string ProductId, string ProductName, string? CategoryName, string? SupplierName,
    string MovementType, int QuantityChanged, decimal? TotalCost, string? Reason, DateTime CreatedAt);

/// <summary>Response of <c>GET /api/stock/movements/detail</c>.</summary>
public sealed record StockMovementDetailsResponse(IReadOnlyList<StockMovementDetailDto> Movements);

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
public sealed record SupplierDto(
    string Id,
    string Name,
    string? ContactPerson,
    string? Email,
    string? Phone,
    string? Address,
    string? City,
    string? Country,
    string? PaymentTerms,
    string? Notes,
    int? Rating,
    bool IsActive);

/// <summary>Creates or updates a supplier.</summary>
public sealed record SaveSupplierRequest(
    string Name,
    string? ContactPerson = null,
    string? Email = null,
    string? Phone = null,
    string? Address = null,
    string? City = null,
    string? Country = null,
    string? PaymentTerms = null,
    string? Notes = null,
    int? Rating = null,
    bool IsActive = true);

// --- Ventes ---

/// <summary>Values for a discount's <c>DiscountType</c>, on a cart line or the global
/// remise: a percentage of the amount, or a flat sum taken off it.</summary>
public static class DiscountTypes
{
    public const string Percentage = "percentage";
    public const string Amount = "amount";
}

/// <summary>One line of a sale being created. <paramref name="UnitPrice"/> is required
/// when the product is not <c>PrixFixe</c> — its price is decided at sale time.</summary>
public sealed record CartItemRequest(
    string ProductId,
    int Quantity,
    decimal? UnitPrice = null,
    decimal Discount = 0,
    string DiscountType = DiscountTypes.Amount);

/// <summary>Creates a sale from a cart. <paramref name="MontantPaye"/> may be less than the
/// computed total (partial payment) or zero (unpaid). <paramref name="RemiseGlobale"/> is a
/// flat amount taken off the sum of the lines, e.g. a negotiated rebate on the whole sale.</summary>
public sealed record CreateVenteRequest(
    IReadOnlyList<CartItemRequest> Items,
    string ModePaiement,
    decimal MontantPaye,
    decimal RemiseGlobale = 0,
    string? ClientNom = null,
    string? ClientTelephone = null,
    string? ClientEmail = null,
    string? Notes = null,
    string? IdempotencyKey = null);

/// <summary>One line of a completed sale.</summary>
public sealed record VenteItemDto(
    string Id,
    string? ProductId,
    string NomProduit,
    int Quantite,
    decimal PrixUnitaire,
    decimal PrixTotal,
    decimal Discount,
    string DiscountType);

/// <summary>One payment recorded against a sale - the detail view's payment history
/// (Lonnii Business's <c>payment_tranches</c>), distinct from the receipt's single summed
/// "Total Payé" line.</summary>
public sealed record PaiementDto(
    string Id,
    decimal Montant,
    string ModePaiement,
    DateTime DatePaiement,
    string? CreatedByName);

/// <summary>A completed sale, as shown on a receipt or in the sales detail view.</summary>
public sealed record VenteDto(
    string Id,
    string NumeroVente,
    DateTime DateVente,
    string? ClientNom,
    string? ClientTelephone,
    string? ClientEmail,
    decimal MontantTotal,
    decimal MontantPaye,
    decimal MontantRestant,
    string StatutPaiement,
    string? ModePaiement,
    string? Notes,
    string? VendeurNom,
    IReadOnlyList<VenteItemDto> Items,
    decimal AvoirAmount = 0,
    bool IsAvoirSolded = false,
    string? AvoirSoldedByName = null,
    DateTime? AvoirSoldedAt = null,
    string? CancellationReason = null,
    string? CancelledByName = null,
    DateTime? CancelledAt = null,
    IReadOnlyList<PaiementDto>? Paiements = null);

/// <summary>One row of the "Liste des Ventes" table - lighter than <see cref="VenteDto"/>,
/// since a list of up to a thousand sales does not need every line item loaded.</summary>
public sealed record VenteListItemDto(
    string Id,
    string NumeroVente,
    DateTime DateVente,
    string? ClientNom,
    string? VendeurNom,
    decimal MontantTotal,
    decimal MontantPaye,
    decimal MontantRestant,
    decimal AvoirAmount,
    bool IsAvoirSolded,
    string StatutPaiement,
    string? CancellationReason,
    string? ModePaiement);

/// <summary>Response of <c>GET /api/ventes</c>.</summary>
public sealed record VentesListResponse(IReadOnlyList<VenteListItemDto> Ventes);

/// <summary>Adds a payment against an existing sale, e.g. settling a facture at the till.</summary>
public sealed record AddPaiementRequest(
    decimal Montant, string ModePaiement, string? Reference = null, string? Notes = null);

/// <summary>Cancels a sale; <paramref name="Motif"/> is mandatory, same as Lonnii Business.</summary>
public sealed record CancelVenteRequest(string Motif);

/// <summary>Edits a sale's client name and/or date - fields sometimes forgotten at creation
/// time, which otherwise skews analytics. At least one of the two must be supplied.</summary>
public sealed record EditVenteRequest(string? ClientNom, DateTime? DateVente);

// --- Ventes: Statistiques ---

/// <summary>One point of the revenue-over-time chart - one calendar day, or one calendar
/// month when the filtered range spans more than <c>StatsEndpoints.DailyBucketMaxDays</c>.</summary>
public sealed record VenteStatsPointDto(DateOnly Date, int NombreVentes, decimal MontantTotal);

/// <summary>One slice of the category breakdown pie chart.</summary>
public sealed record CategorySalesDto(string? CategoryId, string Categorie, decimal MontantTotal, int Quantite);

/// <summary>One bar of the top-products chart.</summary>
public sealed record TopProductDto(string? ProductId, string Nom, int Quantite, decimal MontantTotal);

/// <summary>
/// Response of <c>GET /api/ventes/stats</c> - the "Statistiques" tab's KPI tiles, its
/// payment/category/top-product/time-series charts, and the period-over-period comparison.
///
/// <para>
/// When <c>categoryId</c> or <c>productId</c> narrows the request, every amount here is
/// computed from the matching sale <em>lines</em> rather than whole sales (a sale mixing
/// several categories should not credit all of its total to one), and montant-encaissé /
/// montant-restant / payment-method amounts are that line share prorated by how much of the
/// whole sale has actually been paid.
/// </para>
/// </summary>
public sealed record VentesStatsResponse(
    int TotalVentes,
    decimal ChiffreAffaires,
    decimal MontantEncaisse,
    decimal MontantRestant,
    decimal TotalAvoir,
    decimal VenteMoyenne,
    decimal VenteMax,
    int VentesPayees,
    int VentesPartielles,
    int VentesEnAttente,
    int VentesAnnulees,
    int ClientsUniques,
    decimal PaiementCash,
    decimal PaiementMobile,
    decimal PaiementCarte,
    decimal PaiementAutres,
    decimal MoyenneQuotidienne,
    decimal Croissance,
    IReadOnlyList<CategorySalesDto> CategorySales,
    IReadOnlyList<TopProductDto> TopProducts,
    IReadOnlyList<VenteStatsPointDto> Serie);

// --- Caisse ---

/// <summary>
/// A cash register session, open or closed. Totals are live-computed for an open session
/// (see <c>CaisseEndpoints.ComputeLiveStatsAsync</c>) and stored as-of closing time for a
/// closed one, same as Lonnii Business's own <c>GET /caisse/status</c> and
/// <c>GET /caisse/historique</c>.
/// </summary>
public sealed record CaisseDto(
    int Id,
    string UserId,
    string UserName,
    DateTime DateOuverture,
    decimal MontantInitial,
    decimal MontantInitialCash,
    decimal MontantInitialMobile,
    DateTime? DateFermeture,
    decimal? MontantFinal,
    int TotalVentes,
    decimal TotalChiffreAffaires,
    decimal TotalEncaisse,
    decimal TotalAvoir,
    decimal PaiementCash,
    decimal PaiementMobile,
    decimal PaiementCarte,
    decimal PaiementAutres,
    decimal Ecart,
    bool EcartResolved,
    string? EcartResolutionNote,
    string Status,
    string? Notes,
    IReadOnlyList<CaisseRetraitDto>? Retraits = null,
    decimal? MontantFinalMobile = null)
{
    public decimal TotalRetraits => Retraits?.Sum(r => r.Montant) ?? 0;

    /// <summary>The cash share of <see cref="TotalRetraits"/> - everything not explicitly
    /// tagged mobile money, so a withdrawal recorded before the split existed still counts here.</summary>
    public decimal TotalRetraitsCash => Retraits?.Where(r => r.ModePaiement != "mobile_money").Sum(r => r.Montant) ?? 0;

    public decimal TotalRetraitsMobile => Retraits?.Where(r => r.ModePaiement == "mobile_money").Sum(r => r.Montant) ?? 0;

    /// <summary>Cash the drawer should hold: cash float + cash taken − cash withdrawals.</summary>
    public decimal ExpectedCash => MontantInitialCash + PaiementCash - TotalRetraitsCash;

    /// <summary>Mobile money the till's account should hold: mobile float + mobile payments
    /// − mobile withdrawals.</summary>
    public decimal ExpectedMobile => MontantInitialMobile + PaiementMobile - TotalRetraitsMobile;

    /// <summary>False for a session closed with a cash count alone - every session closed
    /// before the mobile count existed, or by Lonnii Business. Its écart is cash only.</summary>
    public bool MobileCounted => MontantFinalMobile is not null;

    /// <summary>The mobile share of <see cref="Ecart"/>; the rest is cash.</summary>
    public decimal EcartMobile => MontantFinalMobile is { } mobile ? mobile - ExpectedMobile : 0;

    public decimal EcartCash => Ecart - EcartMobile;

    /// <summary>What the till should hold in all, measured the same way <see cref="Ecart"/>
    /// was: a session closed on cash alone is expected to hold its cash alone.</summary>
    public decimal ExpectedTotal => ExpectedCash + (Status == "closed" && !MobileCounted ? 0 : ExpectedMobile);

    /// <summary>What was counted in all at closing; null while open.</summary>
    public decimal? CountedTotal => MontantFinal is { } cash ? cash + (MontantFinalMobile ?? 0) : null;
}

/// <summary>One manual withdrawal ("Retirer de la caisse") from a session. <paramref name="ModePaiement"/>
/// is <c>cash</c> or <c>mobile_money</c> - which pool it came out of - defaulting to <c>cash</c>
/// for a row written before this distinction existed, since every withdrawal was cash then.</summary>
public sealed record CaisseRetraitDto(decimal Montant, string Motif, DateTime Date, string ModePaiement = "cash");

/// <summary>Response of <c>GET /api/caisse/status</c> - null when the caller has no open
/// session right now.</summary>
public sealed record CaisseStatusResponse(CaisseDto? Caisse);

/// <summary>Opens a new session; the float is split by how it will be counted back at
/// closing - <paramref name="MontantInitialCash"/> is what a shop actually reconciles
/// (<see cref="CaisseDto.Ecart"/>), while <paramref name="MontantInitialMobile"/> just
/// records the mobile-money balance the till starts with, e.g. to give change on a mobile
/// payment.</summary>
public sealed record OpenCaisseRequest(
    decimal MontantInitialCash, decimal MontantInitialMobile = 0, string? Notes = null);

/// <summary>Closes the caller's open session. <paramref name="MontantFinal"/> is the cash
/// counted in the drawer; <paramref name="MontantFinalMobile"/> the mobile-money balance
/// found on the till's account. Both are compared against what the session expects and the
/// écart is the sum of the two differences. Mobile is optional so an older client that only
/// sends cash still closes, with a cash-only écart as before.</summary>
public sealed record CloseCaisseRequest(decimal MontantFinal, string? Notes = null, decimal? MontantFinalMobile = null);

/// <summary>Response of <c>GET /api/caisse/historique</c>.</summary>
public sealed record CaisseHistoryResponse(IReadOnlyList<CaisseDto> Caisses, int Total);

/// <summary>One entry of the vendeur filter dropdown on the caisse history screen.</summary>
public sealed record CaisseVendeurDto(string UserId, string UserName);

/// <summary>Values <see cref="ResolveEcartRequest.ResolutionType"/> accepts.</summary>
public static class EcartResolutionTypes
{
    /// <summary>The écart is explained (e.g. a rounding difference) and left as recorded.</summary>
    public const string Justified = "justified";

    /// <summary>The écart is accepted as an unrecovered loss (or gain) and left as recorded.</summary>
    public const string WrittenOff = "written_off";

    /// <summary>The counted amount was mistaken; corrects <c>MontantFinal</c> to the expected
    /// cash figure so the écart becomes zero.</summary>
    public const string Adjusted = "adjusted";
}

/// <summary>Resolves a non-zero écart on a closed session. <paramref name="ResolutionType"/>
/// is one of <see cref="EcartResolutionTypes"/>.</summary>
public sealed record ResolveEcartRequest(string ResolutionType, string? Notes = null);

/// <summary>Takes cash out of the caller's open session's drawer for something other than a
/// sale refund - buying supplies, paying a delivery, etc. Recorded as a <c>sortie</c>
/// <c>CaisseTransaction</c>, but - unlike an avoir refund - kept out of
/// <see cref="CaisseDto.PaiementCash"/> and <see cref="CaisseDto.TotalEncaisse"/>: it has
/// nothing to do with what a customer paid, so it only reduces the cash the drawer is
/// expected to hold, via <see cref="CaisseDto.TotalRetraits"/>.
/// <paramref name="Motif"/> is required - it is what the till's history shows for the
/// withdrawal, since "cash left the drawer" alone explains nothing.</summary>
/// <param name="ModePaiement"><c>cash</c> or <c>mobile_money</c> - which pool the withdrawal
/// comes out of, and which one it is checked against and deducted from.</param>
public sealed record WithdrawCaisseRequest(decimal Montant, string Motif, string ModePaiement = "cash");

// --- Paramètres: reçu et facture ---

/// <summary>
/// What every unset receipt setting falls back to. One copy, used by the API when it builds
/// a <see cref="ReceiptSettingsDto"/> and by the settings editor's live preview, so the
/// preview cannot drift from what actually prints.
///
/// The strings are the DEFAULTs Lonnii Business's own migrations gave those columns
/// (add_facture_text_columns.sql and the rest), kept identical so a shop that has never
/// opened this screen gets the same receipt in both applications.
/// </summary>
public static class ReceiptSettingsDefaults
{
    public const string ReceiptTitle = "REÇU DE VENTE";
    public const string FactureTitle = "FACTURE";
    public const string FactureNoticeTitle = "À RÉGLER À LA CAISSE";
    public const string FactureNoticeText = "Merci de présenter cette facture pour le paiement";
    public const string FactureFooterText = "Merci de votre visite!";
    public const string ReceiptFooterText = "Merci pour votre achat!";
    public const string SellerLabel = "Vendeur";
    public const string AvoirNoticeTitle = "NOTE IMPORTANTE:";
    public const string AvoirNoticeText = "Le client peut présenter ce reçu pour récupérer un avoir de";

    public const string FontFamily = "Courier New";
    public const int FontSize = 11;
    public const int TitleFontSize = 16;

    /// <summary>Smallest and largest body text, in points. The receipt is printed on a
    /// narrow roll, so a size outside this range either stops being legible or starts
    /// wrapping the item table.</summary>
    public const int MinFontSize = 8;

    public const int MaxFontSize = 20;

    public const int MinTitleFontSize = 10;
    public const int MaxTitleFontSize = 30;

    /// <summary>The typefaces offered in the editor. Same list the web app presents, and all
    /// of them ship with Windows, so a receipt configured on one till prints identically on
    /// another rather than silently falling back to a substitute.</summary>
    public static readonly IReadOnlyList<string> FontFamilies =
        ["Courier New", "Arial", "Helvetica", "Times New Roman", "Georgia", "Verdana"];

    /// <summary>Clamps a stored or user-supplied size into the allowed range.</summary>
    public static int ClampFontSize(int size) => Math.Clamp(size, MinFontSize, MaxFontSize);

    public static int ClampTitleFontSize(int size) => Math.Clamp(size, MinTitleFontSize, MaxTitleFontSize);

    /// <summary>What a document hides before the shop has chosen anything. Only the signature
    /// zone: every other section either printed before templates existed, or has no content
    /// until the shop fills in the field behind it, so this keeps an unconfigured workspace
    /// printing exactly the ticket it always did.</summary>
    public static readonly IReadOnlyList<string> DefaultHiddenSections = [ReceiptSections.Signature];

    public const int MaxLegalTextLength = 1000;
}

/// <summary>
/// The printed layouts a reçu or a facture can use. Each document picks its own, because a
/// shop commonly hands out a till ticket as a receipt but an A4 invoice to business clients.
/// </summary>
public static class ReceiptTemplates
{
    /// <summary>The 80 mm till ticket - the only layout that existed before templates.</summary>
    public const string Ticket = "ticket";

    /// <summary>A 58 mm ticket: each item on two lines, so it fits the narrow roll.</summary>
    public const string Compact = "compact";

    /// <summary>A full page: sender and recipient blocks, a bordered item table, totals with
    /// tax and down-payment lines, signature and legal footer.</summary>
    public const string A4 = "a4";

    public static readonly IReadOnlyList<string> All = [Ticket, Compact, A4];

    public static string Label(string template) => template switch
    {
        Compact => "Ticket compact (58 mm)",
        A4 => "A4 détaillé",
        _ => "Ticket (80 mm)",
    };

    /// <summary>An unknown or missing value prints as the ticket rather than failing.</summary>
    public static string Normalise(string? template) =>
        All.FirstOrDefault(t => string.Equals(t, template?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Ticket;
}

/// <summary>
/// The parts of a printed document a shop can switch off, to save paper or because its trade
/// has no use for them. Stored as the list of <em>hidden</em> keys, so a section added later
/// prints by default rather than silently missing from every configured shop.
/// </summary>
public static class ReceiptSections
{
    public const string Logo = "logo";
    public const string Company = "company";
    public const string CompanyContact = "company_contact";
    public const string CompanyLegal = "company_legal";
    public const string Client = "client";
    public const string Seller = "seller";
    public const string Cashier = "cashier";
    public const string UnitPrice = "unit_price";
    public const string Discounts = "discounts";
    public const string Tva = "tva";
    public const string PaymentInfo = "payment_info";
    public const string PaymentHistory = "payment_history";
    public const string Notice = "notice";
    public const string Footer = "footer";
    public const string Qr = "qr";
    public const string Signature = "signature";
    public const string LegalFooter = "legal_footer";

    public static readonly IReadOnlyList<string> All =
    [
        Logo, Company, CompanyContact, CompanyLegal, Client, Seller, Cashier, UnitPrice, Discounts,
        Tva, PaymentInfo, PaymentHistory, Notice, Footer, Qr, Signature, LegalFooter,
    ];

    /// <summary>The sections that can appear on a facture. An unpaid sale has no payment,
    /// cashier or payment history to show, so offering those switches would do nothing.</summary>
    public static readonly IReadOnlyList<string> Facture =
        All.Where(s => s is not (PaymentInfo or PaymentHistory or Cashier)).ToList();

    public static string Label(string section, bool facture) => section switch
    {
        Logo => "Logo",
        Company => "Nom de l'entreprise",
        CompanyContact => "Adresse, téléphone, email",
        CompanyLegal => "Mentions légales (RCCM, NIU…)",
        Client => "Client",
        Seller => "Vendeur",
        Cashier => "Caissier",
        UnitPrice => "Colonne prix unitaire",
        Discounts => "Sous-total et remises",
        Tva => "Détail TVA",
        PaymentInfo => "Paiement (mode, payé, reste)",
        PaymentHistory => "Historique des paiements",
        Notice => facture ? "Encadré « à régler »" : "Notice d'avoir",
        Footer => "Message de fin",
        Qr => "QR code de paiement",
        Signature => "Signature / cachet",
        LegalFooter => "Texte de bas de page",
        _ => section,
    };

    /// <summary>Reads a stored list. Null means the shop never chose, so it gets the defaults;
    /// an empty string means it chose to hide nothing.</summary>
    public static IReadOnlyList<string> Parse(string? stored) =>
        stored is null
            ? ReceiptSettingsDefaults.DefaultHiddenSections
            : Clean(stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Drops unknown keys and duplicates, so a typo or a key from a newer client
    /// cannot be stored and then mean something different later.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string>? sections) =>
        (sections ?? []).Select(s => s.Trim().ToLowerInvariant()).Where(All.Contains).Distinct().ToList();

    public static string Serialize(IEnumerable<string>? sections) => string.Join(",", Clean(sections));
}

/// <summary>
/// A workspace's receipt and invoice configuration, with every default already applied - a
/// caller can print straight from this without a fallback of its own.
/// </summary>
/// <param name="CompanyName">Null when the shop has not set one; the caller shows the
/// workspace name instead, which is the only name it knows.</param>
/// <param name="LogoUrl">API-relative, as <see cref="ImageUploadResponse.ImageUrl"/> - fetch
/// the bytes with the authenticated client, not by pointing an Image control at it.</param>
public sealed record ReceiptSettingsDto(
    string? CompanyName,
    string? NoteUnderQr,
    string? LogoUrl,
    string? QrCodeUrl,
    string ReceiptTitle,
    string FactureTitle,
    string FactureNoticeTitle,
    string FactureNoticeText,
    string FactureFooterText,
    string ReceiptFooterText,
    string SellerLabel,
    string AvoirNoticeTitle,
    string AvoirNoticeText,
    string FontFamily,
    int FontSize,
    int ReceiptTitleFontSize,
    int FactureTitleFontSize,
    string ReceiptTemplate = ReceiptTemplates.Ticket,
    string FactureTemplate = ReceiptTemplates.Ticket,
    IReadOnlyList<string>? ReceiptHiddenSections = null,
    IReadOnlyList<string>? FactureHiddenSections = null,
    string? CompanyAddress = null,
    string? CompanyPhone = null,
    string? CompanyEmail = null,
    string? CompanyLegalInfo = null,
    string? LegalFooterText = null,
    decimal? TvaRate = null)
{
    public IReadOnlyList<string> HiddenSections(bool facture) =>
        (facture ? FactureHiddenSections : ReceiptHiddenSections) ?? ReceiptSettingsDefaults.DefaultHiddenSections;

    public string Template(bool facture) => facture ? FactureTemplate : ReceiptTemplate;
}

/// <summary>
/// Saves the text side of the receipt configuration. The logo and QR code are not here:
/// they are files, uploaded and removed through their own routes, so saving the wording does
/// not require re-sending the images.
/// </summary>
public sealed record UpdateReceiptSettingsRequest(
    string? CompanyName,
    string? NoteUnderQr,
    string ReceiptTitle,
    string FactureTitle,
    string FactureNoticeTitle,
    string FactureNoticeText,
    string FactureFooterText,
    string ReceiptFooterText,
    string SellerLabel,
    string AvoirNoticeTitle,
    string AvoirNoticeText,
    string FontFamily,
    int FontSize,
    int ReceiptTitleFontSize,
    int FactureTitleFontSize,
    string? ReceiptTemplate = null,
    string? FactureTemplate = null,
    IReadOnlyList<string>? ReceiptHiddenSections = null,
    IReadOnlyList<string>? FactureHiddenSections = null,
    string? CompanyAddress = null,
    string? CompanyPhone = null,
    string? CompanyEmail = null,
    string? CompanyLegalInfo = null,
    string? LegalFooterText = null,
    decimal? TvaRate = null);

// --- Images ---

/// <summary>Returned after a photo upload, so the caller can show it immediately.</summary>
public sealed record ImageUploadResponse(string ImageUrl);

// --- Activation ---

/// <summary>
/// What a new installation sends to the licence server before it will run.
///
/// <para>
/// This is what makes a forged credentials file worthless: the server has never registered
/// the workspace such a file invents, so activation refuses it and the installation has no
/// way to proceed. The check is not something the customer's machine could be argued out
/// of - the data it needs simply is not there.
/// </para>
/// <para>
/// The admin credentials are required as well as the workspace id. Without them, anyone
/// who learned a group id could burn through a shop's machine allowance from outside.
/// </para>
/// </summary>
public sealed record ActivationRequest(
    string GroupId,
    string Email,
    string Password,
    string DeviceId,
    string? DeviceName = null,
    string Platform = "windows",
    string? AppVersion = null);

/// <summary>
/// The settings an installation runs on, and the authority for all of them. Stored locally
/// so the shop can then work offline; refreshed whenever it next reaches the server.
/// </summary>
public sealed record ActivationResponse(
    string GroupId,
    string GroupName,
    string Mode,
    int MaxDevices,
    int DevicesUsed,
    string CurrencyLabel,
    bool SubscriptionRequired,
    string? SubscriptionStatus,
    DateTime? SubscriptionExpiresAt,
    DateTime ActivatedAt);

// --- Devices ---

/// <summary>
/// Binds another machine to this workspace - a new till, or one that was replaced.
///
/// <para>
/// Needs an administrator's credentials, and reaches the licence server: a shop adding
/// machines purely on its own hardware could grant itself any number. Counting them
/// centrally is what makes the allowance mean anything, and it is also how a copied
/// installation shows up, since every machine in the other shop is one nobody bound.
/// </para>
/// </summary>
public sealed record RegisterDeviceRequest(
    string Email,
    string Password,
    string DeviceId,
    string? DeviceName = null,
    string? AppVersion = null);

/// <summary>A machine bound to the workspace, as the device list shows it.</summary>
public sealed record DeviceDto(
    string Id,
    string DeviceId,
    string? DeviceName,
    string Platform,
    string? AppVersion,
    DateTime RegisteredAt,
    DateTime LastSeenAt,
    bool IsCurrent);

/// <summary>The workspace's machines and what it is allowed.</summary>
public sealed record DeviceListResponse(int MaxDevices, int Used, IReadOnlyList<DeviceDto> Devices);

// --- Licence refresh ---

/// <summary>
/// Asks the licence server for this workspace's current settings. Sent periodically, and
/// whenever a shop reconnects after we have changed something for them.
/// </summary>
public sealed record LicenceRefreshRequest(string GroupId, string DeviceId, string? AppVersion = null);

/// <summary>
/// The workspace's settings as the licence server sees them now - always the authority,
/// overriding whatever the installation currently believes.
/// </summary>
/// <param name="ServerTime">
/// The server's own clock. The installation stores this rather than its own, so putting a
/// till's clock back cannot extend <paramref name="MustReconnectBy"/>.
/// </param>
/// <param name="MustReconnectBy">
/// When this installation stops working if it has not reached the server again. Computed
/// here, from server time, for the same reason.
///
/// <para>
/// <b>Null in local mode, and that is deliberate.</b> An offline licence is paid in full up
/// front, with no recurring fee, so there is nothing for a deadline to protect - and the
/// tier is sold on working with no internet at all. A deadline there would punish the
/// customer who paid the most. Only online mode, sold cheaply against a yearly fee, has a
/// reason to keep checking in.
/// </para>
/// </param>
public sealed record LicenceRefreshResponse(
    string GroupId,
    string GroupName,
    string Mode,
    int MaxDevices,
    int DevicesUsed,
    string CurrencyLabel,
    bool SubscriptionRequired,
    string? SubscriptionStatus,
    DateTime? SubscriptionExpiresAt,
    bool IsBlocked,
    string? BlockReason,
    DateTime ServerTime,
    DateTime? MustReconnectBy);

// --- First launch ---

/// <summary>
/// Brings a fresh installation to life from the credentials file we issued.
///
/// <para>
/// The file is sent rather than a path, because the person doing the setup is at a till on
/// the shop's network and the file is on their machine, not on the host running the API.
/// </para>
/// </summary>
/// <param name="CredentialsFile">The .lonnii file's bytes, base64 encoded.</param>
/// <param name="Passphrase">Sent to the customer separately from the file.</param>
/// <param name="DeviceId">Fingerprint of the machine being bound, derived by the client.</param>
public sealed record ApplyCredentialsRequest(
    string CredentialsFile,
    string Passphrase,
    string DeviceId,
    string? DeviceName = null,
    string? AppVersion = null);

/// <summary>Reports what the installation became, so the client can go straight to sign-in.</summary>
public sealed record ApplyCredentialsResponse(
    string GroupId,
    string GroupName,
    string AdminEmail,
    string Mode,
    int MaxDevices,
    int DevicesUsed);

// --- Charges ---

/// <summary>One expense. Ported from Lonnii Business's <c>charges</c> table.</summary>
public sealed record ChargeDto(
    int Id,
    string Description,
    decimal Montant,
    string TypeCharge,
    string Categorie,
    DateTime Date,
    string? CreatedByName,
    bool IsRecurring,
    DateTime? RecurringEndDate,
    bool RecurringActive,
    int? RecurringSourceId,
    string? RecurringDay);

/// <summary>A charge category - name, description and colour, customisable per group.</summary>
public sealed record ChargeCategoryDto(int Id, string Nom, string? Description, string Color);

public sealed record ChargesListResponse(IReadOnlyList<ChargeDto> Charges);

/// <summary>Creates or edits a charge. <paramref name="IsRecurring"/> is only honoured when
/// <paramref name="TypeCharge"/> is <c>fixe</c> and the charge is not itself one already
/// generated from a recurring source (see <c>Charge.RecurringSourceId</c>); in every other
/// case the server silently treats it as false, matching backend/routes/gestionCharges.js's
/// own <c>shouldRecur</c> computation. <paramref name="RecurringEndDate"/> is required
/// whenever <paramref name="IsRecurring"/> is true and must cover at least the month after
/// <paramref name="Date"/>.</summary>
public sealed record SaveChargeRequest(
    string Description,
    decimal Montant,
    string TypeCharge,
    string Categorie,
    DateTime Date,
    bool IsRecurring = false,
    DateTime? RecurringEndDate = null,
    string? RecurringDay = null);

/// <summary>Response of <c>GET /api/charges/{id}</c> - the charge itself, its recurring
/// source when it was auto-created from one, every occurrence that source has generated so
/// far, and (for an active recurring source) when its next occurrence is due.</summary>
public sealed record ChargeDetailsResponse(
    ChargeDto Charge,
    ChargeDto SourceCharge,
    string ScheduleDescription,
    DateTime? NextScheduledDate,
    IReadOnlyList<ChargeDto> GeneratedCharges);

public sealed record SaveChargeCategoryRequest(string Nom, string? Description, string? Color);

/// <summary>Restarts a recurring charge stopped with <c>PUT /{id}/stop-recurring</c>,
/// optionally moving its end date - e.g. after negotiating a new lease term.</summary>
public sealed record ReactivateRecurringRequest(DateTime? RecurringEndDate = null);

/// <summary>Response of <c>GET /api/charges/stats</c> for one calendar year -
/// "Analyses" tab.</summary>
/// <param name="FixesAnnee">The year's <c>fixe</c> charges.</param>
/// <param name="VariablesAnnee">The year's <c>variable</c> charges.</param>
/// <param name="FixesParMois">Month (1-12) → fixe charges; the variable part is
/// <paramref name="ParMois"/> minus this.</param>
public sealed record ChargesStatsResponse(
    decimal Total,
    decimal MoyenneMensuelle,
    string CategoriePrincipale,
    IReadOnlyDictionary<int, decimal> ParMois,
    IReadOnlyDictionary<int, decimal> ParTrimestre,
    IReadOnlyDictionary<string, decimal> ParCategorie,
    decimal FixesAnnee,
    decimal VariablesAnnee,
    IReadOnlyDictionary<int, decimal> FixesParMois);

// --- Marges ---

/// <summary>Where a sale line's cost came from - see <see cref="MargeLineDto.CostSource"/>.</summary>
public static class MargeCostSources
{
    /// <summary>A recorded purchase price: the one in force when the sale was made (from
    /// the sale's stock history row) or, failing that, the product's current one.</summary>
    public const string Reel = "reel";

    /// <summary>No purchase price known - none recorded, or the product was deleted or
    /// renamed/reused since the sale: estimated as the line's own sale total less a 30%
    /// markup, the fallback rate Lonnii Business's gestionMarges.js applies.</summary>
    public const string Estime = "estime";

    /// <summary>Counted at zero cost - a vente-libre product (a service), same as the
    /// source app.</summary>
    public const string Aucun = "aucun";
}

/// <summary>One product's margin over the filtered period. <paramref name="Margin"/> is the
/// margin rate on revenue (taux de marque), in percent.</summary>
public sealed record MargeLineDto(
    string? ProductId,
    string Nom,
    string? CategoryId,
    string Categorie,
    int QuantiteVendue,
    decimal Revenue,
    decimal Cost,
    decimal Profit,
    decimal Margin,
    string CostSource);

public sealed record MargeCategoryDto(
    string? CategoryId,
    string Categorie,
    int QuantiteVendue,
    decimal Revenue,
    decimal Cost,
    decimal Profit,
    decimal Margin);

/// <summary>One calendar month (client-local) of the trailing-twelve-months chart.
/// <paramref name="Charges"/> is every charge; <paramref name="ChargesFixes"/> the fixe part of it.</summary>
public sealed record MargeMonthDto(DateOnly Mois, decimal Revenue, decimal Cost, decimal Charges, decimal ChargesFixes);

public sealed record MargeCategoryOptionDto(string Id, string Nom);

/// <summary>
/// Response of <c>GET /api/marges</c> - Lonnii Business's gestionMarges.js figures
/// (revenue, cost of sales, gross and net profit and their rates, per-product, per-category
/// and monthly breakdowns), plus the ratios derived from them and a comparison with the
/// preceding period of the same length.
/// </summary>
/// <param name="TotalCharges">Every charge in the period - never narrowed by a category
/// filter, since a charge has no product category. Same as the source app.</param>
/// <param name="ChargesFixes">The <c>fixe</c> charges - rent, salaries: owed whatever is sold.</param>
/// <param name="ChargesVariables">The <c>variable</c> charges - they move with activity, so
/// the break-even analysis counts them with the cost of sales.</param>
/// <param name="MargeCoutsVariables">MCV = revenue − cost of sales − variable charges.</param>
/// <param name="TauxMcv">MCV as a percentage of revenue.</param>
/// <param name="SeuilRentabilite">Charges fixes ÷ taux de MCV: the revenue at which the MCV
/// exactly pays the fixed charges. Null when the rate is not positive, since no amount of
/// revenue would then break even.</param>
/// <param name="PointMortDate">The day that revenue was reached, assuming sales spread
/// evenly over the period; null when it was not reached within it, or the period is unbounded.</param>
/// <param name="PreviousRevenue">Revenue of the preceding period of the same length; null
/// when the filter has no bounded date range to compare against.</param>
/// <param name="EstimatedLines">Sale lines whose cost is <see cref="MargeCostSources.Estime"/>.</param>
/// <param name="EstimatedRevenue">Revenue carried by those lines.</param>
/// <param name="CategoryOptions">Every product category in the group, for the filter.</param>
public sealed record MargesResponse(
    decimal TotalRevenue,
    decimal TotalCosts,
    decimal TotalCharges,
    decimal ChargesFixes,
    decimal ChargesVariables,
    decimal GrossProfit,
    decimal NetProfit,
    decimal GrossMargin,
    decimal NetMargin,
    int NombreVentes,
    decimal MargeCoutsVariables,
    decimal TauxMcv,
    decimal? SeuilRentabilite,
    DateOnly? PointMortDate,
    decimal? PreviousRevenue,
    decimal? PreviousGrossProfit,
    int EstimatedLines,
    decimal EstimatedRevenue,
    IReadOnlyList<MargeLineDto> Products,
    IReadOnlyList<MargeCategoryDto> Categories,
    IReadOnlyList<MargeMonthDto> Monthly,
    IReadOnlyList<MargeCategoryOptionDto> CategoryOptions);

// --- Programme ---

/// <summary>One worker's day on the Programme board. <paramref name="Type"/> is one of
/// <c>Lonnii.Data.Entities.ProgrammeEntryTypes</c> ("travail", "reunion", "repos") - the
/// client owns the French label the same way it already does for other stored type strings.</summary>
public sealed record ProgrammeEntryDto(
    int Id,
    string UserId,
    string UserName,
    DateOnly Date,
    string Type,
    TimeSpan? HeureDebut,
    TimeSpan? HeureFin,
    string? Note);

/// <summary>Creates or edits one day of one worker's schedule. Saving a second entry for the
/// same worker and date replaces the first rather than adding a duplicate.</summary>
public sealed record SaveProgrammeEntryRequest(
    string UserId,
    DateOnly Date,
    string Type,
    TimeSpan? HeureDebut = null,
    TimeSpan? HeureFin = null,
    string? Note = null);

/// <summary>A notice on the Programme board - <paramref name="UserId"/> null means every
/// worker's printed sheet shows it; set, only that worker's does.</summary>
public sealed record ProgrammeAnnouncementDto(
    int Id,
    string? UserId,
    string? UserName,
    string Titre,
    string Message,
    DateOnly Date,
    string? CreatedByName);

public sealed record SaveProgrammeAnnouncementRequest(
    string? UserId,
    string Titre,
    string Message,
    DateOnly Date);

/// <summary>Response of <c>GET /api/programme</c> for one date range - every entry and
/// announcement in it, optionally narrowed to one worker's own (announcements addressed to
/// someone else are left out, but group-wide ones always come through).</summary>
public sealed record ProgrammeResponse(
    IReadOnlyList<ProgrammeEntryDto> Entries,
    IReadOnlyList<ProgrammeAnnouncementDto> Announcements);

// --- Amortissement ---

/// <summary>One year of a depreciation schedule - a row of <c>amortissement_echeances</c>.
/// <paramref name="ValeurDebutPeriode"/> and <paramref name="ValeurNetteComptable"/> are the
/// net book value at the start and end of the period.</summary>
public sealed record AmortissementEcheanceDto(
    int Annee,
    int NumeroAnnee,
    DateOnly DateDebut,
    DateOnly DateFin,
    decimal ValeurDebutPeriode,
    decimal DotationAnnuelle,
    decimal AmortissementCumule,
    decimal ValeurNetteComptable);

/// <summary>A fixed asset. The four figures at the end are computed for the current year, not
/// stored: <paramref name="AmortissementCumule"/> and <paramref name="ValeurNetteComptable"/>
/// as at 31 December, or as at the exit year for an asset sold or scrapped, whose schedule
/// stops there. <paramref name="TotalementAmorti"/> is true once an active asset's schedule has
/// run out.</summary>
public sealed record ImmobilisationDto(
    int Id,
    string Nom,
    string? Description,
    string Categorie,
    DateOnly DateAcquisition,
    decimal ValeurAcquisition,
    decimal ValeurResiduelle,
    int DureeAmortissement,
    string MethodeAmortissement,
    decimal? TauxDegressif,
    DateOnly? DateMiseEnService,
    string Statut,
    DateOnly? DateCession,
    decimal? ValeurCession,
    string? MotifSortie,
    string? NumeroInventaire,
    string? Localisation,
    string? Fournisseur,
    string? NumeroFacture,
    string? Notes,
    string? CreatedByName,
    decimal AmortissementCumule,
    decimal ValeurNetteComptable,
    decimal DotationAnneeCourante,
    bool TotalementAmorti);

/// <summary>Creates or edits a fixed asset. Its schedule is recomputed and stored on every
/// save. <paramref name="TauxDegressif"/> is the declining-balance coefficient, only read for
/// that method; null uses the fiscal default for the duration.</summary>
public sealed record SaveImmobilisationRequest(
    string Nom,
    string Categorie,
    DateOnly DateAcquisition,
    decimal ValeurAcquisition,
    decimal ValeurResiduelle,
    int DureeAmortissement,
    string MethodeAmortissement,
    decimal? TauxDegressif = null,
    DateOnly? DateMiseEnService = null,
    string? Description = null,
    string? NumeroInventaire = null,
    string? Localisation = null,
    string? Fournisseur = null,
    string? NumeroFacture = null,
    string? Notes = null);

/// <summary>Records an asset leaving the books. <paramref name="Statut"/> is <c>cede</c> (sold,
/// for <paramref name="ValeurCession"/>) or <c>reforme</c> (scrapped).</summary>
public sealed record CederImmobilisationRequest(
    string Statut,
    DateOnly DateCession,
    decimal? ValeurCession,
    string? MotifSortie);

/// <summary>Response of <c>GET /api/amortissement/{id}</c>. For an asset that has left the
/// books, <paramref name="VncALaSortie"/> is its net book value at the end of its exit year
/// and <paramref name="PlusMoinsValue"/> the sale price less that value (negative for a loss,
/// −VNC for a scrapped asset).</summary>
public sealed record ImmobilisationDetailsResponse(
    ImmobilisationDto Immobilisation,
    IReadOnlyList<AmortissementEcheanceDto> Echeances,
    decimal? VncALaSortie,
    decimal? PlusMoinsValue);

public sealed record AmortissementCategorieStatDto(string Categorie, int Nombre, decimal ValeurBrute, decimal ValeurNette);

/// <summary>The four summary tiles, over active assets only - as the source app.</summary>
public sealed record AmortissementStatsDto(
    int Annee,
    int TotalImmobilisations,
    decimal TotalValeurAcquisition,
    decimal DotationAnneeCourante,
    decimal AmortissementCumule,
    decimal ValeurNetteComptable,
    IReadOnlyList<AmortissementCategorieStatDto> ParCategorie);

/// <summary>Response of <c>GET /api/amortissement</c>: every asset of the group, newest
/// acquisition first, and the summary figures.</summary>
public sealed record AmortissementListResponse(
    IReadOnlyList<ImmobilisationDto> Immobilisations,
    AmortissementStatsDto Stats);

// --- Bilan & compte de résultat ---

/// <summary>One account line of the bilan or the compte de résultat.
/// <paramref name="SoldeManuel"/> is what was entered by hand - the écritures on a bilan
/// account, the typed amount on a résultat account; <paramref name="SoldeAuto"/> what the app
/// fed in from other modules (sales, stock, assets, charges); <paramref name="Solde"/> both
/// together.</summary>
public sealed record BilanCompteDto(
    int Id,
    string NumeroCompte,
    string Libelle,
    string TypeCompte,
    string? SousType,
    string? Description,
    bool IsSystem,
    decimal SoldeManuel,
    decimal SoldeAuto,
    decimal Solde);

/// <summary>The assets carried from the Amortissement module: gross value, cumulative
/// depreciation and net book value at the year-end.</summary>
public sealed record BilanImmobilisationsDto(decimal ValeurBrute, decimal Amortissements, decimal ValeurNette);

/// <summary>
/// Response of <c>GET /api/bilan?annee=</c>: the balance sheet at 31 December of
/// <paramref name="Annee"/>, or as of today for the current year
/// (<paramref name="IsProvisoire"/>). <paramref name="Ecart"/> is total actif − total passif;
/// zero means the sheet balances.
/// </summary>
public sealed record BilanResponse(
    int Annee,
    bool IsProvisoire,
    IReadOnlyList<BilanCompteDto> ActifImmobilise,
    IReadOnlyList<BilanCompteDto> ActifCirculant,
    IReadOnlyList<BilanCompteDto> TresorerieActif,
    IReadOnlyList<BilanCompteDto> CapitauxPropres,
    IReadOnlyList<BilanCompteDto> DettesLongTerme,
    IReadOnlyList<BilanCompteDto> DettesCourtTerme,
    IReadOnlyList<BilanCompteDto> TresoreriePassif,
    BilanImmobilisationsDto Immobilisations,
    decimal Stocks,
    decimal CreancesClients,
    decimal ResultatExercice,
    decimal TotalActif,
    decimal TotalPassif,
    decimal Ecart);

/// <summary>
/// What the compte de résultat took from the other modules for the year.
/// <paramref name="CoutMarchandisesVendues"/> is the cost of the goods actually sold, costed
/// exactly as Marges does; <paramref name="ChargesAchats"/> the charges filed under a purchase
/// category, which join it on account 60. <paramref name="VariationStocks"/> (stock fin − stock
/// début) is shown for information only - the cost of goods sold already accounts for what
/// left the shelves, so adding it again would count the same goods twice.
/// </summary>
/// <param name="StockDebutSaisi">False when no 1 January value was stored for the year yet, so
/// the one shown is a guess: today's stock for the year in progress (stored from then on, as
/// the source does), zero for a past year.</param>
/// <param name="VentesEstimees">Sale lines whose cost was estimated, as on the Marges screen.</param>
public sealed record ResultatIntegrationDto(
    decimal Ventes,
    decimal CoutMarchandisesVendues,
    decimal ChargesAchats,
    decimal Charges,
    decimal DotationAmortissement,
    decimal StockDebut,
    decimal StockFin,
    decimal VariationStocks,
    bool StockDebutSaisi,
    int VentesEstimees);

/// <summary>
/// Response of <c>GET /api/bilan/resultat?annee=</c>: income and expenses for one calendar
/// year, grouped SYSCOHADA-style into exploitation, financier and exceptionnel, each with its
/// own sub-result.
/// </summary>
public sealed record ResultatResponse(
    int Annee,
    IReadOnlyList<BilanCompteDto> ProduitsExploitation,
    IReadOnlyList<BilanCompteDto> ChargesExploitation,
    IReadOnlyList<BilanCompteDto> ProduitsFinanciers,
    IReadOnlyList<BilanCompteDto> ChargesFinancieres,
    IReadOnlyList<BilanCompteDto> ProduitsExceptionnels,
    IReadOnlyList<BilanCompteDto> ChargesExceptionnelles,
    ResultatIntegrationDto Integration,
    decimal TotalProduits,
    decimal TotalCharges,
    decimal ResultatExploitation,
    decimal ResultatFinancier,
    decimal ResultatExceptionnel,
    decimal ResultatNet);

/// <summary>Response of <c>GET /api/bilan/comptes</c> - the chart of accounts, without
/// balances.</summary>
public sealed record BilanComptesResponse(
    IReadOnlyList<BilanCompteDto> BilanComptes,
    IReadOnlyList<BilanCompteDto> ResultatComptes);

/// <summary>Creates or edits an account. <paramref name="TableType"/> is <c>bilan</c> or
/// <c>resultat</c> and cannot change on an edit.</summary>
public sealed record SaveBilanCompteRequest(
    string TableType,
    string NumeroCompte,
    string Libelle,
    string TypeCompte,
    string? SousType = null,
    string? Description = null);

/// <summary>A manual entry on a bilan account.</summary>
public sealed record BilanEcritureDto(
    int Id,
    int CompteId,
    string NumeroCompte,
    string CompteLibelle,
    DateOnly DateEcriture,
    string Libelle,
    decimal MontantDebit,
    decimal MontantCredit,
    string? Reference,
    string? Notes,
    string? CreatedByName);

/// <summary>Creates or edits an écriture. Exactly one of debit and credit is expected to be
/// non-zero, though the server only requires that they are not both zero - as the source.</summary>
public sealed record SaveBilanEcritureRequest(
    int CompteId,
    DateOnly DateEcriture,
    string Libelle,
    decimal MontantDebit,
    decimal MontantCredit,
    string? Reference = null,
    string? Notes = null);

/// <summary>Sets the stock value at 1 January of <paramref name="Annee"/>.</summary>
public sealed record StockSnapshotRequest(int Annee, decimal StockValueDebut);

/// <summary>Sets the hand-entered amount of a résultat account.</summary>
public sealed record ResultatCompteSoldeRequest(decimal Solde);

// --- Audit (présences) ---

/// <summary>Sent by every signed-in client about once a minute: "I am still here, on this
/// screen, at this machine". <paramref name="UtcOffsetMinutes"/> is the machine's offset from
/// UTC, so the server files the stretch under the shop's calendar day, not its own.</summary>
public sealed record PresenceHeartbeatRequest(string? Module, string? DeviceName, int UtcOffsetMinutes);

/// <summary>Values of <see cref="AttendanceMemberDto.Status"/>, as the web app spells them.</summary>
public static class AttendanceStatuses
{
    public const string EnLigne = "en-ligne";
    public const string Inactif = "inactif";
    public const string Retard = "retard";
    public const string Absent = "absent";
    public const string Repos = "repos";
    public const string HorsLigne = "hors-ligne";
}

/// <summary>
/// One member's attendance today and this week.
/// </summary>
/// <param name="ScheduleType">Today's Programme entry type (travail, reunion, repos), or null
/// when nothing is planned for them today.</param>
/// <param name="RetardMinutes">Minutes after the planned start of the first connection today,
/// when beyond the 5-minute grace; otherwise null.</param>
/// <param name="Productivity">Worked ÷ planned this week, capped at 100; null with nothing planned.</param>
public sealed record AttendanceMemberDto(
    string UserId,
    string Name,
    string? Email,
    string Role,
    string Status,
    int? RetardMinutes,
    DateTime? FirstLoginAt,
    DateTime? LastSeenAt,
    string? CurrentModule,
    string? DeviceName,
    string? IpAddress,
    string? ScheduleType,
    TimeSpan? ScheduleStart,
    TimeSpan? ScheduleEnd,
    int WeekMinutesWorked,
    int WeekMinutesScheduled,
    int? Productivity);

public sealed record AttendanceResponse(DateOnly Date, IReadOnlyList<AttendanceMemberDto> Members);

/// <summary>One connected stretch in a member's history.</summary>
public sealed record WorkSessionDto(
    DateTime LoginAt, DateTime? LogoutAt, int DurationMinutes, bool IsOpen, string? DeviceName, string? IpAddress);

/// <summary>A day in a member's history, with its planned hours for comparison.</summary>
public sealed record WorkDayDto(
    DateOnly Date,
    int TotalMinutes,
    string? ScheduleType,
    TimeSpan? ScheduleStart,
    TimeSpan? ScheduleEnd,
    IReadOnlyList<WorkSessionDto> Sessions);

public sealed record MemberWorkHistoryResponse(string UserId, string Name, IReadOnlyList<WorkDayDto> Days);

// --- Paramètres: consommation données ---

/// <summary>One section of the data-consumption breakdown. <paramref name="Bytes"/> is the web
/// app's per-row estimate, not a measurement.</summary>
public sealed record DataConsumptionRowDto(string Key, string Label, string Section, int Count, long Bytes);

public sealed record DataConsumptionResponse(
    int? Year,
    int TotalRecords,
    long TotalBytes,
    int Sections,
    int SectionsWithData,
    int TotalFiles,
    int TotalMembers,
    IReadOnlyList<DataConsumptionRowDto> Rows);

// --- Errors ---

/// <summary>A failure response. <paramref name="Required"/> names the missing privilege on a 403.</summary>
public sealed record ApiError(string Error, string? Required = null);
