namespace Lonnii.Data.Entities;

/// <summary>
/// Values stored in <see cref="Vente.StatutPaiement"/>.
///
/// <para>
/// Two vocabularies are in play. The desktop writes the French ones below. The live
/// PostgreSQL <c>ventes.payment_status</c> holds English - <c>paid</c>, <c>partial</c>,
/// <c>pending</c> - alongside a French <c>annule</c>, and its column DEFAULT is
/// <c>completed</c>, a value that appears in no row and that Lonnii Business's own
/// converter does not translate either. Anything reading that table must go through
/// <see cref="Normalise"/> rather than comparing strings directly.
/// </para>
/// </summary>
public static class StatutPaiement
{
    public const string EnAttente = "en_attente";
    public const string Partiel = "partiel";
    public const string Paye = "paye";
    public const string Annule = "annule";

    /// <summary>Spellings found in the live database, mapped onto the values above.</summary>
    private static readonly Dictionary<string, string> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["paid"] = Paye,
        // The live column's DEFAULT. Nothing writes it deliberately, and no row currently
        // holds it, but a row inserted without an explicit status would - and a sale that
        // silently read as unpaid would be worse than one read as paid.
        ["completed"] = Paye,
        ["partial"] = Partiel,
        ["pending"] = EnAttente,
        ["cancelled"] = Annule,
        ["canceled"] = Annule,
    };

    /// <summary>
    /// Turns whatever is stored into one of the four values above. Unknown input is
    /// returned unchanged rather than guessed at, so it surfaces instead of becoming "paid".
    /// </summary>
    public static string Normalise(string? statut) =>
        statut is null ? EnAttente
        : Legacy.TryGetValue(statut, out var known) ? known
        : statut;
}

/// <summary>Values stored in <see cref="Vente.ModePaiement"/> and payment records.</summary>
public static class ModePaiement
{
    public const string Cash = "cash";
    public const string MobileMoney = "mobile_money";
    public const string Carte = "carte";
    public const string Virement = "virement";
    public const string Cheque = "cheque";
}

/// <summary>
/// A sale. Ported from the current <c>ventes</c> table in setup_ventes_tables.sql - the
/// multi-item version with its own line items and payment records, not the older
/// single-product <c>ventes</c> defined in gestion_stock_schema.sql.
/// </summary>
public class Vente
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Human-readable reference, e.g. <c>V2026-00042</c>. Unique within a group.</summary>
    public string NumeroVente { get; set; } = string.Empty;

    public DateTime DateVente { get; set; } = DateTime.UtcNow;

    public string? ClientNom { get; set; }
    public string? ClientTelephone { get; set; }
    public string? ClientEmail { get; set; }

    public decimal MontantTotal { get; set; }

    /// <summary>
    /// What has been paid so far, summed from <see cref="Paiements"/> rather than stored.
    ///
    /// <para>
    /// The live database has no such column - Lonnii Business computes it as
    /// SUM(paiements_ventes.montant) on every read. Storing a copy here would give the same
    /// number two sources of truth, and while the web app and the desktop both write to
    /// that database a sale recorded by one would read back wrong in the other.
    /// </para>
    /// <para>
    /// Not translatable to SQL: a query that needs this must either include
    /// <see cref="Paiements"/> or project <c>v.Paiements.Sum(p =&gt; p.Montant)</c> itself.
    /// </para>
    /// </summary>
    public decimal MontantPaye => Paiements.Sum(p => p.Montant);

    /// <summary>What is still owed. Derived, for the same reason as <see cref="MontantPaye"/>.</summary>
    public decimal MontantRestant => MontantTotal - MontantPaye;

    /// <summary>One of <see cref="Entities.StatutPaiement"/>.</summary>
    public string StatutPaiement { get; set; } = Entities.StatutPaiement.EnAttente;

    /// <summary>One of <see cref="Entities.ModePaiement"/>.</summary>
    public string? ModePaiement { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// True when the customer overpaid and is owed a credit note. Mirrors the avoir
    /// columns added in add_avoir_solded_columns.sql.
    /// </summary>
    public bool IsAvoirSolded { get; set; }

    public DateTime? AvoirSoldedAt { get; set; }

    /// <summary>Id of the user who settled the avoir, if any. Mirrors the live <c>avoir_solded_by</c> column.</summary>
    public string? AvoirSoldedBy { get; set; }

    /// <summary>
    /// Credit owed to the client after an overpayment - set when a payment (or the sale
    /// itself) exceeds <see cref="MontantTotal"/>. Mirrors the live <c>avoir_amount</c>
    /// column, which has no CREATE TABLE in the repo (see lonnii-live-schema-drift memory);
    /// its shape is inferred from backend/routes/ventes.js.
    /// </summary>
    public decimal AvoirAmount { get; set; }

    /// <summary>True whenever <see cref="AvoirAmount"/> is positive. Stored separately,
    /// same as the live column, rather than derived - Lonnii Business writes both together.</summary>
    public bool IsAvoir { get; set; }

    /// <summary>Reason given when the sale was cancelled through <c>PUT /annuler</c>.</summary>
    public string? CancellationReason { get; set; }

    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Id of the user who cancelled the sale. Lonnii Business has no equivalent column -
    /// its own <c>PUT /:id/annuler</c> never records who clicked cancel, only the motif -
    /// so this is desktop-only, added by <c>db/postgres/002_desktop_columns.sql</c> for the
    /// live database the same way <see cref="AvoirSoldedBy"/> already is.
    /// </summary>
    public string? CancelledBy { get; set; }

    /// <summary>
    /// Client-supplied key that makes retrying a sale safe. Mirrors
    /// add_ventes_idempotency_key.sql; important here because a flaky local network
    /// makes a till retry more likely than on the web.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>The caisse session this sale was rung up in, when one was open.</summary>
    public int? CaisseId { get; set; }

    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<VenteItem> Items { get; set; } = [];
    public ICollection<PaiementVente> Paiements { get; set; } = [];
}

