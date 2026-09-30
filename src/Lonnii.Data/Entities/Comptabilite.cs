namespace Lonnii.Data.Entities;

/// <summary>
/// A fixed asset - machine, vehicle, furniture, computer. Ported from Lonnii Business's
/// <c>immobilisations</c> table (backend/setup_amortissement_bilan.sql), including its
/// <c>groupe_id</c> column name. Columns that are nullable there are nullable here too, so a
/// row written by the web app with a gap in it still loads.
/// </summary>
public class Immobilisation
{
    public int Id { get; set; }
    public string GroupId { get; set; } = string.Empty;

    public string Nom { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>One of <c>Lonnii.Shared.Comptabilite.CategoriesImmobilisation</c>.</summary>
    public string Categorie { get; set; } = "materiel";

    public DateOnly DateAcquisition { get; set; }
    public decimal ValeurAcquisition { get; set; }
    public decimal? ValeurResiduelle { get; set; }

    /// <summary>In years.</summary>
    public int DureeAmortissement { get; set; }

    /// <summary>One of <c>Lonnii.Shared.Comptabilite.MethodesAmortissement</c>.</summary>
    public string MethodeAmortissement { get; set; } = "lineaire";

    /// <summary>The declining-balance coefficient despite its name - what the source form
    /// labels "Coefficient dégressif". Null means the fiscal default for the duration.</summary>
    public decimal? TauxDegressif { get; set; }

    /// <summary>When depreciation starts; the acquisition date when null.</summary>
    public DateOnly? DateMiseEnService { get; set; }

    /// <summary>One of <c>Lonnii.Shared.Comptabilite.StatutsImmobilisation</c>; null reads as
    /// active.</summary>
    public string? Statut { get; set; } = "actif";

    public DateOnly? DateCession { get; set; }
    public decimal? ValeurCession { get; set; }
    public string? MotifSortie { get; set; }

    public string? NumeroInventaire { get; set; }
    public string? Localisation { get; set; }
    public string? Fournisseur { get; set; }
    public string? NumeroFacture { get; set; }
    public string? Notes { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<AmortissementEcheance> Echeances { get; set; } = [];
}

/// <summary>
/// One year of an asset's stored depreciation schedule - <c>amortissement_echeances</c>.
/// Rewritten whole whenever the asset is saved. Stored rather than recomputed on read because
/// the source app stores it and sums it in SQL for the bilan and the compte de résultat.
/// </summary>
public class AmortissementEcheance
{
    public int Id { get; set; }
    public int ImmobilisationId { get; set; }
    public string GroupId { get; set; } = string.Empty;

    public int Annee { get; set; }

    /// <summary>1 for the first year of the schedule, and so on.</summary>
    public int NumeroAnnee { get; set; }

    public DateOnly DateDebut { get; set; }
    public DateOnly DateFin { get; set; }

    public decimal ValeurDebutPeriode { get; set; }
    public decimal DotationAnnuelle { get; set; }
    public decimal AmortissementCumule { get; set; }
    public decimal ValeurNetteComptable { get; set; }

    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;

    public Immobilisation? Immobilisation { get; set; }
}

/// <summary>
/// An account of the balance sheet - <c>bilan_comptes</c>. Seeded per group with the source
/// app's 22 SYSCOHADA-style defaults (<see cref="IsSystem"/>) the first time the Bilan is
/// opened.
/// </summary>
public class BilanCompte
{
    public int Id { get; set; }
    public string GroupId { get; set; } = string.Empty;

    public string NumeroCompte { get; set; } = string.Empty;
    public string Libelle { get; set; } = string.Empty;

    /// <summary>One of <c>Lonnii.Shared.Comptabilite.TypesCompteBilan</c>.</summary>
    public string TypeCompte { get; set; } = string.Empty;

    /// <summary>What the automatic integrations key on - see
    /// <c>Lonnii.Shared.Comptabilite.SousTypesCompte</c>.</summary>
    public string? SousType { get; set; }

    /// <summary>Running debits − credits of this account's écritures, kept up to date as the
    /// source app does. Nothing reads it back for the bilan itself, which re-sums the
    /// écritures up to the year-end instead.</summary>
    public decimal? Solde { get; set; }

    public string? Description { get; set; }

    /// <summary>A seeded default account - it can be renamed but not deleted.</summary>
    public bool? IsSystem { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A manual entry on a bilan account, or on one of the compte de résultat's four
/// hand-fed accounts (financier/exceptionnel) - <c>bilan_ecritures</c>. <see cref="TableType"/>
/// (one of <c>Lonnii.Shared.Comptabilite.TablesCompte</c>) says which table
/// <see cref="CompteId"/> is a row of - <c>bilan_comptes</c> or <c>resultat_comptes</c> - since
/// the two share no relational identity to join on directly. There is deliberately no EF
/// foreign key on <see cref="CompteId"/> because of that; the endpoints themselves load and
/// validate the account before writing.
/// </summary>
public class BilanEcriture
{
    public int Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public int CompteId { get; set; }

    /// <summary>"bilan" or "resultat" - which table <see cref="CompteId"/> belongs to. Defaults
    /// to "bilan" for every row written before the résultat side existed.</summary>
    public string TableType { get; set; } = "bilan";

    public DateOnly DateEcriture { get; set; }
    public string Libelle { get; set; } = string.Empty;
    public decimal? MontantDebit { get; set; }
    public decimal? MontantCredit { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An account of the compte de résultat - <c>resultat_comptes</c>. Same shape as
/// <see cref="BilanCompte"/>. Only the financial and exceptional types take écritures (see
/// <c>Lonnii.Shared.Comptabilite.TypesCompteResultat.IsManuel</c>) - every other type is fed by
/// another module and <see cref="Solde"/> stays a hand-set baseline for it, never écriture-fed.
/// </summary>
public class ResultatCompte
{
    public int Id { get; set; }
    public string GroupId { get; set; } = string.Empty;

    public string NumeroCompte { get; set; } = string.Empty;
    public string Libelle { get; set; } = string.Empty;

    /// <summary>One of <c>Lonnii.Shared.Comptabilite.TypesCompteResultat</c>.</summary>
    public string TypeCompte { get; set; } = string.Empty;

    public string? SousType { get; set; }

    /// <summary>Running debit − credit of this account's écritures (only ever posted on the
    /// financial/exceptional types - see <see cref="BilanEcriture.TableType"/>), kept up to
    /// date the same way <see cref="BilanCompte.Solde"/> is. Cosmetic, same as there - the
    /// compte de résultat itself re-sums the year's écritures instead.</summary>
    public decimal? Solde { get; set; }

    public string? Description { get; set; }
    public bool? IsSystem { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime? CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-group settings for how the Bilan and compte de résultat get their numbers - one row per
/// group, created the first time it is changed from the default. <see cref="CalculAutomatique"/>
/// true (the default) is today's behaviour: sales, charges and amortissement feed the
/// statements automatically, and only the four financial/exceptional accounts take écritures.
/// False turns that auto-feed off entirely - every figure, on every account, is then posted by
/// hand as an écriture, for a company that wants to keep its own books instead.
/// </summary>
public class ComptabiliteParametres
{
    public string GroupId { get; set; } = string.Empty;
    public bool CalculAutomatique { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The stock value at 1 January of a year - <c>stock_snapshots</c>. Also the stock at
/// 31 December of the year before, which is how a closed year's bilan values its stock.
///
/// The source repo has no CREATE TABLE for it; this shape is inferred from
/// backend/routes/gestionBilan.js, which reads and writes <c>group_id</c> (not
/// <c>groupe_id</c>, unlike the other tables here), <c>annee</c>, <c>stock_value_debut</c>,
/// <c>snapshot_date</c> and <c>created_by</c>, and relies on <c>ON CONFLICT (group_id, annee)</c>.
/// Keyed on that pair rather than on an id column the live table may not have.
/// </summary>
public class StockSnapshot
{
    public string GroupId { get; set; } = string.Empty;
    public int Annee { get; set; }
    public decimal? StockValueDebut { get; set; }
    public DateTime? SnapshotDate { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}
