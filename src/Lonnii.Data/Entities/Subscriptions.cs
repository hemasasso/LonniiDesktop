namespace Lonnii.Data.Entities;

/// <summary>
/// Values stored in <see cref="DashboardSubscription.Statut"/>.
///
/// French, and capitalised, because that is exactly what the admin dashboard writes -
/// see dashboard/src/components/Subscriptions/Subscriptions.js. Do not "tidy" these into
/// lowercase English: the same rows are read and written by the web dashboard.
/// </summary>
public static class StatutAbonnement
{
    public const string Active = "Active";
    public const string Expire = "Expiré";
    public const string Annule = "Annulé";
    public const string EnAttente = "En attente";
    public const string Inactif = "Inactif";

    /// <summary>
    /// The live column's DEFAULT, and not a value the dashboard ever writes - it uses
    /// "En attente". A row inserted without an explicit statut therefore lands on a value
    /// the dashboard's filters do not match, so it shows in no status tab. Treated here as
    /// equivalent to <see cref="EnAttente"/> rather than as an unknown state.
    /// </summary>
    public const string EnAttenteLegacy = "en_attente";

    public static readonly IReadOnlyList<string> All = [Active, Expire, Annule, EnAttente, Inactif];

    /// <summary>The only status that grants access. The dashboard treats every other value as lapsed.</summary>
    public static bool PermitsAccess(string? statut) => statut == Active;

    /// <summary>True for a contract that has not started yet, in either spelling.</summary>
    public static bool IsPending(string? statut) => statut is EnAttente or EnAttenteLegacy;
}

/// <summary>
/// A billing contract for one group, ported from <c>dashboard_subscriptions</c>.
///
/// Note that this table has no CREATE TABLE anywhere in the Lonnii Business repo - it was
/// created directly against the live database, so the running schema is the only source of
/// truth. The columns here are mirrored from the INSERT, UPDATE and SELECT statements in
/// backend/dashboardVentesProduits.js and must be checked against the live table before
/// this is pointed at imported data.
///
/// Rows are created and edited by admins in the web dashboard, not by the desktop app.
/// The desktop app only ever reads them, to decide whether an online-mode workspace may
/// sign in and sync.
/// </summary>
public class DashboardSubscription
{
    /// <summary>
    /// Auto-incrementing integer, unlike the VARCHAR(36) GUIDs used almost everywhere else
    /// in this schema. Its sequence is still named <c>dashboard_ventes_id_seq</c>, so the
    /// table was most likely grown out of an earlier one rather than designed.
    /// </summary>
    public int Id { get; set; }

    public string GroupId { get; set; } = string.Empty;

    /// <summary>Denormalised group name, as the dashboard stores it. Kept so history survives a rename.</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>The group's admin user, and their denormalised name. The name is NOT NULL live.</summary>
    public string? AdminId { get; set; }
    public string AdminName { get; set; } = string.Empty;

    /// <summary>The contract amount.</summary>
    public decimal Montant { get; set; }

    /// <summary>Amount paid up front, before the contract starts. Null when there was none.</summary>
    public decimal? MontantPreabonnement { get; set; }

    /// <summary>One of <see cref="StatutAbonnement"/>.</summary>
    public string Statut { get; set; } = StatutAbonnement.Active;

    public DateTime? ContractStartDate { get; set; }
    public DateTime? ContractEndDate { get; set; }

    public string? Notes { get; set; }

    /// <summary>Only set when <see cref="Statut"/> is <c>Annulé</c>, matching the dashboard's behaviour.</summary>
    public string? CancellationReason { get; set; }
    public DateTime? CancellationDate { get; set; }

    /// <summary>Who created the row. VARCHAR(36) live, not the integer the route code suggested.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>The signed contract, uploaded through the dashboard.</summary>
    public string? ContractPdfUrl { get; set; }
    public string? ContractPdfFilename { get; set; }

    /// <summary>The column the dashboard sorts the subscription list by.</summary>
    public DateTime? DateVente { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this contract grants access at the given moment: status must be Active and,
    /// where an end date is set, it must not have passed. A missing end date is open-ended,
    /// which is how a manually granted licence is recorded.
    /// </summary>
    public bool IsCurrentAt(DateTime moment) =>
        StatutAbonnement.PermitsAccess(Statut)
        && (ContractStartDate is null || ContractStartDate <= moment)
        && (ContractEndDate is null || ContractEndDate > moment);
}
