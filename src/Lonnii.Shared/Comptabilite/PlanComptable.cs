namespace Lonnii.Shared.Comptabilite;

/// <summary>Which of the two account tables a compte lives in - Lonnii Business's
/// <c>table_type</c> request field.</summary>
public static class TablesCompte
{
    /// <summary><c>bilan_comptes</c>: assets, equity and liabilities.</summary>
    public const string Bilan = "bilan";

    /// <summary><c>resultat_comptes</c>: income and expenses.</summary>
    public const string Resultat = "resultat";
}

/// <summary>Values stored in <c>bilan_comptes.type_compte</c>.</summary>
public static class TypesCompteBilan
{
    public const string ActifImmobilise = "actif_immobilise";
    public const string ActifCirculant = "actif_circulant";
    public const string TresorerieActif = "tresorerie_actif";
    public const string CapitauxPropres = "capitaux_propres";
    public const string DettesLongTerme = "dettes_long_terme";
    public const string DettesCourtTerme = "dettes_court_terme";
    public const string TresoreriePassif = "tresorerie_passif";

    public static readonly IReadOnlyList<string> All =
    [
        ActifImmobilise, ActifCirculant, TresorerieActif,
        CapitauxPropres, DettesLongTerme, DettesCourtTerme, TresoreriePassif,
    ];

    /// <summary>An asset account's balance is debits − credits; every other account's is
    /// credits − debits.</summary>
    public static bool IsActif(string type) => type is ActifImmobilise or ActifCirculant or TresorerieActif;

    public static string Label(string? type) => type switch
    {
        ActifImmobilise => "Actif immobilisé",
        ActifCirculant => "Actif circulant",
        TresorerieActif => "Trésorerie actif",
        CapitauxPropres => "Capitaux propres",
        DettesLongTerme => "Dettes à long terme",
        DettesCourtTerme => "Dettes à court terme",
        TresoreriePassif => "Trésorerie passif",
        _ => type ?? "—",
    };
}

/// <summary>Values stored in <c>resultat_comptes.type_compte</c>.</summary>
public static class TypesCompteResultat
{
    public const string ProduitExploitation = "produit_exploitation";
    public const string ChargeExploitation = "charge_exploitation";
    public const string ProduitFinancier = "produit_financier";
    public const string ChargeFinanciere = "charge_financiere";
    public const string ProduitExceptionnel = "produit_exceptionnel";
    public const string ChargeExceptionnelle = "charge_exceptionnelle";

    public static readonly IReadOnlyList<string> All =
    [
        ProduitExploitation, ChargeExploitation, ProduitFinancier,
        ChargeFinanciere, ProduitExceptionnel, ChargeExceptionnelle,
    ];

    public static bool IsProduit(string type) => type is ProduitExploitation or ProduitFinancier or ProduitExceptionnel;

    /// <summary>The types nothing else in the app feeds, so their amount is typed in by hand -
    /// Bilan.jsx's <c>isManualCompte</c>.</summary>
    public static bool IsManuel(string type) =>
        type is ProduitFinancier or ChargeFinanciere or ProduitExceptionnel or ChargeExceptionnelle;

    public static string Label(string? type) => type switch
    {
        ProduitExploitation => "Produits d'exploitation",
        ChargeExploitation => "Charges d'exploitation",
        ProduitFinancier => "Produits financiers",
        ChargeFinanciere => "Charges financières",
        ProduitExceptionnel => "Produits exceptionnels",
        ChargeExceptionnelle => "Charges exceptionnelles",
        _ => type ?? "—",
    };
}

/// <summary>The <c>sous_type</c> values the automatic integrations look for. An account keeps
/// receiving its automatic amount as long as it keeps its sous-type, whatever it is renamed or
/// renumbered to.</summary>
public static class SousTypesCompte
{
    // bilan_comptes
    public const string Corporelles = "corporelles";
    public const string Amortissements = "amortissements";
    public const string StocksMatieres = "stocks_matieres";
    public const string StocksProduits = "stocks_produits";
    public const string StocksMarchandises = "stocks_marchandises";
    public const string Clients = "clients";
    public const string Resultat = "resultat";

    // resultat_comptes
    public const string VentesMarchandises = "ventes_marchandises";
    public const string Services = "services";
    public const string VariationsStocks = "variations_stocks";
    public const string Achats = "achats";
    public const string ServicesExterieurs = "services_ext";
    public const string AutresServices = "autres_services";
    public const string ImpotsTaxes = "impots_taxes";
    public const string Personnel = "personnel";
    public const string AutresCharges = "autres_charges";
}
