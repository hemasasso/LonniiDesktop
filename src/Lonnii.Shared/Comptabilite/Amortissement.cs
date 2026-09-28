using Lonnii.Shared.Contracts;

namespace Lonnii.Shared.Comptabilite;

/// <summary>Values stored in <c>immobilisations.categorie</c>, with their French labels -
/// the list Lonnii Business's Amortissement.jsx offers.</summary>
public static class CategoriesImmobilisation
{
    public const string Materiel = "materiel";
    public const string Mobilier = "mobilier";
    public const string Vehicule = "vehicule";
    public const string Informatique = "informatique";
    public const string Batiment = "batiment";
    public const string Logiciel = "logiciel";
    public const string Autre = "autre";

    public static readonly IReadOnlyList<string> All =
        [Materiel, Mobilier, Vehicule, Informatique, Batiment, Logiciel, Autre];

    public static string Label(string? categorie) => categorie switch
    {
        Materiel => "Matériel",
        Mobilier => "Mobilier",
        Vehicule => "Véhicule",
        Informatique => "Informatique",
        Batiment => "Bâtiment",
        Logiciel => "Logiciel",
        Autre => "Autre",
        null or "" => "—",
        _ => categorie,
    };
}

/// <summary>Values stored in <c>immobilisations.methode_amortissement</c>. The source schema's
/// comment also names <c>unite_production</c>, but no code there computes it and the form
/// never offers it, so it is not accepted here either.</summary>
public static class MethodesAmortissement
{
    /// <summary>The same dotation every year.</summary>
    public const string Lineaire = "lineaire";

    /// <summary>A fixed rate (linear rate × fiscal coefficient) on the remaining value,
    /// switching to linear once that gives more.</summary>
    public const string Degressif = "degressif";

    /// <summary>Sum of the years' digits: N/S, (N−1)/S, … of the depreciable amount.</summary>
    public const string SommeAnnees = "somme_annees";

    public static readonly IReadOnlyList<string> All = [Lineaire, Degressif, SommeAnnees];

    public static string Label(string? methode) => methode switch
    {
        Lineaire => "Linéaire",
        Degressif => "Dégressif",
        SommeAnnees => "Somme des années",
        _ => methode ?? "—",
    };

    public static string Description(string? methode) => methode switch
    {
        Lineaire => "Dotation constante chaque année",
        Degressif => "Dotation décroissante (coefficient fiscal)",
        SommeAnnees => "Dotation proportionnelle aux années restantes",
        _ => string.Empty,
    };
}

/// <summary>Values stored in <c>immobilisations.statut</c>.</summary>
public static class StatutsImmobilisation
{
    public const string Actif = "actif";

    /// <summary>Sold. <c>valeur_cession</c> holds the price.</summary>
    public const string Cede = "cede";

    /// <summary>Scrapped or taken out of use, with no sale.</summary>
    public const string Reforme = "reforme";

    /// <summary>Named in the source schema but never written by it. The port never writes it
    /// either: an active asset whose schedule has run out is reported through
    /// <c>ImmobilisationDto.TotalementAmorti</c> instead, so the stored status stays "actif"
    /// and the asset keeps its place on the balance sheet at its residual value.</summary>
    public const string TotalementAmorti = "totalement_amorti";

    /// <summary>A null <c>statut</c> is read as active - the column is nullable with a
    /// DEFAULT of 'actif'.</summary>
    public static string Normalise(string? statut) => string.IsNullOrWhiteSpace(statut) ? Actif : statut;

    public static bool IsSortie(string? statut) => Normalise(statut) is Cede or Reforme;

    public static string Label(string? statut) => Normalise(statut) switch
    {
        Actif => "Actif",
        Cede => "Cédé",
        Reforme => "Réformé",
        TotalementAmorti => "Totalement amorti",
        var other => other,
    };
}

/// <summary>
/// Builds a fixed asset's depreciation schedule - a port of <c>calculerAmortissement</c> in
/// Lonnii Business's backend/routes/gestionAmortissement.js, kept figure-for-figure identical
/// because both apps write the result into the same <c>amortissement_echeances</c> table.
///
/// Two edge cases are deliberately fixed rather than ported, both of which left the source's
/// schedule wrong:
/// <list type="bullet">
/// <item>A one-year asset put into service mid-year: the source gave year 1 its prorated
/// share and stopped, so the asset was never fully depreciated. The last year now always
/// takes whatever remains.</item>
/// <item>An asset put into service on or after 15 December: the source's month count came out
/// at 0 months, which it then treated as "no prorata" - a full year's dotation for a few
/// days' use. Depreciation now starts on 1 January of the following year.</item>
/// </list>
/// </summary>
public static class AmortissementCalculator
{
    public const int DureeMaximale = 50;

    /// <summary>The fiscal coefficient the source applies when none is given: 1.25 up to 4
    /// years, 1.75 up to 6, 2.25 beyond.</summary>
    public static decimal CoefficientDegressif(int duree) => duree <= 4 ? 1.25m : duree <= 6 ? 1.75m : 2.25m;

    /// <summary>
    /// Months depreciated in the first calendar year, under the SYSCOHADA prorata rule the
    /// source applies: the month of entry into service counts when it began before the 15th.
    /// 12 means a full first year.
    /// </summary>
    public static int MoisPremiereAnnee(DateOnly miseEnService) =>
        miseEnService.Day < 15 ? 13 - miseEnService.Month : 12 - miseEnService.Month;

