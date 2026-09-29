using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Lonnii.Shared.Security;

namespace Lonnii.Client;

/// <summary>
/// Formats amounts for display, e.g. <c>1 000 FCFA</c>: a plain space for the thousands
/// separator (the way it is written locally) and no decimal places, since the franc has
/// none in practice.
///
/// <see cref="Label"/> and <see cref="DecimalDigits"/> are the only two knobs a switch to
/// another currency would need; every view goes through <see cref="Format"/> or
/// <see cref="CurrencyConverter"/> rather than formatting amounts itself, so that switch
/// happens in this one place.
/// </summary>
public static class Money
{
    public static string Label { get; set; } = "FCFA";

    /// <summary>True when <see cref="Label"/> goes before the amount (<c>$1 000</c>), false
    /// when it follows it (<c>1 000 FCFA</c>). A workspace setting, like the label.</summary>
    public static bool LabelBefore { get; set; }

    public static int DecimalDigits { get; set; } = 0;

    public static string Format(decimal amount) => WithLabel(amount, FormatPlain(Math.Abs(amount)));

    /// <summary>
    /// Attaches <see cref="Label"/> to an already-formatted figure on the configured side.
    /// Takes the unsigned figure so a negative prefixed amount reads <c>-$1 000</c> rather than
    /// <c>$-1 000</c>. A single-character symbol such as <c>$</c> or <c>€</c> sits against the
    /// digits the way it is normally written; a word such as <c>USD</c> keeps its space.
    /// </summary>
    public static string WithLabel(decimal amount, string unsignedFigure) =>
        WithLabel(amount, unsignedFigure, Label, LabelBefore);

    /// <summary>The same, for a label and position not yet applied - Paramètres' preview.</summary>
    public static string WithLabel(decimal amount, string unsignedFigure, string label, bool before)
    {
        var sign = amount < 0 ? "-" : string.Empty;
        if (!before) return $"{sign}{unsignedFigure} {label}";

        var gap = label.Length == 1 && !char.IsLetter(label[0]) ? string.Empty : " ";
        return $"{sign}{label}{gap}{unsignedFigure}";
    }

    /// <summary>The same grouping as <see cref="Format"/>, without the currency label - for
    /// editable fields (price, quantity) where a suffix would get in the way of typing.</summary>
    public static string FormatPlain(decimal amount)
    {
        // NumberDecimalSeparator must be set explicitly - a bare NumberFormatInfo defaults to
        // "." (invariant), which is wrong for French: whenever an amount does carry a decimal
        // part (a manually-priced item, a percentage-derived line), it must read "1 500,50"
        // Français, not "1 500.5".
        var format = new NumberFormatInfo
        {
            NumberGroupSeparator = " ", NumberDecimalSeparator = ",", NumberDecimalDigits = DecimalDigits,
        };
        return amount.ToString("#,0.##", format);
    }

