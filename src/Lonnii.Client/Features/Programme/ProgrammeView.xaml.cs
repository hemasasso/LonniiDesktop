using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Programme;

/// <summary>
/// The Programme module: a freeform monthly board of each worker's work days, meeting days
/// and days off, plus announcements shown alongside them, and a printable sheet per worker.
/// New to the desktop port - see ProgrammeEndpoints for why this does not reuse Lonnii
/// Business's own calendar_events.
/// </summary>
public partial class ProgrammeView : UserControl
{
    private readonly AppSession _session;
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly string[] Weekdays = ["Lun", "Mar", "Mer", "Jeu", "Ven", "Sam", "Dim"];

    private readonly bool _canCreate;
    private readonly bool _canEdit;
    private readonly bool _canDelete;

    private List<GroupMemberDto> _members = [];
    private DateOnly _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private List<ProgrammeEntryDto> _entries = [];
    private List<ProgrammeAnnouncementDto> _announcements = [];

    private sealed record WorkerRow(string Id, string Nom);

    public ProgrammeView(AppSession session)
    {
        _session = session;
        _canCreate = _session.Can(Priv.Option.CreateEvents);
        _canEdit = _session.Can(Priv.Option.EditEvents);
        _canDelete = _session.Can(Priv.Option.DeleteEvents);
        InitializeComponent();

        SubtitleText.Text = _session.Groupe?.Nom;
        AddAnnouncementButton.Visibility = _canCreate ? Visibility.Visible : Visibility.Collapsed;

        foreach (var day in Weekdays)
        {
            WeekdaysRow.Children.Add(new TextBlock
            {
                Text = day, FontWeight = FontWeights.Bold, FontSize = 11,
                Foreground = (Brush)FindResource("TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _members = await _session.Api.GetMembersAsync();
        }
        catch (ApiException)
        {
            _members = [];
        }

        // A non-admin sees only their own board - there is no separate privilege for
        // "view every worker's schedule", so restricting here keeps a member from browsing
        // colleagues' days just because Programme itself is visible to them.
        var isAdmin = _session.IsAdmin;
        var visibleMembers = isAdmin
            ? _members
            : _members.Where(m => m.IdUser == _session.User?.IdUser).ToList();

        var rows = visibleMembers.Select(m => new WorkerRow(m.IdUser, DisplayName(m))).ToList();
        WorkerCombo.ItemsSource = rows;
        WorkerCombo.IsEnabled = rows.Count > 1;

        var selfRow = rows.FirstOrDefault(r => r.Id == _session.User?.IdUser);
        WorkerCombo.SelectedItem = selfRow ?? rows.FirstOrDefault();
    }

    private static string DisplayName(GroupMemberDto m)
    {
        var full = $"{m.FirstName} {m.LastName}".Trim();
        return !string.IsNullOrWhiteSpace(full) ? full : m.Username ?? m.Email;
    }

    private async void WorkerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadAsync();

    private async void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        await LoadAsync();
    }

    private async void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        await LoadAsync();
    }

    private async void Today_Click(object sender, RoutedEventArgs e)
    {
        _month = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        await LoadAsync();
    }

    /// <summary>The day picked in "Aller au", outlined on the board so it stands out once the
    /// month is on screen. It stays marked until another date is picked.</summary>
    private DateOnly? _focusDate;

    /// <summary>Jumps straight to the month of the picked date - quicker than clicking ‹ › a
    /// dozen times to reach a meeting planned next year or a day to check last spring.</summary>
    private async void GoToDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (GoToDatePicker.SelectedDate is not { } picked) return;

        _focusDate = DateOnly.FromDateTime(picked);
        _month = new DateOnly(picked.Year, picked.Month, 1);
        await LoadAsync();
    }

    private string? SelectedWorkerId => (WorkerCombo.SelectedItem as WorkerRow)?.Id;
    private string SelectedWorkerName => (WorkerCombo.SelectedItem as WorkerRow)?.Nom ?? string.Empty;

    private async Task LoadAsync()
    {
        MonthLabel.Text = _month.ToString("MMMM yyyy", French);
        MonthLabel.Text = char.ToUpper(MonthLabel.Text[0], French) + MonthLabel.Text[1..];

        if (SelectedWorkerId is not { } workerId)
        {
            _entries = [];
            _announcements = [];
            BuildCalendar();
            BuildAnnouncements();
            return;
        }

        var start = _month;
        var end = _month.AddMonths(1).AddDays(-1);

        try
        {
            var response = await _session.Api.GetProgrammeAsync(start, end, workerId);
            _entries = response.Entries.ToList();
            _announcements = response.Announcements.ToList();
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
            _entries = [];
            _announcements = [];
        }

        BuildCalendar();
        BuildAnnouncements();
    }

    // --- Calendar board ---

