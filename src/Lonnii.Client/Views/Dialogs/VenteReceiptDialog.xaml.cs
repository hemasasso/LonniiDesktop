using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Shows a completed sale as a printable document. Two distinct layouts, matching Lonnii
/// Business's own two print templates (VentePrintModals.jsx) - an unpaid sale prints as a
/// FACTURE the client settles later at the till, a paid one as a REÇU DE VENTE - chosen from
/// <see cref="VenteDto.StatutPaiement"/> rather than a caller-supplied flag, so a receipt
/// reopened later always reflects what the sale actually is now, not what it was created as.
///
/// <para>
/// Every piece of fixed wording on the paper - both titles, the seller's label, the two
/// boxed notices and the footers - plus the logo, the payment QR code and the typeface come
/// from the workspace's <see cref="ReceiptSettingsDto"/>, edited in Paramètres →
/// "Paramètre Reçu et Facture". The dialog is handed those settings rather than fetching
/// them, so opening it stays synchronous and a finished sale never waits on the network;
/// <see cref="ShowForAsync"/> is the way callers get them.
/// </para>
///
/// The same <see cref="ReceiptPaper"/> border shown on screen is what
/// <see cref="PrintDialog.PrintVisual"/> sends to the printer, so there is no separate
/// "print layout" to keep in sync with the preview.
/// </summary>
public partial class VenteReceiptDialog : Window
{
    private readonly VenteDto _vente;
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>
    /// Loads the workspace's receipt configuration - cached after the first call - and shows
    /// the document. The single entry point, so no call site can accidentally print with the
    /// built-in defaults while the shop has its own configured.
    /// </summary>
    public static async Task ShowForAsync(VenteDto vente, AppSession session, Window? owner)
    {
        var settings = await session.GetReceiptSettingsAsync();

        new VenteReceiptDialog(vente, session, settings, session.ReceiptLogo, session.ReceiptQrCode)
        {
            Owner = owner,
        }.ShowDialog();
    }

