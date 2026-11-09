using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lonnii.Client.Services;

public enum KeypadLayout
{
    /// <summary>Numeric for date boxes, number-like names, number-typed bindings or a numeric InputScope; letters otherwise.</summary>
    Auto,
    Numeric,
    Text,
    /// <summary>Never open the keypad for this box.</summary>
    None,
}

public enum KeypadDock { Bottom, Left, Right }

/// <summary>
/// On a touch POS terminal (<see cref="DeviceProfile.IsPos"/>) tapping any TextBox or PasswordBox
/// opens an on-screen keypad docked at the bottom, left or right of the screen; the window behind
/// moves or makes room so nothing stays hidden under it. The keypad never takes focus: the box
/// keeps it, and every key goes through the box's normal text input, so PreviewTextInput filters
/// (DateTyping, digit filters) and live TextChanged handlers (search) behave as with a keyboard.
///
/// Mark a box with <c>services:TouchKeypad.Layout="Numeric"</c> in XAML, or
/// <see cref="SetLayout"/> in code, when Auto cannot tell it holds a number.
/// </summary>
public static class TouchKeypad
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.RegisterAttached(
        "Layout", typeof(KeypadLayout), typeof(TouchKeypad), new PropertyMetadata(KeypadLayout.Auto));

    public static KeypadLayout GetLayout(DependencyObject o) => (KeypadLayout)o.GetValue(LayoutProperty);
    public static void SetLayout(DependencyObject o, KeypadLayout value) => o.SetValue(LayoutProperty, value);

    public static void Register()
    {
        if (!DeviceProfile.IsPos) return;

        // Touch is promoted to mouse, so one handler covers finger, pen and mouse. handledEventsToo:
        // TextBox marks its own mouse-up handled.
        var handler = new MouseButtonEventHandler((s, _) =>
        {
            if (s is Control c && !DragScroll.JustScrolled) Open(c);
        });
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewMouseLeftButtonUpEvent, handler, true);
        EventManager.RegisterClassHandler(typeof(PasswordBox), UIElement.PreviewMouseLeftButtonUpEvent, handler, true);

        DragScroll.Register();

        // Lists (ListBox, ListView, DataGrid, TreeView) scroll a whole row at a time by default,
        // which makes a finger drag jump row by row. Pixel steps follow the finger smoothly, like
        // the Ventes product grid; virtualization stays on, so long lists stay fast.
        EventManager.RegisterClassHandler(typeof(ItemsControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is (ListBox or DataGrid or TreeView) and not ComboBox and ItemsControl list
                    && list.ReadLocalValue(VirtualizingPanel.ScrollUnitProperty) == DependencyProperty.UnsetValue)
                    VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
            }));

        // No tap dot / press-and-hold ring / flick gestures under the finger.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is not Window w) return;
                Stylus.SetIsTapFeedbackEnabled(w, false);
                Stylus.SetIsPressAndHoldEnabled(w, false);
                Stylus.SetIsFlicksEnabled(w, false);
                Stylus.SetIsTouchFeedbackEnabled(w, false);
                // A finger pulling on the top bar of a maximised window dragged it out of place
                // (BlockWindowMove). The keypad itself moves windows with SetWindowPos, unaffected.
                if (w is not TouchKeypadWindow
                    && PresentationSource.FromVisual(w) is HwndSource source)
                    source.AddHook(BlockWindowMove);
            }));
        // Never let reaching the end of a list bounce the whole window.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.ManipulationBoundaryFeedbackEvent,
            new EventHandler<ManipulationBoundaryFeedbackEventArgs>((_, e) => e.Handled = true), true);
    }

    private const int WM_SYSCOMMAND = 0x0112, SC_MOVE = 0xF010;

    private static IntPtr BlockWindowMove(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Only a maximised window stays put: pulling down on its top bar un-maximised and shifted
        // it. Normal windows and dialogs still drag freely, so one placed badly can always be
        // brought back to reach its close button.
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_MOVE && IsZoomed(hwnd)) handled = true;
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    /// <summary>Which side the keypad docks to; chosen on the keypad, remembered on this computer.</summary>
    public static KeypadDock Dock
    {
        get => Enum.TryParse<KeypadDock>(App.Settings.KeypadDock, out var d) ? d : KeypadDock.Bottom;
        set
        {
            App.Settings.KeypadDock = value.ToString();
            App.Settings.Save();
        }
    }

    private static TouchKeypadWindow? _current;

    public static void Open(Control target)
    {
        if (!target.IsEnabled || target is TextBox { IsReadOnly: true }) return;
        if (Window.GetWindow(target) is null or TouchKeypadWindow) return;

        var layout = Resolve(target);
        if (layout == KeypadLayout.None) return;
        if (_current is { } open && open.Target == target) return;

        _current?.Close();
        if (!target.IsKeyboardFocusWithin) target.Focus();

        var window = new TouchKeypadWindow(target, layout == KeypadLayout.Numeric);
        window.Closed += (_, _) => { if (_current == window) _current = null; };
        _current = window;
        window.Show();
    }

    private static readonly System.Text.RegularExpressions.Regex NumericName = new(
        "Pri[xc]|Cost|Cout|Quantit|Qty|Qte|Montant|Amount|Remise|Discount|Credit|Debit|Marge|Threshold|Seuil|" +
        "Valeur|Residuelle|Phone|Telephone|Rate|Port|Day|Jump|Gros|DetailBox|Stock|Total|Taux",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static KeypadLayout Resolve(Control target)
    {
        var layout = GetLayout(target);
        if (layout != KeypadLayout.Auto) return layout;
        if (target is PasswordBox) return KeypadLayout.Text;
        if (target is DatePickerTextBox) return KeypadLayout.Numeric;

        if (target.InputScope?.Names is { Count: > 0 } names && names[0] is InputScopeName { NameValue: var scope }
            && scope is InputScopeNameValue.Number or InputScopeNameValue.Digits or InputScopeNameValue.TelephoneNumber
                or InputScopeNameValue.CurrencyAmount)
            return KeypadLayout.Numeric;

        // Quantities, prices, amounts, phones…: told apart by the box's name across the whole app
        // (PriceBox, MontantRecuBox, QuantityBox, CreditBox…), so new screens get it for free.
        if (NumericName.IsMatch(target.Name)) return KeypadLayout.Numeric;
        // Emailed / SMS codes are 6 digits.
        if (target is TextBox { Name: "CodeBox", MaxLength: > 0 and <= 8 }) return KeypadLayout.Numeric;

        if (BindingOperations.GetBindingExpression(target, TextBox.TextProperty) is { } binding
            && binding.ResolvedSource is { } source && binding.ResolvedSourcePropertyName is { } name)
        {
            var type = source.GetType().GetProperty(name)?.PropertyType;
            type = type is null ? null : Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(int) || type == typeof(long) || type == typeof(decimal)
                || type == typeof(double) || type == typeof(float) || type == typeof(short))
                return KeypadLayout.Numeric;
        }

        return KeypadLayout.Text;
    }

    internal static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        for (var c = d; c is not null;
             c = c is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(c) : LogicalTreeHelper.GetParent(c))
            if (c is T t) return t;
        return null;
    }
}

