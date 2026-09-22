namespace Lonnii.Data.Entities;

/// <summary>Values stored in <see cref="Vente.StatutPaiement"/>.</summary>
public static class StatutPaiement
{
    public const string EnAttente = "en_attente";
    public const string Partiel = "partiel";
    public const string Paye = "paye";
    public const string Annule = "annule";
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
    public decimal MontantPaye { get; set; }
    public decimal MontantRestant { get; set; }

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
/// Receipt and invoice configuration for one group. Ported from <c>ventes_parametres</c>
/// plus the later facture/font/receipt-footer migrations.
/// </summary>
public class VentesParametres
{
    public string GroupeId { get; set; } = string.Empty;
    public string? CompanyName { get; set; }
    public string? NoteUnderQr { get; set; }
    public string? LogoPath { get; set; }
    public string? QrCodePath { get; set; }

    public string? FactureTitle { get; set; }
    public string? FactureHeaderText { get; set; }
    public string? FactureFooterText { get; set; }
    public string? ReceiptFooterText { get; set; }

    /// <summary>Heading shown above the avoir notice on a receipt.</summary>
    public string? AvoirNoticeTitle { get; set; } = "NOTE IMPORTANTE:";

    public string? AvoirNoticeText { get; set; } = "Le client peut présenter ce reçu pour récupérer un avoir de";

    public string? FontFamily { get; set; }
    public int? FontSize { get; set; }

    public bool ShowDate { get; set; } = true;
    public bool ShowDocumentSignatory { get; set; }
    public string? DocumentSignatory { get; set; }

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
