using System.Windows;
using Lonnii.Client.Printing;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Shows a sale as a printable document. An unpaid sale prints as a FACTURE the client
/// settles later at the till, a paid one as a REÇU - decided from the sale's current status,
/// so a receipt reopened later reflects what the sale is now - unless the Action button's
/// "Facture" choice explicitly asked for one anyway, for a customer who wants an
/// invoice-formatted copy of an already-paid sale. Each document uses the layout and sections
/// chosen in Paramètres → "Paramètre Reçu et Facture"; <see cref="ReceiptDocument"/> draws it.
///
/// The dialog is handed the settings rather than fetching them, so opening it stays
/// synchronous and a finished sale never waits on the network; <see cref="ShowForAsync"/> is
/// the way callers get them.
/// </summary>
public partial class VenteReceiptDialog : Window
{
    private readonly Func<bool, System.Windows.Controls.Border> _build;
    private readonly bool _singlePage;
    private readonly string _jobName;

    /// <summary>
    /// Loads the workspace's receipt configuration - cached after the first call - and shows
    /// the document. The single entry point, so no call site can accidentally print with the
    /// built-in defaults while the shop has its own configured.
    /// </summary>
    public static async Task ShowForAsync(VenteDto vente, AppSession session, Window? owner, bool forceFacture = false)
    {
        var settings = await session.GetReceiptSettingsAsync();

        new VenteReceiptDialog(ReceiptData.FromVente(vente, forceFacture), settings, session.ReceiptLogo,
            session.ReceiptQrCode, session.Groupe?.Nom ?? "Lonnii")
        {
            Owner = owner,
        }.ShowDialog();
    }

    /// <summary>Also used by the settings editor to show and test-print its sample sale at
    /// full size, with settings that have not been saved yet.</summary>
    public VenteReceiptDialog(
        ReceiptData data, ReceiptSettingsDto settings, byte[]? logo, byte[]? qrCode, string fallbackCompany)
    {
        InitializeComponent();

        var facture = data.IsFacture;
        var template = settings.Template(facture);

        _build = forPrint => ReceiptDocument.Build(data, settings, logo, qrCode, fallbackCompany, forPrint);
        _singlePage = template == ReceiptTemplates.A4;
        _jobName = $"{(facture ? "Facture" : "Reçu")} {data.Numero}";

        Title = facture ? "Facture" : "Reçu de vente";
        PrintButton.Content = facture ? "🖨 Imprimer Facture" : "🖨 Imprimer";
        TemplateText.Text = $"Modèle : {ReceiptTemplates.Label(template)}";

        PaperHost.Content = _build(false);

        (Width, Height) = template switch
        {
            ReceiptTemplates.A4 => (900d, 900d),
            ReceiptTemplates.Compact => (380d, 720d),
            _ => (520d, 720d),
        };
    }

    private void Print_Click(object sender, RoutedEventArgs e) =>
        ReceiptDocument.Print(() => _build(true), _singlePage, _jobName);
}