/// <summary>A sale line item. Ported from <c>ventes_items</c>.</summary>
public class VenteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string VenteId { get; set; } = string.Empty;

    /// <summary>Null when the product was later deleted; <see cref="NomProduit"/> preserves the name.</summary>
    public string? ProductId { get; set; }

    /// <summary>Product name captured at sale time, so history survives product edits.</summary>
    public string NomProduit { get; set; } = string.Empty;

    public int Quantite { get; set; }
    public decimal PrixUnitaire { get; set; }
    public decimal PrixTotal { get; set; }

    public decimal Discount { get; set; }

    /// <summary>Either <c>percentage</c> or <c>amount</c>. Source: add_discount_type_to_ventes_items.sql.</summary>
    public string DiscountType { get; set; } = "percentage";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Vente? Vente { get; set; }
}

/// <summary>A payment against a sale. Ported from <c>paiements_ventes</c>.</summary>
public class PaiementVente
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string VenteId { get; set; } = string.Empty;
    public decimal Montant { get; set; }

    /// <summary>One of <see cref="Entities.ModePaiement"/>.</summary>
    public string ModePaiement { get; set; } = Entities.ModePaiement.Cash;

    public DateTime DatePaiement { get; set; } = DateTime.UtcNow;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Vente? Vente { get; set; }
}

/// <summary>Cash register session status.</summary>
public static class CaisseStatus
{
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>
/// A cash register session. Ported from <c>caisses</c>. A group allows only one open
/// session per user, enforced by a filtered unique index in the source schema and
/// reproduced in <c>LonniiDbContext</c>.
/// </summary>
public class Caisse
{
    public int Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>User name captured at opening time, so closed sessions read correctly later.</summary>
    public string UserName { get; set; } = string.Empty;

    public DateTime DateOuverture { get; set; } = DateTime.UtcNow;

    /// <summary>Float placed in the drawer at opening.</summary>
    public decimal MontantInitial { get; set; }

    public DateTime? DateFermeture { get; set; }

    /// <summary>Cash counted at closing.</summary>
    public decimal? MontantFinal { get; set; }

    public int TotalVentes { get; set; }
    public decimal TotalChiffreAffaires { get; set; }
    public decimal TotalEncaisse { get; set; }
    public decimal TotalAvoir { get; set; }
    public decimal TotalRestant { get; set; }

    public decimal PaiementCash { get; set; }
    public decimal PaiementMobile { get; set; }
    public decimal PaiementCarte { get; set; }
    public decimal PaiementAutres { get; set; }

    /// <summary>Counted cash minus expected cash. Resolving a non-zero écart needs can_resolve_caisse_ecart.</summary>
    public decimal Ecart { get; set; }

    public bool EcartResolved { get; set; }
    public string? EcartResolvedBy { get; set; }
    public DateTime? EcartResolvedAt { get; set; }
    public string? EcartResolutionNote { get; set; }

    /// <summary>One of <see cref="CaisseStatus"/>.</summary>
    public string Status { get; set; } = CaisseStatus.Open;

    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Cash movements that are not ordinary sale payments. Ported from <c>caisse_transactions</c>:
/// <c>entree</c> is money in (settling an older invoice), <c>sortie</c> is money out
/// (refunding an avoir).
/// </summary>
public class CaisseTransaction
{
    public int Id { get; set; }
    public int CaisseId { get; set; }
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Either <c>entree</c> or <c>sortie</c>.</summary>
    public string Type { get; set; } = "entree";