/// <summary>
/// Press and drag anywhere in a scrollable area to scroll it, like a phone. Built on mouse events,
/// not WPF touch panning: touch arrives as mouse on many POS drivers and on Spacedesk, where
/// panning never starts. A press that moves less than a few pixels stays a normal tap.
/// </summary>
internal static class DragScroll
{
    private const double Threshold = 10;

    /// <summary>True from the end of a drag until the next press: that release is not a tap.</summary>
    public static bool JustScrolled { get; private set; }

    private static Window? _window;
    private static DependencyObject? _source;
    private static ListBoxItem? _pendingItem;
    private static ScrollViewer? _active;
    private static Point _start;
    private static double _startV, _startH;
    private static bool _pressed, _dragging, _vertical;
    /// <summary>The current press came as real touch: the mouse events WPF promotes from it are ignored.</summary>
    private static bool _touch;

    public static void Register()
    {
        // Real touch (WPF touch events) and mouse-style input (Spacedesk, drivers that send touch
        // as mouse) both drive the same drag; whichever arrives first owns the press.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewTouchDownEvent,
            new EventHandler<TouchEventArgs>(OnTouchDown), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewTouchMoveEvent,
            new EventHandler<TouchEventArgs>(OnTouchMove), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewTouchUpEvent,
            new EventHandler<TouchEventArgs>(OnTouchUp), true);

        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnMouseDown), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent,
            new MouseEventHandler(OnMouseMove), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnMouseUp), true);

        // Windows may turn a vertical finger drag into wheel messages: send them to the scroll area
        // under the finger (most areas already scroll on wheel; this is for the log).
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler((_, e) => Log($"molette {e.Delta} sur {(e.OriginalSource as DependencyObject)?.GetType().Name}")), true);
    }

    // ---------- touch ----------

    private static void OnTouchDown(object sender, TouchEventArgs e)
    {
        if (sender is not Window w || w is TouchKeypadWindow) return;
        Log($"toucher sur {(e.OriginalSource as DependencyObject)?.GetType().Name}");
        _touch = true;
        Begin(w, e.OriginalSource as DependencyObject, e.GetTouchPoint(w).Position);
    }

    private static void OnTouchMove(object sender, TouchEventArgs e)
    {
        if (!_touch || _window is null || sender != _window) return;
        if (MoveTo(e.GetTouchPoint(_window).Position)) e.Handled = true;
    }

    private static void OnTouchUp(object sender, TouchEventArgs e)
    {
        if (!_touch) return;
        End();
    }

    // ---------- mouse ----------

    private static bool Promoted(MouseEventArgs e) => _touch && e.StylusDevice is not null;

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Promoted(e))
        {
            // The press already began from touch; only stop the list item picking itself now.
            if (_pendingItem is not null) e.Handled = true;
            return;
        }
        _touch = false;
        if (sender is not Window w || w is TouchKeypadWindow) return;
        Begin(w, e.OriginalSource as DependencyObject, e.GetPosition(w));
        if (_pendingItem is not null) e.Handled = true;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (Promoted(e) || !_pressed || _window is null || sender != _window) return;
        if (e.LeftButton != MouseButtonState.Pressed) { Reset(); return; }
        if (MoveTo(e.GetPosition(_window))) e.Handled = true;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is not null && (_touch || JustScrolled))
        {
            // Release promoted from a touch already handled in OnTouchUp.
            if (JustScrolled) e.Handled = true;
            return;
        }
        if (End()) e.Handled = true;
    }

    // ---------- shared ----------

    private static void Begin(Window w, DependencyObject? source, Point at)
    {
        JustScrolled = false;
        Reset();
        if (TouchKeypad.FindParent<ScrollBar>(source) is not null) return; // the bar itself works as usual
        if (TouchKeypad.FindParent<ScrollViewer>(source) is null)
        {
            Log($"appui sur {source?.GetType().Name} hors de toute zone défilante ({Types(source)})");
            return;
        }

        _window = w;
        _source = source;
        _start = at;
        _pressed = true;

        // A list item picks itself on press (the product grid adds to the cart right then), which
        // would add a product at the start of every drag. Hold the pick until the finger lifts.
        if (TouchKeypad.FindParent<ListBoxItem>(source) is { } item and not ComboBoxItem
            && TouchKeypad.FindParent<ButtonBase>(source) is null
            && TouchKeypad.FindParent<TextBoxBase>(source) is null)
            _pendingItem = item;
    }

    /// <summary>Returns true while dragging (the move is consumed).</summary>
    private static bool MoveTo(Point at)
    {
        if (!_pressed) return false;
        var d = at - _start;
        if (!_dragging)
        {
            if (Math.Abs(d.X) < Threshold && Math.Abs(d.Y) < Threshold) return false;
            _vertical = Math.Abs(d.Y) >= Math.Abs(d.X);
            _active = Scrollable(_source, _vertical);
            Log(_active is null
                ? $"glisser {(_vertical ? "vertical" : "horizontal")} ({d.X:0},{d.Y:0}) sur {_source?.GetType().Name}: rien à faire défiler ({Chain(_source)})"
                : $"glisser {(_vertical ? "vertical" : "horizontal")} ({d.X:0},{d.Y:0}) sur {_source?.GetType().Name}: défile '{_active.Name}' (reste {(_vertical ? _active.ScrollableHeight : _active.ScrollableWidth):0})");
            if (_active is null) { _pressed = false; return false; }
            _dragging = true;
            _pendingItem = null;
            _startV = _active.VerticalOffset;
            _startH = _active.HorizontalOffset;
            _start = at;
            d = new Vector();
            Mouse.Capture(_active, CaptureMode.SubTree); // cancels the pressed button / text selection
        }

        if (_vertical)
        {
            var perPixel = _active!.CanContentScroll && _active.ActualHeight > 0 ? _active.ViewportHeight / _active.ActualHeight : 1;
            _active.ScrollToVerticalOffset(_startV - d.Y * perPixel);
        }
        else
        {
            var perPixel = _active!.CanContentScroll && _active.ActualWidth > 0 ? _active.ViewportWidth / _active.ActualWidth : 1;
            _active.ScrollToHorizontalOffset(_startH - d.X * perPixel);
        }
        return true;
    }

    /// <summary>Ends the press; returns true when it consumed the release (a drag, or a held item pick).</summary>
    private static bool End()
    {
        var consumed = false;
        if (_dragging)
        {
            JustScrolled = true;
            Mouse.Capture(null);
            consumed = true;
        }
        else if (_pendingItem is { IsEnabled: true } item
                 && ItemsControl.ItemsControlFromItemContainer(item) is ListBox list)
        {
            // A tap, not a drag: pick the item now, as the press would have.
            if (list.SelectionMode == SelectionMode.Single) item.IsSelected = true;
            else item.IsSelected = !item.IsSelected;
            consumed = true;
        }
        Reset();
        return consumed;
    }

    private static void Reset()
    {
        _pressed = _dragging = false;
        _pendingItem = null;
        _active = null;
    }

    // Diagnostics for touch testing: set to true to write one line per press/drag in client-errors.log.
    private const bool Diagnostics = false;
    private static int _logged;
    private static void Log(string line)
    {
        if (Diagnostics && _logged++ < 300) App.Log("Défilement tactile: " + line);
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? from)
    {
        for (var c = from; c is not null; c = c is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(c) : LogicalTreeHelper.GetParent(c))
            yield return c;
    }

    private static string Types(DependencyObject? from) =>
        string.Join(" > ", Ancestors(from).Take(14).Select(a => a is FrameworkElement { Name.Length: > 0 } f ? $"{a.GetType().Name}#{f.Name}" : a.GetType().Name));

    private static string Chain(DependencyObject? from)
    {
        var parts = Ancestors(from).OfType<ScrollViewer>()
            .Select(sv => $"ScrollViewer '{sv.Name}' H={sv.ScrollableHeight:0} W={sv.ScrollableWidth:0}").ToList();
        return parts.Count == 0 ? "aucun ScrollViewer" : string.Join(" > ", parts);
    }

    /// <summary>The nearest scroll area around the press that can really move that way.</summary>
    private static ScrollViewer? Scrollable(DependencyObject? from, bool vertical) =>
        Ancestors(from).OfType<ScrollViewer>()
            .FirstOrDefault(sv => vertical ? sv.ScrollableHeight > 0 : sv.ScrollableWidth > 0);
}

