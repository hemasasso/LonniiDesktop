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
    IReadOnlyList<CaisseRetraitDto>? Retraits = null)
{
    public decimal TotalRetraits => Retraits?.Sum(r => r.Montant) ?? 0;
}

/// <summary>One manual cash withdrawal ("Retirer de la caisse") from a session.</summary>
public sealed record CaisseRetraitDto(decimal Montant, string Motif, DateTime Date);

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

/// <summary>Closes the caller's open session; <paramref name="MontantFinal"/> is the cash
/// actually counted in the drawer, compared against what the session's sales expect.</summary>
public sealed record CloseCaisseRequest(decimal MontantFinal, string? Notes = null);

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
/// <c>CaisseTransaction</c>, so it folds into <see cref="CaisseDto.PaiementCash"/> and
/// <see cref="CaisseDto.TotalEncaisse"/> the same way an avoir refund already does.
/// <paramref name="Motif"/> is required - it is what the till's history shows for the
/// withdrawal, since "cash left the drawer" alone explains nothing.</summary>
public sealed record WithdrawCaisseRequest(decimal Montant, string Motif);

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
    int FactureTitleFontSize);

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
    int FactureTitleFontSize);

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
public sealed record ChargesStatsResponse(
    decimal Total,
    decimal MoyenneMensuelle,
    string CategoriePrincipale,
    IReadOnlyDictionary<int, decimal> ParMois,
    IReadOnlyDictionary<int, decimal> ParTrimestre,
    IReadOnlyDictionary<string, decimal> ParCategorie);

// --- Errors ---

/// <summary>A failure response. <paramref name="Required"/> names the missing privilege on a 403.</summary>
public sealed record ApiError(string Error, string? Required = null);
