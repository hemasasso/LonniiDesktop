using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lonnii.Client.Services;
using Lonnii.Client.Views.Dialogs;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Views.Modules;

/// <summary>
/// Audit → Planning &amp; Présences: who is at work right now, who came in late, who has not
/// come in, and each member's hours this week against what the Programme planned. The web
/// app's Audit screen (Audit.jsx → WorkSchedule) with its schedule editor left out, since the
/// Programme board is where hours are planned on the desktop.
///
/// Refreshes itself every minute while on screen - the same cadence clients report presence
/// at, so the board is never more than a beat behind.
/// </summary>
public partial class AuditView : UserControl
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly AppSession _session;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool _loading;

    public AuditView(AppSession session)
    {
        _session = session;
        InitializeComponent();

        _refreshTimer.Tick += async (_, _) => await LoadAsync();

        // Only poll while visible: a module stays alive in the shell's cache after the user
        // moves on, and there is no reason for it to keep asking the server in the background.
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                await LoadAsync();
                _refreshTimer.Start();
            }
            else _refreshTimer.Stop();
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var response = await _session.Api.GetAttendanceAsync();
            MessagePanel.Visibility = Visibility.Collapsed;

            var rows = response.Members.Select(m => new AttendanceRow(m, this)).ToList();
            AttendanceGrid.ItemsSource = rows;
            EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var date = response.Date.ToDateTime(TimeOnly.MinValue).ToString("dddd d MMMM yyyy", French);
            SubtitleText.Text = char.ToUpper(date[0], French) + date[1..];
            UpdatedText.Text = $"Mis à jour à {DateTime.Now:HH:mm}";

            OnlineCount.Text = rows.Count(r => r.Dto.Status is AttendanceStatuses.EnLigne or AttendanceStatuses.Inactif).ToString();
            LateCount.Text = rows.Count(r => r.Dto.RetardMinutes is not null).ToString();
            AbsentCount.Text = rows.Count(r => r.Dto.Status == AttendanceStatuses.Absent).ToString();
            PlannedCount.Text = rows.Count(r => r.Dto.ScheduleType is "travail" or "reunion").ToString();

            var scored = rows.Where(r => r.Dto.Productivity is not null).ToList();
            ProductivityAverage.Text = scored.Count == 0 ? "—" : $"{scored.Average(r => r.Dto.Productivity!.Value):0} %";
        }
        catch (ApiException ex)
        {
            MessageText.Text = ex.Message;
            MessagePanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
        }
    }

    private void AttendanceGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AttendanceGrid.SelectedItem is AttendanceRow row) OpenHistory(row);
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AttendanceRow row) OpenHistory(row);
    }

    private void OpenHistory(AttendanceRow row) =>
        new MemberWorkHistoryDialog(_session, row.Dto.UserId, row.Name) { Owner = Window.GetWindow(this) }.ShowDialog();

    internal Brush Brush(string key) => (Brush)FindResource(key);

    /// <summary>Hours and minutes the way a manager reads them: <c>7 h 30</c>, <c>45 min</c>.</summary>
    internal static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00}";

    /// <summary>One member's line on the board, with every column pre-formatted.</summary>
    public sealed class AttendanceRow(AttendanceMemberDto dto, AuditView view)
    {
        public AttendanceMemberDto Dto { get; } = dto;

        public string Name => Dto.Name;
        public string RoleDisplay => GroupRoles.DisplayName(Dto.Role);

        public string StatusDisplay => Dto.Status switch
        {
            AttendanceStatuses.EnLigne => "En ligne",
            AttendanceStatuses.Inactif => "Inactif",
            AttendanceStatuses.Retard => "En retard",
            AttendanceStatuses.Absent => "Absent",
            AttendanceStatuses.Repos => "Repos",
            _ => "Hors ligne",
        };

        public Brush StatusBrush => Dto.Status switch
        {
            AttendanceStatuses.EnLigne => view.Brush("Success"),
            AttendanceStatuses.Inactif => view.Brush("Accent"),
            AttendanceStatuses.Retard => view.Brush("Warning"),
            AttendanceStatuses.Absent => view.Brush("Danger"),
            _ => view.Brush("TextMuted"),
        };

        public string ScheduleDisplay => Dto.ScheduleType switch
        {
            null => "—",
            "repos" => "Repos",
            var type => $"{(type == "reunion" ? "Réunion" : "Travail")}{Hours(Dto.ScheduleStart, Dto.ScheduleEnd)}",
        };

        public string ArrivalDisplay => Dto.FirstLoginAt is { } at ? at.ToLocalTime().ToString("HH:mm") : "—";

        public string RetardDisplay => Dto.RetardMinutes is { } late ? Duration(late) : "—";

        public string LastSeenDisplay
        {
            get
            {
                if (Dto.LastSeenAt is not { } seen) return "Jamais";
                var local = seen.ToLocalTime();
                return local.Date == DateTime.Today ? $"Aujourd'hui {local:HH:mm}"
                    : local.Date == DateTime.Today.AddDays(-1) ? $"Hier {local:HH:mm}"
                    : local.ToString("dd/MM HH:mm");
            }
        }

        public string ModuleDisplay => Dto.CurrentModule ?? "—";

        public string DeviceDisplay => Dto.DeviceName ?? Dto.IpAddress ?? "—";

        public string WeekDisplay => Dto.WeekMinutesScheduled > 0
            ? $"{Duration(Dto.WeekMinutesWorked)} / {Duration(Dto.WeekMinutesScheduled)}"
            : Duration(Dto.WeekMinutesWorked);

        public double ProductivityValue => Dto.Productivity ?? 0;
        public string ProductivityDisplay => Dto.Productivity is { } p ? $"{p} %" : "—";
        public Visibility ProductivityVisibility => Dto.Productivity is null ? Visibility.Hidden : Visibility.Visible;

        public Brush ProductivityBrush => Dto.Productivity switch
        {
            >= 80 => view.Brush("Success"),
            >= 50 => view.Brush("Warning"),
            _ => view.Brush("Danger"),
        };

        private static string Hours(TimeSpan? start, TimeSpan? end) =>
            start is { } s && end is { } e ? $" {s:hh\\:mm}-{e:hh\\:mm}"
            : start is { } only ? $" dès {only:hh\\:mm}"
            : string.Empty;
    }
}