/// <summary>The keypad itself: a borderless window docked to a screen edge that never activates.</summary>
internal sealed class TouchKeypadWindow : Window
{
    public Control Target { get; }

    private readonly bool _numeric;
    private readonly Grid _keys = new();
    private readonly DockPanel _root;
    private readonly StackPanel _dockButtons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 0) };
    private bool _shift;
    private bool _symbols;
    private bool _closing;

    private const double KeyWidth = 58;
    private const double KeyHeight = 54;

    public TouchKeypadWindow(Control target, bool numeric)
    {
        Target = target;
        _numeric = numeric;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Owner = Window.GetWindow(target);
        SetResourceReference(BackgroundProperty, "Surface");
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "BorderStrong");

        // Header, left to right: a small close key far from ⌫ (top right of the keys), then the
        // dock choice, then the title. Nothing that closes sits above the delete key.
        _root = new DockPanel { Margin = new Thickness(8) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var close = MakeKey("✕", SafeClose, width: 52, height: 40);
        close.FontSize = 16;
        close.ToolTip = "Fermer le clavier";
        header.Children.Add(close);
        BuildDockButtons();
        header.Children.Add(_dockButtons);
        var title = new TextBlock
        {
            Text = numeric ? "Pavé numérique" : "Clavier",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 13,
            Margin = new Thickness(12, 0, 4, 0),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        header.Children.Add(title);
        System.Windows.Controls.DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        _root.Children.Add(header);
        _root.Children.Add(_keys);
        Content = _root;

        BuildKeys();

        // The box losing focus to anything else (another box, a button) ends the typing.
        Target.LostKeyboardFocus += OnTargetLostFocus;
        Target.Unloaded += OnTargetUnloaded;
        Owner.Deactivated += OnOwnerDeactivated;

        Opacity = 0; // shown once scaled and placed, so it never flashes at the wrong size
        SourceInitialized += (_, _) => MakeNonActivating();
        // Should Windows activate the keypad anyway (touch on some drivers), hand activation and
        // focus straight back so the box keeps receiving the keys.
        Activated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            Owner?.Activate();
            EnsureFocus();
        });
        Loaded += (_, _) =>
        {
            Relayout();
            if (_numeric && Target is TextBox box) box.SelectAll(); // the first digit replaces "0"/"1"
        };
    }

    private void Relayout()
    {
        ScaleToScreen();
        Dispatcher.BeginInvoke(() => { Place(); Opacity = 1; }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        Target.LostKeyboardFocus -= OnTargetLostFocus;
        Target.Unloaded -= OnTargetUnloaded;
        if (Owner is not null) Owner.Deactivated -= OnOwnerDeactivated;
        base.OnClosed(e);

        // Give the window its place and room back - unless the next box's keypad took over.
        Dispatcher.BeginInvoke(() =>
        {
            if (Application.Current.Windows.OfType<TouchKeypadWindow>().Any(k => k.IsVisible)) return;
            RestoreOwner();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnTargetLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Scrolling, tapping a button or a label in the same window keeps the keypad open (the next
        // key gives the box its focus back). It closes only by its ✕, when another box takes over
        // (TouchKeypad.Open), or when the user leaves the window (OnOwnerDeactivated).
        if (_closing || e.NewFocus is null) return;
        if (e.NewFocus is DependencyObject d && (d == this || Window.GetWindow(d) == this))
            Dispatcher.BeginInvoke(EnsureFocus);
    }

    private void OnTargetUnloaded(object sender, RoutedEventArgs e) => SafeClose();
    // Close when the user clicks outside the window that opened it (another window or program),
    // not when the keypad itself took activation for a moment (checked once the switch settled).
    private void OnOwnerDeactivated(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_closing || IsActive || Owner?.IsActive == true) return;
        SafeClose();
    }, System.Windows.Threading.DispatcherPriority.Background);

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    // ---------- dock choice ----------

    private void BuildDockButtons()
    {
        _dockButtons.Children.Clear();
        foreach (var (dock, label, tip) in new[]
                 {
                     (KeypadDock.Left, "◧", "Clavier à gauche"),
                     (KeypadDock.Bottom, "⬓", "Clavier en bas"),
                     (KeypadDock.Right, "◨", "Clavier à droite"),
                 })
        {
            var d = dock;
            var key = MakeKey(label, () =>
            {
                TouchKeypad.Dock = d;
                BuildDockButtons();
                Relayout();
            }, width: 52, height: 40);
            key.ToolTip = tip;
            if (TouchKeypad.Dock == dock)
            {
                key.SetResourceReference(BackgroundProperty, "Accent");
                key.SetResourceReference(ForegroundProperty, "TextOnAccent");
            }
            _dockButtons.Children.Add(key);
        }
    }

    // ---------- layouts ----------

    private void BuildKeys()
    {
        _keys.Children.Clear();
        _keys.RowDefinitions.Clear();
        if (_numeric) BuildNumeric();
        else BuildText();
    }

    private void BuildNumeric()
    {
        var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var rows = new List<List<ButtonBase>>
        {
            new() { KeyFor("7"), KeyFor("8"), KeyFor("9"), Backspace() },
            new() { KeyFor("4"), KeyFor("5"), KeyFor("6"), MakeKey("C", ClearAll) },
            new() { KeyFor("1"), KeyFor("2"), KeyFor("3"), KeyFor("-") },
            new() { KeyFor(sep), KeyFor("0"), KeyFor("000"), Ok() },
        };
        foreach (var row in rows) foreach (var key in row) { key.Width = 76; key.Height = 62; }
        AddRows(rows);
    }

    private void BuildText()
    {
        var rows = new List<List<ButtonBase>>
        {
            "1234567890".Select(c => KeyFor(c.ToString())).Append(Backspace()).ToList(),
        };

        if (_symbols)
        {
            rows.Add(new[] { "@", "#", "&", "*", "(", ")", "-", "_", "+", "=" }.Select(KeyFor).ToList());
            rows.Add(new[] { "/", ":", ";", "'", "\"", "!", "?", "%", "€", "$" }.Select(KeyFor).ToList());
            rows.Add(new[] { "à", "â", "ê", "ë", "î", "ô", "ù", "û", "œ", "°" }.Select(Letter).ToList());
        }
        else
        {
            rows.Add("azertyuiop".Select(c => Letter(c.ToString())).ToList());
            rows.Add("qsdfghjklm".Select(c => Letter(c.ToString())).ToList());
            var third = new List<ButtonBase> { Shift() };
            third.AddRange("wxcvbn".Select(c => Letter(c.ToString())));
            third.AddRange(new[] { "é", "è", "ç" }.Select(Letter));
            rows.Add(third);
        }

        var space = KeyFor(" ", label: "espace");
        space.Width = KeyWidth * 4 + 12;
        rows.Add(new List<ButtonBase>
        {
            MakeKey(_symbols ? "ABC" : "?123", () => { _symbols = !_symbols; BuildKeys(); }, width: KeyWidth * 1.5),
            KeyFor(","), KeyFor("@"), space, KeyFor("."), KeyFor("-"), Ok(),
        });

        AddRows(rows);
    }

    private void AddRows(List<List<ButtonBase>> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            _keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var key in rows[i]) panel.Children.Add(key);
            Grid.SetRow(panel, i);
            _keys.Children.Add(panel);
        }
    }

    // ---------- keys ----------

    private ButtonBase KeyFor(string text) => KeyFor(text, null);

    private ButtonBase KeyFor(string text, string? label) => MakeKey(label ?? text, () => Type(text));

    private ButtonBase Letter(string letter)
    {
        var shown = _shift ? letter.ToUpper(CultureInfo.CurrentCulture) : letter;
        return MakeKey(shown, () =>
        {
            Type(_shift ? letter.ToUpper(CultureInfo.CurrentCulture) : letter);
            if (_shift) { _shift = false; BuildKeys(); }
        });
    }

    private ButtonBase Shift()
    {
        var key = MakeKey("⇧", () => { _shift = !_shift; BuildKeys(); });
        if (_shift) key.SetResourceReference(BackgroundProperty, "AccentLight");
        return key;
    }

    private ButtonBase Backspace()
    {
        var key = new RepeatButton { Delay = 400, Interval = 70 };
        StyleKey(key, "⌫", KeyWidth * 1.4, KeyHeight);
        key.Click += (_, _) => DeleteBack();
        return key;
    }

    private ButtonBase Ok()
    {
        var key = MakeKey("OK", Submit, width: KeyWidth * 1.4);
        key.SetResourceReference(BackgroundProperty, "Accent");
        key.SetResourceReference(ForegroundProperty, "TextOnAccent");
        key.FontWeight = FontWeights.SemiBold;
        return key;
    }

    private ButtonBase MakeKey(string label, Action action, double width = KeyWidth, double height = KeyHeight)
    {
        var key = new Button();
        StyleKey(key, label, width, height);
        key.Click += (_, _) => action();
        return key;
    }

    private static readonly ControlTemplate KeyTemplate = BuildTemplate();

    private static ControlTemplate BuildTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(ButtonBase)) { VisualTree = border };
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.6, "Bd"));
        template.Triggers.Add(pressed);
        return template;
    }

    private static void StyleKey(ButtonBase key, string label, double width, double height)
    {
        key.Content = label;
        key.Width = width;
        key.Height = height;
        key.Margin = new Thickness(3);
        key.FontSize = 20;
        key.Focusable = false;
        key.IsTabStop = false;
        key.Template = KeyTemplate;
        key.SetResourceReference(BackgroundProperty, "SurfaceAlt");
        key.SetResourceReference(ForegroundProperty, "TextPrimary");
        key.SetResourceReference(BorderBrushProperty, "Border");
    }

    // ---------- typing into the box ----------

    private void EnsureFocus()
    {
        if (!Target.IsKeyboardFocusWithin) Target.Focus();
    }

    private void Type(string text)
    {
        EnsureFocus();
        // Through the input system, so the box's PreviewTextInput handlers see it like a keystroke.
        TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, Target, text));
    }

    private void DeleteBack()
    {
        EnsureFocus();
        if (EditingCommands.Backspace.CanExecute(null, Target))
            EditingCommands.Backspace.Execute(null, Target);
        else if (Target is PasswordBox { Password.Length: > 0 } pb)
            pb.Password = pb.Password[..^1];
    }

    private void ClearAll()
    {
        EnsureFocus();
        switch (Target)
        {
            case TextBox box: box.Clear(); break;
            case PasswordBox pb: pb.Clear(); break;
        }
    }

    /// <summary>OK = the Enter key: the box (or its form) reacts as it would to Enter, then the keypad closes.</summary>
    private void Submit()
    {
        var target = Target;
        SafeClose();
        if (target is TextBox { AcceptsReturn: true }) return;
        if (PresentationSource.FromVisual(target) is not { } source) return;

        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (preview.Handled) return;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    // ---------- size and placement (device pixels: right on any monitor and DPI, e.g. a Spacedesk tablet) ----------

    /// <summary>Keys sized to the screen. At the bottom: about 60% of its width for letters, 30% for
    /// the pad, at most 45% of its height. On a side: narrower (45% / 28%), up to 70% of the height.
    /// Never smaller than the base size.</summary>
    private void ScaleToScreen()
    {
        _root.LayoutTransform = Transform.Identity;
        _root.UpdateLayout();
        var natural = new Size(_root.ActualWidth + 16, _root.ActualHeight + 16);
        if (OwnerWorkArea() is not { } work || natural.Width <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var workW = (work.Right - work.Left) / dpi.DpiScaleX;
        var workH = (work.Bottom - work.Top) / dpi.DpiScaleY;
        var bottom = TouchKeypad.Dock == KeypadDock.Bottom;
        var widthShare = bottom ? (_numeric ? 0.25 : 0.60) : (_numeric ? 0.25 : 0.45);
        var heightShare = _numeric ? 0.30 : bottom ? 0.40 : 0.55;
        var scale = Math.Min(workW * widthShare / natural.Width, workH * heightShare / natural.Height);
        scale = Math.Clamp(scale, 1.0, 3.0);
        _root.LayoutTransform = new ScaleTransform(scale, scale);
        _root.UpdateLayout();
    }

    private RECT? OwnerWorkArea()
    {
        var hwnd = Owner is null ? IntPtr.Zero : new WindowInteropHelper(Owner).Handle;
        var monitor = hwnd != IntPtr.Zero
            ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
            : MonitorFromPoint(default, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? info.rcWork : null;
    }

    /// <summary>The window that made room for a keypad: its original place and content transform.</summary>
    private static (Window Window, int Left, int Top, Transform Transform)? _adjusted;

    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var me) || OwnerWorkArea() is not { } work) return;
        int w = me.Right - me.Left, h = me.Bottom - me.Top;
        var dock = TouchKeypad.Dock;

        var x = dock switch
        {
            KeypadDock.Left => work.Left,
            KeypadDock.Right => work.Right - w,
            _ => (work.Left + work.Right) / 2 - w / 2,
        };
        var y = work.Bottom - h;
        SetWindowPos(hwnd, IntPtr.Zero, Math.Max(work.Left, x), Math.Max(work.Top, y), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        MakeRoom(work, x, y, w);
    }

    /// <summary>
    /// Only when the keypad covers the box being typed in: moves the window behind out of the way
    /// as far as the screen allows (a dialog slides up, or sideways), then slides its content by
    /// whatever is still needed (a maximised window). Just far enough for the box to clear the
    /// keypad, and nothing is resized - padding a full-screen Ventes squashed every panel.
    /// </summary>
    private void MakeRoom(RECT work, int kx, int ky, int kw)
    {
        if (Owner is not { } owner || owner.Content is not FrameworkElement content) return;
        var ownerHwnd = new WindowInteropHelper(owner).Handle;
        if (!GetWindowRect(ownerHwnd, out var current)) return;

        if (_adjusted is { } prev && prev.Window != owner) RestoreOwner();
        // Measure the box where it really sits, not where the previous keypad slid it.
        if (_adjusted is { } a1)
        {
            content.RenderTransform = a1.Transform;
            content.UpdateLayout();
        }
        var original = _adjusted is { } a0 ? (a0.Left, a0.Top) : (current.Left, current.Top);
        var boxTl = Target.PointToScreen(new Point(0, 0));
        var boxBr = Target.PointToScreen(new Point(Target.ActualWidth, Target.ActualHeight));
        int shiftX = original.Item1 - current.Left, shiftY = original.Item2 - current.Top;
        var box = new RECT
        {
            Left = (int)boxTl.X + shiftX, Top = (int)boxTl.Y + shiftY,
            Right = (int)boxBr.X + shiftX, Bottom = (int)boxBr.Y + shiftY,
        };
        if (!GetWindowRect(new WindowInteropHelper(this).Handle, out var keypad)) return;
        var covered = box.Left < keypad.Right && box.Right > keypad.Left && box.Top < keypad.Bottom && box.Bottom > keypad.Top;
        if (!covered)
        {
            RestoreOwner();
            return;
        }

        if (_adjusted is null) _adjusted = (owner, current.Left, current.Top, content.RenderTransform);
        var (_, left, top, _) = _adjusted.Value;

        // Work from the window's original place, not wherever the last keypad pushed it.
        int width = current.Right - current.Left;
        var dpi = VisualTreeHelper.GetDpi(owner);
        var gap = (int)(12 * dpi.DpiScaleY);
        var movable = owner.WindowState == WindowState.Normal;
        int moveX = 0, moveY = 0, slideX = 0, slideY = 0;

        switch (TouchKeypad.Dock)
        {
            case KeypadDock.Bottom:
            {
                var need = box.Bottom + gap - keypad.Top;
                if (need <= 0) break;
                moveY = movable ? Math.Min(need, Math.Max(0, top - work.Top)) : 0;
                slideY = need - moveY;
                moveY = -moveY; slideY = -slideY;
                break;
            }
            case KeypadDock.Left:
            {
                var need = keypad.Right + gap - box.Left;
                if (need <= 0) break;
                moveX = movable ? Math.Min(need, Math.Max(0, work.Right - (left + width))) : 0;
                slideX = need - moveX;
                break;
            }
            case KeypadDock.Right:
            {
                var need = box.Right + gap - keypad.Left;
                if (need <= 0) break;
                moveX = movable ? Math.Min(need, Math.Max(0, left - work.Left)) : 0;
                slideX = need - moveX;
                moveX = -moveX; slideX = -slideX;
                break;
            }
        }

        int newLeft = left + moveX, newTop = top + moveY;
        if (newLeft != current.Left || newTop != current.Top)
            SetWindowPos(ownerHwnd, IntPtr.Zero, newLeft, newTop, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        content.RenderTransform = slideX == 0 && slideY == 0
            ? _adjusted.Value.Transform
            : new TranslateTransform(slideX / dpi.DpiScaleX, slideY / dpi.DpiScaleY);
    }

    private static void RestoreOwner()
    {
        if (_adjusted is not { } a) return;
        _adjusted = null;
        if (a.Window.Content is FrameworkElement content) content.RenderTransform = a.Transform;
        var hwnd = new WindowInteropHelper(a.Window).Handle;
        if (hwnd != IntPtr.Zero && a.Window.WindowState == WindowState.Normal)
            SetWindowPos(hwnd, IntPtr.Zero, a.Left, a.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ---------- Win32 ----------

    private const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // A tap on the keypad must not activate it: activation would take focus from the box.
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private void MakeNonActivating()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