    private void BuildCalendar()
    {
        CalendarGrid.Children.Clear();
        CalendarGrid.RowDefinitions.Clear();
        CalendarGrid.ColumnDefinitions.Clear();

        for (var i = 0; i < 7; i++)
            CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var daysInMonth = DateTime.DaysInMonth(_month.Year, _month.Month);
        var leading = ((int)_month.ToDateTime(TimeOnly.MinValue).DayOfWeek + 6) % 7; // Monday = 0
        var rows = (int)Math.Ceiling((leading + daysInMonth) / 7.0);

        for (var i = 0; i < rows; i++)
            CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });

        var entriesByDate = _entries.ToDictionary(e => e.Date);
        var today = DateOnly.FromDateTime(DateTime.Today);

        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateOnly(_month.Year, _month.Month, day);
            var index = leading + day - 1;
            entriesByDate.TryGetValue(date, out var entry);

            var cell = BuildCell(date, entry, isToday: date == today);
            Grid.SetRow(cell, index / 7);
            Grid.SetColumn(cell, index % 7);
            CalendarGrid.Children.Add(cell);
        }
    }

    private Border BuildCell(DateOnly date, ProgrammeEntryDto? entry, bool isToday)
    {
        // Plain string literals rather than Lonnii.Data.Entities.ProgrammeEntryTypes: the
        // client project does not reference Lonnii.Data, same reason ChargesView hardcodes
        // "fixe"/"variable" instead of referencing ChargeTypes.
        var (label, brush) = entry switch
        {
            { Type: "travail" } e => (TimeRange(e) is { Length: > 0 } t ? $"Travail · {t}" : "Travail", (Brush)FindResource("Accent")),
            { Type: "reunion" } e => (TimeRange(e) is { Length: > 0 } t ? $"Réunion · {t}" : "Réunion", new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6))),
            { Type: "repos" } => ("Repos", (Brush)FindResource("TextMuted")),
            _ => (null, (Brush)FindResource("TextMuted")),
        };

        var stack = new StackPanel { Margin = new Thickness(8) };
        stack.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(), FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            Foreground = isToday ? (Brush)FindResource("Accent") : (Brush)FindResource("TextPrimary"),
        });

        if (label is not null)
        {
            stack.Children.Add(new Border
            {
                Background = brush, CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = label, FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        if (!string.IsNullOrWhiteSpace(entry?.Note))
        {
            stack.Children.Add(new TextBlock
            {
                Text = entry.Note, FontSize = 10, Foreground = (Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxHeight = 40,
            });
        }

        // The picked "Aller au" day wins over today's outline, since it is what the user just asked to see.
        var isFocus = date == _focusDate;
        var cell = new Border
        {
            Margin = new Thickness(2), CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("Surface"),
            BorderBrush = isFocus ? (Brush)FindResource("Warning")
                : isToday ? (Brush)FindResource("Accent") : (Brush)FindResource("Border"),
            BorderThickness = new Thickness(isFocus ? 3 : isToday ? 2 : 1),
            Child = stack,
        };

        if (_canCreate)
        {
            cell.Cursor = Cursors.Hand;
            cell.MouseLeftButtonUp += async (_, _) => await OpenEntryDialogAsync(date, entry);
        }

        return cell;
    }

    private static string TimeRange(ProgrammeEntryDto e) =>
        e.HeureDebut is { } debut && e.HeureFin is { } fin
            ? $"{debut:hh\\:mm}-{fin:hh\\:mm}"
            : e.HeureDebut is { } d ? $"{d:hh\\:mm}" : string.Empty;

    private async Task OpenEntryDialogAsync(DateOnly date, ProgrammeEntryDto? existing)
    {
        if (SelectedWorkerId is not { } workerId) return;

        var dialog = new ProgrammeEntryDialog(workerId, SelectedWorkerName, date, existing)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (dialog.ClearRequested && existing is not null)
            {
                if (!_canDelete)
                {
                    ShowMessage("Seul un administrateur peut effacer une journée déjà planifiée.");
                    return;
                }
                await _session.Api.DeleteProgrammeEntryAsync(existing.Id);
            }
            else if (dialog.Result is { } request)
            {
                await _session.Api.SaveProgrammeEntryAsync(request);
            }

            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    // --- Announcements ---

    private void BuildAnnouncements()
    {
        AnnouncementsList.Items.Clear();

        foreach (var a in _announcements.OrderByDescending(a => a.Date))
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = a.Titre, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{a.Date.ToString("d MMMM", French)}" + (a.UserId is null ? " · Tout le monde" : $" · {a.UserName}"),
                FontSize = 11, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 2, 0, 4),
            });
            stack.Children.Add(new TextBlock
            {
                Text = a.Message, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });

            if (_canEdit || _canDelete)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

                if (_canEdit)
                {
                    var editButton = new Button { Content = "Modifier", Style = (Style)FindResource("SecondaryButton"), Margin = new Thickness(0, 0, 6, 0) };
                    editButton.Click += async (_, _) => await EditAnnouncementAsync(a);
                    actions.Children.Add(editButton);
                }

                if (_canDelete)
                {
                    var deleteButton = new Button { Content = "Supprimer", Style = (Style)FindResource("DangerButton") };
                    deleteButton.Click += async (_, _) => await DeleteAnnouncementAsync(a);
                    actions.Children.Add(deleteButton);
                }

                stack.Children.Add(actions);
            }

            AnnouncementsList.Items.Add(new Border
            {
                Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8),
                Child = stack,
            });
        }
    }

    private async void AddAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProgrammeAnnouncementDialog(_members, null, _month)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.CreateProgrammeAnnouncementAsync(request);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private async Task EditAnnouncementAsync(ProgrammeAnnouncementDto announcement)
    {
        var dialog = new ProgrammeAnnouncementDialog(_members, announcement, _month)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } request) return;

        try
        {
            await _session.Api.UpdateProgrammeAnnouncementAsync(announcement.Id, request);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private async Task DeleteAnnouncementAsync(ProgrammeAnnouncementDto announcement)
    {
        if (MessageBox.Show(Window.GetWindow(this), $"Supprimer l'annonce « {announcement.Titre} » ?",
                "Supprimer l'annonce", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _session.Api.DeleteProgrammeAnnouncementAsync(announcement.Id);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    // --- Print ---

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWorkerId is null) return;

        var start = _month;
        var end = _month.AddMonths(1).AddDays(-1);

        var dialog = new ProgrammeSheetDialog(
            _session.Groupe?.Nom, SelectedWorkerName, start, end, _entries, _announcements)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    // --- Messages ---

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;
}
