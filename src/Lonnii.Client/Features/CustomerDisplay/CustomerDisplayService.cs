using System.Windows;
using System.Windows.Threading;
using Lonnii.Client.Services;
using Microsoft.Win32;

namespace Lonnii.Client.Features.CustomerDisplay;

/// <summary>
/// Drives everything the customer can see: the second-monitor window and any pole display or
/// LCD. The till reports its cart in one place (<see cref="Update"/>); this turns it into a
/// snapshot and hands it to each enabled target, so a new kind of display is one more target
/// and no change to the till.
///
/// <para>
/// UI-thread only, except <see cref="SystemEvents.DisplaySettingsChanged"/>, which it marshals
/// back itself. Every failure is contained: a display that is unplugged, or a port another
/// program holds, must never stop a sale.
/// </para>
/// </summary>
public sealed class CustomerDisplayService
{
    public static CustomerDisplayService Instance { get; } = new();

    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(8);

    private readonly DispatcherTimer _holdTimer = new() { Interval = HoldFor };

    private CustomerDisplaySettings _settings = CustomerDisplaySettings.Load();
    private AppSession? _session;
    private bool _started;

    private CustomerDisplayWindow? _window;
    private CustomerDisplayWindow? _preview;
    private SerialDisplayTarget? _serial;
    private NetworkDisplayTarget? _network;

    private DisplayBranding _branding = new("Lonnii");
    private CartState _cart = new([], 0, 0, 0, 0, null);
    private CustomerDisplaySnapshot? _hold;
    private CustomerDisplaySnapshot? _lastPublished;

    private Dictionary<string, (int Quantity, decimal Total)> _previousLines = [];
    private string? _focusLineId;

    private sealed record CartState(
        IReadOnlyList<DisplayLine> Lines, decimal Subtotal, decimal Discount, decimal Tva, decimal Total, decimal? Recu);