    public VenteReceiptDialog(
        VenteDto vente, AppSession session, ReceiptSettingsDto settings, byte[]? logo, byte[]? qrCode)
    {
        _vente = vente;
        InitializeComponent();

        var isFacture = vente.StatutPaiement == "en_attente";

        // Done before anything is written or added: the walk rescales the sizes the XAML
        // declares, which are the nominal ones ReceiptTypography is calibrated against, and
        // only sees TextBlocks that exist at this point. The rows built below take the size
        // as an argument instead.
        var fontSize = settings.FontSize;
        ApplyFontSizes(ReceiptPaper, fontSize);

        // The attached property, not a FontFamily setter: the paper is a Border, which is
        // not a Control and so has none. TextElement's inherits down to every TextBlock on
        // it, including the item and payment rows built below.
        TextElement.SetFontFamily(ReceiptPaper, new FontFamily(settings.FontFamily));

        Title = isFacture ? "Facture" : "Reçu de vente";
        DocumentTitleText.Text = isFacture ? settings.FactureTitle : settings.ReceiptTitle;
        DocumentTitleText.FontSize = isFacture ? settings.FactureTitleFontSize : settings.ReceiptTitleFontSize;
        PrintButton.Content = isFacture ? "🖨 Imprimer Facture" : "🖨 Imprimer";

        if (logo is not null)
        {
            LogoImage.Source = ImageHelper.FromBytes(logo);
            LogoImage.Visibility = Visibility.Visible;
        }

        // The workspace name is the fallback, not a default the shop chose - it is the only
        // name the desktop knows before anyone opens the settings screen.
        CompanyText.Text = settings.CompanyName ?? session.Groupe?.Nom ?? "Lonnii";
        NumeroText.Text = $"N° {vente.NumeroVente}";

        var local = vente.DateVente.ToLocalTime();
        DateText.Text = $"{local.ToString("d MMMM yyyy", French)} à {local:HH:mm}";

        ClientText.Text = string.IsNullOrWhiteSpace(vente.ClientNom) ? "N/A"
            : string.IsNullOrWhiteSpace(vente.ClientTelephone) ? vente.ClientNom
            : $"{vente.ClientNom} ({vente.ClientTelephone})";

        VendeurRow.Visibility = string.IsNullOrWhiteSpace(vente.VendeurNom)
            ? Visibility.Collapsed : Visibility.Visible;
        VendeurLabelText.Text = $"{settings.SellerLabel}:";
        VendeurText.Text = PersonName.Abbreviate(vente.VendeurNom);
        VendeurText.ToolTip = vente.VendeurNom;

        foreach (var item in vente.Items) ItemsList.Items.Add(BuildItemRow(item, fontSize));

        // Sous-total is the pre-discount sum, so REMISE TOTALE (their combined per-line
        // discounts) shows as its own line rather than being silently folded away - same
        // reason the cart itself shows it that way (VentesView.UpdateTotals).
        var rawSubtotal = vente.Items.Sum(i => i.PrixUnitaire * i.Quantite);
        var itemDiscounts = rawSubtotal - vente.Items.Sum(i => i.PrixTotal);
        var globalRemise = Math.Max(0, vente.Items.Sum(i => i.PrixTotal) - vente.MontantTotal);
        var totalRemise = itemDiscounts + globalRemise;

        SubtotalText.Text = Money.Format(rawSubtotal);
        if (totalRemise > 0)
        {
            RemiseRow.Visibility = Visibility.Visible;
            RemiseLabelText.Text = isFacture ? "Remise totale:" : "Remise globale:";
            RemiseText.Text = $"- {Money.Format(totalRemise)}";
        }

        TotalText.Text = Money.Format(vente.MontantTotal);

        if (isFacture)
        {
            PaymentInfoPanel.Visibility = Visibility.Collapsed;
            FactureNoticePanel.Visibility = Visibility.Visible;
            FactureNoticeTitleText.Text = settings.FactureNoticeTitle;
            FactureNoticeBodyText.Text = settings.FactureNoticeText;
            FooterText.Text = settings.FactureFooterText;
        }
        else
        {
            PaymentInfoPanel.Visibility = Visibility.Visible;
            FactureNoticePanel.Visibility = Visibility.Collapsed;
            FooterText.Text = settings.ReceiptFooterText;

            ModePaiementText.Text = PaymentLabel(vente.ModePaiement);
            MontantPayeText.Text = Money.Format(vente.MontantPaye);
            StatutText.Text = StatutLabel(vente.StatutPaiement);

            // The most recent payment's recorder - the person who actually took the money
            // last, which is what "Caissier" should mean when it differs from the Vendeur
            // who rang the sale up (see lonnii-preparer-cashier-flow: a préparateur builds
            // the cart, a caissier settles it later, sometimes as a single payment that the
            // "Historique des Paiements" table below never renders for).
            var caissierNom = vente.Paiements?.LastOrDefault()?.CreatedByName;
            if (!string.IsNullOrWhiteSpace(caissierNom) && caissierNom != vente.VendeurNom)
            {
                CaissierRow.Visibility = Visibility.Visible;
                CaissierText.Text = PersonName.Abbreviate(caissierNom);
                CaissierText.ToolTip = caissierNom;
            }

            if (vente.MontantRestant > 0)
            {
                RestantRow.Visibility = Visibility.Visible;
                RestantText.Text = Money.Format(vente.MontantRestant);
            }

            // The client overpaid: shown regardless of whether it was already settled, so a
            // reprinted receipt still accounts for the money - "(soldé)" is the only
            // difference once VentesEndpoints.SolderAvoirAsync has paid it out.
            if (vente.AvoirAmount > 0)
            {
                AvoirRow.Visibility = Visibility.Visible;
                AvoirText.Text = vente.IsAvoirSolded
                    ? $"{Money.Format(vente.AvoirAmount)} (soldé)"
                    : Money.Format(vente.AvoirAmount);

                if (!vente.IsAvoirSolded)
                {
                    AvoirNoticePanel.Visibility = Visibility.Visible;
                    AvoirNoticeTitleText.Text = settings.AvoirNoticeTitle;
                    // The amount is appended rather than configurable, so a shop cannot
                    // write a notice that leaves out what the client is actually owed.
                    AvoirNoticeText.Text = $"{settings.AvoirNoticeText} {Money.Format(vente.AvoirAmount)}";
                }
            }
        }

        // Alongside the "Total Payé" line above, not instead of it - same as Lonnii
        // Business's own receipt/facture, gated purely on tranche count regardless of
        // whether this prints as a facture or a receipt.
        if (vente.Paiements is { Count: > 1 } paiements)
        {
            PaymentHistoryPanel.Visibility = Visibility.Visible;
            for (var i = 0; i < paiements.Count; i++)
                PaymentHistoryList.Items.Add(BuildPaymentRow(i + 1, paiements[i], fontSize));
        }

        var qrNote = settings.NoteUnderQr;
        if (qrCode is not null || qrNote is not null)
        {
            QrPanel.Visibility = Visibility.Visible;
            QrImage.Visibility = qrCode is null ? Visibility.Collapsed : Visibility.Visible;
            if (qrCode is not null) QrImage.Source = ImageHelper.FromBytes(qrCode);
            QrNoteText.Text = qrNote ?? string.Empty;
        }
    }

