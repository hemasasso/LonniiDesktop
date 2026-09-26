using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// A printable report for one caisse session - what was sold, what came in by payment type,
/// what was withdrawn and why, and how the counted cash compared with what was expected.
/// Built in code rather than XAML because most of it is a variable-length list of label/value
/// rows (one per withdrawal, optional écart lines).
/// </summary>
public partial class CaisseReportDialog : Window
{
    private readonly CaisseDto _caisse;

    // Explicit brushes on every TextBlock: the app's implicit TextBlock style sets a
    // theme-dependent Foreground, which in dark mode would print pale text on this white paper.
    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x3B, 0x9E));
    private static readonly Brush Loss = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));

    public CaisseReportDialog(CaisseDto caisse, string? groupName)
    {
        _caisse = caisse;
        InitializeComponent();
        Build(groupName);
    }

    private void Build(string? groupName)
    {
        var c = _caisse;
        var isOpen = c.Status != "closed";

        Centered(groupName ?? "Lonnii", 16, FontWeights.Bold, Accent);
        Centered("RAPPORT DE CAISSE", 13, FontWeights.Bold, Accent, top: 4);
        Centered($"Session n° {c.Id}", 11, FontWeights.Normal, Muted, top: 4);
        Divider();

        Row("Vendeur", c.UserName, bold: true);
        Row("Ouverture", c.DateOuverture.ToLocalTime().ToString("dd/MM/yyyy HH:mm"));
        Row("Fermeture", c.DateFermeture?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—");
        Row("Statut", isOpen ? "Ouverte (chiffres en cours)" : "Fermée", bold: true);
        Divider();

        Section("VENTES");
        Row("Nombre de ventes", c.TotalVentes.ToString());
        Row("Chiffre d'affaires", Money.Format(c.TotalChiffreAffaires), bold: true);
        if (c.TotalAvoir > 0) Row("Avoirs clients non soldés", Money.Format(c.TotalAvoir));
        Divider();

        Section("ENCAISSEMENTS");
        // PaiementCash already has cash-drawer movements (withdrawals, avoir refunds, older
        // factures settled) folded in - see CaisseEndpoints.ComputeLiveStatsAsync.
        Row("Espèces (net des mouvements)", Money.Format(c.PaiementCash));
        Row("Mobile Money", Money.Format(c.PaiementMobile));
        Row("Carte", Money.Format(c.PaiementCarte));
        if (c.PaiementAutres != 0) Row("Autres", Money.Format(c.PaiementAutres));
        Row("Total encaissé", Money.Format(c.TotalEncaisse), bold: true);
        Divider();

        Section("FONDS INITIAL");
        Row("Espèces", Money.Format(c.MontantInitialCash));
        if (c.MontantInitialMobile > 0) Row("Mobile Money", Money.Format(c.MontantInitialMobile));

        var retraits = c.Retraits ?? [];
        if (retraits.Count > 0)
        {
            Divider();
            Section("RETRAITS");
            foreach (var r in retraits)
            {
                Row($"{r.Date.ToLocalTime():HH:mm}  {r.Motif}", $"-{Money.Format(r.Montant)}", color: Loss, wrapLabel: true);
            }
            Row("Total retraits", $"-{Money.Format(c.TotalRetraits)}", bold: true, color: Loss);
        }

        Divider();
        Section("CONTRÔLE DES ESPÈCES");
        // For a closed session the écart is stored as counted - expected, so expected is
        // recovered from the two stored figures rather than recomputed from ones that may
        // have been corrected since (an "Ajusté" résolution moves MontantFinal).
        var expected = isOpen || c.MontantFinal is null
            ? c.MontantInitialCash + c.PaiementCash
            : c.MontantFinal.Value - c.Ecart;
        Row("Espèces attendues", Money.Format(expected));

        if (!isOpen && c.MontantFinal is { } counted)
        {
            Row("Espèces comptées", Money.Format(counted));
            var ecartText = c.Ecart switch
            {
                0 => "Aucun",
                > 0 => $"+{Money.Format(c.Ecart)} (excédent)",
                _ => $"-{Money.Format(-c.Ecart)} (manque)",
            };
            Row("Écart", ecartText, bold: true, color: c.Ecart == 0 ? Ink : Loss);
            if (c.Ecart != 0 || c.EcartResolved)
            {
                Row("Écart résolu", c.EcartResolved ? "Oui" : "Non");
                if (!string.IsNullOrWhiteSpace(c.EcartResolutionNote))
                    Note($"Résolution : {c.EcartResolutionNote}");
            }
        }

        if (!string.IsNullOrWhiteSpace(c.Notes))
        {
            Divider();
            Note($"Notes : {c.Notes}");
        }

        Divider();
        Centered($"Imprimé le {DateTime.Now:dd/MM/yyyy à HH:mm}", 10, FontWeights.Normal, Muted);
        Centered("Signature du caissier : ____________________", 11, FontWeights.Normal, Ink, top: 18);
    }

    private void Centered(string text, double size, FontWeight weight, Brush brush, double top = 0) =>
        PaperContent.Children.Add(new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
        });

    private void Section(string title) =>
        PaperContent.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ink,
            Margin = new Thickness(0, 0, 0, 6),
        });

    private void Row(string label, string value, bool bold = false, Brush? color = null, bool wrapLabel = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, Foreground = color ?? Muted,
            TextWrapping = wrapLabel ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 10, 0),
        });

        var valueText = new TextBlock
        {
            Text = value, FontSize = 12, Foreground = color ?? Ink,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);

        PaperContent.Children.Add(grid);
    }

    private void Note(string text) =>
        PaperContent.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 3),
        });

    private void Divider() =>
        PaperContent.Children.Add(new Line
        {
            X1 = 0, Y1 = 0, X2 = 384, Y2 = 0, Stroke = Ink, StrokeThickness = 1,
            StrokeDashArray = [4, 2], Margin = new Thickness(0, 10, 0, 10),
        });

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() != true) return;

        printDialog.PrintVisual(ReportPaper, $"Rapport de caisse {_caisse.Id}");
    }
}
