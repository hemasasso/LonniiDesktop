using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Lonnii.Client.Common.Controls;

/// <summary>An optional time of day, set by mouse wheel, a drop-down list or Up/Down — never typed.</summary>
public partial class TimeWheelPicker : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(TimeSpan?), typeof(TimeWheelPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((TimeWheelPicker)d).Render()));

    /// <summary>The picked time, or null when none is set.</summary>
    public TimeSpan? Value
    {
        get => (TimeSpan?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>What the first wheel turn or click fills in when no time is set yet.</summary>
    public TimeSpan DefaultValue { get; set; } = new(8, 0, 0);

    /// <summary>Minutes move in steps of this size. A stored time off the step (07:47) still
    /// shows as-is; the first turn snaps it onto the grid.</summary>
    public int MinuteStep { get; set; } = 5;

    private bool _listIsHours;

    public TimeWheelPicker()
    {
        InitializeComponent();
        Render();
    }

    private void Render()
    {
        HourText.Text = Value is { } v ? v.Hours.ToString("00") : "--";
        MinuteText.Text = Value is { } m ? m.Minutes.ToString("00") : "--";

        // A resource reference rather than a looked-up brush, so a theme switch while the
        // dialog is open still recolours the digits.
        var brush = Value is null ? "TextMuted" : "TextPrimary";
        HourText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        MinuteText.SetResourceReference(TextBlock.ForegroundProperty, brush);

        ClearButton.Visibility = Value is null ? Visibility.Hidden : Visibility.Visible;
    }

    private void Step(bool hours, int direction)
    {
        // The first interaction only fills in the default, so an empty picker never
        // lands on a surprising 01:00 or 23:00 after one accidental notch.
        if (Value is not { } current)
        {
            Value = DefaultValue;
            return;
        }

        if (hours)
        {
            Value = new TimeSpan((current.Hours + direction + 24) % 24, current.Minutes, 0);
            return;
        }

        var step = Math.Max(1, MinuteStep);
        var minutes = current.Minutes;
        var next = direction > 0
            ? (minutes / step + 1) * step
            : ((minutes + step - 1) / step - 1) * step;

        // Minutes wrap on their own rather than carrying into the hour, the way a clock
        // wheel does: 55 -> 00 keeps 08, it does not jump to 09.
        if (next >= 60) next = 0;
        if (next < 0) next = 60 - step;

        Value = new TimeSpan(current.Hours, next, 0);
    }

    private void Segment_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Step(sender == HourSegment, e.Delta > 0 ? 1 : -1);
        ((UIElement)sender).Focus();

        // Without this the dialog's own ScrollViewer would scroll too.
        e.Handled = true;
    }

    private void Segment_KeyDown(object sender, KeyEventArgs e)
    {
        var hours = sender == HourSegment;
        switch (e.Key)
        {
            case Key.Up:
                Step(hours, 1);
                break;
            case Key.Down:
                Step(hours, -1);
                break;
            case Key.Space:
            case Key.F4:
                OpenList(hours);
                break;
            case Key.Delete:
            case Key.Back:
                Value = null;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Segment_Click(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).Focus();
        OpenList(sender == HourSegment);
        e.Handled = true;
    }

    private void OpenList(bool hours)
    {
        _listIsHours = hours;

        var step = Math.Max(1, MinuteStep);
        var items = hours
            ? Enumerable.Range(0, 24).ToList()
            : Enumerable.Range(0, 60 / step).Select(i => i * step).ToList();

        var current = Value ?? DefaultValue;
        var selected = hours
            ? current.Hours
            : items.LastOrDefault(m => m <= current.Minutes);

        WheelList.ItemsSource = items;
        WheelList.SelectedItem = selected;
        WheelPopup.PlacementTarget = hours ? HourSegment : MinuteSegment;
        WheelPopup.IsOpen = true;

        // ScrollIntoView only works once the popup's list has been laid out.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            WheelList.ScrollIntoView(selected);
            if (WheelList.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem item)
                item.Focus();
        });
    }

    private void Commit(int picked)
    {
        var current = Value ?? DefaultValue;
        Value = _listIsHours
            ? new TimeSpan(picked, current.Minutes, 0)
            : new TimeSpan(current.Hours, picked, 0);

        WheelPopup.IsOpen = false;
        (_listIsHours ? HourSegment : MinuteSegment).Focus();
    }

    private void WheelList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(WheelList, e.OriginalSource as DependencyObject) is ListBoxItem { Content: int picked })
            Commit(picked);
    }

    private void WheelList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && WheelList.SelectedItem is int picked)
        {
            Commit(picked);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            WheelPopup.IsOpen = false;
            (_listIsHours ? HourSegment : MinuteSegment).Focus();
            e.Handled = true;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Value = null;
}
