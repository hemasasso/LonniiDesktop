using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Printing;

public sealed record ReceiptLine(string Name, int Quantity, decimal UnitPrice, decimal Total)
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
    IReadOnlyList<ReceiptPayment> Paiements)
{
    /// <summary>An unpaid sale prints as a facture to settle at the till; anything else as a reçu.</summary>
    public bool IsFacture => StatutPaiement == "en_attente";

    /// <summary>The pre-discount sum, so the discounts can be shown as their own line.</summary>
    public decimal RawSubtotal => Lines.Sum(l => l.UnitPrice * l.Quantity);

    /// <summary>Per-line discounts plus any global one taken off the whole sale.</summary>
    public decimal TotalRemise
    {
        get
        {
            var linesTotal = Lines.Sum(l => l.Total);
            return RawSubtotal - linesTotal + Math.Max(0, linesTotal - MontantTotal);
        }
    }

    public static ReceiptData FromVente(VenteDto vente)
    {
        // The last payment's recorder is who actually took the money. Only named when it is
        // someone other than the seller, or every ordinary sale would print the same name twice.
        var caissier = vente.Paiements?.LastOrDefault()?.CreatedByName;

        return new ReceiptData(
            vente.NumeroVente,
            vente.DateVente.ToLocalTime(),
            vente.ClientNom,
            vente.ClientTelephone,
            vente.ClientEmail,
            vente.VendeurNom,
            !string.IsNullOrWhiteSpace(caissier) && caissier != vente.VendeurNom ? caissier : null,
            vente.Items.Select(i => new ReceiptLine(i.NomProduit, i.Quantite, i.PrixUnitaire, i.PrixTotal)).ToList(),
            vente.MontantTotal,
            vente.MontantPaye,
            vente.MontantRestant,
            vente.StatutPaiement,
            vente.ModePaiement,
            vente.AvoirAmount,
            vente.IsAvoirSolded,
            (vente.Paiements ?? [])
                .Select(p => new ReceiptPayment(p.Montant, p.ModePaiement, p.DatePaiement.ToLocalTime(), p.CreatedByName))
                .ToList());
    }

    /// <summary>
    /// The settings editor's stand-in sale. The reçu version deliberately exercises every
    /// optional section at once - a discounted line, a second cashier, two payments and an
    /// overpayment - so a shop sees each switch do something before it prints for real.
    /// </summary>
    public static ReceiptData Sample(bool facture)
    {
        ReceiptLine[] lines =
        [
            new("Chemise Oxford", 2, 8500m, 17000m),
            new("Pantalon Chino", 1, 12500m, 12500m),
            new("Ceinture Cuir", 1, 4000m, 3500m),
        ];
        var total = lines.Sum(l => l.Total);
        var now = DateTime.Now;

        if (facture)
        {
            return new ReceiptData("V2026-00042", now, "Client Exemple", "690 00 00 00", "client@exemple.com",
                "Jean Dupont", null, lines, total, 0m, total, "en_attente", null, 0m, false, []);
        }

        ReceiptPayment[] paiements =
        [
            new(20000m, "cash", now.AddDays(-2), "Jean Dupont"),
            new(18000m, "mobile_money", now, "Awa Diallo"),
        ];
        var paye = paiements.Sum(p => p.Amount);

        return new ReceiptData("V2026-00042", now, "Client Exemple", "690 00 00 00", "client@exemple.com",
            "Jean Dupont", "Awa Diallo", lines, total, paye, 0m, "paye", "cash", paye - total, false, paiements);
    }
}
