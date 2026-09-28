using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// One worker's printable programme for a date range: every day's type (Travail, Réunion,
/// Repos), plus the announcements that apply to them - group-wide ones and their own. Built in
/// code rather than XAML, same reason as CaisseReportDialog: a variable-length list of days.
/// </summary>
public partial class ProgrammeSheetDialog : Window
{
    private readonly string _workerName;

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly Brush Ink = Brushes.Black;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x3B, 0x9E));
    private static readonly Brush ReunionColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
    private static readonly Brush ReposColor = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9E));

    public ProgrammeSheetDialog(
        string? groupName, string workerName, DateOnly start, DateOnly end,
        IReadOnlyList<ProgrammeEntryDto> entries, IReadOnlyList<ProgrammeAnnouncementDto> announcements)
    {
        _workerName = workerName;
        InitializeComponent();
        Build(groupName, start, end, entries, announcements);
    }

    private void Build(
        string? groupName, DateOnly start, DateOnly end,
        IReadOnlyList<ProgrammeEntryDto> entries, IReadOnlyList<ProgrammeAnnouncementDto> announcements)
    {
        var byDate = entries.ToDictionary(e => e.Date);

        Centered(groupName ?? "Lonnii", 16, FontWeights.Bold, Accent);
        Centered("PROGRAMME", 13, FontWeights.Bold, Accent, top: 4);
        Centered(_workerName, 12, FontWeights.Bold, Ink, top: 6);
        Centered($"{start:dd/MM/yyyy} — {end:dd/MM/yyyy}", 11, FontWeights.Normal, Muted, top: 2);
        Divider();

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            byDate.TryGetValue(date, out var entry);
            DayRow(date, entry);
        }

        if (announcements.Count > 0)
        {
            Divider();
            Section("ANNONCES");
            foreach (var a in announcements.OrderBy(a => a.Date))
            {
                Row($"{a.Date:dd/MM}  {a.Titre}", string.Empty, bold: true, wrapLabel: true);
                Note(a.Message);
            }
        }

        Divider();
        Centered($"Imprimé le {DateTime.Now:dd/MM/yyyy à HH:mm}", 10, FontWeights.Normal, Muted);
        Centered("Signature : ____________________", 11, FontWeights.Normal, Ink, top: 18);
    }

    private void DayRow(DateOnly date, ProgrammeEntryDto? entry)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        grid.Children.Add(new TextBlock
        {
            Text = date.ToString("dddd d MMMM", French), FontSize = 12,
            Foreground = isWeekend ? Muted : Ink,
            FontWeight = entry is not null ? FontWeights.Bold : FontWeights.Normal,
        });

        var (label, color) = entry switch
        {
            { Type: "travail" } e => (TimeRange(e), Accent),
            { Type: "reunion" } e => ($"Réunion{(TimeRange(e) is { Length: > 0 } t ? $" · {t}" : string.Empty)}", ReunionColor),
            { Type: "repos" } => ("Repos", ReposColor),
            _ => ("—", Muted),
        };

        var valueText = new TextBlock
        {
            Text = label, FontSize = 12, Foreground = color, FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);

        PaperContent.Children.Add(grid);
        if (!string.IsNullOrWhiteSpace(entry?.Note)) Note(entry.Note);
    }

    private static string TimeRange(ProgrammeEntryDto e) =>
        e.HeureDebut is { } debut && e.HeureFin is { } fin
            ? $"{debut:hh\\:mm}-{fin:hh\\:mm}"
            : e.HeureDebut is { } d ? $"à partir de {d:hh\\:mm}" : string.Empty;

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
            Text = label, FontSize = 12, Foreground = color ?? Ink, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            TextWrapping = wrapLabel ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 10, 0),
        });

        if (!string.IsNullOrEmpty(value))
        {
            var valueText = new TextBlock
            {
                Text = value, FontSize = 12, Foreground = color ?? Ink,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
        }

        PaperContent.Children.Add(grid);
    }

    private void Note(string text) =>
        PaperContent.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

    private void Divider() =>
        PaperContent.Children.Add(new Line
        {
            X1 = 0, Y1 = 0, X2 = 424, Y2 = 0, Stroke = Ink, StrokeThickness = 1,
            StrokeDashArray = [4, 2], Margin = new Thickness(0, 10, 0, 10),
        });

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() != true) return;

        printDialog.PrintVisual(ReportPaper, $"Programme - {_workerName}");
    }
}
