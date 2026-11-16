using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Ventes;

public sealed record ReceiptLine(string Name, int Quantity, decimal UnitPrice, decimal Total, string? Unite = null,
    int Quarts = 0, decimal? PrixFraction = null, decimal Emballages = 0, int? Contenance = null, decimal Credit = 0)
{
    /// <summary>Before any discount: the whole units plus the fraction's own price.</summary>
    public decimal Gross => UnitPrice * Quantity + (PrixFraction ?? 0);

    public decimal Discount => Math.Max(0, Gross - Total);

    /// <summary>"3", "1 + 1/2" or "1/2".</summary>
    public string QuantityLabel => SaleCharges.QuantityLabel(Quantity, Quarts);
}

public sealed record ReceiptPayment(decimal Amount, string? Mode, DateTime Date, string? By, string? Reference = null);

/// <summary>
/// What a printed reçu or facture shows about one sale, independent of where it came from -
/// a real <see cref="VenteDto"/> or the settings editor's sample. Dates are already local.
/// </summary>
public sealed record ReceiptData(
    string Numero,
    DateTime Date,
    string? ClientNom,
    string? ClientTelephone,
    string? ClientEmail,
    string? VendeurNom,
    string? CaissierNom,
    IReadOnlyList<ReceiptLine> Lines,
    decimal MontantTotal,
    decimal MontantPaye,
    decimal MontantRestant,
    string StatutPaiement,
    string? ModePaiement,
    decimal AvoirAmount,
    bool IsAvoirSolded,
    IReadOnlyList<ReceiptPayment> Paiements,
    decimal? TvaRate = null,
    decimal? TvaAmount = null,
    bool ForceFacture = false,
    string? ServeurNom = null,
    decimal? PrelevementRate = null,
    decimal? PrelevementAmount = null,
    decimal? EffortGuerreAmount = null,
    decimal? ConsigneAmount = null,
    decimal? LivraisonAmount = null,
    decimal? EmballagesPris = null,
    decimal? EmballagesRendus = null,
    decimal? ConsigneRendue = null,
    IReadOnlyList<EmballageRenduDto>? Rendus = null,
    decimal? EmballagesCredit = null,
    decimal? EmballagesCreditRestant = null,
    decimal? EmballagesDusAvant = null)
{
    /// <summary>"Emballages : pris 2 caisses · déduit du crédit 2 caisses · rapportés 0 caisse ·
    /// crédit restant 4 caisses", or null when not tracked.</summary>
    public string? EmballagesLine(ReceiptSettingsDto s)
    {
        if (EmballagesPris is null && EmballagesRendus is null) return null;
        var credit = EmballagesCredit ?? 0;
        var line = $"Emballages : pris {PrisLabel(s)}";
        if (credit > 0) line += $" · déduit du crédit {s.Emballages(credit, SharedLineUnit)}";
        line += $" · rapportés {RendusLabel(s)}";
        var reste = (EmballagesPris ?? 0) - credit - (EmballagesRendus ?? 0);
        if (reste > 0) line += $" · à rapporter {ResteLabel(s, reste)}";
        else if (reste == 0 && credit > 0 && (EmballagesCreditRestant ?? 0) == 0) line += " · rien à rapporter";
        if (AnciensRendus(reste) is > 0 and var anciens) line += $" · dont {s.Emballages(anciens, SharedLineUnit)} dus d'avant";
        var totalDu = TotalDuLine(s);
        if (EmballagesCreditRestant is > 0) line += $" · crédit restant {s.Emballages(EmballagesCreditRestant.Value, SharedLineUnit)}";
        else if (reste < 0 && totalDu is null) line += " · rien à rapporter";
        if (totalDu is not null) line += $" · {totalDu}";
        return line;
    }

    /// <summary>One column of the A4/A5 emballages block: "Emballages de 12 :" with what was
    /// taken and brought back of that size, e.g. "(1 cs (Cast), 1/4 cs (Braf))".</summary>
    public sealed record EmballageColumn(string Header, string Pris, string Rendus);

    /// <summary>The emballages block by size - only the sizes in this sale - plus a note for the
    /// credit / what is left to bring back. Null when the lines carry no detail (older sales).</summary>
    public (IReadOnlyList<EmballageColumn> Columns, string? Note)? EmballagesTable(ReceiptSettingsDto s)
    {
        if (EmballagesPris is null && EmballagesRendus is null) return null;
        var lines = Lines.Where(l => l.Emballages > 0).ToList();
        if (lines.Count == 0 || lines.Sum(l => l.Emballages) != (EmballagesPris ?? 0)) return null;
        if ((EmballagesRendus ?? 0) > 0 && !RendusDetailed) return null;

        static string Short(string? name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return "";
            n = n.Length > 4 ? n[..4] : n;
            return char.ToUpper(n[0]) + n[1..].ToLower();
        }
        string Parts(IEnumerable<(decimal Count, string? Name)> parts, string abbr)
        {
            var list = parts.Where(p => p.Count > 0).GroupBy(p => Short(p.Name))
                .Select(g => g.Key.Length > 0 ? $"{SaleCharges.CountLabel(g.Sum(p => p.Count))} {abbr} ({g.Key})"
                                               : $"{SaleCharges.CountLabel(g.Sum(p => p.Count))} {abbr}")
                .ToList();
            return list.Count == 0 ? "(0)" : $"({string.Join(", ", list)})";
        }

        var rendus = Rendus ?? [];
        var sizes = lines.Select(l => l.Contenance is > 0 ? l.Contenance : null)
            .Concat(rendus.Select(r => r.Contenance is > 0 ? r.Contenance : null))
            .Distinct().OrderBy(c => c ?? int.MaxValue).ToList();
        var columns = sizes.Select(size =>
        {
            // Each product keeps its own display unit; when a size's products share one it heads the
            // column, otherwise the generic "emballage" does (a sale may mix caisses and casiers).
            var unit = UnitForLines(lines.Where(l => (l.Contenance is > 0 ? l.Contenance : null) == size));
            var abbr = unit == "caisse" ? "cs" : unit.Length <= 3 ? unit : $"{unit[0]}{unit[^1]}";
            var plural = unit.EndsWith('s') || unit.EndsWith('x') ? unit : unit + "s";
            var header = char.ToUpper(plural[0]) + plural[1..];
            return new EmballageColumn(
                size is { } n ? $"{header} de {n} :" : $"{header} :",
                Parts(lines.Where(l => (l.Contenance is > 0 ? l.Contenance : null) == size).Select(l => (l.Emballages, (string?)l.Name)), abbr),
                Parts(rendus.Where(r => (r.Contenance is > 0 ? r.Contenance : null) == size).Select(r => (r.Quantite, (string?)r.NomProduit)), abbr));
        }).ToList();

        // The rest of the story in one line: credit used, what is left to bring back, credit left.
        var credit = EmballagesCredit ?? 0;
        var notes = new List<string>();
        if (credit > 0) notes.Add($"déduit du crédit {s.Emballages(credit, SharedLineUnit)}");
        var reste = (EmballagesPris ?? 0) - credit - (EmballagesRendus ?? 0);
        if (reste > 0) notes.Add($"à rapporter {ResteLabel(s, reste)}");
        if (AnciensRendus(reste) is > 0 and var anciens) notes.Add($"dont {s.Emballages(anciens, SharedLineUnit)} dus d'avant");
        var totalDu = TotalDuLine(s);
        if (EmballagesCreditRestant is > 0) notes.Add($"crédit restant {s.Emballages(EmballagesCreditRestant.Value, SharedLineUnit)}");
        else if (reste < 0 && totalDu is null) notes.Add("rien à rapporter");
        // The client's whole debt in empties, old ones included: what the user asked to see on the facture.
        if (totalDu is not null) notes.Add(totalDu);
        var note = notes.Count == 0 ? null : char.ToUpper(notes[0][0]) + string.Join(" · ", notes)[1..];
        return (columns, note);
    }

    private bool RendusDetailed => Rendus is { Count: > 0 } && Rendus.Sum(r => r.Quantite) == (EmballagesRendus ?? 0);

    /// <summary>
    /// Of the empties brought back beyond this sale (<paramref name="reste"/> below 0), the ones that
    /// paid off what the client already owed: the server keeps as credit only what is left after those,
    /// so the excess minus the credit recorded is the old debt repaid. (A client who also had unused
    /// credit from before is the rare case where this reads a little low.)
    /// </summary>
    private decimal AnciensRendus(decimal reste) =>
        reste >= 0 ? 0
        : EmballagesDusAvant is { } avant ? Math.Min(Math.Max(0, avant), -reste)
        : Math.Max(0, -reste - (EmballagesCreditRestant ?? 0));

    /// <summary>
    /// "Total à rapporter : 1 + 1/2 caisses (dont 1/2 d'avant)" - the client's whole debt in empties after
    /// this sale, old ones included, when they owed some before. Null otherwise (the sale's own line says it all).
    /// </summary>
    private string? TotalDuLine(ReceiptSettingsDto s)
    {
        var avant = EmballagesDusAvant ?? 0;
        if (avant <= 0) return null;
        var total = avant + (EmballagesPris ?? 0) - (EmballagesRendus ?? 0);
        if (total <= 0) return null;
        var anciens = Math.Min(avant, total);
        return $"Total à rapporter : {s.Emballages(total, SharedLineUnit)} (dont {s.Emballages(anciens, SharedLineUnit)} d'avant)";
    }

    /// <summary>The unit for a total over the sale's emballages: the products' shared display unit
    /// ("caisse"), or null (= "emballage") when they differ.</summary>
    private string? SharedLineUnit => SaleCharges.SharedUnit(Lines.Where(l => l.Emballages > 0).Select(l => l.Unite));

    /// <summary>A product's own display unit, from its sale line; null (= generic "emballage") when
    /// this sale has no line for it.</summary>
    private string? UnitOf(string? name) =>
        Lines.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase))?.Unite;

    /// <summary>The one display unit shared by these lines' products, or "emballage" when they differ
    /// or none is set - a column header or total that spans several units has no single word.</summary>
    private static string UnitForLines(IEnumerable<ReceiptLine> lines)
    {
        var units = lines.Select(l => SaleCharges.RealUnit(l.Unite)).Where(u => u is not null).Distinct().ToList();
        return units.Count == 1 ? units[0]!.ToLowerInvariant() : "emballage";
    }

    /// <summary>"1 caisse de 16 (BRAFASO), 3/4 casier de 24 (Castel)" - each product named, in its own unit.</summary>
    private string RendusLabel(ReceiptSettingsDto s) => RendusDetailed
        ? s.EmballagesDetail(Rendus!.Select(r => (r.Quantite, r.Contenance, (string?)r.NomProduit, UnitOf(r.NomProduit))))
        : s.Emballages(EmballagesRendus ?? 0, SharedLineUnit);

    /// <summary>What is left of each product taken now, once its credit and its own empties are deducted.</summary>
    private string ResteLabel(ReceiptSettingsDto s, decimal reste)
    {
        var lines = Lines.Where(l => l.Emballages > 0).ToList();
        if (lines.Count == 0 || ((EmballagesRendus ?? 0) > 0 && !RendusDetailed)) return s.Emballages(reste, SharedLineUnit);
        var left = lines
            .GroupBy(l => (l.Name, l.Contenance, l.Unite))
            .Select(g => (Count: g.Sum(l => l.Emballages - l.Credit)
                    - (Rendus ?? []).Where(r => string.Equals(r.NomProduit, g.Key.Name, StringComparison.CurrentCultureIgnoreCase)).Sum(r => r.Quantite),
                g.Key.Contenance, Name: (string?)g.Key.Name, Unit: (string?)g.Key.Unite))
            .Where(x => x.Count > 0).ToList();
        // Only when the detail adds up; otherwise (old debts paid back too) the plain count.
        return left.Count > 0 && left.Sum(x => x.Count) == reste ? s.EmballagesDetail(left) : s.Emballages(reste, SharedLineUnit);
    }

    /// <summary>"1 caisse de 12 + 1 caisse (Castel)" when the lines carry their emballages.</summary>
    private string PrisLabel(ReceiptSettingsDto s) =>
        Lines.Any(l => l.Emballages > 0) && Lines.Sum(l => l.Emballages) == (EmballagesPris ?? 0)
            ? s.EmballagesDetail(Lines.Where(l => l.Emballages > 0).Select(l => (l.Emballages, l.Contenance, (string?)l.Name, (string?)l.Unite)))
            : s.Emballages(EmballagesPris ?? 0, SharedLineUnit);

    /// <summary>Everything added after the TVA: prélèvement, effort de guerre, consigne, livraison.</summary>
    public decimal ChargesTotal =>
        (PrelevementAmount ?? 0) + (EffortGuerreAmount ?? 0) + (ConsigneAmount ?? 0) + (LivraisonAmount ?? 0)
        - (ConsigneRendue ?? 0);

    /// <summary>True when the document must break the total down beyond the TVA.</summary>
    public bool HasCharges => ChargesTotal != 0 || ConsigneRendue is > 0;

    /// <summary>An unpaid sale prints as a facture to settle at the till; anything else as a
    /// reçu - unless the till asked for a facture anyway, which ForceFacture always honours
    /// regardless of payment status. That is what a partially-paid ("partiel") sale's own
    /// "Imprimer facture" action relies on: without it, IsFacture would be false (StatutPaiement
    /// is "partiel", not "en_attente") and the document would silently become a reçu instead -
    /// forcing it keeps that facture the ORIGINAL invoice, the full amount with no payment
    /// lines, alongside the sale's own separate reçu for what has actually been paid.</summary>
    public bool IsFacture => StatutPaiement == "en_attente" || ForceFacture;

    /// <summary>The pre-discount sum, so the discounts can be shown as their own line.</summary>
    public decimal RawSubtotal => Lines.Sum(l => l.Gross);

    /// <summary>The total before any TVA the till added - what the discounts are measured against.</summary>
    public decimal NetTotal => MontantTotal - (TvaAmount ?? 0) - ChargesTotal;

    /// <summary>Per-line discounts plus any global one taken off the whole sale.</summary>
    public decimal TotalRemise
    {
        get
        {
            var linesTotal = Lines.Sum(l => l.Total);
            return RawSubtotal - linesTotal + Math.Max(0, linesTotal - NetTotal);
        }
    }

    public static ReceiptData FromVente(VenteDto vente, bool forceFacture = false)
    {
        // The last payment's recorder is who actually took the money. Always passed along:
        // whether it prints is the "Caissier" section's own switch, so a shop that ticks it
        // sees the name even when the seller and the cashier are the same person.
        var caissier = vente.Paiements?.LastOrDefault()?.CreatedByName;

        return new ReceiptData(
            vente.NumeroVente,
            vente.DateVente.ToLocalTime(),
            vente.ClientNom,
            vente.ClientTelephone,
            vente.ClientEmail,
            vente.VendeurNom,
            string.IsNullOrWhiteSpace(caissier) ? null : caissier,
            vente.Items.Select(i => new ReceiptLine(i.NomProduit, i.Quantite, i.PrixUnitaire, i.PrixTotal, i.Unite,
                i.Quarts ?? 0, i.PrixFraction, i.EmballagesPris ?? 0, i.EmballageContenance, i.EmballagesCredit ?? 0)).ToList(),
            vente.MontantTotal,
            vente.MontantPaye,
            vente.MontantRestant,
            vente.StatutPaiement,
            vente.ModePaiement,
            vente.AvoirAmount,
            vente.IsAvoirSolded,
            (vente.Paiements ?? [])
                .Select(p => new ReceiptPayment(p.Montant, p.ModePaiement, p.DatePaiement.ToLocalTime(), p.CreatedByName, p.Reference))
                .ToList(),
            vente.TvaRate,
            vente.TvaAmount,
            forceFacture,
            vente.ServeurNom,
            vente.PrelevementRate,
            vente.PrelevementAmount,
            vente.EffortGuerreAmount,
            vente.ConsigneAmount,
            vente.LivraisonAmount,
            vente.EmballagesPris,
            vente.EmballagesRendus,
            vente.ConsigneRendue,
            vente.Rendus,
            vente.EmballagesCredit,
            vente.EmballagesCreditRestant,
            vente.EmballagesDusAvant);
    }

    /// <summary>
    /// The settings editor's stand-in sale. The reçu version deliberately exercises every
    /// optional section at once - a discounted line, a second cashier, two payments and an
    /// overpayment - so a shop sees each switch do something before it prints for real.
    /// </summary>
    /// <param name="addedTvaRate">The rate the till would add on top, so the sample shows the
    /// same TVA lines a real sale would; null when prices already include TVA.</param>
    public static ReceiptData Sample(bool facture, decimal? addedTvaRate = null, decimal? prelevementRate = null,
        decimal? tvaRate = null, string? tvaMode = null, bool prelevementApresTva = false)
    {
        ReceiptLine[] lines =
        [
            new("Chemise Oxford", 2, 8500m, 17000m),
            new("Pantalon Chino", 1, 12500m, 12500m),
            new("Ceinture Cuir", 1, 4000m, 3500m),
        ];
        var net = lines.Sum(l => l.Total);
        var tva = addedTvaRate is { } rate ? TvaModes.Added(net, rate) : (decimal?)null;
        decimal? prelevement = prelevementRate is > 0
            ? SaleCharges.Prelevement(net, prelevementRate.Value, tvaRate, tvaMode, prelevementApresTva) : null;
        if (prelevement is 0) prelevement = null;
        var total = net + (tva ?? 0) + (prelevement ?? 0);
        var now = DateTime.Now;

        if (facture)
        {
            return new ReceiptData("V2026-00042", now, "Client Exemple", "690 00 00 00", "client@exemple.com",
                "Jean Dupont", null, lines, total, 0m, total, "en_attente", null, 0m, false, [], addedTvaRate, tva, ServeurNom: "Koffi Yao",
                PrelevementRate: prelevement is null ? null : prelevementRate, PrelevementAmount: prelevement);
        }

        const decimal avoir = 5000m;
        ReceiptPayment[] paiements =
        [
            new(20000m, "cash", now.AddDays(-2), "Jean Dupont"),
            new(total + avoir - 20000m, "mobile_money", now, "Awa Diallo"),
        ];
        var paye = paiements.Sum(p => p.Amount);

        return new ReceiptData("V2026-00042", now, "Client Exemple", "690 00 00 00", "client@exemple.com",
            "Jean Dupont", "Awa Diallo", lines, total, paye, 0m, "paye", "cash", avoir, false, paiements,
            addedTvaRate, tva, ServeurNom: "Koffi Yao",
            PrelevementRate: prelevement is null ? null : prelevementRate, PrelevementAmount: prelevement);
    }
}
