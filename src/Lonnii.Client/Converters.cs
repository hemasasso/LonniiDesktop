using System.Globalization;
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

    public static int DecimalDigits { get; set; } = 0;

    public static string Format(decimal amount) => $"{FormatPlain(amount)} {Label}";

    /// <summary>The same grouping as <see cref="Format"/>, without the currency label - for
    /// editable fields (price, quantity) where a suffix would get in the way of typing.</summary>
    public static string FormatPlain(decimal amount)
    {
        var format = new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalDigits = DecimalDigits };
        return amount.ToString("#,0.##", format);
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
