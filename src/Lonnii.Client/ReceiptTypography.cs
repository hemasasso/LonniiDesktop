using Lonnii.Shared.Contracts;

namespace Lonnii.Client;

/// <summary>
/// Turns a workspace's configured base font size into the individual sizes a printed reçu
/// or facture uses.
///
/// <para>
/// The printed document and the settings editor's preview both go through here, which is
/// the only thing that keeps them the same document. Every nominal size below is what
/// <c>VenteReceiptDialog</c> hard-coded before the sizes became configurable, so a
/// workspace still on the default <see cref="ReceiptSettingsDefaults.FontSize"/> of 11
/// prints exactly what it printed before.
/// </para>
/// </summary>
public static class ReceiptTypography
{
    /// <summary>The base size the nominal sizes below were chosen against.</summary>
    private const double Reference = ReceiptSettingsDefaults.FontSize;

    /// <summary>
    /// A nominal size scaled to the workspace's chosen base. Floored at 6pt: below that the
    /// item table stops being readable at all, and a receipt nobody can read is worse than
    /// one that ignored the setting.
    /// </summary>
    public static double Scale(double nominal, int fontSize) =>
        Math.Max(6, Math.Round(nominal * fontSize / Reference, 1));

    /// <summary>Company name, above the document title.</summary>
    public static double Company(int fontSize) => Scale(16, fontSize);

    /// <summary>Sale number, date, and the client/vendeur lines.</summary>
    public static double Meta(int fontSize) => Scale(11, fontSize);

    public static double Body(int fontSize) => Scale(12, fontSize);

    /// <summary>The item table: its header, its rows, and the payment-history table.</summary>
    public static double Table(int fontSize) => Scale(11, fontSize);

    public static double TableSmall(int fontSize) => Scale(10, fontSize);

    /// <summary>The boxed grand total.</summary>
    public static double Total(int fontSize) => Scale(14, fontSize);
}
