using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Members;

/// <summary>
/// One member's last 30 days: each connection, the day's total, and what the Programme had
/// planned - so a pattern of late arrivals or missed days is visible at a glance. The web app's
/// per-member history (GET attendance/member/:userId/history).
/// </summary>
public partial class MemberWorkHistoryDialog : Window
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private readonly AppSession _session;
    private readonly string _userId;

    public MemberWorkHistoryDialog(AppSession session, string userId, string name)
    {
        _session = session;
        _userId = userId;
        InitializeComponent();

        HeaderText.Text = name;
        SummaryText.Text = "30 derniers jours";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var history = await _session.Api.GetMemberWorkHistoryAsync(_userId);
            var rows = history.Days.Select(d => new DayRow(d, this)).ToList();

            DaysGrid.ItemsSource = rows;
            EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var worked = history.Days.Sum(d => d.TotalMinutes);
            var present = history.Days.Count(d => d.Sessions.Count > 0);
            var missed = rows.Count(r => r.IsMissed);
            SummaryText.Text = $"30 derniers jours · {present} jour(s) de présence · {AuditView.Duration(worked)} au total"
                               + (missed > 0 ? $" · {missed} jour(s) prévu(s) sans connexion" : string.Empty);
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    public sealed class DayRow(WorkDayDto day, MemberWorkHistoryDialog owner)
    {
        private bool IsPlannedWork => day.ScheduleType is "travail" or "reunion";

        public bool IsMissed => IsPlannedWork && day.Sessions.Count == 0 && day.Date < DateOnly.FromDateTime(DateTime.Today);

        /// <summary>First connection later than 5 minutes past the planned start - the same
        /// grace the server applies.</summary>
        private bool IsLate =>
            IsPlannedWork && day.ScheduleStart is { } start && day.Sessions.Count > 0
            && day.Sessions.Min(s => s.LoginAt).ToLocalTime().TimeOfDay > start + TimeSpan.FromMinutes(5);

        public string DateDisplay
        {
            get
            {
                var text = day.Date.ToDateTime(TimeOnly.MinValue).ToString("ddd d MMM yyyy", French);
                return char.ToUpper(text[0], French) + text[1..];
            }
        }

        public string PlannedDisplay => day.ScheduleType switch
        {
            null => "—",
            "repos" => "Repos",
            var type => (type == "reunion" ? "Réunion" : "Travail")
                        + (day.ScheduleStart is { } s && day.ScheduleEnd is { } e ? $" {s:hh\\:mm}-{e:hh\\:mm}" : string.Empty),
        };

        public string SessionsDisplay => day.Sessions.Count == 0
            ? "Aucune connexion"
            : string.Join("\n", day.Sessions.Select(s =>
                $"{s.LoginAt.ToLocalTime():HH:mm} → {(s.IsOpen ? "en cours" : s.LogoutAt?.ToLocalTime().ToString("HH:mm") ?? "?")}"
                + $"  ({AuditView.Duration(s.DurationMinutes)})"
                + (s.DeviceName is { } device ? $" · {device}" : string.Empty)));

        public string TotalDisplay => day.TotalMinutes > 0 ? AuditView.Duration(day.TotalMinutes) : "—";

        public string FlagDisplay => IsMissed ? "Absent" : IsLate ? "Retard" : string.Empty;

        public Brush FlagBrush => IsMissed ? owner.Brush("Danger") : owner.Brush("Warning");
    }
}