    public decimal Montant { get; set; }
    public string? Description { get; set; }

    /// <summary>E.g. <c>facture_anterieure</c>, <c>avoir_solde</c>, <c>adjustment</c>.</summary>
    public string? Category { get; set; }

    public string? ModePaiement { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Caisse? Caisse { get; set; }
}

/// <summary>
/// Receipt and invoice configuration for one group - what "Paramètre Reçu et Facture"
/// edits, and what <c>VenteReceiptDialog</c> prints.
///
/// <para>
/// Every property below is a column that exists in Lonnii Business's own
/// <c>ventes_parametres</c>: <c>create_ventes_parametres_table.sql</c> for the first five,
/// then <c>add_facture_text_columns.sql</c>, <c>add_receipt_footer_text.sql</c>,
/// <c>add_avoir_notice_columns.sql</c> and <c>add_font_config_columns.sql</c>. Nothing is
/// invented: the web app and the desktop write the same row, so a shop that configures its
/// receipt in one sees it in the other.
/// </para>
/// <para>
/// All the text columns are nullable with a database DEFAULT rather than NOT NULL, so a row
/// written by an older client is still readable. Callers should not fall back on their own
/// wording - <see cref="Lonnii.Shared.Contracts.ReceiptSettingsDefaults"/> holds the single
/// set of defaults both sides use.
/// </para>
/// <para>
/// Keyed on <see cref="GroupeId"/> even though the live table also carries a
/// <c>SERIAL</c> <c>id</c>: <c>groupe_id</c> is UNIQUE there and is the only key anything
/// looks a row up by. Leaving <c>id</c> off the model means an INSERT omits it and the
/// sequence default fills it in.
/// </para>
/// </summary>
public class VentesParametres
{
    public string GroupeId { get; set; } = string.Empty;

    /// <summary>Business name printed at the top, above the document title. Falls back to
    /// the workspace name when unset, which is what the desktop showed before this existed.</summary>
    public string? CompanyName { get; set; }

    /// <summary>Caption under the QR code, e.g. "Scannez pour payer".</summary>
    public string? NoteUnderQr { get; set; }

    /// <summary>API-relative URL of the stored logo, e.g. <c>/api/images/receipt-logos/&lt;file&gt;</c>.
    /// Named <c>logo_path</c> live, where Lonnii Business stores a static <c>/uploads/...</c>
    /// path instead; both are a URL the same client resolves, so the column is shared.</summary>
    public string? LogoPath { get; set; }

    /// <summary>API-relative URL of the stored payment QR code. See <see cref="LogoPath"/>.</summary>
    public string? QrCodePath { get; set; }

    // --- Facture (an unpaid sale, settled at the till later) ----------------------
    public string? FactureTitle { get; set; }

    /// <summary>Bold heading of the boxed notice, e.g. "À RÉGLER À LA CAISSE".</summary>
    public string? FactureNoticeTitle { get; set; }

    public string? FactureNoticeText { get; set; }
    public string? FactureFooterText { get; set; }
    public int? FactureTitleFontSize { get; set; }

    // --- Reçu (a settled sale) ----------------------------------------------------
    public string? ReceiptTitle { get; set; }
    public string? ReceiptFooterText { get; set; }
    public int? ReceiptTitleFontSize { get; set; }

    /// <summary>Label printed before the seller's name. Configurable because the person who
    /// rang the sale up is called something different per trade - "Vendeur", "Caissier",
    /// "Préparateur" (see the lonnii-preparer-cashier-flow memory).</summary>
    public string? SellerLabel { get; set; }

    // --- Avoir notice (receipt only, when the client overpaid) --------------------
    public string? AvoirNoticeTitle { get; set; }
    public string? AvoirNoticeText { get; set; }

    // --- Typeface, shared by both documents ---------------------------------------
    public string? ReceiptFontFamily { get; set; }
    public int? ReceiptFontSize { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A sales customer. Ported from <c>clients</c>.</summary>
public class Client
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = string.Empty;
    public string Nom { get; set; } = string.Empty;
    public string? Telephone { get; set; }
    public string? Email { get; set; }
    public string? Adresse { get; set; }
    public string? Ville { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Ventes module activity feed. Ported from <c>ventes_user_activity</c>.</summary>
public class VentesUserActivity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GroupId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string? Action { get; set; }
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
