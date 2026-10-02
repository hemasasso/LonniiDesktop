using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Ventes;

public sealed record ReceiptLine(string Name, int Quantity, decimal UnitPrice, decimal Total, string? Unite = null)
{
    public decimal Discount => Math.Max(0, UnitPrice * Quantity - Total);
}

public sealed record ReceiptPayment(decimal Amount, string? Mode, DateTime Date, string? By);

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
    bool ForceFacture = false)
{
    /// <summary>An unpaid sale prints as a facture to settle at the till; anything else as a
    /// reçu - unless the till asked for a facture anyway, which ForceFacture always honours
    /// regardless of payment status. That is what a partially-paid ("partiel") sale's own
    /// "Imprimer facture" action relies on: without it, IsFacture would be false (StatutPaiement
    /// is "partiel", not "en_attente") and the document would silently become a reçu instead -
    /// forcing it keeps that facture the ORIGINAL invoice, the full amount with no payment
    /// lines, alongside the sale's own separate reçu for what has actually been paid.</summary>
    public bool IsFacture => StatutPaiement == "en_attente" || ForceFacture;

    /// <summary>The pre-discount sum, so the discounts can be shown as their own line.</summary>
    public decimal RawSubtotal => Lines.Sum(l => l.UnitPrice * l.Quantity);

    /// <summary>The total before any TVA the till added - what the discounts are measured against.</summary>
    public decimal NetTotal => MontantTotal - (TvaAmount ?? 0);

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
            vente.Items.Select(i => new ReceiptLine(i.NomProduit, i.Quantite, i.PrixUnitaire, i.PrixTotal, i.Unite)).ToList(),
            vente.MontantTotal,
            vente.MontantPaye,
            vente.MontantRestant,
            vente.StatutPaiement,
            vente.ModePaiement,
            vente.AvoirAmount,
            vente.IsAvoirSolded,
            (vente.Paiements ?? [])
                .Select(p => new ReceiptPayment(p.Montant, p.ModePaiement, p.DatePaiement.ToLocalTime(), p.CreatedByName))
                .ToList(),
            vente.TvaRate,
            vente.TvaAmount,
            forceFacture);
    }

    /// <summary>
    /// The settings editor's stand-in sale. The reçu version deliberately exercises every
    /// optional section at once - a discounted line, a second cashier, two payments and an
    /// overpayment - so a shop sees each switch do something before it prints for real.
    /// </summary>
    /// <param name="addedTvaRate">The rate the till would add on top, so the sample shows the
    /// same TVA lines a real sale would; null when prices already include TVA.</param>
    public static ReceiptData Sample(bool facture, decimal? addedTvaRate = null)
    {
        ReceiptLine[] lines =
        [
            new("Chemise Oxford", 2, 8500m, 17000m),
            new("Pantalon Chino", 1, 12500m, 12500m),
            new("Ceinture Cuir", 1, 4000m, 3500m),
        ];
        var tva = addedTvaRate is { } rate ? TvaModes.Added(lines.Sum(l => l.Total), rate) : (decimal?)null;
        var total = lines.Sum(l => l.Total) + (tva ?? 0);
        var now = DateTime.Now;

        if (facture)
        {
            return new ReceiptData("V2026-00042", now, "Client Exemple", "690 00 00 00", "client@exemple.com",
                "Jean Dupont", null, lines, total, 0m, total, "en_attente", null, 0m, false, [], addedTvaRate, tva);
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
            addedTvaRate, tva);
    }
}
