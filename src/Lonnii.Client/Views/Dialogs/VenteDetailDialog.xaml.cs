using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// The "Détails" view for a sale - a read-only information panel (client info, vendeur,
/// payment history, line items, notes), distinct from <see cref="VenteReceiptDialog"/>'s
/// printable receipt/facture. Mirrors Lonnii Business's own separate detail modal in
/// ListeVentes.jsx/Ventes.jsx, which "Détails" opens there instead of the print template -
/// on this port, the two actions used to open the same dialog, which is what this replaces.
/// </summary>
public partial class VenteDetailDialog : Window
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public VenteDetailDialog(VenteDto vente)
    {
        InitializeComponent();

        TitleText.Text = $"Vente #{vente.NumeroVente}";

        SectionsPanel.Children.Add(BuildClientSection(vente));
        SectionsPanel.Children.Add(BuildVenteSection(vente));
        SectionsPanel.Children.Add(BuildFinancialSection(vente));

        if (vente.Paiements is { Count: > 0 } paiements)
            SectionsPanel.Children.Add(BuildPaiementsSection(paiements));

        if (vente.Items.Count > 0)
            SectionsPanel.Children.Add(BuildItemsSection(vente.Items));

        if (!string.IsNullOrWhiteSpace(vente.Notes))
            SectionsPanel.Children.Add(BuildSection("Notes", new TextBlock
            {
                Text = vente.Notes, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            }));
    }

    // --- Sections ---

    private static FrameworkElement BuildClientSection(VenteDto vente)
    {
        var grid = new Grid();
        AddRows(grid, 3);
        AddRow(grid, 0, "Nom", vente.ClientNom ?? "Non spécifié");
        AddRow(grid, 1, "Téléphone", vente.ClientTelephone ?? "Non spécifié");
        AddRow(grid, 2, "Email", vente.ClientEmail ?? "Non spécifié");
        return BuildSection("Informations client", grid);
    }

    private static FrameworkElement BuildVenteSection(VenteDto vente)
    {
        var isCancelled = vente.StatutPaiement == "annule";
        var hasMotif = isCancelled && !string.IsNullOrWhiteSpace(vente.CancellationReason);
        var hasCanceller = isCancelled && !string.IsNullOrWhiteSpace(vente.CancelledByName);
        var hasCancelledAt = isCancelled && vente.CancelledAt is not null;
        var rowCount = 4 + (hasMotif ? 1 : 0) + (hasCanceller ? 1 : 0) + (hasCancelledAt ? 1 : 0);

        var grid = new Grid();
        AddRows(grid, rowCount);
        AddRow(grid, 0, "Date", vente.DateVente.ToLocalTime().ToString("dd/MM/yyyy à HH:mm", French));
        AddRow(grid, 1, "Vendeur", vente.VendeurNom ?? "Non spécifié");
        AddRow(grid, 2, "Mode de paiement", PaymentLabel(vente.ModePaiement));
        AddRow(grid, 3, "Statut", StatutLabel(vente.StatutPaiement),
            valueBrushKey: StatutColorKey(vente.StatutPaiement));

        var row = 4;
        if (hasMotif)
            AddRow(grid, row++, "Motif d'annulation", vente.CancellationReason!, valueBrushKey: "Danger");
        if (hasCanceller)
            AddRow(grid, row++, "Annulé par", vente.CancelledByName!, valueBrushKey: "Danger");
        if (hasCancelledAt)
            AddRow(grid, row++, "Annulé le", vente.CancelledAt!.Value.ToLocalTime().ToString("dd/MM/yyyy à HH:mm", French),
                valueBrushKey: "Danger");

        return BuildSection("Informations vente", grid);
    }

    private static FrameworkElement BuildFinancialSection(VenteDto vente)
    {
        var panel = new StackPanel();
        var grid = new Grid();
        AddRows(grid, 3);
        AddRow(grid, 0, "Montant total", Money.Format(vente.MontantTotal), valueBold: true);
        AddRow(grid, 1, "Montant payé", Money.Format(vente.MontantPaye), valueBrushKey: "Success", valueBold: true);

        if (vente.AvoirAmount > 0)
        {
            var label = vente.IsAvoirSolded ? "Avoir soldé" : "Avoir à remettre";
            AddRow(grid, 2, label, Money.Format(vente.AvoirAmount),
                valueBrushKey: vente.IsAvoirSolded ? "Success" : "Warning", valueBold: true);
        }
        else if (vente.MontantRestant > 0)
        {
            AddRow(grid, 2, "Montant restant", Money.Format(vente.MontantRestant), valueBrushKey: "Danger", valueBold: true);
        }
        else
        {
            AddRow(grid, 2, "Montant restant", "✓ Soldé", valueBrushKey: "Success", valueBold: true);
        }

        panel.Children.Add(grid);

        if (vente.AvoirAmount > 0)
        {
            var notice = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
            notice.Inlines.Add(new Run(vente.IsAvoirSolded ? "Avoir soldé : " : "Avoir client : ") { FontWeight = FontWeights.Bold });
            notice.Inlines.Add(vente.IsAvoirSolded
                ? $"L'avoir de {Money.Format(vente.AvoirAmount)} a été remboursé au client"
                    + (vente.AvoirSoldedByName is { } by ? $" par {by}" : string.Empty)
                    + (vente.AvoirSoldedAt is { } at ? $" le {at.ToLocalTime():dd/MM/yyyy à HH:mm}." : ".")
                : $"Le client peut présenter ce reçu pour récupérer {Money.Format(vente.AvoirAmount)}.");
            SetBrush(notice, TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(notice);
        }

        return BuildSection("Détails financiers", panel);
    }

    private static FrameworkElement BuildPaiementsSection(IReadOnlyList<PaiementDto> paiements)
    {
        var headers = new[] { "#", "Montant", "Mode", "Date", "Par" };
        var widths = new[] { 26.0, 100, 110, 140, 1 };
        var rows = paiements.Select((p, i) => new[]
        {
            (i + 1).ToString(),
            Money.Format(p.Montant),
            PaymentLabel(p.ModePaiement),
            p.DatePaiement.ToLocalTime().ToString("dd/MM/yy HH:mm"),
            p.CreatedByName ?? "-",
        });
        return BuildSection("Historique des paiements", BuildTable(headers, widths, rows));
    }

    private static FrameworkElement BuildItemsSection(IReadOnlyList<VenteItemDto> items)
    {
        var headers = new[] { "Produit", "Qté", "Prix unitaire", "Total" };
        var widths = new[] { 1.0, 40, 110, 110 };
        var rows = items.Select(i => new[]
        {
            i.NomProduit,
            i.Quantite.ToString(),
            Money.Format(i.PrixUnitaire),
            Money.Format(i.PrixTotal),
        });
        return BuildSection("Produits vendus", BuildTable(headers, widths, rows));
    }

    // --- Building blocks ---

    private static Border BuildSection(string title, UIElement content)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        var header = new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 8),
        };
        SetBrush(header, TextBlock.ForegroundProperty, "TextSecondary");
        panel.Children.Add(header);
        panel.Children.Add(content);

        var border = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 12),
            Child = panel,
        };
        SetBrush(border, Border.BackgroundProperty, "SurfaceAlt");
        SetBrush(border, Border.BorderBrushProperty, "Border");
        return border;
    }

    /// <summary>A "label ⟷ value" row, the same layout as Lonnii Business's
    /// <c>.detail-item</c> - label on the left, value right-aligned.</summary>
    private static void AddRow(
        Grid grid, int row, string label, string value, string? valueBrushKey = null, bool valueBold = false)
    {
        var labelText = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        SetBrush(labelText, TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetRow(labelText, row);
        grid.Children.Add(labelText);

        var valueText = new TextBlock
        {
            Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 320,
            FontWeight = valueBold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        SetBrush(valueText, TextBlock.ForegroundProperty, valueBrushKey ?? "TextPrimary");
        Grid.SetRow(valueText, row);
        grid.Children.Add(valueText);
    }

    private static void AddRows(Grid grid, int count)
    {
        for (var i = 0; i < count; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    }

    /// <summary>A small themed table for the payment history and line items - same column
    /// approach as VentesView's list rows (a Grid header plus one Grid per data row).</summary>
    private static FrameworkElement BuildTable(string[] headers, double[] starOrFixedWidths, IEnumerable<string[]> rows)
    {
        GridLength Width(double w) => w <= 1 ? new GridLength(1, GridUnitType.Star) : new GridLength(w);

        var panel = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        for (var c = 0; c < headers.Length; c++)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = Width(starOrFixedWidths[c]) });
            var text = new TextBlock
            {
                Text = headers[c], FontSize = 11, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 0, 6, 0),
            };
            SetBrush(text, TextBlock.ForegroundProperty, "TextMuted");
            Grid.SetColumn(text, c);
            header.Children.Add(text);
        }
        panel.Children.Add(header);

        foreach (var cells in rows)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            for (var c = 0; c < cells.Length; c++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = Width(starOrFixedWidths[c]) });
                var text = new TextBlock
                {
                    Text = cells[c], FontSize = 12, Margin = new Thickness(2, 0, 6, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                SetBrush(text, TextBlock.ForegroundProperty, "TextPrimary");
                Grid.SetColumn(text, c);
                row.Children.Add(text);
            }
            panel.Children.Add(row);
        }

        return panel;
    }

    private static void SetBrush(FrameworkElement element, DependencyProperty property, string key) =>
        element.SetResourceReference(property, key);

    private static string PaymentLabel(string? modePaiement) => modePaiement switch
    {
        "cash" => "Espèces",
        "mobile_money" => "Mobile Money",
        "carte" => "Carte",
        "virement" => "Virement",
        "cheque" => "Chèque",
        _ => modePaiement ?? "Non spécifié",
    };

    private static string StatutLabel(string statutPaiement) => statutPaiement switch
    {
        "paye" => "Payé",
        "partiel" => "Paiement partiel",
        "en_attente" => "En attente",
        "annule" => "Annulé",
        _ => statutPaiement,
    };

    private static string StatutColorKey(string statutPaiement) => statutPaiement switch
    {
        "paye" => "Success",
        "partiel" => "Warning",
        "annule" => "Danger",
        _ => "TextMuted",
    };
}
