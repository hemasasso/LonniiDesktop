using System.Text;

namespace Lonnii.Shared.Text;

/// <summary>
/// An amount written out in French words, as an A4 reçu or facture states it under the
/// figures ("Arrêtée la présente facture à la somme de : douze mille cinq cents FCFA").
/// Traditional spelling with hyphens between the tens and units: vingt et un, soixante et
/// onze, quatre-vingts / quatre-vingt-un, deux cents / deux cent un / deux cent mille, mille
/// invariable, millions and milliards plural.
/// </summary>
public static class FrenchNumberWords
{
    private static readonly string[] Units =
    [
        "zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf",
        "dix", "onze", "douze", "treize", "quatorze", "quinze", "seize",
    ];

    private static readonly string[] Tens =
        ["", "dix", "vingt", "trente", "quarante", "cinquante", "soixante"];

    /// <summary>A whole number in words: 0 → "zéro", 1 250 → "mille deux cent cinquante".</summary>
    public static string ToWords(long number)
    {
        if (number == 0) return Units[0];
        if (number < 0) return "moins " + ToWords(-number);

        var parts = new List<string>();
        long milliards = number / 1_000_000_000, millions = number / 1_000_000 % 1000;
        long milliers = number / 1000 % 1000, rest = number % 1000;

        if (milliards > 0) parts.Add($"{ToWords(milliards)} milliard{(milliards > 1 ? "s" : "")}");
        // "million(s)" is a noun, so a cent / vingt before it keeps its plural s.
        if (millions > 0) parts.Add($"{Below1000(millions, last: true)} million{(millions > 1 ? "s" : "")}");
        // "mille" is invariable and never "un mille"; cent / vingt before it lose their s.
        if (milliers > 0) parts.Add(milliers == 1 ? "mille" : $"{Below1000(milliers, last: false)} mille");
        if (rest > 0) parts.Add(Below1000(rest, last: true));

        return string.Join(" ", parts);
    }

    /// <summary>
    /// An amount with its currency label, decimals spoken after "virgule" when there are any:
    /// 12 500 → "douze mille cinq cents FCFA"; 10.5 with "$" and 2 digits → "dix virgule
    /// cinquante $".
    /// </summary>
    public static string Amount(decimal amount, string currencyLabel, int decimalDigits = 0)
    {
        amount = Math.Round(Math.Abs(amount), Math.Max(0, decimalDigits), MidpointRounding.AwayFromZero);
        var whole = (long)Math.Truncate(amount);
        var text = new StringBuilder(ToWords(whole));

        if (decimalDigits > 0)
        {
            var fraction = (long)Math.Round((amount - whole) * (decimal)Math.Pow(10, decimalDigits));
            if (fraction > 0) text.Append(" virgule ").Append(ToWords(fraction));
        }

        if (!string.IsNullOrWhiteSpace(currencyLabel)) text.Append(' ').Append(currencyLabel.Trim());
        return text.ToString();
    }

    /// <summary>The sentence printed under the totals of an A4 document.</summary>
    /// <param name="paid">For a partly paid reçu: what was paid. With <paramref name="reste"/> above
    /// zero, the sentence also says what was paid and what is still owed.</param>
    public static string Sentence(decimal amount, string currencyLabel, int decimalDigits, bool facture,
        decimal paid = 0, decimal reste = 0, decimal avoir = 0)
    {
        var words = Amount(amount, currencyLabel, decimalDigits);
        if (facture) return $"Arrêtée la présente facture à la somme de : {words}.";
        // Overpaid, the excess kept as the customer's credit: say what was handed over and how much is owed back.
        if (avoir > 0)
            return $"Arrêté le présent reçu à la somme de : {words}. Montant versé : {Amount(paid, currencyLabel, decimalDigits)},"
                + $" dont {Amount(avoir, currencyLabel, decimalDigits)} en avoir client.";
        if (reste <= 0) return $"Arrêté le présent reçu à la somme de : {words}.";
        return $"Arrêté le présent reçu à la somme de : {words}, dont {Amount(paid, currencyLabel, decimalDigits)} versés"
            + $" et {Amount(reste, currencyLabel, decimalDigits)} restant à payer.";
    }

    /// <param name="last">Nothing but a noun (million) or the end follows: only then do
    /// "cents" and "quatre-vingts" take their plural s.</param>
    private static string Below1000(long n, bool last)
    {
        var hundreds = n / 100;
        var rest = n % 100;
        var text = new StringBuilder();

        if (hundreds > 0)
        {
            if (hundreds > 1) text.Append(Units[hundreds]).Append(' ');
            text.Append("cent");
            if (hundreds > 1 && rest == 0 && last) text.Append('s');
            if (rest > 0) text.Append(' ');
        }

        if (rest > 0) text.Append(Below100(rest, last));
        return text.ToString();
    }

    private static string Below100(long n, bool last)
    {
        if (n <= 16) return Units[n];
        if (n < 20) return "dix-" + Units[n - 10];

        var ten = n / 10;
        var unit = n % 10;

        // 70-79 and 90-99 count on from soixante / quatre-vingt: soixante-douze, quatre-vingt-dix-sept.
        if (ten is 7 or 9)
        {
            var baseWord = ten == 7 ? "soixante" : "quatre-vingt";
            var remainder = n - (ten == 7 ? 60 : 80);
            var joiner = ten == 7 && remainder == 11 ? " et " : "-";
            return baseWord + joiner + Below100(remainder, last);
        }

        if (ten == 8)
            return unit == 0 ? (last ? "quatre-vingts" : "quatre-vingt") : "quatre-vingt-" + Units[unit];

        if (unit == 0) return Tens[ten];
        return Tens[ten] + (unit == 1 ? " et un" : "-" + Units[unit]);
    }
}
