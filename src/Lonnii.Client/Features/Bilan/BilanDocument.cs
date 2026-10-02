using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Bilan;

/// <summary>
/// Builds a printable FlowDocument for the Bilan and Compte de Résultat tabs of BilanView.
///
/// Unlike <see cref="ReceiptDocument"/>, which draws one fixed-size page, this uses a real
/// FlowDocument paginator: a full chart of accounts easily runs past one page, and this is
/// meant to be handed to someone else (an owner, an accountant) rather than glanced at on
/// screen, so content has to keep flowing onto further pages instead of being cut off.
/// </summary>
public static class BilanDocument
{
    private static readonly Brush Muted = Brushes.DimGray;
    private static readonly Brush Warn = Brushes.Firebrick;
    private static readonly Brush RuleBrush = Brushes.Black;

    /// <summary>Shows the print dialog and, if accepted, paginates and prints the document
    /// against the chosen printer's actual page size - printed straight to a physical printer,
    /// or to a PDF file via the "Microsoft Print to PDF" printer the dialog already lists,
    /// which is how this also covers handing the statement to someone else as a file.</summary>
    public static void Print(FlowDocument document, string jobName)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        document.PageWidth = dialog.PrintableAreaWidth;
        document.PageHeight = dialog.PrintableAreaHeight;
        document.PagePadding = new Thickness(36);
        document.ColumnWidth = double.PositiveInfinity;

        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        dialog.PrintDocument(paginator, jobName);
    }

    public static FlowDocument BuildBilan(BilanResponse b, string company)
    {
        var doc = NewDocument();
        Heading(doc, "BILAN", company, b.Annee, b.IsProvisoire);

        doc.Blocks.Add(SectionTitle("ACTIF"));
        var actif = NewTable();
        Accounts(actif, "Actif immobilisé", b.ActifImmobilise);
        Accounts(actif, "Actif circulant", b.ActifCirculant);
        Accounts(actif, "Trésorerie", b.TresorerieActif);
        TotalRow(actif, "TOTAL ACTIF", b.TotalActif);
        doc.Blocks.Add(actif);

        doc.Blocks.Add(SectionTitle("PASSIF"));
        var passif = NewTable();
        Accounts(passif, "Capitaux propres", b.CapitauxPropres);
        Accounts(passif, "Dettes à long terme", b.DettesLongTerme);
        Accounts(passif, "Dettes à court terme", b.DettesCourtTerme);
        if (b.TresoreriePassif.Count > 0) Accounts(passif, "Trésorerie passif", b.TresoreriePassif);
        TotalRow(passif, "TOTAL PASSIF", b.TotalPassif);
        doc.Blocks.Add(passif);

        if (b.Ecart != 0)
            doc.Blocks.Add(Note($"Écart actif / passif : {Money.Format(b.Ecart)} — le bilan ne s'équilibre pas.", warn: true));

        return doc;
    }

    public static FlowDocument BuildResultat(ResultatResponse r, string company)
    {
        var doc = NewDocument();
        Heading(doc, "COMPTE DE RÉSULTAT", company, r.Annee, r.Annee == DateTime.Now.Year);

        var i = r.Integration;
        doc.Blocks.Add(SectionTitle("Données intégrées automatiquement"));
        var integ = NewTable(labelStar: 3, amountStar: 1);
        Row(integ, "Ventes de marchandises", i.Ventes);
        Row(integ, "Coût des marchandises vendues", i.CoutMarchandisesVendues);
        Row(integ, "Charges (module Charges)", i.Charges);
        Row(integ, "Dotations aux amortissements", i.DotationAmortissement);
        Row(integ, "Stock début", i.StockDebut);
        Row(integ, "Stock fin", i.StockFin);
        Row(integ, "Variation de stocks (information)", i.VariationStocks);
        doc.Blocks.Add(integ);

        doc.Blocks.Add(SectionTitle("PRODUITS"));
        var produits = NewTable();
        Accounts(produits, "Produits d'exploitation", r.ProduitsExploitation);
        Accounts(produits, "Produits financiers", r.ProduitsFinanciers);
        Accounts(produits, "Produits exceptionnels", r.ProduitsExceptionnels);
        TotalRow(produits, "TOTAL PRODUITS", r.TotalProduits);
        doc.Blocks.Add(produits);

        doc.Blocks.Add(SectionTitle("CHARGES"));
        var charges = NewTable();
        Accounts(charges, "Charges d'exploitation", r.ChargesExploitation);
        Accounts(charges, "Charges financières", r.ChargesFinancieres);
        Accounts(charges, "Charges exceptionnelles", r.ChargesExceptionnelles);
        TotalRow(charges, "TOTAL CHARGES", r.TotalCharges);
        doc.Blocks.Add(charges);

        doc.Blocks.Add(SectionTitle("RÉSULTATS"));
        var resultats = NewTable(labelStar: 3, amountStar: 1);
        Row(resultats, "Résultat d'exploitation", r.ResultatExploitation);
        Row(resultats, "Résultat financier", r.ResultatFinancier);
        Row(resultats, "Résultat exceptionnel", r.ResultatExceptionnel);
        doc.Blocks.Add(resultats);
        doc.Blocks.Add(Note($"RÉSULTAT NET : {Money.Format(r.ResultatNet)}", warn: r.ResultatNet < 0));

        return doc;
    }

    private static FlowDocument NewDocument() => new()
    {
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 12,
    };

    private static void Heading(FlowDocument doc, string title, string company, int annee, bool provisoire)
    {
        doc.Blocks.Add(new Paragraph(new Run(company.ToUpperInvariant()))
        {
            FontSize = 12, Foreground = Muted, Margin = new Thickness(0),
        });
        doc.Blocks.Add(new Paragraph(new Run(title))
        {
            FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0),
        });
        doc.Blocks.Add(new Paragraph(new Run(
            $"Exercice {annee}{(provisoire ? " (provisoire)" : " (clôturé)")} — imprimé le {DateTime.Now:dd/MM/yyyy HH:mm}"))
        {
            FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 2, 0, 16),
        });
    }

    private static Paragraph SectionTitle(string text) => new(new Run(text))
    {
        FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 18, 0, 6),
    };

    /// <summary>A table with N°/Libellé/Saisi/Automatique/Solde columns, for a compte listing.</summary>
    private static Table NewTable(double labelStar = 0, double amountStar = 0)
    {
        var table = new Table { CellSpacing = 0 };
        if (labelStar > 0)
        {
            // The two-column layout used for the Résultat "integration"/"résultats" tables.
            table.Columns.Add(new TableColumn { Width = new GridLength(labelStar, GridUnitType.Star) });
            table.Columns.Add(new TableColumn { Width = new GridLength(amountStar, GridUnitType.Star) });
        }
        else
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(1.5, GridUnitType.Star) });
            table.Columns.Add(new TableColumn { Width = new GridLength(4, GridUnitType.Star) });
            table.Columns.Add(new TableColumn { Width = new GridLength(1.5, GridUnitType.Star) });
            table.Columns.Add(new TableColumn { Width = new GridLength(1.5, GridUnitType.Star) });
            table.Columns.Add(new TableColumn { Width = new GridLength(1.5, GridUnitType.Star) });

            var group = new TableRowGroup();
            var header = new TableRow { Background = Brushes.WhiteSmoke };
            header.Cells.Add(Cell("N° compte", bold: true));
            header.Cells.Add(Cell("Libellé", bold: true));
            header.Cells.Add(Cell("Saisi", bold: true, right: true));
            header.Cells.Add(Cell("Automatique", bold: true, right: true));
            header.Cells.Add(Cell("Solde", bold: true, right: true));
            group.Rows.Add(header);
            table.RowGroups.Add(group);
        }

        if (table.RowGroups.Count == 0) table.RowGroups.Add(new TableRowGroup());
        return table;
    }

    private static void Accounts(Table table, string title, IReadOnlyList<BilanCompteDto> comptes)
    {
        var group = table.RowGroups[0];

        var titleRow = new TableRow();
        var titleCell = Cell(title, bold: true, italic: true);
        titleCell.ColumnSpan = 5;
        titleCell.Padding = new Thickness(4, 8, 4, 2);
        titleRow.Cells.Add(titleCell);
        group.Rows.Add(titleRow);

        foreach (var c in comptes)
        {
            var row = new TableRow();
            row.Cells.Add(Cell(c.NumeroCompte));
            row.Cells.Add(Cell(c.Libelle));
            row.Cells.Add(Cell(Money.Format(c.SoldeManuel), right: true));
            row.Cells.Add(Cell(Money.Format(c.SoldeAuto), right: true));
            row.Cells.Add(Cell(Money.Format(c.Solde), right: true, bold: true));
            group.Rows.Add(row);
        }

        if (comptes.Count == 0)
        {
            var empty = new TableRow();
            var cell = Cell("Aucun compte", italic: true);
            cell.ColumnSpan = 5;
            empty.Cells.Add(cell);
            group.Rows.Add(empty);
        }
    }

    private static void TotalRow(Table table, string label, decimal amount)
    {
        var row = new TableRow { Background = Brushes.WhiteSmoke };
        var labelCell = Cell(label, bold: true);
        labelCell.ColumnSpan = 4;
        labelCell.BorderBrush = RuleBrush;
        labelCell.BorderThickness = new Thickness(0, 1, 0, 0);
        row.Cells.Add(labelCell);
        var amountCell = Cell(Money.Format(amount), bold: true, right: true);
        amountCell.BorderBrush = RuleBrush;
        amountCell.BorderThickness = new Thickness(0, 1, 0, 0);
        row.Cells.Add(amountCell);
        table.RowGroups[0].Rows.Add(row);
    }

    /// <summary>A plain label/amount row, for the Résultat tables that have no compte columns.</summary>
    private static void Row(Table table, string label, decimal amount)
    {
        var row = new TableRow();
        row.Cells.Add(Cell(label));
        row.Cells.Add(Cell(Money.Format(amount), right: true));
        table.RowGroups[0].Rows.Add(row);
    }

    private static TableCell Cell(string text, bool bold = false, bool italic = false, bool right = false)
    {
        var run = new Run(text);
        var paragraph = new Paragraph(run) { Margin = new Thickness(0) };
        if (bold) paragraph.FontWeight = FontWeights.Bold;
        if (italic) paragraph.FontStyle = FontStyles.Italic;
        if (right) paragraph.TextAlignment = TextAlignment.Right;
        return new TableCell(paragraph) { Padding = new Thickness(4, 2, 4, 2) };
    }

    private static Paragraph Note(string text, bool warn) => new(new Run(text))
    {
        FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 0),
        Foreground = warn ? Warn : Brushes.Black,
    };
}
