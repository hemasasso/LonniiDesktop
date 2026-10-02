using System.Globalization;
using System.Text;

namespace Lonnii.Client.Features.CustomerDisplay;

/// <summary>
/// Turns a <see cref="CustomerDisplaySnapshot"/> into the few rows of plain ASCII a pole
/// display or character LCD can show, and those rows into the bytes each protocol expects.
/// Pure functions, no I/O, so the layouts can be pinned by tests.
/// </summary>
public static class CharacterDisplayFormatter
{
    /// <summary>Exactly <paramref name="rows"/> strings of exactly <paramref name="columns"/> characters.</summary>
    public static string[] Format(CustomerDisplaySnapshot s, int columns, int rows)
    {
        columns = Math.Max(columns, 8);
        rows = Math.Max(rows, 1);

        var lines = s.Mode switch
        {
            DisplayMode.Idle => IdleRows(s, columns, rows),
            DisplayMode.Thanks => ThanksRows(s, columns, rows),
            _ => SellingRows(s, columns, rows),
        };

        // Always a full grid: a shorter line would leave the previous frame's tail on the glass.
        var grid = new string[rows];
        for (var i = 0; i < rows; i++)
            grid[i] = Fit(i < lines.Count ? lines[i] : string.Empty, columns);
        return grid;
    }

    private static List<string> IdleRows(CustomerDisplaySnapshot s, int columns, int rows)
    {
        var lines = new List<string> { Center(Ascii(s.Branding.Company), columns) };
        if (rows >= 2) lines.Add(Center("Bienvenue", columns));
        return lines;
    }

    private static List<string> ThanksRows(CustomerDisplaySnapshot s, int columns, int rows)
    {
        var change = s.Difference is > 0 ? s.Difference : null;
        var total = Money2("TOTAL", s.Total, columns);
        var closing = s.Facture ? "A regler en caisse" : "Merci, a bientot!";

        if (rows == 1) return [Center("Merci!", columns)];
        if (rows == 2) return [change is { } c ? Money2("MONNAIE", c, columns) : total, Center(closing, columns)];

        var lines = new List<string> { total };
        if (change is { } rendu) lines.Add(Money2("MONNAIE", rendu, columns));
        while (lines.Count < rows - 1) lines.Add(string.Empty);
        lines.Add(Center(closing, columns));
        return lines;
    }

    private static List<string> SellingRows(CustomerDisplaySnapshot s, int columns, int rows)
    {
        var focus = s.Lines.FirstOrDefault(l => l.Id == s.FocusLineId) ?? s.Lines.LastOrDefault();
        if (focus is null) return IdleRows(s, columns, rows);

        var total = Money2("TOTAL", s.Total, columns);
        var name = Ascii(focus.Unite is null ? focus.Name : $"{focus.Name} ({focus.Unite})");

        if (rows == 1) return [total];

        // Two rows is the common pole display: the item and its price, then the running total.
        if (rows == 2) return [Join($"{focus.Quantity}x {name}", Plain(focus.Total), columns), total];

        var lines = new List<string>
        {
            name,
            Join($"{focus.Quantity} x {Plain(focus.UnitPrice)}", Plain(focus.Total), columns),
        };

        // Spare rows (4-row LCDs) show what the cashier is collecting.
        while (lines.Count < rows - 1) lines.Add(string.Empty);
        if (rows >= 4 && s.Difference is { } diff)
        {
            lines[rows - 2] = diff >= 0
                ? Money2("MONNAIE", diff, columns)
                : Money2("MANQUE", -diff, columns);
        }

        lines.Add(total);
        return lines;
    }

    /// <summary>The bare figure, "8 500" - item lines have no room for the currency.</summary>
    private static string Plain(decimal value) => Ascii(Money.FormatPlain(value));

    /// <summary>"TOTAL" on the left and the amount on the right, with the currency when it still
    /// fits on the row and without it on a narrow one.</summary>
    private static string Money2(string label, decimal value, int columns)
    {
        var withCurrency = Ascii(Money.Format(value));
        return label.Length + 1 + withCurrency.Length <= columns
            ? Join(label, withCurrency, columns)
            : Join(label, Plain(value), columns);
    }

    /// <summary>Label on the left, value on the right; the label gives way if they do not both fit.</summary>
    private static string Join(string left, string right, int columns)
    {
        left = Ascii(left);
        right = Ascii(right);
        if (right.Length >= columns) return right[..columns];

        var room = columns - right.Length - 1;
        if (left.Length > room) left = left[..Math.Max(room, 0)];
        return left + new string(' ', columns - left.Length - right.Length) + right;
    }

    private static string Center(string text, int columns)
    {
        if (text.Length >= columns) return text[..columns];
        return new string(' ', (columns - text.Length) / 2) + text;
    }

    private static string Fit(string text, int columns) =>
        text.Length >= columns ? text[..columns] : text.PadRight(columns);

    /// <summary>Character displays have no accents: "Café" becomes "Cafe", anything else
    /// outside ASCII a question mark (the euro sign, a currency symbol, an emoji).</summary>
    public static string Ascii(string text)
    {
        var decomposed = text.Replace("€", "EUR").Replace(' ', ' ').Replace(' ', ' ')
            .Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c is >= ' ' and <= '~' ? c : '?');
        }
        return sb.ToString();
    }

    // --- Bytes ---

    /// <summary>The bytes that put <paramref name="rows"/> on a display speaking <paramref name="protocol"/>.</summary>
    public static byte[] Encode(string protocol, IReadOnlyList<string> rows)
    {
        var ascii = Encoding.ASCII;
        var bytes = new List<byte>();

        switch (protocol)
        {
            case DisplayProtocols.Cd5220:
                // ESC Q A <text> CR is the upper line, ESC Q B the lower one; the standard has no more.
                for (var i = 0; i < Math.Min(rows.Count, 2); i++)
                {
                    bytes.AddRange([0x1B, 0x51, i == 0 ? (byte)'A' : (byte)'B']);
                    bytes.AddRange(ascii.GetBytes(rows[i]));
                    bytes.Add(0x0D);
                }
                break;

            case DisplayProtocols.EscPos:
                // US $ x y puts the cursor (1-based) before each row's text.
                for (var i = 0; i < rows.Count; i++)
                {
                    bytes.AddRange([0x1F, 0x24, 0x01, (byte)(i + 1)]);
                    bytes.AddRange(ascii.GetBytes(rows[i]));
                }
                break;

            default:
                // 0x0C starts a frame; every row, including the last, ends with a line feed.
                bytes.Add(0x0C);
                foreach (var row in rows)
                {
                    bytes.AddRange(ascii.GetBytes(row));
                    bytes.Add(0x0A);
                }
                break;
        }

        return [.. bytes];
    }

    /// <summary>Sent once when a port or socket opens: wipes the display and resets its state.</summary>
    public static byte[] Initialise(string protocol) => protocol switch
    {
        DisplayProtocols.Cd5220 => [0x1B, 0x40],
        DisplayProtocols.EscPos => [0x1B, 0x40, 0x0C],
        _ => [],
    };
}
