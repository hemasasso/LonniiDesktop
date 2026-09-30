using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Printing;

/// <summary>
/// Draws a printed reçu or facture in any of the <see cref="ReceiptTemplates"/>, leaving out
/// whatever <see cref="ReceiptSections"/> the shop switched off.
///
/// <para>
/// The one place a document's layout lives: <c>VenteReceiptDialog</c> shows and prints what
/// this returns, and the settings editor previews it with a sample sale. Keeping a second
/// copy of each layout in XAML for the preview is how the two used to drift apart.
/// </para>
/// <para>
/// Always black on white with literal colours, never theme resources: the paper has to look
/// the same in dark mode, and it is printed outside any window's resource scope.
/// </para>
/// </summary>
public sealed class ReceiptDocument
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly Brush Accent = Frozen("#4C3B9E");
    private static readonly Brush Muted = Frozen("#333333");
    private static readonly Brush Faint = Frozen("#666666");
    private static readonly Brush Danger = Frozen("#E74C3C");
    private static readonly Brush Warning = Frozen("#B45309");
    private static readonly Brush Shade = Frozen("#F0F0F0");
    private static readonly Brush ScreenEdge = Frozen("#CCCCCC");

    private readonly ReceiptData _data;
    private readonly ReceiptSettingsDto _settings;
    private readonly byte[]? _logo;
    private readonly byte[]? _qr;
    private readonly string _company;
    private readonly bool _facture;
    private readonly HashSet<string> _hidden;
    private readonly int _size;

    private ReceiptDocument(
        ReceiptData data, ReceiptSettingsDto settings, byte[]? logo, byte[]? qr, string company)
    {
        _data = data;
        _settings = settings;
        _logo = logo;
        _qr = qr;
        _company = company;
        _facture = data.IsFacture;
        _hidden = settings.HiddenSections(_facture).ToHashSet();
        _size = settings.FontSize;
    }

    /// <param name="fallbackCompany">Printed when the shop set no company name - the
    /// workspace name, the only one the desktop knows.</param>
    /// <param name="forPrint">Leaves off the grey edge that separates the paper from the
    /// window on screen.</param>
    public static Border Build(
        ReceiptData data, ReceiptSettingsDto settings, byte[]? logo, byte[]? qr,
        string fallbackCompany, bool forPrint)
    {
        var document = new ReceiptDocument(data, settings, logo, qr, settings.CompanyName ?? fallbackCompany);

        var paper = settings.Template(data.IsFacture) switch
        {
            ReceiptTemplates.A4 => document.BuildA4(),
            ReceiptTemplates.Compact => document.BuildCompact(),
            _ => document.BuildTicket(),
        };

        // Attached properties: a Border is not a Control and has no font of its own, but
        // these inherit down to every TextBlock on the paper.
        TextElement.SetFontFamily(paper, new FontFamily(settings.FontFamily));
        TextElement.SetForeground(paper, Brushes.Black);
        paper.Background = Brushes.White;
        paper.SnapsToDevicePixels = true;

        if (!forPrint)
        {
            paper.BorderBrush = ScreenEdge;
            paper.BorderThickness = new Thickness(1);
        }

        return paper;
    }

    /// <summary>
    /// Asks for a printer and prints a freshly built paper, scaled down if it is wider than
    /// the printer can print - an 80 mm ticket sent to a 58 mm roll would otherwise lose its
    /// right-hand column. An A4 page is also scaled to fit the page's height, so a long sale
    /// prints small rather than cut off at the bottom.
    /// </summary>
    public static void Print(Func<Border> buildForPrint, bool singlePage, string jobName)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        var paper = buildForPrint();
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        paper.Measure(unbounded);
        var desired = paper.DesiredSize;

        var scale = 1.0;
        if (dialog.PrintableAreaWidth > 0) scale = Math.Min(scale, dialog.PrintableAreaWidth / desired.Width);
        if (singlePage && dialog.PrintableAreaHeight > 0)
            scale = Math.Min(scale, dialog.PrintableAreaHeight / desired.Height);

        if (scale < 1) paper.LayoutTransform = new ScaleTransform(scale, scale);

        paper.Measure(unbounded);
        paper.Arrange(new Rect(paper.DesiredSize));
        paper.UpdateLayout();

        dialog.PrintVisual(paper, jobName);
    }

    // =====================================================================================
    // Ticket 80 mm - the layout every shop printed before templates existed
    // =====================================================================================

    private Border BuildTicket()
    {
        const double contentWidth = 384;
        var s = new StackPanel();

        AddCenteredHeader(s, logoHeight: 72);
        s.Children.Add(Dashed(contentWidth, new Thickness(0, 12, 0, 10)));

        if (AddPeople(s)) s.Children.Add(Dashed(contentWidth, new Thickness(0, 10, 0, 10)));

        var showPrice = Show(ReceiptSections.UnitPrice);
        s.Children.Add(TicketTableHeader(showPrice));
        foreach (var line in _data.Lines) s.Children.Add(TicketItemRow(line, showPrice));

        s.Children.Add(Dashed(contentWidth, new Thickness(0, 10, 0, 10)));

        AddTotals(s, new Thickness(10, 8, 10, 8));
        AddPaymentInfo(s);
        AddNotice(s);
        AddTicketPaymentHistory(s, contentWidth);
        AddTicketSignature(s, twoParties: true);
        AddFooter(s);
        AddTicketLegalFooter(s);
        AddQr(s, 130);

        return new Border { Width = 440, Padding = new Thickness(28), Child = s };
    }

    private Grid TicketTableHeader(bool showPrice)
    {
        var grid = TicketColumns(showPrice);
        grid.Margin = new Thickness(0, 0, 0, 6);
        var size = ReceiptTypography.Table(_size);

        Place(grid, 0, Text("Article", size, FontWeights.SemiBold));
        Place(grid, 1, Text("Qte", size, FontWeights.SemiBold, align: TextAlignment.Center));
        if (showPrice) Place(grid, 2, Text($"P.U ({Money.Label})", size, FontWeights.SemiBold, align: TextAlignment.Right));
        Place(grid, showPrice ? 3 : 2, Text($"Total ({Money.Label})", size, FontWeights.SemiBold, align: TextAlignment.Right));
        return grid;
    }

    /// <summary>When a line carries its own discount, the total column shows the
    /// pre-discount amount struck through, the discount, and the final total.</summary>
    private Grid TicketItemRow(ReceiptLine line, bool showPrice)
    {
        var body = ReceiptTypography.Table(_size);
        var small = ReceiptTypography.TableSmall(_size);

        var grid = TicketColumns(showPrice);
        grid.Margin = new Thickness(0, 0, 0, 6);

        // The "Qte" column is too narrow for a unit label (e.g. "Carton") - it goes after the
        // name instead, same as the till would say it: "Soda (Carton)".
        var name = Text(line.Unite is null ? line.Name : $"{line.Name} ({line.Unite})", body);
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        Place(grid, 0, name);
        Place(grid, 1, Text(line.Quantity.ToString(), body, align: TextAlignment.Center));
        if (showPrice) Place(grid, 2, Text(Money.FormatPlain(line.UnitPrice), body, align: TextAlignment.Right));

        FrameworkElement total;
        if (line.Discount > 0 && Show(ReceiptSections.Discounts))
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var raw = Text(Money.FormatPlain(line.UnitPrice * line.Quantity), small, brush: Brushes.Gray, align: TextAlignment.Right);
            raw.TextDecorations = TextDecorations.Strikethrough;
            stack.Children.Add(raw);
            stack.Children.Add(Text($"- {Money.FormatPlain(line.Discount)} (Remise)", small, brush: Danger, align: TextAlignment.Right));
            stack.Children.Add(Text(Money.FormatPlain(line.Total), body, FontWeights.Bold, align: TextAlignment.Right));
            total = stack;
        }
        else
        {
            total = Text(Money.FormatPlain(line.Total), body, align: TextAlignment.Right);
        }

        Place(grid, showPrice ? 3 : 2, total);
        return grid;
    }

    private static Grid TicketColumns(bool showPrice)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        if (showPrice) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        return grid;
    }

    private void AddTicketPaymentHistory(StackPanel s, double contentWidth)
    {
        if (!ShowsPaymentHistory) return;

        var small = ReceiptTypography.TableSmall(_size);
        s.Children.Add(Dashed(contentWidth, new Thickness(0, 14, 0, 10)));
        s.Children.Add(Text("HISTORIQUE DES PAIEMENTS", ReceiptTypography.Table(_size), FontWeights.Bold, margin: new Thickness(0, 0, 0, 6)));

        double[] widths = [20, 80, 76, 98];
        var header = HistoryColumns(widths);
        header.Margin = new Thickness(0, 0, 0, 4);
        Place(header, 0, Text("#", small, FontWeights.SemiBold));
        Place(header, 1, Text($"Montant ({Money.Label})", small, FontWeights.SemiBold, align: TextAlignment.Right, margin: new Thickness(0, 0, 6, 0)));
        Place(header, 2, Text("Mode", small, FontWeights.SemiBold));
        Place(header, 3, Text("Date", small, FontWeights.SemiBold));
        Place(header, 4, Text("Par", small, FontWeights.SemiBold));
        s.Children.Add(header);

        for (var i = 0; i < _data.Paiements.Count; i++)
        {
            var p = _data.Paiements[i];
            var row = HistoryColumns(widths);
            row.Margin = new Thickness(0, 0, 0, 3);
            Place(row, 0, Text((i + 1).ToString(), small));
            Place(row, 1, Text(Money.FormatPlain(p.Amount), small, FontWeights.Bold, align: TextAlignment.Right, margin: new Thickness(0, 0, 6, 0)));
            Place(row, 2, Text(PaymentLabel(p.Mode), small));
            Place(row, 3, Text(p.Date.ToString("dd/MM/yy HH:mm", French), small));
            var by = Text(PersonName.Abbreviate(p.By) is { Length: > 0 } abbrev ? abbrev : "-", ReceiptTypography.Scale(9, _size));
            by.TextWrapping = TextWrapping.NoWrap;
            by.TextTrimming = TextTrimming.CharacterEllipsis;
            Place(row, 4, by);
            s.Children.Add(row);
        }
    }

    private static Grid HistoryColumns(double[] widths)
    {
        var grid = new Grid();
        foreach (var w in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private void AddTicketSignature(StackPanel s, bool twoParties)
    {
        if (!Show(ReceiptSections.Signature)) return;

        var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (twoParties)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        Place(grid, 0, SignatureLine("Signature client"));
        if (twoParties) Place(grid, 2, SignatureLine("Cachet / signature"));
        s.Children.Add(grid);

        FrameworkElement SignatureLine(string label)
        {
            var stack = new StackPanel();
            stack.Children.Add(new Border
            {
                Height = 40, BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 0, 0, 1),
            });
            stack.Children.Add(Text(label, ReceiptTypography.Scale(9, _size), brush: Faint,
                align: TextAlignment.Center, margin: new Thickness(0, 3, 0, 0)));
            return stack;
        }
    }

    private void AddTicketLegalFooter(StackPanel s)
    {
        if (!Show(ReceiptSections.LegalFooter) || _settings.LegalFooterText is not { } legal) return;

        var text = Text(legal, ReceiptTypography.Scale(9, _size), brush: Faint, align: TextAlignment.Center,
            margin: new Thickness(0, 10, 0, 0));
        text.FontStyle = FontStyles.Italic;
        s.Children.Add(text);
    }

    // =====================================================================================
    // Ticket compact 58 mm - each item on two lines so nothing is squeezed into columns
    // =====================================================================================

    private Border BuildCompact()
    {
        const double contentWidth = 220;
        var s = new StackPanel();

        AddCenteredHeader(s, logoHeight: 48);
        s.Children.Add(Dashed(contentWidth, new Thickness(0, 8, 0, 6)));

        if (AddPeople(s)) s.Children.Add(Dashed(contentWidth, new Thickness(0, 6, 0, 6)));

        var body = ReceiptTypography.Table(_size);
        var small = ReceiptTypography.TableSmall(_size);
        var showPrice = Show(ReceiptSections.UnitPrice);

        foreach (var line in _data.Lines)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
            item.Children.Add(Text(line.Name, body, FontWeights.SemiBold));

            var amounts = new Grid();
            amounts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            amounts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var qty = line.Unite is null ? line.Quantity.ToString() : $"{line.Quantity} {line.Unite}";
            Place(amounts, 0, Text(
                showPrice ? $"{qty} x {Money.FormatPlain(line.UnitPrice)}" : $"x {qty}",
                small, brush: Muted));
            Place(amounts, 1, Text(Money.FormatPlain(line.Total), body, FontWeights.Bold, align: TextAlignment.Right));
            item.Children.Add(amounts);

            if (line.Discount > 0 && Show(ReceiptSections.Discounts))
                item.Children.Add(Text($"Remise - {Money.FormatPlain(line.Discount)}", small, brush: Danger));

            s.Children.Add(item);
        }

        s.Children.Add(Dashed(contentWidth, new Thickness(0, 6, 0, 6)));

        AddTotals(s, new Thickness(6, 5, 6, 5));
        AddPaymentInfo(s);
        AddNotice(s);

        if (ShowsPaymentHistory)
        {
            s.Children.Add(Dashed(contentWidth, new Thickness(0, 8, 0, 6)));
            s.Children.Add(Text("PAIEMENTS", body, FontWeights.Bold, margin: new Thickness(0, 0, 0, 3)));
            for (var i = 0; i < _data.Paiements.Count; i++)
            {
                var p = _data.Paiements[i];
                s.Children.Add(Text(
                    $"{i + 1}. {Money.FormatPlain(p.Amount)} {PaymentLabel(p.Mode)} - {p.Date.ToString("dd/MM HH:mm", French)}",
                    small, margin: new Thickness(0, 0, 0, 2)));
            }
        }

        AddTicketSignature(s, twoParties: false);
        AddFooter(s);
        AddTicketLegalFooter(s);
        AddQr(s, 100);

        return new Border { Width = 240, Padding = new Thickness(10), Child = s };
    }

    // =====================================================================================
    // A4 détaillé - sender and recipient blocks, bordered table, totals, signature
    // =====================================================================================

    private Border BuildA4()
    {
        var root = new DockPanel { LastChildFill = true };

        // Docked first so it sits at the foot of the page however short the sale is.
        if (Show(ReceiptSections.LegalFooter) && _settings.LegalFooterText is { } legal)
        {
            var text = Text(legal, ReceiptTypography.Scale(9, _size), brush: Faint, align: TextAlignment.Center);
            text.FontStyle = FontStyles.Italic;
            var footer = new Border
            {
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 16, 0, 0), Child = text,
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
        }

        var body = new StackPanel();
        root.Children.Add(body);

        body.Children.Add(A4Header());

        var references = new List<string>();
        if (Show(ReceiptSections.Seller) && !string.IsNullOrWhiteSpace(_data.VendeurNom))
            references.Add($"{_settings.SellerLabel} : {_data.VendeurNom}");
        if (ShowsCashier) references.Add($"Caissier : {_data.CaissierNom}");
        if (references.Count > 0)
            body.Children.Add(Text(string.Join("        ", references), ReceiptTypography.Meta(_size), brush: Muted,
                margin: new Thickness(0, 14, 0, 0)));

        body.Children.Add(new Border
        {
            BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 2, 0, 0), Margin = new Thickness(0, 10, 0, 12),
        });

        body.Children.Add(A4ItemTable());
        body.Children.Add(A4TotalsZone());

        if (ShowsPaymentHistory) body.Children.Add(A4PaymentHistory());

        body.Children.Add(A4Bottom());

        return new Border { Width = 794, MinHeight = 1123, Padding = new Thickness(44), Child = root };
    }

    private Grid A4Header()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var sender = new StackPanel();
        if (Show(ReceiptSections.Logo) && _logo is not null)
        {
            sender.Children.Add(new Image
            {
                Source = ImageHelper.FromBytes(_logo), MaxHeight = 80, MaxWidth = 240, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
            });
        }
        if (Show(ReceiptSections.Company))
            sender.Children.Add(Text(_company, ReceiptTypography.Company(_size), FontWeights.Bold, Accent));
        if (Show(ReceiptSections.CompanyContact))
            foreach (var line in ContactLines)
                sender.Children.Add(Text(line, ReceiptTypography.Meta(_size), brush: Muted, margin: new Thickness(0, 2, 0, 0)));
        if (Show(ReceiptSections.CompanyLegal))
            foreach (var line in LegalLines)
                sender.Children.Add(Text(line, ReceiptTypography.Scale(9, _size), brush: Faint, margin: new Thickness(0, 2, 0, 0)));
        Place(header, 0, sender);

        var right = new StackPanel();
        var titleBox = new StackPanel();
        titleBox.Children.Add(Text(Title, TitleSize, FontWeights.Bold, Accent));
        titleBox.Children.Add(Text($"N° : {_data.Numero}", ReceiptTypography.Meta(_size), FontWeights.SemiBold, margin: new Thickness(0, 4, 0, 0)));
        titleBox.Children.Add(Text($"DATE : {_data.Date.ToString("dd/MM/yyyy HH:mm", French)}", ReceiptTypography.Meta(_size)));
        right.Children.Add(new Border
        {
            Background = Shade, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8), Child = titleBox,
        });

        if (Show(ReceiptSections.Client))
        {
            var client = new StackPanel();
            client.Children.Add(Text("CLIENT", ReceiptTypography.Scale(9, _size), FontWeights.SemiBold, Faint));
            client.Children.Add(Text(string.IsNullOrWhiteSpace(_data.ClientNom) ? "N/A" : _data.ClientNom,
                ReceiptTypography.Body(_size), FontWeights.Bold, margin: new Thickness(0, 2, 0, 0)));
            if (!string.IsNullOrWhiteSpace(_data.ClientTelephone))
                client.Children.Add(Text($"Tél : {_data.ClientTelephone}", ReceiptTypography.Meta(_size), brush: Muted));
            if (!string.IsNullOrWhiteSpace(_data.ClientEmail))
                client.Children.Add(Text(_data.ClientEmail, ReceiptTypography.Meta(_size), brush: Muted));

            right.Children.Add(new Border
            {
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 16, 0, 0), Child = client,
            });
        }
        Place(header, 2, right);

        return header;
    }

    /// <summary>A bordered table with column rules running the full height, like a
    /// pre-printed invoice form. The minimum height keeps a short sale from producing a
    /// cramped table floating at the top of the page.</summary>
    private Border A4ItemTable()
    {
        var showPrice = Show(ReceiptSections.UnitPrice);
        var table = new Grid { MinHeight = 320 };

        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        if (showPrice) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        var columns = table.ColumnDefinitions.Count;

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var _ in _data.Lines) table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (var c = 0; c < columns - 1; c++)
        {
            var rule = new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 0, 1, 0) };
            Grid.SetRowSpan(rule, _data.Lines.Count + 1);
            Place(table, c, rule, row: 1);
        }

        string[] headers = showPrice
            ? ["DÉSIGNATION", "QUANTITÉ", $"P.U ({Money.Label})", $"MONTANT ({Money.Label})"]
            : ["DÉSIGNATION", "QUANTITÉ", $"MONTANT ({Money.Label})"];
        var tableSize = ReceiptTypography.Table(_size);
        for (var c = 0; c < columns; c++)
        {
            Place(table, c, new Border
            {
                Background = Shade, BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(0, 0, c < columns - 1 ? 1 : 0, 1), Padding = new Thickness(6, 5, 6, 5),
                Child = Text(headers[c], tableSize, FontWeights.Bold, align: TextAlignment.Center),
            });
        }

        var cell = new Thickness(6, 4, 6, 4);
        for (var r = 0; r < _data.Lines.Count; r++)
        {
            var line = _data.Lines[r];
            var name = new StackPanel { Margin = cell };
            name.Children.Add(Text(line.Name, tableSize));
            if (line.Discount > 0 && Show(ReceiptSections.Discounts))
                name.Children.Add(Text($"Remise : - {Money.FormatPlain(line.Discount)}", ReceiptTypography.TableSmall(_size), brush: Danger));

            var c = 0;
            Place(table, c++, name, r + 1);
            var qtyText = line.Unite is null ? line.Quantity.ToString() : $"{line.Quantity} {line.Unite}";
            Place(table, c++, Text(qtyText, tableSize, align: TextAlignment.Center, margin: cell), r + 1);
            if (showPrice)
                Place(table, c++, Text(Money.FormatPlain(line.UnitPrice), tableSize, align: TextAlignment.Right, margin: cell), r + 1);
            Place(table, c, Text(Money.FormatPlain(line.Total), tableSize, align: TextAlignment.Right, margin: cell), r + 1);
        }

        return new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Child = table };
    }

    private Grid A4TotalsZone()
    {
        var zone = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        zone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        zone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        zone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        var left = new StackPanel();
        if (ShowsPaymentInfo)
        {
            left.Children.Add(Text($"Mode de paiement : {PaymentLabel(_data.ModePaiement)}", ReceiptTypography.Meta(_size)));
            left.Children.Add(Text($"Statut : {StatutLabel(_data.StatutPaiement)}", ReceiptTypography.Meta(_size), FontWeights.SemiBold,
                margin: new Thickness(0, 2, 0, 0)));
        }
        AddNotice(left);
        Place(zone, 0, left);

        var rows = new List<(string Label, string Value, Brush? Brush, bool Emphasis)>();
        var tax = TaxBreakdown;
        if (Show(ReceiptSections.Discounts))
        {
            rows.Add(("SOUS-TOTAL", Money.Format(_data.RawSubtotal), null, false));
            if (_data.TotalRemise > 0) rows.Add(("REMISE", $"- {Money.Format(_data.TotalRemise)}", Danger, false));
        }
        if (tax is { } t)
        {
            rows.Add(("TOTAL HT", Money.Format(t.Ht), null, false));
            rows.Add(($"TVA ({TvaRateLabel} %)", Money.Format(t.Tva), null, false));
        }
        rows.Add((tax is null ? "TOTAL" : "TOTAL TTC", Money.Format(_data.MontantTotal), null, true));

        if (ShowsPaymentInfo)
        {
            // "Acompte" while money is still owed, the way a French invoice names a part-payment.
            rows.Add((_data.MontantRestant > 0 ? "ACOMPTE VERSÉ" : "MONTANT PAYÉ", Money.Format(_data.MontantPaye), null, false));
            if (_data.MontantRestant > 0) rows.Add(("RESTE À PAYER", Money.Format(_data.MontantRestant), Warning, true));
            if (_data.AvoirAmount > 0)
                rows.Add(("AVOIR CLIENT", AvoirLabel, Warning, false));
        }

        var totals = new StackPanel();
        for (var i = 0; i < rows.Count; i++)
        {
            var (label, value, brush, emphasis) = rows[i];
            var size = emphasis ? ReceiptTypography.Total(_size) : ReceiptTypography.Meta(_size);
            var weight = emphasis ? FontWeights.Bold : FontWeights.Normal;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Place(grid, 0, Text(label, size, weight, brush));
            Place(grid, 1, Text(value, size, weight, brush, TextAlignment.Right));

            totals.Children.Add(new Border
            {
                Background = emphasis ? Shade : Brushes.Transparent,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1, i == 0 ? 1 : emphasis ? 2 : 0, 1, 1),
                Padding = new Thickness(8, 4, 8, 4),
                Child = grid,
            });
        }
        Place(zone, 2, totals);

        return zone;
    }

    private StackPanel A4PaymentHistory()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var size = ReceiptTypography.TableSmall(_size);
        panel.Children.Add(Text("HISTORIQUE DES PAIEMENTS", ReceiptTypography.Table(_size), FontWeights.Bold, margin: new Thickness(0, 0, 0, 4)));

        var table = new Grid();
        foreach (var width in new[] { 30.0, 140, 140, 120 })
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i <= _data.Paiements.Count; i++) table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        string[] headers = ["#", "Date", $"Montant ({Money.Label})", "Mode", "Par"];
        for (var c = 0; c < headers.Length; c++)
        {
            Place(table, c, new Border
            {
                Background = Shade, BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6, 3, 6, 3), Child = Text(headers[c], size, FontWeights.Bold),
            });
        }

        var cell = new Thickness(6, 3, 6, 3);
        for (var r = 0; r < _data.Paiements.Count; r++)
        {
            var p = _data.Paiements[r];
            Place(table, 0, Text((r + 1).ToString(), size, margin: cell), r + 1);
            Place(table, 1, Text(p.Date.ToString("dd/MM/yyyy HH:mm", French), size, margin: cell), r + 1);
            Place(table, 2, Text(Money.FormatPlain(p.Amount), size, FontWeights.SemiBold, margin: cell), r + 1);
            Place(table, 3, Text(PaymentLabel(p.Mode), size, margin: cell), r + 1);
            Place(table, 4, Text(p.By ?? "-", size, margin: cell), r + 1);
        }

        panel.Children.Add(new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Child = table });
        return panel;
    }

    private Grid A4Bottom()
    {
        var bottom = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        if (Show(ReceiptSections.Footer))
            left.Children.Add(Text(FooterText, ReceiptTypography.Body(_size), FontWeights.SemiBold, Accent));
        if (ShowsQr)
        {
            if (_qr is not null)
            {
                left.Children.Add(new Image
                {
                    Source = ImageHelper.FromBytes(_qr), MaxHeight = 110, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
                });
            }
            if (_settings.NoteUnderQr is { } note)
                left.Children.Add(Text(note, ReceiptTypography.Table(_size), brush: Muted, margin: new Thickness(0, 4, 0, 0)));
        }
        Place(bottom, 0, left);

        if (Show(ReceiptSections.Signature))
        {
            var signatures = new StackPanel { Orientation = Orientation.Horizontal };
            signatures.Children.Add(SignatureBox("Signature du client", new Thickness(0, 0, 14, 0)));
            signatures.Children.Add(SignatureBox("Cachet et signature", new Thickness(0)));
            Place(bottom, 2, signatures);
        }

        return bottom;

        FrameworkElement SignatureBox(string label, Thickness margin)
        {
            var stack = new StackPanel { Width = 160, Margin = margin };
            stack.Children.Add(Text(label, ReceiptTypography.Scale(9, _size), FontWeights.SemiBold, Faint));
            stack.Children.Add(new Border
            {
                Height = 80, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Margin = new Thickness(0, 3, 0, 0),
            });
            return stack;
        }
    }

    // =====================================================================================
    // Pieces shared by the two tickets
    // =====================================================================================

    private void AddCenteredHeader(StackPanel s, double logoHeight)
    {
        if (Show(ReceiptSections.Logo) && _logo is not null)
        {
            s.Children.Add(new Image
            {
                Source = ImageHelper.FromBytes(_logo), MaxHeight = logoHeight, Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        if (Show(ReceiptSections.Company))
            s.Children.Add(Text(_company, ReceiptTypography.Company(_size), FontWeights.Bold, Accent, TextAlignment.Center));

        if (Show(ReceiptSections.CompanyContact))
            foreach (var line in ContactLines)
                s.Children.Add(Text(line, ReceiptTypography.Scale(10, _size), brush: Faint, align: TextAlignment.Center,
                    margin: new Thickness(0, 1, 0, 0)));

        if (Show(ReceiptSections.CompanyLegal))
            foreach (var line in LegalLines)
                s.Children.Add(Text(line, ReceiptTypography.Scale(9, _size), brush: Faint, align: TextAlignment.Center));

        s.Children.Add(Text(Title, TitleSize, FontWeights.Bold, Accent, TextAlignment.Center, new Thickness(0, 4, 0, 0)));
        s.Children.Add(Text($"N° {_data.Numero}", ReceiptTypography.Meta(_size), brush: Muted, align: TextAlignment.Center,
            margin: new Thickness(0, 4, 0, 0)));
        s.Children.Add(Text($"{_data.Date.ToString("d MMMM yyyy", French)} à {_data.Date:HH:mm}", ReceiptTypography.Meta(_size),
            brush: Faint, align: TextAlignment.Center, margin: new Thickness(0, 2, 0, 0)));
    }

    /// <summary>Client, seller and cashier lines. False when all three are switched off, so
    /// the caller does not draw a separator around nothing.</summary>
    private bool AddPeople(StackPanel s)
    {
        var added = false;
        var size = ReceiptTypography.Body(_size);

        if (Show(ReceiptSections.Client))
        {
            var client = string.IsNullOrWhiteSpace(_data.ClientNom) ? "N/A"
                : string.IsNullOrWhiteSpace(_data.ClientTelephone) ? _data.ClientNom
                : $"{_data.ClientNom} ({_data.ClientTelephone})";
            s.Children.Add(Row("Client:", client, size, valueWeight: FontWeights.SemiBold));
            added = true;
        }

        if (Show(ReceiptSections.Seller) && !string.IsNullOrWhiteSpace(_data.VendeurNom))
        {
            s.Children.Add(Row($"{_settings.SellerLabel}:", PersonName.Abbreviate(_data.VendeurNom), size, valueWeight: FontWeights.SemiBold));
            added = true;
        }

        if (ShowsCashier)
        {
            s.Children.Add(Row("Caissier:", PersonName.Abbreviate(_data.CaissierNom), size, valueWeight: FontWeights.SemiBold));
            added = true;
        }

        return added;
    }

    private void AddTotals(StackPanel s, Thickness totalPadding)
    {
        var size = ReceiptTypography.Body(_size);
        var tax = TaxBreakdown;

        if (Show(ReceiptSections.Discounts))
        {
            s.Children.Add(Row("Sous-total:", Money.Format(_data.RawSubtotal), size));
            if (_data.TotalRemise > 0)
                s.Children.Add(Row(_facture ? "Remise totale:" : "Remise globale:", $"- {Money.Format(_data.TotalRemise)}", size, Danger));
        }

        if (tax is { } t)
        {
            s.Children.Add(Row("Total HT:", Money.Format(t.Ht), size));
            s.Children.Add(Row($"TVA ({TvaRateLabel} %):", Money.Format(t.Tva), size));
        }

        var totalSize = ReceiptTypography.Total(_size);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Place(grid, 0, Text(tax is null ? "TOTAL:" : "TOTAL TTC:", totalSize, FontWeights.Bold));
        Place(grid, 1, Text(Money.Format(_data.MontantTotal), totalSize, FontWeights.Bold, align: TextAlignment.Right));

        s.Children.Add(new Border
        {
            Background = Shade, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            Padding = totalPadding, Margin = new Thickness(0, 4, 0, 10), Child = grid,
        });
    }

    private void AddPaymentInfo(StackPanel s)
    {
        if (!ShowsPaymentInfo) return;

        var size = ReceiptTypography.Body(_size);
        s.Children.Add(Row("Total Payé:", Money.Format(_data.MontantPaye), size));
        s.Children.Add(Row("Mode de paiement:", PaymentLabel(_data.ModePaiement), size));
        s.Children.Add(Row("Statut:", StatutLabel(_data.StatutPaiement), size, valueWeight: FontWeights.SemiBold));
        if (_data.MontantRestant > 0)
            s.Children.Add(Row("Reste à payer:", Money.Format(_data.MontantRestant), size, Warning, FontWeights.SemiBold));
        if (_data.AvoirAmount > 0)
            s.Children.Add(Row("Avoir client:", AvoirLabel, size, Warning, FontWeights.SemiBold));
    }

    /// <summary>The facture's pay-at-till box, or the reçu's credit note box when the client
    /// overpaid and has not been paid back yet.</summary>
    private void AddNotice(StackPanel s)
    {
        if (!Show(ReceiptSections.Notice)) return;

        if (_facture)
        {
            s.Children.Add(DashedBox(_settings.FactureNoticeTitle, _settings.FactureNoticeText));
        }
        else if (_data.AvoirAmount > 0 && !_data.IsAvoirSolded)
        {
            // The amount is appended rather than configurable, so a shop cannot write a
            // notice that leaves out what the client is actually owed.
            s.Children.Add(DashedBox(_settings.AvoirNoticeTitle, $"{_settings.AvoirNoticeText} {Money.Format(_data.AvoirAmount)}"));
        }
    }

    private void AddFooter(StackPanel s)
    {
        if (!Show(ReceiptSections.Footer)) return;
        s.Children.Add(Text(FooterText, ReceiptTypography.Body(_size), FontWeights.SemiBold, Accent, TextAlignment.Center,
            new Thickness(0, 16, 0, 0)));
    }

    private void AddQr(StackPanel s, double height)
    {
        if (!ShowsQr) return;

        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (_qr is not null)
            panel.Children.Add(new Image
            {
                Source = ImageHelper.FromBytes(_qr), MaxHeight = height, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        if (_settings.NoteUnderQr is { } note)
            panel.Children.Add(Text(note, ReceiptTypography.Table(_size), brush: Muted, align: TextAlignment.Center,
                margin: new Thickness(0, 5, 0, 0)));
        s.Children.Add(panel);
    }

    private Grid DashedBox(string title, string text)
    {
        var box = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        box.Children.Add(new Rectangle
        {
            Stroke = Brushes.Black, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 }, RadiusX = 2, RadiusY = 2,
        });
        var content = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        content.Children.Add(Text(title, ReceiptTypography.Body(_size), FontWeights.Bold, align: TextAlignment.Center));
        content.Children.Add(Text(text, ReceiptTypography.Table(_size), brush: Muted, align: TextAlignment.Center,
            margin: new Thickness(0, 3, 0, 0)));
        box.Children.Add(content);
        return box;
    }

    // =====================================================================================
    // Values
    // =====================================================================================

    private bool Show(string section) => !_hidden.Contains(section);

    private bool ShowsCashier => !_facture && Show(ReceiptSections.Cashier) && !string.IsNullOrWhiteSpace(_data.CaissierNom);

    private bool ShowsPaymentInfo => !_facture && Show(ReceiptSections.PaymentInfo);

    /// <summary>Only for a sale settled in more than one instalment: with a single payment
    /// the table would just repeat the "Total Payé" line.</summary>
    private bool ShowsPaymentHistory => !_facture && Show(ReceiptSections.PaymentHistory) && _data.Paiements.Count > 1;

    private bool ShowsQr => Show(ReceiptSections.Qr) && (_qr is not null || _settings.NoteUnderQr is not null);

    private string Title => _facture ? _settings.FactureTitle : _settings.ReceiptTitle;

    private int TitleSize => _facture ? _settings.FactureTitleFontSize : _settings.ReceiptTitleFontSize;

    private string FooterText => _facture ? _settings.FactureFooterText : _settings.ReceiptFooterText;

    private string AvoirLabel => _data.IsAvoirSolded
        ? $"{Money.Format(_data.AvoirAmount)} (soldé)"
        : Money.Format(_data.AvoirAmount);

    private IEnumerable<string> ContactLines
    {
        get
        {
            if (_settings.CompanyAddress is { } address) yield return address;
            if (_settings.CompanyPhone is { } phone) yield return $"Tél : {phone}";
            if (_settings.CompanyEmail is { } email) yield return email;
        }
    }

    private IEnumerable<string> LegalLines =>
        (_settings.CompanyLegalInfo ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string TvaRateLabel => (TaxBreakdown?.Rate ?? 0).ToString("0.##", French);

    /// <summary>
    /// A sale the till added TVA to prints exactly what was charged, whatever the settings
    /// say now - even with the section switched off, since otherwise the total would exceed
    /// the lines above it with nothing to explain the difference. Otherwise, when prices
    /// include TVA, the total is split back into HT and TVA. Null when there is no rate, the
    /// section is hidden, or prices are HT but this sale was recorded before TVA was added.
    /// </summary>
    private (decimal Ht, decimal Tva, decimal Rate)? TaxBreakdown
    {
        get
        {
            if (_data.TvaAmount is > 0 && _data.TvaRate is { } charged)
                return (_data.NetTotal, _data.TvaAmount.Value, charged);

            if (!Show(ReceiptSections.Tva)) return null;

            if (_settings.TvaMode != TvaModes.Incluse || _settings.TvaRate is not { } rate || rate <= 0) return null;

            var ht = Math.Round(_data.MontantTotal / (1 + rate / 100m), Money.DecimalDigits, MidpointRounding.AwayFromZero);
            return (ht, _data.MontantTotal - ht, rate);
        }
    }

    public static string PaymentLabel(string? modePaiement) => modePaiement switch
    {
        "cash" => "Espèces",
        "mobile_money" => "Mobile Money",
        "carte" => "Carte",
        "virement" => "Virement",
        "cheque" => "Chèque",
        _ => modePaiement ?? "—",
    };

    public static string StatutLabel(string statutPaiement) => statutPaiement switch
    {
        "paye" => "Payé",
        "partiel" => "Partiel",
        "en_attente" => "En attente",
        "annule" => "Annulé",
        _ => statutPaiement,
    };

    // =====================================================================================
    // Element helpers
    // =====================================================================================

    private static TextBlock Text(
        string text, double size, FontWeight? weight = null, Brush? brush = null,
        TextAlignment align = TextAlignment.Left, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = brush ?? Brushes.Black,
        TextAlignment = align,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0),
    };

    private static Grid Row(string label, string value, double size, Brush? brush = null, FontWeight? valueWeight = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Place(grid, 0, Text(label, size, brush: brush ?? Muted, margin: new Thickness(0, 0, 8, 0)));
        Place(grid, 1, Text(value, size, valueWeight, brush, TextAlignment.Right));
        return grid;
    }

    private static Line Dashed(double width, Thickness margin) => new()
    {
        X1 = 0, Y1 = 0, X2 = width, Y2 = 0, Stroke = Brushes.Black, StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 4, 2 }, Margin = margin,
    };

    private static void Place(Grid grid, int column, UIElement element, int row = 0)
    {
        Grid.SetColumn(element, column);
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
