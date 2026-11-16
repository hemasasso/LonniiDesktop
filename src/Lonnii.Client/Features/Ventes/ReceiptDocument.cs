using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Ventes;

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
            ReceiptTemplates.A5 => document.BuildA5(),
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
            if (paper.MinHeight > 0) ShowPageBreaks(paper);
        }

        return paper;
    }

    /// <summary>
    /// Asks for a printer and prints a freshly built paper, scaled down if it is wider than
    /// the printer can print - an 80 mm ticket sent to a 58 mm roll would otherwise lose its
    /// right-hand column. An A4 page is also scaled to fit the page's height, so a long sale
    /// prints small rather than cut off at the bottom.
    /// </summary>
    public static void Print(Func<Border> buildForPrint, bool singlePage, string jobName, bool a5 = false)
    {
        var dialog = new PrintDialog();

        // Straight to Windows' default printer: asking every time annoyed shops with one printer.
        // The choice still opens with Shift held, or when there is no default printer.
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0 && HasDefaultPrinter(dialog))
        {
            try
            {
                PrintOn(dialog, buildForPrint, singlePage, jobName, a5);
                return;
            }
            catch (Exception)
            {
                dialog = new PrintDialog(); // the default printer failed: let the user pick another
            }
        }

        if (a5) AskForA5(dialog);
        if (dialog.ShowDialog() != true) return;
        PrintOn(dialog, buildForPrint, singlePage, jobName, a5);
    }

    /// <summary>
    /// Prints on this computer's default printer without asking - a phone's request, the person
    /// being at the counter rather than at this screen. Throws when Windows has no printer.
    /// </summary>
    public static void PrintSilently(Func<Border> buildForPrint, bool singlePage, string jobName, bool a5 = false)
    {
        var dialog = new PrintDialog();
        _ = dialog.PrintQueue ?? throw new InvalidOperationException("Aucune imprimante par défaut sur ce poste.");
        PrintOn(dialog, buildForPrint, singlePage, jobName, a5);
    }

    /// <summary>Asks the printer for A5 sheets. A printer without A5 keeps its own paper, and the
    /// half-size page then prints centred at the top of it.</summary>
    private static void AskForLandscape(PrintDialog dialog)
    {
        try
        {
            if (dialog.PrintTicket is { } ticket) ticket.PageOrientation = System.Printing.PageOrientation.Landscape;
        }
        catch (Exception)
        {
            // The driver refused: the page is then scaled to fit portrait paper.
        }
    }

    private static void AskForA5(PrintDialog dialog)
    {
        try
        {
            if (dialog.PrintTicket is { } ticket)
                ticket.PageMediaSize = new System.Printing.PageMediaSize(System.Printing.PageMediaSizeName.ISOA5);
        }
        catch (Exception)
        {
            // The driver refused the paper size: print on what it has.
        }
    }

    private static void PrintOn(PrintDialog dialog, Func<Border> buildForPrint, bool singlePage, string jobName, bool a5 = false)
    {
        if (a5) AskForA5(dialog);
        var paper = buildForPrint();
        if (paper.Tag as string == PageOrientations.Paysage) AskForLandscape(dialog);
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        paper.Measure(unbounded);
        var desired = paper.DesiredSize;

        var (originX, originY) = ImageableOrigin(dialog);
        var scale = 1.0;
        if (dialog.PrintableAreaWidth > 0)
            scale = Math.Min(scale, Math.Max(1, dialog.PrintableAreaWidth - 2 * originX) / desired.Width);

        // A4 / A5 longer than one sheet continues on the next page instead of shrinking.
        // Its MinHeight (the sheet's height) is dropped first: only the content counts.
        if (singlePage && dialog.PrintableAreaHeight > 0)
        {
            paper.MinHeight = 0;
            paper.Measure(unbounded);
            var usable = Math.Max(1, dialog.PrintableAreaHeight - 2 * originY);
            // Always through the paged printer, so every sheet carries "Page 1 / N".
            PrintPaged(dialog, paper, scale, originY, usable, jobName);
            return;
        }

        if (scale < 1) paper.LayoutTransform = new ScaleTransform(scale, scale);

        // Centred in the printable area, past the printer's own unprintable margin: arranged at
        // (0,0) the ticket/facture came out pushed to the left on wider paper.
        var page = new Grid();
        paper.HorizontalAlignment = HorizontalAlignment.Center;
        paper.VerticalAlignment = VerticalAlignment.Top;
        page.Children.Add(paper);

        paper.Measure(unbounded);
        var width = dialog.PrintableAreaWidth > 0
            ? Math.Max(dialog.PrintableAreaWidth - 2 * originX, paper.DesiredSize.Width)
            : paper.DesiredSize.Width;
        page.Width = width;
        page.Margin = new Thickness(originX, originY, 0, 0);
        page.Measure(unbounded);
        page.Arrange(new Rect(page.DesiredSize));
        page.UpdateLayout();

        dialog.PrintVisual(page, jobName);
    }

    /// <summary>On screen, an A4/A5 page longer than one sheet shows where each printed page ends
    /// ("— Page 1 / 2 —" on a dashed line). Nothing is drawn when it fits on one sheet.</summary>
    private static void ShowPageBreaks(Border paper)
    {
        if (paper.Child is not UIElement content) return;
        var overlay = new Canvas { IsHitTestVisible = false };
        var host = new Grid();
        paper.Child = null;
        host.Children.Add(content);
        host.Children.Add(overlay);
        paper.Child = host;

        var sheet = paper.MinHeight;
        void Refresh()
        {
            overlay.Children.Clear();
            var total = paper.ActualHeight;
            if (total <= sheet + 1) return;
            var texts = TextBounds(paper).ToList();
            var cuts = new List<double>();
            for (var start = 0.0; start + sheet < total - 1;)
            {
                var end = SafeCut(texts, start, start + sheet - PageNumberRoom, sheet - PageNumberRoom);
                cuts.Add(end);
                start = end;
            }
            var count = cuts.Count + 1;
            var offset = paper.Padding.Top + paper.BorderThickness.Top;
            var width = Math.Max(0, paper.ActualWidth - paper.Padding.Left - paper.Padding.Right);
            for (var i = 0; i < cuts.Count; i++)
            {
                var y = cuts[i] - offset;
                var line = new Line
                {
                    X1 = -paper.Padding.Left, X2 = width + paper.Padding.Right, Y1 = y, Y2 = y,
                    Stroke = Brushes.IndianRed, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 6, 4 },
                };
                overlay.Children.Add(line);
                var label = new TextBlock
                {
                    Text = $"Page {i + 1} / {count}  ·  suite page {i + 2}", FontSize = 10,
                    Foreground = Brushes.IndianRed, Background = Brushes.White, Padding = new Thickness(6, 0, 6, 0),
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, Math.Max(0, (width - label.DesiredSize.Width) / 2));
                Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
                overlay.Children.Add(label);
            }
        }
        var pending = false;
        paper.SizeChanged += (_, _) =>
        {
            if (pending) return;
            pending = true;
            paper.Dispatcher.BeginInvoke(() => { pending = false; Refresh(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    /// <summary>Room kept at the foot of each printed sheet for "Page X / N".</summary>
    private const double PageNumberRoom = 18;

    /// <summary>Prints a tall page over several sheets. Each sheet shows a slice of the same
    /// paper; slices end between lines of text, never through one.</summary>
    private static void PrintPaged(PrintDialog dialog, Border paper, double scale, double originY, double usable, string jobName)
    {
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        paper.Measure(unbounded);
        paper.Arrange(new Rect(paper.DesiredSize));
        paper.UpdateLayout();

        var width = paper.ActualWidth;
        var total = paper.ActualHeight;
        var sliceHeight = usable / scale;
        // A sale longer than one sheet keeps room at each foot for "Page X / N".
        if (total > sliceHeight + 1) sliceHeight = (usable - PageNumberRoom) / scale;
        var texts = TextBounds(paper).ToList();

        var pageWidth = dialog.PrintableAreaWidth;
        var pageHeight = dialog.PrintableAreaHeight;
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);

        var starts = new List<(double Start, double End)>();
        for (var start = 0.0; start < total - 1;)
        {
            var end = Math.Min(start + sliceHeight, total);
            if (end < total) end = SafeCut(texts, start, end, sliceHeight);
            starts.Add((start, end));
            start = end;
        }

        for (var i = 0; i < starts.Count; i++)
        {
            var (start, end) = starts[i];
            var slice = new Rectangle
            {
                Width = width * scale,
                Height = (end - start) * scale,
                Fill = new VisualBrush(paper)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, start, width, end - start),
                    Stretch = Stretch.Fill,
                },
            };
            var sheet = new FixedPage { Width = pageWidth, Height = pageHeight, Background = Brushes.White };
            FixedPage.SetLeft(slice, Math.Max(0, (pageWidth - slice.Width) / 2));
            FixedPage.SetTop(slice, originY);
            sheet.Children.Add(slice);

            if (starts.Count > 1)
            {
                var number = new TextBlock
                {
                    Text = $"Page {i + 1} / {starts.Count}", FontSize = 9, Foreground = Brushes.Gray,
                    Width = pageWidth, TextAlignment = TextAlignment.Center,
                };
                FixedPage.SetTop(number, Math.Max(0, pageHeight - originY - 14));
                sheet.Children.Add(number);
            }

            var content = new PageContent();
            ((System.Windows.Markup.IAddChild)content).AddChild(sheet);
            document.Pages.Add(content);
        }

        dialog.PrintDocument(document.DocumentPaginator, jobName);
    }

    /// <summary>Where every line of text sits on the paper, so a page break can avoid them.</summary>
    private static IEnumerable<Rect> TextBounds(Visual root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is FrameworkElement { ActualHeight: > 0 } element and (TextBlock or Image))
            {
                Rect? bounds = null;
                try { bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize)); }
                catch (InvalidOperationException) { }
                if (bounds is { } b) yield return b;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>Moves a page break up until it falls between lines; gives up past half a page.</summary>
    private static double SafeCut(List<Rect> texts, double start, double end, double sliceHeight)
    {
        var cut = end;
        for (var guard = 0; guard < 200; guard++)
        {
            var hit = texts.FirstOrDefault(r => r.Top < cut - 0.5 && r.Bottom > cut + 0.5);
            if (hit == default) return cut;
            cut = hit.Top - 1;
            if (cut <= start + sliceHeight / 2) return end;
        }
        return cut;
    }

    private static bool HasDefaultPrinter(PrintDialog dialog)
    {
        try { return dialog.PrintQueue is not null; }
        catch (Exception) { return false; }
    }

    /// <summary>Where the printer can start printing on the page (its unprintable margin), or 0.</summary>
    private static (double X, double Y) ImageableOrigin(PrintDialog dialog)
    {
        try
        {
            var area = dialog.PrintQueue?.GetPrintCapabilities(dialog.PrintTicket)?.PageImageableArea;
            return area is null ? (0, 0) : (area.OriginWidth, area.OriginHeight);
        }
        catch (Exception)
        {
            // A driver that cannot report its capabilities prints from the corner, as before.
            return (0, 0);
        }
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
        Place(grid, 1, Text(line.QuantityLabel, body, align: TextAlignment.Center));
        if (showPrice) Place(grid, 2, Text(Money.FormatPlain(line.UnitPrice), body, align: TextAlignment.Right));

        FrameworkElement total;
        if (line.Discount > 0 && Show(ReceiptSections.Discounts))
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var raw = Text(Money.FormatPlain(line.Gross), small, brush: Brushes.Gray, align: TextAlignment.Right);
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
        if (!ShowsSignature) return;

        // The compact ticket has room for one line: the client's, or the cachet when the shop
        // turned the client's signature off.
        var client = ShowsClientSignature;
        var cachet = twoParties || !client;
        var both = client && cachet;

        var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (both)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        if (client) Place(grid, 0, SignatureLine("Signature client"));
        if (cachet) Place(grid, both ? 2 : 0, SignatureLine("Cachet / signature"));
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
            var qty = line.Unite is null ? line.QuantityLabel : $"{line.QuantityLabel} ({line.Unite})";
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

    /// <summary>True while building the A5 page: same layout, narrower columns.</summary>
    private bool _a5;

    /// <summary>Mode / Statut moved under "Arrêté le présent…" when the totals split in two blocks.</summary>
    private StackPanel? _paymentInfoBelow;
    private StackPanel? _noticeBelow;

    /// <summary>A width on the A4 page, narrowed when the page is narrow (A5 portrait).</summary>
    private double W(double a4, double a5) => _a5 && !_settings.Landscape ? a5 : a4;

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
        if (ShowsServeur) references.Add($"{_settings.ServeurLabel} : {_data.ServeurNom}");
        if (ShowsCashier) references.Add($"Caissier : {_data.CaissierNom}");
        // The client is a plain line too: a box took a lot of room for "N/A".
        string? clientLine = null;
        if (Show(ReceiptSections.Client) && !string.IsNullOrWhiteSpace(_data.ClientNom))
        {
            var client = $"Client : {_data.ClientNom.Trim()}";
            if (!string.IsNullOrWhiteSpace(_data.ClientTelephone)) client += $" · Tél : {_data.ClientTelephone}";
            if (!string.IsNullOrWhiteSpace(_data.ClientEmail)) client += $" · {_data.ClientEmail}";
            clientLine = client;
        }
        // Client on the left; vendeur / serveur / caissier at the right end.
        if (references.Count > 0 || clientLine is not null)
        {
            var line = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (clientLine is not null) Place(line, 0, Text(clientLine, ReceiptTypography.Meta(_size), brush: Muted));
            if (references.Count > 0)
                Place(line, 1, Text(string.Join("        ", references), ReceiptTypography.Meta(_size), brush: Muted,
                    align: TextAlignment.Right, margin: new Thickness(16, 0, 0, 0)));
            body.Children.Add(line);
        }

        body.Children.Add(new Border
        {
            BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 2, 0, 0), Margin = new Thickness(0, 10, 0, 12),
        });

        body.Children.Add(A4ItemTable());
        _paymentInfoBelow = null;
        _noticeBelow = null;
        body.Children.Add(A4TotalsZone());
        // Order under the totals: "Arrêté le présent…", Mode / Statut, then the emballages.
        if (_settings.AmountInWords(_facture)) body.Children.Add(A4AmountInWords());
        if (_paymentInfoBelow is { } paymentInfo) body.Children.Add(paymentInfo);
        if (_data.EmballagesTable(_settings) is { } table)
            body.Children.Add(EmballagesBlock(table.Columns, table.Note));
        else if (_data.EmballagesLine(_settings) is { } emballages)
            body.Children.Add(Text(emballages, ReceiptTypography.Meta(_size), FontWeights.SemiBold,
                align: TextAlignment.Left, margin: new Thickness(0, 6, 0, 0)));
        // "À régler à la caisse" / avoir notice after the emballages.
        if (_noticeBelow is { } notice) body.Children.Add(notice);

        if (ShowsPaymentHistory) body.Children.Add(A4PaymentHistory());

        body.Children.Add(A4Bottom());

        var (width, height) = (_a5, _settings.Landscape) switch
        {
            (true, false) => (559d, 794d),
            (true, true) => (794d, 559d),
            (false, true) => (1123d, 794d),
            _ => (794d, 1123d),
        };
        return new Border
        {
            Width = width, MinHeight = height, Padding = new Thickness(_a5 ? 26 : 44), Child = root,
            Tag = _settings.Landscape ? PageOrientations.Paysage : null,
        };
    }

    /// <summary>A5: the very same A4 page drawn at A5 size (A5 is A4 x 1/sqrt 2), so both look alike.</summary>
    private Border BuildA5()
    {
        // Laid out at A5 size with readable text, rather than the A4 page shrunk (tiny print).
        _a5 = true;
        try { return BuildA4(); }
        finally { _a5 = false; }
    }

    private Grid A4Header()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(W(300, 210)) });

        var sender = new StackPanel();
        if (Show(ReceiptSections.Logo) && _logo is not null)
        {
            sender.Children.Add(new Image
            {
                Source = ImageHelper.FromBytes(_logo), MaxHeight = W(80, 56), MaxWidth = W(240, 160), Stretch = Stretch.Uniform,
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

        Place(header, 2, right);

        return header;
    }

    /// <summary>A bordered table with column rules running the full height. Its height
    /// follows the sale: it grows with each line instead of a fixed empty box.</summary>
    private Border A4ItemTable()
    {
        var showPrice = Show(ReceiptSections.UnitPrice);
        var table = new Grid();

        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Grows to fit "2 + 1/2 Caisse" on one line; the désignation column gives up the room.
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = W(90, 64) });
        if (showPrice) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(W(120, 84)) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(W(130, 96)) });
        var columns = table.ColumnDefinitions.Count;

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var _ in _data.Lines) table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });

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
            var qtyText = line.Unite is null ? line.QuantityLabel : $"{line.QuantityLabel} ({line.Unite})";
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
        zone.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(W(320, 240)) });

        var left = new StackPanel();
        if (ShowsPaymentInfo)
        {
            left.Children.Add(Text($"Mode de paiement : {PaymentModeLine(_data.ModePaiement, _data.Paiements)}", ReceiptTypography.Meta(_size)));
            left.Children.Add(Text($"Statut : {StatutLabel(_data.StatutPaiement)}", ReceiptTypography.Meta(_size), FontWeights.SemiBold,
                margin: new Thickness(0, 2, 0, 0)));
        }
        var noticeAt = left.Children.Count;
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

        // With extra charges the total splits in two blocks side by side - the tax block ending
        // on TOTAL TTC, then the charges ending on NET À PAYER - so the page stays short.
        var charges = ChargeRows.ToList();
        var second = rows;
        if (charges.Count > 0)
        {
            rows.Add((tax is null ? "TOTAL" : "TOTAL TTC", Money.Format(_data.MontantTotal - _data.ChargesTotal), null, true));
            second = [];
            foreach (var (label, amount) in charges)
                second.Add((label.ToUpper(French), Money.Format(amount), null, false));
            second.Add(("NET À PAYER", Money.Format(_data.MontantTotal), null, true));
        }
        else
        {
            rows.Add((tax is null ? "TOTAL" : "TOTAL TTC", Money.Format(_data.MontantTotal), null, true));
        }

        if (ShowsPaymentInfo)
        {
            // "Acompte" while money is still owed, the way a French invoice names a part-payment.
            second.Add((_data.MontantRestant > 0 ? "ACOMPTE VERSÉ" : "MONTANT PAYÉ", Money.Format(_data.MontantPaye), null, false));
            if (_data.MontantRestant > 0) second.Add(("RESTE À PAYER", Money.Format(_data.MontantRestant), Warning, true));
            if (_data.AvoirAmount > 0)
                second.Add(("AVOIR CLIENT", AvoirLabel, Warning, false));
        }

        if (charges.Count == 0)
        {
            Place(zone, 2, TotalsBlock(rows));
            return zone;
        }

        // Two blocks: payment info moves above them.
        var split = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Payment mode and status go under the totals, just before the amount in words.
        left.Margin = new Thickness(0, 10, 0, 0);
        zone.Children.Remove(left);
        Grid.SetColumnSpan(left, 3);
        var a = TotalsBlock(rows); a.VerticalAlignment = VerticalAlignment.Top;
        var b = TotalsBlock(second); b.VerticalAlignment = VerticalAlignment.Top;
        Place(split, 0, a);
        Place(split, 2, b);
        // Mode / Statut are added by the page after "Arrêté le présent…", the notice after the emballages.
        _paymentInfoBelow = left;
        if (left.Children.Count > noticeAt)
        {
            var notice = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            while (left.Children.Count > noticeAt)
            {
                var child = left.Children[noticeAt];
                left.Children.RemoveAt(noticeAt);
                notice.Children.Add(child);
            }
            _noticeBelow = notice;
        }
        return split;
    }

    private StackPanel TotalsBlock(List<(string Label, string Value, Brush? Brush, bool Emphasis)> rows)
    {
        var totals = new StackPanel();
        for (var i = 0; i < rows.Count; i++)
        {
            var (label, value, brush, emphasis) = rows[i];
            // TOTAL TTC / NET À PAYER: the size of the table's QUANTITÉ / P.U / MONTANT headings.
            var size = emphasis ? ReceiptTypography.Table(_size) : ReceiptTypography.Meta(_size);
            var weight = emphasis ? FontWeights.Bold : FontWeights.Normal;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Place(grid, 0, Text(label, size, weight, brush));
            Place(grid, 1, Text(value, size, weight, brush, TextAlignment.Right));

            // Only the totals and what was paid are boxed; the detail lines above stay plain.
            var boxed = IsBoxed(rows[i]);
            var previousBoxed = i > 0 && IsBoxed(rows[i - 1]);
            totals.Children.Add(new Border
            {
                Background = emphasis ? Shade : Brushes.Transparent,
                BorderBrush = Brushes.Black,
                BorderThickness = !boxed ? new Thickness(0)
                    : new Thickness(1, emphasis ? 2 : previousBoxed ? 0 : 1, 1, 1),
                Padding = boxed ? new Thickness(8, 4, 8, 4) : new Thickness(8, 2, 8, 2),
                Margin = boxed && !previousBoxed && i > 0 ? new Thickness(0, 4, 0, 0) : new Thickness(0),
                Child = grid,
            });
        }
        return totals;
    }

    private static bool IsBoxed((string Label, string Value, Brush? Brush, bool Emphasis) row) =>
        row.Emphasis || row.Label is "MONTANT PAYÉ" or "ACOMPTE VERSÉ" or "RESTE À PAYER" or "AVOIR CLIENT";

    /// <summary>"Emballages de 12 : (1 cs (Cast)) prise" / "... Rendus" - one column per size in
    /// this sale, then the credit / what is left to bring back.</summary>
    private StackPanel EmballagesBlock(IReadOnlyList<ReceiptData.EmballageColumn> columns, string? note)
    {
        var size = ReceiptTypography.Meta(_size);
        var block = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var grid = new Grid();
        for (var i = 0; i < columns.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            for (var row = 0; row < 2; row++)
            {
                var head = Text(c.Header, size, FontWeights.SemiBold, margin: new Thickness(0, 1, 6, 1));
                var value = Text($"{(row == 0 ? c.Pris : c.Rendus)} {(row == 0 ? "prise" : "Rendus")}", size,
                    margin: new Thickness(0, 1, 16, 1));
                Grid.SetRow(head, row); Grid.SetColumn(head, i * 2);
                Grid.SetRow(value, row); Grid.SetColumn(value, i * 2 + 1);
                grid.Children.Add(head);
                grid.Children.Add(value);
            }
        }
        block.Children.Add(grid);
        if (note is not null)
            block.Children.Add(Text(note, size, FontWeights.SemiBold, margin: new Thickness(0, 2, 0, 0)));
        return block;
    }

    /// <summary>"Montant en lettres" (receipt settings): the total written out under the figures,
    /// as French invoices state it.</summary>
    private TextBlock A4AmountInWords()
    {
        var sentence = Lonnii.Shared.Text.FrenchNumberWords.Sentence(
            _data.MontantTotal, Money.Label, Money.DecimalDigits, _facture,
            paid: _data.MontantPaye, reste: _data.MontantRestant,
            avoir: _data.IsAvoirSolded ? 0 : _data.AvoirAmount);
        var block = Text(sentence, ReceiptTypography.Meta(_size), FontWeights.SemiBold,
            margin: new Thickness(0, 10, 0, 0));
        block.TextWrapping = TextWrapping.Wrap;
        block.FontStyle = FontStyles.Italic;
        return block;
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

        if (ShowsSignature)
        {
            var signatures = new StackPanel { Orientation = Orientation.Horizontal };
            if (ShowsClientSignature)
                signatures.Children.Add(SignatureBox("Signature du client", new Thickness(0, 0, 14, 0)));
            signatures.Children.Add(SignatureBox("Cachet et signature", new Thickness(0)));
            Place(bottom, 2, signatures);
        }

        return bottom;

        FrameworkElement SignatureBox(string label, Thickness margin)
        {
            var stack = new StackPanel { Width = W(160, 120), Margin = margin };
            stack.Children.Add(Text(label, ReceiptTypography.Scale(9, _size), FontWeights.SemiBold, Faint));
            stack.Children.Add(new Border
            {
                // Blank room to sign/stamp, no line: a line pushed short reçus onto a 2nd page.
                Height = 30, Margin = new Thickness(0, 3, 0, 0),
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

        if (ShowsServeur)
        {
            s.Children.Add(Row($"{_settings.ServeurLabel}:", _data.ServeurNom!, size, valueWeight: FontWeights.SemiBold));
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

        foreach (var (label, amount) in ChargeRows)
            s.Children.Add(Row($"{label}:", Money.Format(amount), size));
        if (_data.EmballagesLine(_settings) is { } emballagesTicket)
            s.Children.Add(Text(emballagesTicket, size, FontWeights.SemiBold, margin: new Thickness(0, 2, 0, 2)));

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
        s.Children.Add(Row("Mode de paiement:", PaymentModeLine(_data.ModePaiement, _data.Paiements), size));
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

    /// <summary>Left off when the seller line already names the same person - one cashier who
    /// both sold and took the money read twice ("Caissier : S. Hema" / "Caissier : S. Hema").</summary>
    private bool ShowsCashier => !_facture && Show(ReceiptSections.Cashier) && !string.IsNullOrWhiteSpace(_data.CaissierNom)
        && !(Show(ReceiptSections.Seller) && string.Equals(_data.VendeurNom?.Trim(), _data.CaissierNom.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The serveur line: only while the shop has the field on, its section is ticked,
    /// and the sale actually names one. Under the shop's own label ("Agent : Koffi").</summary>
    private bool ShowsServeur => _settings.ServeurEnabled && Show(ReceiptSections.Serveur)
        && !string.IsNullOrWhiteSpace(_data.ServeurNom);

    private bool ShowsPaymentInfo =>!_facture && Show(ReceiptSections.PaymentInfo);

    /// <summary>Only for a sale settled in more than one instalment: with a single payment
    /// the table would just repeat the "Total Payé" line.</summary>
    /// <summary>The shop's Signature switch - forced on whenever the sale created an avoir: a
    /// credit owed to the customer is a commitment both sides sign and stamp.</summary>
    private bool ShowsSignature => Show(ReceiptSections.Signature) || _data.AvoirAmount > 0;

    /// <summary>The customer's box within the zone - its own switch, honoured even on an avoir:
    /// some customers refuse to sign, and the shop's cachet still prints.</summary>
    private bool ShowsClientSignature => Show(ReceiptSections.ClientSignature);

    private bool ShowsPaymentHistory =>!_facture && Show(ReceiptSections.PaymentHistory) && _data.Paiements.Count > 1;

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
    /// What the sale added after the TVA, in this order: prélèvement, effort de guerre, consigne,
    /// livraison. Always printed when charged, whatever the sections say - the total would
    /// otherwise exceed the lines above it with nothing to explain the difference.
    /// </summary>
    private IEnumerable<(string Label, decimal Amount)> ChargeRows
    {
        get
        {
            if (_data.PrelevementAmount is > 0)
                yield return ($"{_settings.PrelevementName} ({(_data.PrelevementRate ?? 0).ToString("0.##", French)} %)", _data.PrelevementAmount.Value);
            if (_data.EffortGuerreAmount is > 0) yield return (_settings.EffortGuerreName, _data.EffortGuerreAmount.Value);
            if (_data.ConsigneAmount is > 0) yield return ("Consigne emballages", _data.ConsigneAmount.Value);
            if (_data.LivraisonAmount is > 0) yield return ("Livraison", _data.LivraisonAmount.Value);
            if (_data.ConsigneRendue is > 0) yield return ("Consigne rendue", -_data.ConsigneRendue.Value);
        }
    }

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

            // The charges added after the TVA carry none of it.
            var ttc = _data.MontantTotal - _data.ChargesTotal;
            var ht = Math.Round(ttc / (1 + rate / 100m), Money.DecimalDigits, MidpointRounding.AwayFromZero);
            return (ht, ttc - ht, rate);
        }
    }

    public static string PaymentLabel(string? modePaiement) => modePaiement switch
    {
        "cash"         => "Espèces",
        "mobile_money" => "Mobile Money",
        "orange_money" => "Orange Money",
        "carte"        => "Banque/Carte",
        "virement"     => "Virement",
        "cheque"       => "Chèque",
        "google_pay"   => "Google Pay",
        "apple_pay"    => "Apple Pay",
        "paypal"       => "PayPal",
        "mixte"        => "Mixte",
        _ => modePaiement ?? "—",
    };

    /// <summary>The "Mode de paiement" value: the method's name, or for a Paiement mixte each
    /// part with its amount (and mobile reference) - "Espèces 5 000 + Mobile Money 3 000 (réf. X)".</summary>
    public static string PaymentModeLine(string? modePaiement, IReadOnlyList<ReceiptPayment> paiements)
    {
        if (modePaiement != "mixte" || paiements.Count == 0) return PaymentLabel(modePaiement);
        return string.Join(" + ", paiements.Select(p =>
            $"{PaymentLabel(p.Mode)} {Money.FormatPlain(p.Amount)}"
            + (string.IsNullOrWhiteSpace(p.Reference) ? "" : $" (réf. {p.Reference})")));
    }

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
