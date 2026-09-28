using Lonnii.Data.Entities;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// How a fixed asset's schedule turns into figures for one year - shared by the Amortissement
/// screen and the Bilan so the two can never disagree.
///
/// Where this departs from Lonnii Business: its SQL sums every stored échéance of the group,
/// so an asset sold in 2025 kept adding a dotation to 2026's expenses and stayed on every
/// later balance sheet, and an asset bought in 2026 already showed on the 2024 one. Here an
/// asset is only depreciated up to its exit year, and only appears on a balance sheet whose
/// year-end falls between its acquisition and its exit.
/// </summary>
internal static class AmortissementRules
{
    /// <summary>The stored schedule, or - for a row the web app wrote without one - the
    /// computed one, as gestionAmortissement.js falls back to.</summary>
    public static IReadOnlyList<AmortissementEcheanceDto> Echeances(Immobilisation i) =>
        i.Echeances.Count > 0
            ? i.Echeances.OrderBy(e => e.NumeroAnnee).Select(ToDto).ToList()
            : AmortissementCalculator.Calculer(
                i.ValeurAcquisition, i.ValeurResiduelle ?? 0m, i.DureeAmortissement, i.MethodeAmortissement,
                i.TauxDegressif, i.DateAcquisition, i.DateMiseEnService);

    /// <summary>The last year an asset is depreciated: its exit year once sold or scrapped,
    /// otherwise none. An exit recorded without a date (only possible from outside this app)
    /// is dated at its last update.</summary>
    public static int? DerniereAnnee(Immobilisation i) =>
        StatutsImmobilisation.IsSortie(i.Statut)
            ? i.DateCession?.Year ?? i.UpdatedAt?.Year ?? i.DateAcquisition.Year
            : null;

    /// <summary>The dotation charged for <paramref name="annee"/>.</summary>
    public static decimal DotationDe(Immobilisation i, IReadOnlyList<AmortissementEcheanceDto> echeances, int annee) =>
        DerniereAnnee(i) is { } last && annee > last
            ? 0m
            : echeances.Where(e => e.Annee == annee).Sum(e => e.DotationAnnuelle);

    /// <summary>Cumulative depreciation at 31 December of <paramref name="annee"/>, frozen at
    /// the exit year for an asset that has left the books.</summary>
    public static decimal CumuleAu(Immobilisation i, IReadOnlyList<AmortissementEcheanceDto> echeances, int annee)
    {
        var jusqua = DerniereAnnee(i) is { } last ? Math.Min(annee, last) : annee;
        return echeances.Where(e => e.Annee <= jusqua).Sum(e => e.DotationAnnuelle);
    }

    /// <summary>Whether the asset is still owned at <paramref name="finExercice"/>: acquired by
    /// then, and not yet sold or scrapped.</summary>
    public static bool AuBilan(Immobilisation i, DateOnly finExercice)
    {
        if (i.DateAcquisition > finExercice) return false;
        if (!StatutsImmobilisation.IsSortie(i.Statut)) return true;
        return i.DateCession is { } sortie && sortie > finExercice;
    }

    public static AmortissementEcheanceDto ToDto(AmortissementEcheance e) => new(
        e.Annee, e.NumeroAnnee, e.DateDebut, e.DateFin,
        e.ValeurDebutPeriode, e.DotationAnnuelle, e.AmortissementCumule, e.ValeurNetteComptable);
}
