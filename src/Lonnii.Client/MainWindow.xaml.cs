using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lonnii.Client.Features.CustomerDisplay;
using Lonnii.Client.Features.Payments;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client;

/// <summary>
/// The application shell: top navigation bar, privilege-filtered module list, content
/// host and status bar.
///
/// The navigation is built from the menu the API returns rather than from a list held
/// here, so the desktop shows exactly what Lonnii Business would show the same user. A
/// module the user cannot open never appears.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSession _session = App.Session;
    private readonly Dictionary<string, UserControl> _openModules = new(StringComparer.Ordinal);
    private readonly Stack<(string Key, string Label)> _backStack = new();
    private string? _currentKey;
    private string? _currentLabel;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Current;

        _session.Changed += (_, _) => Dispatcher.Invoke(BuildShell);

        InputBindings.Add(new KeyBinding(
            new RelayCommand(async () => await RefreshAsync()), Key.F5, ModifierKeys.None));

        Loaded += (_, _) =>
        {
            BuildShell();
            ApplyTopBarStyle(); // the user is known by now
            CustomerDisplayService.Instance.Start(_session);
            PaymentProviderRegistry.Register(new LigdiCashProvider());
            PaymentProviderRegistry.Register(new OrangeMoneyProvider());
        };

        ThemeManager.Changed += (_, _) =>
        {
            ApplyThemeButtonVisuals();
            RebuildModulesForTheme();
        };
        ApplyThemeButtonVisuals();

        _presenceTimer.Tick += async (_, _) => await SendPresenceAsync();
        _presenceTimer.Start();

        _liveTimer.Tick += async (_, _) => await LiveSyncAsync();
        _liveTimer.Start();

        // Licence: renew quietly in the background, and stop hard when the host says the
        // offline deadline has passed.
        LonniiApiClient.LicenceLocked += message => Dispatcher.InvokeAsync(() => ShowLicenceLock(message));
        _licenceTimer.Tick += async (_, _) => await SyncLicenceAsync();
        _licenceTimer.Start();
        _session.Changed += (_, _) => _ = SyncLicenceAsync(onlyIfStale: true);

        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
        Loaded += async (_, _) => await CheckForUpdateAsync();
        _session.Changed += async (_, _) => await CheckForUpdateAsync();

        _notificationTimer.Tick += async (_, _) => await RefreshNotificationsAsync();
        _notificationTimer.Start();
        Loaded += async (_, _) => await RefreshNotificationsAsync();
        _session.Changed += async (_, _) => await RefreshNotificationsAsync();
        Closing += (_, _) =>
        {
            EndPresence();
            CustomerDisplayService.Instance.Stop();
        };

        // After a sign-out the app lives on, so a closed shell must not keep polling.
        Closed += (_, _) =>
        {
            _licenceTimer.Stop();
            _updateTimer.Stop();
            _notificationTimer.Stop();
        };
    }

    // --- Updates ---

    /// <summary>Hourly: the main computer itself only looks for new versions every six hours.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(1) };

    private Lonnii.Shared.Security.UpdateManifest? _update;

    /// <summary>
    /// Shows the banner when the shop's main computer holds a newer version. Admins only: the
    /// install closes the app (and, on the main computer, briefly the tills' server), so it is
    /// theirs to time. Quiet on any failure - an update can always wait for the next check.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        if (_session.IsRemote || !(_session.IsAdmin || _session.IsAdminGeneral))
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var manifest = await _session.Api.LatestUpdateAsync();
            _update = manifest is not null && manifest.IsNewerThan(AppUpdater.CurrentVersion) ? manifest : null;
            _ = RefreshNotificationsAsync(); // the bell lists the update too
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return;
        }

        if (_update is null)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBannerText.Text = $"Nouvelle version de Lonnii disponible : {_update.Version} (vous avez {AppUpdater.CurrentVersion})."
            + (string.IsNullOrWhiteSpace(_update.Notes) ? "" : " " + _update.Notes.Trim());
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } update) return;

        // Installing closes the app: a cart being rung up would be lost.
        if (_openModules.Values.OfType<Features.Ventes.VentesView>().Any(v => v.SaleInProgress))
        {
            MessageBox.Show(this,
                "Une vente est en cours sur cet ordinateur. Terminez-la ou videz le panier, puis installez la mise à jour.",
                "Mise à jour de Lonnii", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var onServer = AppUpdater.ServerIsThisComputer(_session.Api.BaseAddress);
        var message = $"Installer la version {update.Version} maintenant ?\n\n"
            + "Lonnii va se fermer pendant l'installation (quelques minutes), puis se rouvrir tout seul.";
        if (onServer)
            message += "\n\nC'est l'ordinateur principal : les caisses reliées seront déconnectées pendant l'installation.";

        if (MessageBox.Show(this, message, "Mise à jour de Lonnii", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return;

        UpdateInstallButton.IsEnabled = false;
        UpdateBannerText.Text = "Préparation de la mise à jour…";

        var error = await AppUpdater.InstallAsync(_session.Api, update);
        if (error is null)
        {
            // Said before closing, so a window vanishing is not taken for a crash.
            UpdateInstallButton.Visibility = Visibility.Collapsed;
            UpdateBannerText.FontWeight = FontWeights.SemiBold;
            UpdateBannerText.Text = $"Installation de la version {update.Version} en cours… "
                + "Lonnii va se fermer, puis se rouvrir automatiquement dans quelques minutes.";
            Mouse.OverrideCursor = Cursors.Wait;
            await Task.Delay(TimeSpan.FromSeconds(4));

            Mouse.OverrideCursor = null;
            EndPresence();
            Application.Current.Shutdown();
            return;
        }

        UpdateInstallButton.IsEnabled = true;
        await CheckForUpdateAsync();
        MessageBox.Show(this, error, "Mise à jour de Lonnii", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // --- Licence (offline deadline) ---

    /// <summary>Every five minutes: a block or a removed computer on the dashboard reaches a
    /// connected shop within minutes. The call is a few hundred bytes, so this costs nothing.</summary>
    private static readonly TimeSpan LicenceSyncInterval = TimeSpan.FromMinutes(5);
    private readonly System.Windows.Threading.DispatcherTimer _licenceTimer = new() { Interval = LicenceSyncInterval };

    private DateTime _lastLicenceSync = DateTime.MinValue;

    /// <summary>
    /// Asks the host to renew the licence. Never surfaces an error: no connection is the
    /// normal case for an hour or a day, and the deadline - not this call - is what stops
    /// the till. A block or a removed computer, though, is shown at once: the host answers
    /// with a locked status and the lock screen comes up with the reason.
    /// </summary>
    private async Task SyncLicenceAsync(bool onlyIfStale = false)
    {
        if (_session.Groupe is not { } groupe || _session.IsRemote) return;
        if (onlyIfStale && DateTime.UtcNow - _lastLicenceSync < TimeSpan.FromMinutes(5)) return;

        _lastLicenceSync = DateTime.UtcNow;
        try
        {
            var status = await _session.Api.SyncLicenceAsync(groupe.Id);
            _licenceTimer.Interval = LicenceSyncInterval;

            if (status.Expired) ShowLicenceLock(status.Message ?? "L'accès à cet espace est suspendu.");
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            // Missed (internet down, or a request that timed out as it came back): the timer
            // simply tries again at its next tick.
            _licenceTimer.Interval = LicenceSyncInterval;
        }
    }

    /// <summary>Blocks the shell behind the lock screen until the licence is renewed. Several
    /// calls can fail at once when the lock lands, so only the first opens the window.</summary>
    private void ShowLicenceLock(string message)
    {
        if (_session.Groupe is not { } groupe) return;

        // Renewed: reload whatever the refused calls left empty.
        if (Features.Licensing.LicenceLockWindow.ShowLocked(this, groupe.Id, message))
            _ = RefreshAsync();
    }

    // --- Presence (Audit → présences) ---

    /// <summary>Once a minute: well inside the server's 2-minute "en ligne" window, so a
    /// working till never flickers offline between beats.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromSeconds(60) };

    /// <summary>Tells the server this user is here, on this screen. Never surfaces an error:
    /// attendance is a by-product of working, and a failed beat must not interrupt a sale.</summary>
    private async Task SendPresenceAsync()
    {
        if (_session.Groupe is null || _session.IsRemote) return;
        try { await _session.Api.SendPresenceAsync(_currentLabel); }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
    }

    // --- Live updates (privileges changed from a phone, a laptop or another till) ---

    private readonly System.Windows.Threading.DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private long? _liveVersion;
    private bool _liveBusy;

    /// <summary>
    /// Polls the shop's change counter. When it moves, re-reads privileges and the menu and
    /// rebuilds the shell only if what this user may do actually changed - a rebuild closes the
    /// open screens, which must never happen to a cashier mid-sale over an unrelated change.
    /// Silent on failure: offline, the till simply keeps working with what it has.
    /// </summary>
    private async Task LiveSyncAsync()
    {
        if (_liveBusy || _session.Groupe is null) { _liveVersion = null; return; }
        _liveBusy = true;
        try
        {
            var version = await _session.Api.GetLiveVersionAsync();
            var previous = _liveVersion;
            _liveVersion = version;
            if (previous is null || previous == version) return;

            var privileges = await _session.Api.GetMyPrivilegesAsync();
            var menu = await _session.Api.GetMenuAsync();
            var same = Serialize(privileges) == Serialize(_session.Privileges)
                       && Serialize(menu) == Serialize(_session.Menu);
            if (same) return;

            await RefreshAsync();
            StatusText.Text = "Vos droits ont été mis à jour";
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException) { }
        finally { _liveBusy = false; }

        static string Serialize<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);
    }

    /// <summary>Closes the stretch now rather than leaving the server to notice the silence
    /// ten minutes later. Bounded wait: closing the window must not hang on a dead network.</summary>
    private void EndPresence()
    {
        _presenceTimer.Stop();
        _liveTimer.Stop();
        if (_session.Groupe is null || _session.IsRemote) return;
        try { Task.Run(() => _session.Api.EndPresenceAsync()).Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
    }

    private void DarkMode_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    private void ApplyThemeButtonVisuals()
    {
        DarkModeButton.ToolTip = ThemeManager.IsDark ? "Mode clair" : "Mode sombre";
        ApplyTopBarStyle();
    }

    /// <summary>The signed-in user's bar colour and pattern (Apparence de la barre).</summary>
    private void ApplyTopBarStyle() =>
        TopBarPreferences.Apply(TopBarPreferences.For(_session.User?.IdUser), TopBar, Resources, ThemeManager.IsDark);

    private void TopBarStyle_Click(object sender, RoutedEventArgs e)
    {
        var userId = _session.User?.IdUser;
        var dialog = new Lonnii.Client.Common.TopBarStyleDialog(TopBarPreferences.For(userId)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        TopBarPreferences.Save(userId, dialog.Result);
        ApplyTopBarStyle();
    }

    /// <summary>
    /// Asks the window manager for a dark caption (black bar, white text/buttons) instead
    /// of restyling the title bar ourselves. Windows draws and drives it - dragging,
    /// resizing, Aero Snap, the system menu - so there is no custom hit-testing to get
    /// wrong. Silently does nothing on a Windows version that does not support it.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var useDark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows without this DWM attribute: keep the default light caption.
        }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    /// <summary>The nav bar's background is always the dark purple gradient, regardless of
    /// the Windows theme, so it always wants the white-ink mark - never <see cref="AppIcon"/>,
    /// which switches with the OS and would go invisible in light mode.</summary>
    /// <summary>Rebuilds every part of the shell that depends on the current group and privileges.</summary>
    private void BuildShell()
    {
        var groupe = _session.Groupe;
        var menu = _session.Menu;

        Title = groupe is null ? "Lonnii" : groupe.Nom;

        StatusUser.Text = _session.DisplayName;

        RemoteBanner.Visibility = _session.IsRemote ? Visibility.Visible : Visibility.Collapsed;
        if (_session.IsRemote)
        {
            var at = _session.RemoteSnapshotAt?.ToLocalTime().ToString("dd/MM/yyyy à HH:mm") ?? "—";
            RemoteBannerText.Text = $"Accès à distance, en lecture seule : données de la boutique au {at} " +
                                    "(dernière sauvegarde envoyée par le serveur de la boutique).";
        }

        // A TextBlock with its own colour, not a bare string: a string is wrapped in a TextBlock
        // that takes the theme's text colour (white at night) over the button's, unreadable on gold.
        AvatarButton.Content = new TextBlock
        {
            Text = Initials(_session.DisplayName),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x27)),
            FontWeight = FontWeights.Bold,
            FontSize = 12,
        };
        AvatarGroupHeader.Header = groupe?.Nom ?? "Aucun espace";
        AvatarRoleHeader.Header = DescribeRole();
        AvatarRoleHeader.Visibility = string.IsNullOrEmpty(DescribeRole()) ? Visibility.Collapsed : Visibility.Visible;

        BuildTopNav(menu);

        // Land where the user last was, if they may still open it; otherwise on the first
        // thing they are allowed to open.
        if (_currentKey is null)
        {
            var saved = UiState.For(_session).LastModule;
            var first = menu?.Gestion.Concat(menu.Espace).FirstOrDefault(e => e.Key == saved)
                        ?? menu?.Gestion.FirstOrDefault() ?? menu?.Espace.FirstOrDefault();
            if (first is not null) Navigate(first.Key, first.Label);
            else ShowPlaceholder("Aucun module disponible",
                "Aucun privilège ne vous a encore été accordé dans cet espace. " +
                "Demandez à un administrateur de vous en accorder.");
        }
    }

    /// <summary>A short description of the user's standing, shown under the group name in
    /// the avatar menu.</summary>
    private string DescribeRole() =>
        _session.Privileges is null
            ? string.Empty
            : GroupRoles.DisplayName(_session.Privileges.Role, _session.IsAdminGeneral);

    /// <summary>Two letters for the avatar circle: initials of the first and last name, or
    /// the first two letters of a single-word name.</summary>
    private static string Initials(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
        };
    }

    /// <summary>
    /// Fills the top bar: the workspace entries (Programme, Options...) followed by one
    /// pill per Gestion section. The sections used to live inside a single "Gestion"
    /// dropdown; they are pulled up here so the three of them - Opérations, Finance &amp;
    /// Analyse, Administration - are one click away instead of two.
    /// </summary>
    private void BuildTopNav(MenuResponse? menu)
    {
        NavPillPanel.Children.Clear();
        SectionPopup.IsOpen = false;
        SectionPopupPanel.Children.Clear();
        if (menu is null) return;

        foreach (var entry in menu.Espace)
            NavPillPanel.Children.Add(NavPill(entry, entry.Key == _currentKey));

        foreach (var section in menu.GestionSections)
            NavPillPanel.Children.Add(SectionPill(section));
    }

    /// <summary>A pill in the top bar that opens directly into a module (Chat, Programme,
    /// Options...). The active module's pill is filled yellow; every other one is a
    /// translucent white so it reads on the purple gradient without competing with it.</summary>
    private Button NavPill(MenuEntryDto entry, bool isActive)
    {
        var button = new Button
        {
            // Foreground set here directly, not just on the Button: a plain-string or
            // unstyled-TextBlock Content gets wrapped in / stays a TextBlock that takes the
            // implicit TextBlock style's Foreground (dark TextPrimary) instead of inheriting
            // the Button's - a Style setter beats inherited property value. Same bug BackButton
            // had; ButtonContentText normally fixes it by binding back to the ancestor Button,
            // but a flat White is simpler here since every pill wants the same colour anyway.
            Content = PillText($"{Glyph(entry.Key)} {entry.Label}", isActive),
            ToolTip = entry.Description,
            Cursor = Cursors.Hand,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
            Template = (ControlTemplate)Resources["PillNavButtonTemplate"]!,
        };
        ApplyPillColours(button, isActive);

        button.Click += (_, _) => Navigate(entry.Key, entry.Label);
        return button;
    }

    /// <summary>A pill's label. Its Foreground is set on the TextBlock itself because the
    /// implicit TextBlock style would otherwise beat the inherited Button Foreground.</summary>
    private static TextBlock PillText(string text, bool isActive)
    {
        var block = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold };
        block.SetResourceReference(TextBlock.ForegroundProperty, isActive ? "HeaderPillActiveText" : "HeaderPillText");
        return block;
    }

    /// <summary>Solid pill colours from the palette, by reference so a theme switch repaints them.</summary>
    private static void ApplyPillColours(Button button, bool isActive)
    {
        button.SetResourceReference(Control.BackgroundProperty, isActive ? "HeaderPillActive" : "HeaderPill");
        button.SetResourceReference(Control.BorderBrushProperty, isActive ? "HeaderPillActive" : "HeaderPillBorder");
    }

    /// <summary>A section pill (Opérations, Finance &amp; Analyse, Administration). It never
    /// navigates on its own - there is no single screen behind a section - it opens
    /// <see cref="SectionPopup"/> on its own modules instead, and stays highlighted for as
    /// long as the open module is one of them.</summary>
    private Button SectionPill(MenuSectionDto section)
    {
        var isActive = section.Entries.Any(e => e.Key == _currentKey);

        var button = new Button
        {
            Content = PillText($"{SectionGlyph(section.Id)} {section.Label}  ▾", isActive),
            ToolTip = section.Description,
            Cursor = Cursors.Hand,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
            Template = (ControlTemplate)Resources["PillNavButtonTemplate"]!,
        };
        ApplyPillColours(button, isActive);

        button.Click += (_, _) =>
        {
            // Clicking the pill whose menu is already showing closes it; clicking a
            // different one swaps the contents over rather than leaving the old list up.
            var reopening = SectionPopup.IsOpen && ReferenceEquals(SectionPopup.PlacementTarget, button);
            SectionPopup.IsOpen = false;
            if (reopening) return;

            SectionPopupPanel.Children.Clear();
            foreach (var entry in section.Entries) SectionPopupPanel.Children.Add(DropdownRow(entry));

            SectionPopup.PlacementTarget = button;
            SectionPopup.IsOpen = true;
        };
        return button;
    }

    /// <summary>One row in the Gestion dropdown: the same accent-stripe-plus-label look the
    /// old sidebar used.</summary>
    private Button DropdownRow(MenuEntryDto entry)
    {
        var accent = (Brush)new BrushConverter().ConvertFromString(entry.Accent)!;
        var isCurrent = entry.Key == _currentKey;

        var stripe = new Border
        {
            Width = 4,
            Background = accent,
            CornerRadius = new CornerRadius(2),
        };

        var label = new TextBlock
        {
            Text = entry.Label,
            FontSize = 14,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
        };

        var row = new DockPanel { Height = 38 };
        DockPanel.SetDock(stripe, Dock.Left);
        row.Children.Add(stripe);
        row.Children.Add(label);

        var button = new Button
        {
            Content = row,
            ToolTip = entry.Description,
            Cursor = Cursors.Hand,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(2, 1, 2, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            FocusVisualStyle = null,
            Template = (ControlTemplate)Resources["DropdownRowTemplate"]!,
        };

        // SetResourceReference rather than a one-time Application.Current.Resources[...]
        // lookup: a captured brush instance goes stale the moment ThemeManager swaps the
        // palette dictionary, and a row outlives that swap whenever its popup is still
        // open - the current module's row would keep whatever theme was active when the
        // menu was built, e.g. light mode's near-white AccentLight showing as a blank pale
        // box after switching to dark. Transparent has no theme to go stale on.
        if (isCurrent) button.SetResourceReference(Control.BackgroundProperty, "AccentLight");
        else button.Background = Brushes.Transparent;

        button.Click += (_, _) =>
        {
            SectionPopup.IsOpen = false;
            Navigate(entry.Key, entry.Label);
        };
        return button;
    }

    /// <summary>Small glyph per workspace module, matching the icon each pill shows in the
    /// web app closely enough to tell them apart at a glance.</summary>
    private static string Glyph(string key) => key switch
    {
        "program" => "🗓",
        _ => "•",
    };

    /// <summary>Glyph for a Gestion section pill: goods moving, money measured, the place
    /// the rules are set.</summary>
    private static string SectionGlyph(string id) => id switch
    {
        "operations" => "📦",
        "finance" => "📈",
        "administration" => "🛡",
        _ => "•",
    };

    /// <summary>
    /// Opens a module, reusing the instance if it is already open so grids keep their
    /// scroll position and filters when the user moves between screens.
    /// </summary>
    private void Navigate(string key, string label, bool recordHistory = true)
    {
        if (recordHistory && _currentKey is not null && _currentKey != key)
            _backStack.Push((_currentKey, _currentLabel ?? _currentKey));

        _currentKey = key;
        _currentLabel = label;
        StatusText.Text = label;

        UiState.For(_session).LastModule = key;
        UiState.Save();
        BackButton.IsEnabled = _backStack.Count > 0;

        if (!_openModules.TryGetValue(key, out var view))
        {
            view = CreateModule(key, label);
            _openModules[key] = view;
        }

        ContentHost.Content = view;
        BuildTopNav(_session.Menu);

        _ = SendPresenceAsync();
    }

    /// <summary>
    /// Many views paint code-built parts (calendar days, cards, chips) with FindResource, which
    /// copies the brush once instead of following the palette, so a theme switch left them in the
    /// old colours. Dropping the cached modules and rebuilding the open one repaints everything;
    /// it costs that screen its scroll position and filters. A sale in progress is kept as it is:
    /// a few old-theme colours are better than a cashier losing the cart.
    /// </summary>
    private void RebuildModulesForTheme()
    {
        foreach (var key in _openModules.Keys.ToList())
            if (_openModules[key] is not Features.Ventes.VentesView { SaleInProgress: true })
                _openModules.Remove(key);
        if (_currentKey is not null)
            Navigate(_currentKey, _currentLabel ?? _currentKey, recordHistory: false);
    }

    private UserControl CreateModule(string key, string label) => key switch
    {
        "gestion-de-stock" => new StockView(_session),
        "ventes" => new VentesView(_session),
        "charges" => new ChargesView(_session),
        "marges" => new MargesView(_session),
        "amortissement" => new AmortissementView(_session),
        "bilan" => new BilanView(_session),
        "program" => new ProgrammeView(_session),
        "audit" => new AuditView(_session),
        "configuration" => new ConfigurationView(_session),
        "parametres" => CreateParametresView(),
        _ => PlaceholderView.For(label, key),
    };

    private void ShowPlaceholder(string title, string message)
    {
        ContentHost.Content = PlaceholderView.Message(title, message);
        StatusText.Text = title;
    }

    // --- Top bar commands ---

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_backStack.Count == 0) return;
        var (key, label) = _backStack.Pop();
        Navigate(key, label, recordHistory: false);
        BackButton.IsEnabled = _backStack.Count > 0;
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        var menu = _session.Menu;
        var first = menu?.Gestion.FirstOrDefault() ?? menu?.Espace.FirstOrDefault();
        if (first is not null) Navigate(first.Key, first.Label);
    }

    private void Avatar_Click(object sender, RoutedEventArgs e)
    {
        AvatarMenu.PlacementTarget = AvatarButton;
        AvatarMenu.IsOpen = true;
    }

    /// <summary>No notification feed exists yet; this just says so rather than pretending
    /// there is one to check.</summary>
    // --- Notifications ---

    /// <summary>Every few minutes, so a till left open or stock running out shows without F5.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromMinutes(3) };

    private IReadOnlyList<NotificationDto> _notifications = [];

    /// <summary>
    /// Asks the API for the current alerts and updates the badge. The server works out stock,
    /// backup, announcements and open tills and decides who sees which; "update available"
    /// is added here from <see cref="_update"/>, which only admins ever have. Quiet on failure:
    /// the previous list stays until the next try.
    /// </summary>
    private async Task RefreshNotificationsAsync()
    {
        try
        {
            var response = await _session.Api.GetNotificationsAsync();
            var items = response.Items.ToList();
            if (_update is not null)
                items.Insert(0, new NotificationDto("update", "info",
                    $"Mise à jour disponible : {_update.Version}",
                    $"Vous avez la version {AppUpdater.CurrentVersion}.", null));
            _notifications = items.Where(n => !_dismissedNotifications.Contains(DismissKey(n))).ToList();
        }
        catch (Exception ex) when (ex is ApiException or System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return;
        }

        ApplyNotificationBadge();
        if (NotificationsPopup.IsOpen) FillNotificationsPanel();
    }

    private void Notifications_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationsPopup.IsOpen)
        {
            NotificationsPopup.IsOpen = false;
            return;
        }

        FillNotificationsPanel();
        NotificationsPopup.IsOpen = true;
        _ = RefreshNotificationsAsync();
    }

    private void FillNotificationsPanel()
    {
        NotificationsPanel.Children.Clear();
        if (_notifications.Count == 0)
        {
            var none = new TextBlock { Text = "Aucune notification pour le moment.", Margin = new Thickness(8, 6, 8, 10) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            NotificationsPanel.Children.Add(none);
            return;
        }

        foreach (var item in _notifications) NotificationsPanel.Children.Add(NotificationRow(item));
    }

    /// <summary>Lines this user closed with "×". Keyed by the whole text, so an alert that changes
    /// (a third product out of stock) shows again; kept until the app closes.</summary>
    private readonly HashSet<string> _dismissedNotifications = [];

    private static string DismissKey(NotificationDto item) => $"{item.Kind}|{item.Title}|{item.Message}";

    /// <summary>The line's own buttons, on the right: "Clôturer" on someone else's open till for
    /// the Admin Général, and "×" to hide the line. Their clicks are marked handled so they do
    /// not also open the line's screen.</summary>
    private StackPanel Actions(NotificationDto item)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(panel, Dock.Right);

        if (item.Kind == "caisse" && int.TryParse(item.ActionId, out var caisseId) && _session.IsAdminGeneral && !_session.IsRemote)
        {
            var close = new Button
            {
                Content = new TextBlock { Text = "Clôturer", FontSize = 11, Style = (Style)FindResource("ButtonContentText") },
                Style = (Style)FindResource("DangerButton"),
                MinWidth = 0, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Clôturer cette caisse sans comptage",
            };
            close.Click += async (_, e) =>
            {
                e.Handled = true;
                await ForceCloseCaisseAsync(caisseId, item);
            };
            panel.Children.Add(close);
        }

        var dismiss = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10 },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Padding = new Thickness(4), FocusVisualStyle = null, ToolTip = "Masquer",
            Template = (ControlTemplate)Resources["DropdownRowTemplate"]!,
        };
        ((TextBlock)dismiss.Content).SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        dismiss.Click += (_, e) =>
        {
            e.Handled = true;
            _dismissedNotifications.Add(DismissKey(item));
            _notifications = _notifications.Where(n => DismissKey(n) != DismissKey(item)).ToList();
            ApplyNotificationBadge();
            FillNotificationsPanel();
        };
        panel.Children.Add(dismiss);
        return panel;
    }

    /// <summary>The Admin Général closing a till its owner left open. Asked first, since the
    /// drawer is not counted: the history records the close as forced.</summary>
    private async Task ForceCloseCaisseAsync(int caisseId, NotificationDto item)
    {
        NotificationsPopup.IsOpen = false;
        var answer = MessageBox.Show(this,
            $"{item.Title}\n{item.Message}\n\nClôturer cette caisse maintenant ? Les montants ne seront pas comptés : " +
            "la caisse est fermée sur les montants attendus, et l'historique indiquera une clôture forcée.",
            "Clôturer la caisse", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.ForceCloseCaisseAsync(caisseId);
            StatusText.Text = "Caisse clôturée";
        }
        catch (ApiException ex)
        {
            ShowApiError(ex);
        }
        await RefreshNotificationsAsync();
    }

    private void ApplyNotificationBadge()
    {
        NotificationBadge.Visibility = _notifications.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationBadgeText.Text = _notifications.Count > 9 ? "9+" : _notifications.Count.ToString();
    }

    /// <summary>A coloured icon by kind and urgency, a bold title and a muted detail line.
    /// Clicking opens the screen where the problem is dealt with.</summary>
    private Button NotificationRow(NotificationDto item)
    {
        var colourKey = item.Severity switch { "danger" => "Danger", "warning" => "Warning", _ => "Accent" };

        var icon = new TextBlock
        {
            Text = item.Kind switch
            {
                "stock" => "",   // package
                "backup" => "",  // cloud
                "annonce" => "", // megaphone
                "caisse" => "",  // money
                "update" => "",  // download
                _ => "",
            },
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            Width = 26,
            Margin = new Thickness(0, 2, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, colourKey);

        var title = new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var detail = new TextBlock { Text = item.Message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var text = new StackPanel();
        text.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(item.Message)) text.Children.Add(detail);

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(Actions(item));
        row.Children.Add(text);

        var button = new Button
        {
            Content = row,
            Cursor = item.Module is null && item.Kind != "update" ? Cursors.Arrow : Cursors.Hand,
            Padding = new Thickness(8, 8, 8, 8),
            Margin = new Thickness(2, 1, 2, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FocusVisualStyle = null,
            Template = (ControlTemplate)Resources["DropdownRowTemplate"]!,
        };

        button.Click += (_, _) =>
        {
            if (item.Kind == "update")
            {
                // The banner holds the install button; bring it back if it was hidden.
                NotificationsPopup.IsOpen = false;
                UpdateBanner.Visibility = Visibility.Visible;
                return;
            }
            if (item.Module is null) return;
            var entry = _session.Menu?.Gestion.Concat(_session.Menu.Espace)
                .FirstOrDefault(m => m.Key == item.Module);
            if (entry is null) return; // this user's menu does not reach that screen
            NotificationsPopup.IsOpen = false;
            Navigate(entry.Key, entry.Label);
        };
        return button;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        StatusText.Text = "Actualisation…";
        try
        {
            await _session.RefreshAsync();

            // Privileges may have changed under us; drop cached modules so they reload.
            _openModules.Clear();
            var key = _currentKey;
            _currentKey = null;

            BuildShell();

            if (key is not null &&
                (_session.Menu?.Gestion.Concat(_session.Menu.Espace)
                    .FirstOrDefault(e => e.Key == key) is { } entry))
            {
                Navigate(entry.Key, entry.Label, recordHistory: false);
            }

            StatusText.Text = "Actualisé";
        }
        catch (ApiException ex)
        {
            ShowApiError(ex);
        }
    }

    /// <summary>The signed-in user's own name, username and phone. From afar it is saved on the online
    /// account at once and applied to the shop's account when the shop next connects.</summary>
    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileDialog(_session) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        StatusText.Text = _session.IsRemote
            ? "Profil enregistré — appliqué à la boutique à sa prochaine connexion"
            : "Profil enregistré";
    }

    /// <summary>
    /// Changes the signed-in user's own password. Available to everyone, since a member
    /// has no Options screen to reach it from.
    /// </summary>
    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ChangePasswordDialog(_session) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        MessageBox.Show(this,
            "Votre mot de passe a été modifié.\n\nVos autres sessions ont été fermées.",
            "Mot de passe", MessageBoxButton.OK, MessageBoxImage.Information);

        StatusText.Text = "Mot de passe modifié";
    }

    private ParametresView CreateParametresView()
    {
        var view = new ParametresView(_session);
        view.EspaceDeleted += (_, _) => OnEspaceDeleted();
        return view;
    }

    /// <summary>
    /// The espace this window was showing no longer exists. Offer the ones that remain; if none is
    /// chosen there is nothing left to work in, so the app closes rather than sit on a deleted espace.
    /// </summary>
    private void OnEspaceDeleted()
    {
        var picker = new GroupPickerWindow { Owner = this };
        if (picker.ShowDialog() != true)
        {
            EndPresence();
            CustomerDisplayService.Instance.Stop();
            _session.SignOut();
            Application.Current.Shutdown();
            return;
        }

        _openModules.Clear();
        _currentKey = null;
        _backStack.Clear();
        BackButton.IsEnabled = false;
        BuildShell();
    }

    private void ChangeGroup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new GroupPickerWindow { Owner = this };
        if (picker.ShowDialog() != true) return;

        _openModules.Clear();
        _currentKey = null;
        _backStack.Clear();
        BackButton.IsEnabled = false;
        BuildShell();
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Se déconnecter de Lonnii ?", "Déconnexion",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        EndPresence();
        CustomerDisplayService.Instance.Stop();
        _session.SignOut();

        // Back to the sign-in screen, not out of the app: the till is shared, and the next
        // person signs in where the last one left. The shell closes once the app no longer
        // shuts down with it.
        var app = (App)Application.Current;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Close();
        app.Dispatcher.BeginInvoke(app.SignInAndOpenShell);
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        // No web address on screen: from afar it is simply "Lonnii"; at the shop, the server's machine.
        // 127.0.0.1 means nothing to a shop: on the main computer, say so in words.
        var server = _session.IsRemote
            ? "Lonnii"
            : AppUpdater.ServerIsThisComputer(_session.Api.BaseAddress)
                ? "cet ordinateur (ordinateur principal)"
                : Uri.TryCreate(_session.Api.BaseAddress, UriKind.Absolute, out var address) ? address.Host : _session.Api.BaseAddress;

        MessageBox.Show(this,
            $"Lonnii Business {AppUpdater.CurrentVersion} — poste Windows\n\n" +
            $"Serveur : {server}\n" +
            $"Espace : {_session.Groupe?.Nom}\n" +
            $"Utilisateur : {_session.DisplayName}\n\n" +
            (_session.IsRemote
                ? "Accès à distance : vous voyez la dernière sauvegarde de la boutique."
                : "Les données sont conservées sur l'ordinateur hôte."),
            "À propos", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowApiError(ApiException ex)
    {
        StatusText.Text = "Erreur";
        MessageBox.Show(this, ex.Message, "Lonnii", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

/// <summary>A minimal ICommand, for the few keyboard shortcuts the shell binds.</summary>
public class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();

    /// <summary>Kept so the compiler does not warn on the unused event.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