    private CustomerDisplayService()
    {
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            _hold = null;
            Publish();
        };
    }

    public CustomerDisplaySettings Settings => _settings.Clone();

    public string? SerialStatus => _serial?.Status;

    public string? NetworkStatus => _network?.Status;

    /// <summary>True while a full-screen window is up on a second monitor.</summary>
    public bool IsScreenActive => _window is not null;

    public IReadOnlyList<MonitorInfo> Monitors => MonitorEnumerator.GetAll();

    // --- Lifecycle ---

    /// <summary>Starts watching for displays. Called once the shell is up; safe to call again.</summary>
    public void Start(AppSession session)
    {
        _session = session;
        if (_branding.Company == "Lonnii" && session.Groupe?.Nom is { } name) _branding = _branding with { Company = name };

        if (!_started)
        {
            _started = true;
            // Plugging in or unplugging a monitor re-runs auto-detection by itself.
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        Rebuild();
        _ = RefreshBrandingAsync();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _holdTimer.Stop();
        _hold = null;
        CloseTargets();
        _preview?.Close();
        _preview = null;
        _lastPublished = null;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_started) ReconcileScreen();
        });

    /// <summary>Saves new settings and applies them straight away.</summary>
    public void Apply(CustomerDisplaySettings settings)
    {
        _settings = settings.Clone();
        _settings.Save();
        if (_started) Rebuild();
    }

    /// <summary>Re-reads the shop's name, logo and QR code (they may have just been changed in Paramètres).</summary>
    public async Task RefreshBrandingAsync()
    {
        if (_session is not { } session) return;

        try
        {
            var receipt = await session.GetReceiptSettingsAsync();
            _branding = new DisplayBranding(
                receipt.CompanyName ?? session.Groupe?.Nom ?? "Lonnii",
                session.ReceiptLogo, session.ReceiptQrCode,
                string.IsNullOrWhiteSpace(receipt.NoteUnderQr) ? null : receipt.NoteUnderQr);
            Publish();
        }
        catch (ApiException)
        {
            // The name alone is a fine idle screen.
        }
    }

    // --- What the till reports ---

    /// <summary>The cart as it stands. Call after every change to it, or to the amount handed over.</summary>
    public void Update(
        IReadOnlyList<DisplayLine> lines, decimal subtotal, decimal discount, decimal tva, decimal total, decimal? recu)
    {
        _cart = new CartState(lines, subtotal, discount, tva, total, recu);
        TrackFocus(lines);

        // Right after a sale the till empties its cart; that must not wipe the thank-you early.
        // A new sale starting (anything in the cart) ends the hold at once.
        if (_hold is not null)
        {
            if (lines.Count == 0) return;
            _holdTimer.Stop();
            _hold = null;
        }

        Publish();
    }

    /// <summary>A sale was just recorded: say thank you (and the change) for a few seconds.</summary>
    /// <param name="change">Change handed back, when there was any.</param>
    public void ShowThanks(decimal total, decimal? change, bool facture)
    {
        _hold = new CustomerDisplaySnapshot(
            DisplayMode.Thanks, _branding, [], total, 0, 0, total,
            Recu: change is > 0 ? total + change : null, Facture: facture);
        StartHold();
        Publish();
    }

    /// <summary>Shows a made-up sale on every enabled display for a few seconds. Returns false
    /// when nothing would show it (no second screen, no text display), so the settings screen can say so.</summary>
    public bool ShowTest()
    {
        DisplayLine[] lines =
        [
            new("t1", "Chemise Oxford", 2, 8_500m, 17_000m),
            new("t2", "Pantalon Chino", 1, 12_500m, 12_500m),
            new("t3", "Ceinture Cuir", 1, 3_500m, 3_500m),
        ];
        _hold = new CustomerDisplaySnapshot(
            DisplayMode.Selling, _branding, lines, 33_000m, 0, 0, 33_000m, Recu: 40_000m, FocusLineId: "t3");
        StartHold();
        Publish();
        return _window is not null || _serial is not null || _network is not null;
    }

    /// <summary>Opens an ordinary window showing what the customer would see, for trying it
    /// without a second screen.</summary>
    public void OpenPreview()
    {
        if (_preview is null)
        {
            _preview = new CustomerDisplayWindow(preview: true);
            _preview.ApplyStyle(_settings);
            _preview.Closed += (_, _) => _preview = null;
            _preview.Show();
        }
        else
        {
            _preview.Activate();
        }

        if (_lastPublished is { } snapshot) _preview.Show(snapshot);
        else Publish();
    }

    private void StartHold()
    {
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    /// <summary>The line the character displays should talk about: the one that just changed.</summary>
    private void TrackFocus(IReadOnlyList<DisplayLine> lines)
    {
        string? changed = null;
        foreach (var line in lines)
        {
            if (!_previousLines.TryGetValue(line.Id, out var before)
                || before.Quantity != line.Quantity || before.Total != line.Total)
                changed = line.Id;
        }

        _previousLines = lines.ToDictionary(l => l.Id, l => (l.Quantity, l.Total));

        if (changed is not null) _focusLineId = changed;
        else if (!lines.Any(l => l.Id == _focusLineId)) _focusLineId = lines.LastOrDefault()?.Id;
    }

    // --- Output ---

    private CustomerDisplaySnapshot Build() =>
        _hold ?? new CustomerDisplaySnapshot(
            _cart.Lines.Count == 0 ? DisplayMode.Idle : DisplayMode.Selling,
            _branding, _cart.Lines, _cart.Subtotal, _cart.Discount, _cart.Tva, _cart.Total,
            _cart.Recu, _focusLineId);

    private void Publish()
    {
        if (!_started) return;

        var snapshot = Build();
        _lastPublished = snapshot;

        try
        {
            _window?.Show(snapshot);
            _preview?.Show(snapshot);
            _serial?.Show(snapshot);
            _network?.Show(snapshot);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A display that misbehaves is not worth interrupting a sale for.
        }
    }

    // --- Targets ---

    private void Rebuild()
    {
        CloseTargets(keepPreview: true);

        if (_settings.Enabled)
        {
            if (_settings.Serial.Enabled) _serial = new SerialDisplayTarget(_settings.Serial);
            if (_settings.Network.Enabled) _network = new NetworkDisplayTarget(_settings.Network);
        }

        ReconcileScreen();
        _preview?.ApplyStyle(_settings);
        Publish();
    }

    /// <summary>Opens, moves or closes the second-monitor window to match the settings and
    /// the monitors present right now.</summary>
    private void ReconcileScreen()
    {
        MonitorInfo? monitor = null;
        if (_settings.Enabled && _settings.ScreenEnabled)
            monitor = MonitorEnumerator.Pick(MonitorEnumerator.GetAll(), _settings.AutoDetectScreen, _settings.ScreenDevice);

        if (monitor is null)
        {
            _window?.Close();
            _window = null;
            return;
        }

        if (_window is null)
        {
            _window = new CustomerDisplayWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }

        _window.ApplyStyle(_settings);

        _window.MoveTo(monitor);
        if (_lastPublished is { } snapshot) _window.Show(snapshot);
    }

    private void CloseTargets(bool keepPreview = false)
    {
        _window?.Close();
        _window = null;
        _serial?.Dispose();
        _serial = null;
        _network?.Dispose();
        _network = null;
        if (!keepPreview)
        {
            _preview?.Close();
            _preview = null;
        }
    }
}
