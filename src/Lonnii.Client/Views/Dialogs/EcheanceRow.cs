using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>One row of a tableau d'amortissement, as the schedule grids show it.
/// <see cref="IsCurrentYear"/> highlights this year's line, as the source table does.</summary>
public sealed record EcheanceRow(AmortissementEcheanceDto Echeance, bool IsCurrentYear, bool IsApresSortie = false)
{
    public int Annee => Echeance.Annee;
    public int NumeroAnnee => Echeance.NumeroAnnee;
    public string Periode => $"{Echeance.DateDebut:dd/MM/yyyy} – {Echeance.DateFin:dd/MM/yyyy}";
    public string VncDebut => Money.FormatPlain(Echeance.ValeurDebutPeriode);
    public string Dotation => Money.FormatPlain(Echeance.DotationAnnuelle);
    public string Cumule => Money.FormatPlain(Echeance.AmortissementCumule);
    public string VncFin => Money.FormatPlain(Echeance.ValeurNetteComptable);

    public static List<EcheanceRow> From(IEnumerable<AmortissementEcheanceDto> echeances, int? derniereAnnee = null)
    {
        var year = DateTime.Now.Year;
        return echeances
            .Select(e => new EcheanceRow(e, e.Annee == year, derniereAnnee is { } last && e.Annee > last))
            .ToList();
    }
}