    /// <summary>
    /// Rescales every TextBlock already in the paper from the workspace's chosen base size.
    /// The sizes in the XAML are the nominal ones <see cref="ReceiptTypography"/> is
    /// calibrated against, so a workspace on the default 11 comes out unchanged.
    ///
    /// A tree walk rather than twenty named fields: the alternative is naming every label on
    /// the paper purely so code can resize it, which makes the XAML harder to read and is one
    /// more thing to forget when a row is added.
    /// </summary>
    private static void ApplyFontSizes(DependencyObject element, int fontSize)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(element))
        {
            if (child is TextBlock text)
                text.FontSize = ReceiptTypography.Scale(text.FontSize, fontSize);

            if (child is DependencyObject node)
                ApplyFontSizes(node, fontSize);
        }
    }

    /// <summary>One row of the "Historique des Paiements" table - same five columns, same
    /// date format (<c>dd/MM/yy HH:mm</c>), as Lonnii Business's VentePrintModals.jsx.</summary>
    private static Grid BuildPaymentRow(int number, PaiementDto paiement, int fontSize)
    {
        var small = ReceiptTypography.TableSmall(fontSize);
        var tiny = ReceiptTypography.Scale(9, fontSize);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(98) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var numberText = new TextBlock { Text = number.ToString(), FontSize = small, Foreground = Brushes.Black };
        // FormatPlain, not Format: the header already says "Montant (FCFA)", and repeating
        // the currency label on every row was overflowing this column into "Mode" next to it.
        var montantText = new TextBlock
        {
            Text = Money.FormatPlain(paiement.Montant), FontSize = small, FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 6, 0),
        };
        var modeText = new TextBlock { Text = PaymentLabel(paiement.ModePaiement), FontSize = small, Foreground = Brushes.Black };
        var dateText = new TextBlock
        {
            Text = paiement.DatePaiement.ToLocalTime().ToString("dd/MM/yy HH:mm", French),
            FontSize = small, Foreground = Brushes.Black,
        };
        var parText = new TextBlock
        {
            Text = PersonName.Abbreviate(paiement.CreatedByName) is { Length: > 0 } abbrev ? abbrev : "-",
            ToolTip = paiement.CreatedByName, FontSize = tiny, Foreground = Brushes.Black,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Grid.SetColumn(montantText, 1);
        Grid.SetColumn(modeText, 2);
        Grid.SetColumn(dateText, 3);
        Grid.SetColumn(parText, 4);
        grid.Children.Add(numberText);
        grid.Children.Add(montantText);
        grid.Children.Add(modeText);
        grid.Children.Add(dateText);
        grid.Children.Add(parText);

        return grid;
    }

    /// <summary>One item row. When a line carries its own discount, the total column shows
    /// the pre-discount amount struck through, the discount taken off, and the final total -
    /// same three-part layout as the printed template it mirrors.</summary>
    private static Grid BuildItemRow(VenteItemDto item, int fontSize)
    {
        var body = ReceiptTypography.Table(fontSize);
        var small = ReceiptTypography.TableSmall(fontSize);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        var name = new TextBlock
        {
            Text = item.NomProduit, FontSize = body, Foreground = Brushes.Black,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top,
        };
        var qty = new TextBlock
        {
            Text = item.Quantite.ToString(), FontSize = body, Foreground = Brushes.Black,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        var unitPrice = new TextBlock
        {
            Text = Money.FormatPlain(item.PrixUnitaire), FontSize = body, Foreground = Brushes.Black,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };

        var rawLineTotal = item.PrixUnitaire * item.Quantite;
        var discount = rawLineTotal - item.PrixTotal;

        FrameworkElement totalElement;
        if (discount > 0)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            stack.Children.Add(new TextBlock
            {
                Text = Money.FormatPlain(rawLineTotal), FontSize = small, Foreground = Brushes.Gray,
                TextDecorations = TextDecorations.Strikethrough, TextAlignment = TextAlignment.Right,
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"- {Money.FormatPlain(discount)} (Remise)", FontSize = small,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#E74C3C")!,
                TextAlignment = TextAlignment.Right,
            });
            stack.Children.Add(new TextBlock
            {
                Text = Money.FormatPlain(item.PrixTotal), FontSize = body, FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black, TextAlignment = TextAlignment.Right,
            });
            totalElement = stack;
        }
        else
        {
            totalElement = new TextBlock
            {
                Text = Money.FormatPlain(item.PrixTotal), FontSize = body, Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            };
        }

        Grid.SetColumn(qty, 1);
        Grid.SetColumn(unitPrice, 2);
        Grid.SetColumn(totalElement, 3);
        grid.Children.Add(name);
        grid.Children.Add(qty);
        grid.Children.Add(unitPrice);
        grid.Children.Add(totalElement);

        return grid;
    }

    private static string PaymentLabel(string? modePaiement) => modePaiement switch
    {
        "cash" => "Espèces",
        "mobile_money" => "Mobile Money",
        "carte" => "Carte",
        "virement" => "Virement",
        "cheque" => "Chèque",
        _ => modePaiement ?? "—",
    };

    private static string StatutLabel(string statutPaiement) => statutPaiement switch
    {
        "paye" => "Payé",
        "partiel" => "Partiel",
        "en_attente" => "En attente",
        "annule" => "Annulé",
        _ => statutPaiement,
    };

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() != true) return;

        printDialog.PrintVisual(ReceiptPaper, $"{Title} {_vente.NumeroVente}");
    }
}