    /// <summary>
    /// Groups the same way as <see cref="FormatPlain(decimal)"/> but with a fixed number of
    /// decimal digits rather than trimming trailing zeros - for a margin or percentage figure
    /// that should read the same width whether or not the amount happens to be a whole
    /// number, e.g. <c>1 000,25</c> rather than <c>1 000</c> or <c>1 000,2</c>.
    /// </summary>
    public static string FormatPlain(decimal amount, int decimalDigits)
    {
        var format = new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalSeparator = "," };
        var pattern = decimalDigits > 0 ? "#,0." + new string('0', decimalDigits) : "#,0";
        return amount.ToString(pattern, format);
    }

    /// <summary>Groups a whole number the same way, for quantity and threshold fields.</summary>
    public static string FormatPlain(int amount) =>
        amount.ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = " " });

    /// <summary>
    /// Parses text a till operator typed back into a number, accepting the group spaces
    /// <see cref="FormatPlain(decimal)"/> writes and either decimal separator.
    /// </summary>
    public static bool TryParse(string text, out decimal value) =>
        decimal.TryParse(StripGrouping(text).Replace(',', '.'),
            NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    public static bool TryParse(string text, out int value) =>
        int.TryParse(StripGrouping(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string StripGrouping(string text) =>
        text.Replace(" ", "").Replace(" ", "").Trim();
}

/// <summary>
/// Rounds a LiveCharts axis step up to a "nice" 1/2/5 × 10^n number, so a value axis gets
/// roughly <see cref="NiceStep"/>'s <c>targetTicks</c> separators instead of the
/// one-tick-per-raw-unit spacing LiveCharts falls back to for currency-sized ranges - a
/// "Chiffre d'Affaires" or "Produits Immobilisant le Plus de Valeur" axis spanning 270 000
/// was defaulting to a tick roughly every 10 000, cramming every label together.
/// </summary>
public static class ChartAxis
{
    public static double NiceStep(double max, int targetTicks = 6)
    {
        if (!double.IsFinite(max) || max <= 0) return 1;

        var rawStep = max / targetTicks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var normalized = rawStep / magnitude;
        var niceNormalized = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;

        return niceNormalized * magnitude;
    }
}

/// <summary>
/// Shortens a vendor/cashier name for a compact column - "Sassama Hema" becomes "S. Hema" -
/// so a list of many sales stays easy to scan. The full name is never lost: every place this
/// is used keeps it as the element's ToolTip, and "Détails" (<c>VenteDetailDialog</c>) shows
/// it in full rather than abbreviating.
/// </summary>
public static class PersonName
{
    public static string Abbreviate(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return fullName ?? string.Empty;

        var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? fullName : $"{char.ToUpper(parts[0][0])}. {string.Join(' ', parts.Skip(1))}";
    }
}

/// <summary>
/// Picks <c>ButtonBase</c>'s string-only content template only when a Button's <c>Content</c>
/// is actually a plain string, and returns null (WPF's own default presentation) otherwise -
/// in particular for a composite Content such as an icon+label StackPanel, which must render
/// directly rather than being run through a DataTemplate whose <c>{Binding}</c> would just
/// show that StackPanel's <c>ToString()</c>. An unconditional <c>ContentTemplate</c> Setter
/// cannot express this: unlike WPF's own implicit-DataTemplate lookup, which is skipped
/// entirely for UIElement content, an explicit ContentTemplate always applies regardless of
/// the Content's runtime type.
/// </summary>
public class ButtonStringContentTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StringTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is string ? StringTemplate : null;
}

/// <summary>Wraps <see cref="Money.Format"/> for use directly in a binding.</summary>
public class CurrencyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        decimal amount => Money.Format(amount),
        int amount => Money.Format(amount),
        _ => string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Amounts are display-only here.");
}

/// <summary>
/// The letter shown in a workspace card's avatar circle, standing in for the logo the web
/// app displays. Returns a single uppercase character, or a bullet when the name is empty,
/// so the circle is never blank.
/// </summary>
public class InitialConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = (value as string)?.TrimStart();
        return string.IsNullOrEmpty(name)
            ? "•"
            : char.ToUpper(name[0], culture).ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Initials are display-only.");
}

/// <summary>
/// True when a product's purchase price is missing (null or zero) - the one figure a shop
/// forgetting to fill in silently turns every margin report into an estimate (see
/// <c>MargesView.EstimateNotice</c>). Bound from a product list row's <c>CostPrice</c> so the
/// gap can be flagged right where it is fixed, in Gestion de Stock, rather than only after the
/// fact in Analyse des Marges.
/// </summary>
public class CostPriceMissingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not decimal cost || cost <= 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Display-only.");
}

/// <summary>
/// Turns a stored role such as <c>member</c> into its French label.
///
/// The role is stored in English so the schema matches Lonnii Business and a future
/// import needs no translation; only what appears on screen is localised.
/// </summary>
public class RoleLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        GroupRoles.DisplayName(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Role labels are display-only.");
}
