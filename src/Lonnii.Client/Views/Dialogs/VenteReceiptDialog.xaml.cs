using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Shows a completed sale as a printable receipt. The same <see cref="ReceiptPaper"/> border
/// shown on screen is what <see cref="PrintDialog.PrintVisual"/> sends to the printer, so
/// there is no separate "print layout" to keep in sync with the preview.
/// </summary>
public partial class VenteReceiptDialog : Window
{
    private readonly VenteDto _vente;

    public VenteReceiptDialog(VenteDto vente, AppSession session)
    {
        _vente = vente;
        InitializeComponent();

        CompanyText.Text = session.Groupe?.Nom ?? "Lonnii";
        NumeroText.Text = vente.NumeroVente;
        DateText.Text = vente.DateVente.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

        if (string.IsNullOrWhiteSpace(vente.ClientNom))
        {
            ClientPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ClientText.Text = string.IsNullOrWhiteSpace(vente.ClientTelephone)
                ? vente.ClientNom
                : $"{vente.ClientNom} ({vente.ClientTelephone})";
        }

        foreach (var item in vente.Items) ItemsList.Items.Add(BuildItemRow(item));

        var subtotal = vente.Items.Sum(i => i.PrixTotal);
        var remise = Math.Max(0, subtotal - vente.MontantTotal);

        SubtotalText.Text = Money.Format(subtotal);
        if (remise > 0)
        {
            RemiseRow.Visibility = Visibility.Visible;
            RemiseText.Text = $"- {Money.Format(remise)}";
        }
        TotalText.Text = Money.Format(vente.MontantTotal);

        ModePaiementText.Text = PaymentLabel(vente.ModePaiement);
        MontantPayeText.Text = Money.Format(vente.MontantPaye);
        if (vente.MontantRestant > 0)
        {
            RestantRow.Visibility = Visibility.Visible;
            RestantText.Text = Money.Format(vente.MontantRestant);
        }
    }

    private static Grid BuildItemRow(VenteItemDto item)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        var name = new TextBlock
        {
            Text = item.NomProduit, Foreground = Brushes.Black,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var qty = new TextBlock { Text = item.Quantite.ToString(), Foreground = Brushes.Black, TextAlignment = TextAlignment.Right };
        var unitPrice = new TextBlock { Text = Money.FormatPlain(item.PrixUnitaire), Foreground = Brushes.Black, TextAlignment = TextAlignment.Right };
        var total = new TextBlock { Text = Money.FormatPlain(item.PrixTotal), Foreground = Brushes.Black, TextAlignment = TextAlignment.Right };

        Grid.SetColumn(qty, 1);
        Grid.SetColumn(unitPrice, 2);
        Grid.SetColumn(total, 3);
        grid.Children.Add(name);
        grid.Children.Add(qty);
        grid.Children.Add(unitPrice);
        grid.Children.Add(total);

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

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() != true) return;

        printDialog.PrintVisual(ReceiptPaper, $"Reçu {_vente.NumeroVente}");
    }
}
