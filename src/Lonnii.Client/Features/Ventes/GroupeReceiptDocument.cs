using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Ventes;

/// <summary>
/// The combined reçu of a "Paiement Groupé": who paid, which factures it settled, and what
/// was handed over. One layout only (an 80 mm ticket) - Lonnii Business prints it that way
/// too, with its own small template rather than the per-sale reçu layouts. It does honour the
/// shop's reçu wording, font and the sections switched off for the reçu, so the logo, company
/// name, cashier line and footer match its other reçus.
///
/// <para>
/// Always black on white with literal colours, never theme resources, for the same reason as
/// <see cref="ReceiptDocument"/>: the paper must look the same in dark mode and prints outside
/// any window's resource scope.
/// </para>
/// </summary>
public static class GroupeReceiptDocument
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Brush Accent = Frozen("#4C3B9E");
    private static readonly Brush Faint = Frozen("#666666");
    private static readonly Brush Warning = Frozen("#B45309");
    private static readonly Brush ScreenEdge = Frozen("#CCCCCC");

    private const double ContentWidth = 384;

    public static Border Build(
        GroupePaiementDto payment, ReceiptSettingsDto settings, byte[]? logo, string fallbackCompany, bool forPrint)
    {
        var hidden = settings.HiddenSections(facture: false).ToHashSet();
        var size = settings.FontSize;
        var body = ReceiptTypography.Table(size);
        var small = ReceiptTypography.TableSmall(size);
        var date = payment.Date.ToLocalTime();

        var s = new StackPanel();

        if (!hidden.Contains(ReceiptSections.Logo) && logo is not null)
        {
            s.Children.Add(new Image
            {
                Source = ImageHelper.FromBytes(logo), MaxHeight = 72, Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        if (!hidden.Contains(ReceiptSections.Company))
            s.Children.Add(Text(settings.CompanyName ?? fallbackCompany, ReceiptTypography.Company(size),
                FontWeights.Bold, Accent, TextAlignment.Center));

        s.Children.Add(Text(settings.ReceiptTitle, settings.ReceiptTitleFontSize, FontWeights.Bold, Accent,
            TextAlignment.Center, new Thickness(0, 4, 0, 0)));
        s.Children.Add(Text("Paiement groupé", ReceiptTypography.Meta(size), FontWeights.SemiBold,
            align: TextAlignment.Center, margin: new Thickness(0, 2, 0, 0)));
        s.Children.Add(Text($"{date.ToString("d MMMM yyyy", French)} à {date:HH:mm}", ReceiptTypography.Meta(size),
            brush: Faint, align: TextAlignment.Center, margin: new Thickness(0, 4, 0, 0)));
        s.Children.Add(Dashed(new Thickness(0, 12, 0, 10)));

        var people = false;
        if (!hidden.Contains(ReceiptSections.Client) && !string.IsNullOrWhiteSpace(payment.ClientName) && payment.ClientName != "N/A")
        {
            s.Children.Add(Row("Client :", payment.ClientName, ReceiptTypography.Meta(size), valueWeight: FontWeights.SemiBold));
            people = true;
        }
        if (!hidden.Contains(ReceiptSections.Cashier) && !string.IsNullOrWhiteSpace(payment.CaissierName))
        {
            s.Children.Add(Row($"{settings.SellerLabel} :", payment.CaissierName, ReceiptTypography.Meta(size), valueWeight: FontWeights.SemiBold));
            people = true;
        }
        if (people) s.Children.Add(Dashed(new Thickness(0, 10, 0, 10)));

        s.Children.Add(Text("Factures payées :", body, FontWeights.SemiBold, margin: new Thickness(0, 0, 0, 6)));
        foreach (var facture in payment.Factures)
            s.Children.Add(Row(facture.NumeroVente, Money.Format(facture.Montant), body, valueWeight: FontWeights.SemiBold));

        s.Children.Add(Dashed(new Thickness(0, 10, 0, 10)));

        s.Children.Add(Row("Total commande :", Money.Format(payment.Factures.Sum(f => f.MontantOriginal)), body));
        s.Children.Add(Row("Reste à payer :", Money.Format(0), body));
        s.Children.Add(Row("Mode de paiement :", ReceiptDocument.PaymentLabel(payment.ModePaiement), body));

        s.Children.Add(new Border
        {
            Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 8, 10, 8),
            Background = Frozen("#F0F0F0"),
            Child = Row("MONTANT VERSÉ", Money.Format(payment.MontantPaye), ReceiptTypography.Total(size),
                valueWeight: FontWeights.Bold, labelWeight: FontWeights.Bold),
        });

        if (payment.AvoirAmount > 0)
        {
            s.Children.Add(Row(payment.IsAvoirSolded ? "AVOIR (SOLDÉ)" : "AVOIR CRÉÉ", Money.Format(payment.AvoirAmount),
                body, Warning, FontWeights.Bold, FontWeights.Bold, new Thickness(0, 8, 0, 0)));
        }

        if (!hidden.Contains(ReceiptSections.Footer))
            s.Children.Add(Text(
                string.IsNullOrWhiteSpace(settings.ReceiptFooterText) ? "Merci pour votre paiement !" : settings.ReceiptFooterText,
                ReceiptTypography.Body(size), FontWeights.SemiBold, Accent, TextAlignment.Center, new Thickness(0, 16, 0, 0)));

        var paper = new Border { Width = 440, Padding = new Thickness(28), Child = s };
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

    private static Grid Row(
        string label, string value, double size, Brush? brush = null, FontWeight? valueWeight = null,
        FontWeight? labelWeight = null, Thickness? margin = null)
    {
        var grid = new Grid { Margin = margin ?? new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = Text(label, size, labelWeight, brush, margin: new Thickness(0, 0, 8, 0));
        var right = Text(value, size, valueWeight, brush, TextAlignment.Right);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static System.Windows.Shapes.Line Dashed(Thickness margin) => new()
    {
        X1 = 0, Y1 = 0, X2 = ContentWidth, Y2 = 0, Stroke = Brushes.Black, StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 4, 2 }, Margin = margin,
    };

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