    /// <summary>Checks a request before anything is computed or stored; null when valid.</summary>
    public static string? Valider(decimal valeurAcquisition, decimal valeurResiduelle, int duree, string methode, decimal? coefficient)
    {
        if (valeurAcquisition <= 0) return "La valeur d'acquisition doit être supérieure à zéro.";
        if (valeurResiduelle < 0) return "La valeur résiduelle ne peut pas être négative.";
        if (valeurResiduelle >= valeurAcquisition) return "La valeur résiduelle doit être inférieure à la valeur d'acquisition.";
        if (duree is < 1 or > DureeMaximale) return $"La durée d'amortissement doit être comprise entre 1 et {DureeMaximale} ans.";
        if (!MethodesAmortissement.All.Contains(methode)) return "Méthode d'amortissement invalide.";
        // The source form's own bounds (min 1, max 3).
        if (coefficient is { } c && (c < 1 || c > 3)) return "Le coefficient dégressif doit être compris entre 1 et 3.";
        return null;
    }

    public static IReadOnlyList<AmortissementEcheanceDto> Calculer(
        decimal valeurAcquisition, decimal valeurResiduelle, int duree, string methode,
        decimal? coefficient, DateOnly dateAcquisition, DateOnly? dateMiseEnService)
    {
        if (duree < 1) return [];

        var start = dateMiseEnService ?? dateAcquisition;
        return methode switch
        {
            MethodesAmortissement.Lineaire => Lineaire(valeurAcquisition, valeurResiduelle, duree, start),
            MethodesAmortissement.Degressif => Degressif(valeurAcquisition, duree, coefficient ?? CoefficientDegressif(duree), start),
            MethodesAmortissement.SommeAnnees => SommeAnnees(valeurAcquisition, valeurResiduelle, duree, start),
            _ => [],
        };
    }

    /// <summary>The year the first dotation falls in, and how many months of it count.</summary>
    private static (int PremiereAnnee, int Mois) Depart(DateOnly start)
    {
        var mois = MoisPremiereAnnee(start);
        return mois <= 0 ? (start.Year + 1, 12) : (start.Year, mois);
    }

    private static List<AmortissementEcheanceDto> Lineaire(decimal va, decimal vr, int duree, DateOnly start)
    {
        var amortissable = va - vr;
        var annuelle = amortissable / duree;
        var (premiereAnnee, mois) = Depart(start);
        var prorata = mois < 12;

        var echeances = new List<AmortissementEcheanceDto>(duree);
        var cumule = 0m;

        for (var i = 1; i <= duree; i++)
        {
            var annee = premiereAnnee + i - 1;
            var vncDebut = R(va - cumule);

            // Always exactly N years, as in the source: a prorated first year leaves its
            // shortfall to the last one rather than adding a year N+1.
            var dotation = i == duree ? amortissable - cumule
                : i == 1 && prorata ? annuelle * mois / 12m
                : annuelle;

            dotation = R(Math.Min(dotation, amortissable - cumule));
            cumule = R(cumule + dotation);

            echeances.Add(new AmortissementEcheanceDto(
                annee, i, new DateOnly(annee, 1, 1), new DateOnly(annee, 12, 31),
                vncDebut, dotation, cumule, Math.Max(R(va - cumule), 0m)));
        }

        return echeances;
    }

    /// <summary>Depreciates down to zero, ignoring the residual value - as the source does, and
    /// as the fiscal declining-balance method does.</summary>
    private static List<AmortissementEcheanceDto> Degressif(decimal va, int duree, decimal coefficient, DateOnly start)
    {
        var taux = coefficient / duree;
        var (premiereAnnee, mois) = Depart(start);
        var prorata = mois < 12;

        var echeances = new List<AmortissementEcheanceDto>(duree);
        var vnc = va;
        var cumule = 0m;

        for (var i = 1; i <= duree; i++)
        {
            var annee = premiereAnnee + i - 1;
            var vncDebut = vnc;

            decimal dotation;
            if (i == duree)
            {
                dotation = vnc;
            }
            else if (i == 1 && prorata)
            {
                dotation = vnc * taux * mois / 12m;
            }
            else
            {
                // Switch to linear over the remaining years once that gives more.
                var anneesRestantes = duree - i + 1;
                dotation = taux >= 1m / anneesRestantes ? vnc * taux : vnc / anneesRestantes;
            }

            dotation = Math.Min(R(dotation), vnc);
            cumule = R(cumule + dotation);
            vnc = R(vnc - dotation);

            echeances.Add(new AmortissementEcheanceDto(
                annee, i, new DateOnly(annee, 1, 1), new DateOnly(annee, 12, 31),
                vncDebut, dotation, cumule, Math.Max(vnc, 0m)));
        }

        return echeances;
    }

    /// <summary>Anniversary years from the date of entry into service, with no prorata -
    /// as in the source.</summary>
    private static List<AmortissementEcheanceDto> SommeAnnees(decimal va, decimal vr, int duree, DateOnly start)
    {
        var amortissable = va - vr;
        var somme = duree * (duree + 1) / 2m;

        var echeances = new List<AmortissementEcheanceDto>(duree);
        var cumule = 0m;

        for (var i = 1; i <= duree; i++)
        {
            var vncDebut = va - cumule;
            var dotation = i == duree
                ? amortissable - cumule
                : R(amortissable * (duree - i + 1) / somme);

            cumule = R(cumule + dotation);

            echeances.Add(new AmortissementEcheanceDto(
                start.Year + i - 1, i, start.AddYears(i - 1), start.AddYears(i).AddDays(-1),
                vncDebut, dotation, cumule, Math.Max(R(va - cumule), 0m)));
        }

        return echeances;
    }

    /// <summary>JavaScript's <c>Math.round(x * 100) / 100</c> for the positive amounts
    /// involved here.</summary>
    private static decimal R(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
