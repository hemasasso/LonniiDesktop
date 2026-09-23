using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lonnii.Client.Services;
using Lonnii.Client.Views;
using Lonnii.Client.Views.Modules;
using Lonnii.Client.Views.Dialogs;
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

        Loaded += (_, _) => BuildShell();

        ThemeManager.Changed += (_, _) => ApplyThemeButtonVisuals();
        ApplyThemeButtonVisuals();
    }

    private void DarkMode_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    private void ApplyThemeButtonVisuals()
    {
        DarkModeButton.Content = ThemeManager.IsDark ? "☀" : "🌙";
        DarkModeButton.ToolTip = ThemeManager.IsDark ? "Mode clair" : "Mode sombre";
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
        StatusHost.Text = _session.Api.BaseAddress ?? string.Empty;

        AvatarButton.Content = Initials(_session.DisplayName);
        AvatarGroupHeader.Header = groupe?.Nom ?? "Aucun espace";
        AvatarRoleHeader.Header = DescribeRole();
        AvatarRoleHeader.Visibility = string.IsNullOrEmpty(DescribeRole()) ? Visibility.Collapsed : Visibility.Visible;

        BuildTopNav(menu);

        // Land on the first thing the user is allowed to open.
        if (_currentKey is null)
        {
            var first = menu?.Gestion.FirstOrDefault() ?? menu?.Espace.FirstOrDefault();
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

    private void BuildTopNav(MenuResponse? menu)
    {
        NavPillPanel.Children.Clear();
        GestionPopup.IsOpen = false;
        GestionPopupPanel.Children.Clear();
        if (menu is null) return;

        foreach (var entry in menu.Espace)
        {
            if (entry.Key == "espace/gestion")
            {
                NavPillPanel.Children.Add(GestionPill(entry, menu));
                continue;
            }

            NavPillPanel.Children.Add(NavPill(entry, entry.Key == _currentKey));
        }

        BuildGestionPopup(menu);
    }

    /// <summary>Fills the Gestion dropdown - the admin grouping when the user is an admin,
    /// same as the web app's Gestion page, or a flat list otherwise.</summary>
    private void BuildGestionPopup(MenuResponse menu)
    {
        if (_session.IsAdmin && menu.GestionSections.Count > 0)
        {
            foreach (var section in menu.GestionSections)
            {
                GestionPopupPanel.Children.Add(SectionHeader(section.Label.ToUpperInvariant()));
                foreach (var entry in section.Entries) GestionPopupPanel.Children.Add(DropdownRow(entry));
            }
        }
        else
        {
            foreach (var entry in menu.Gestion) GestionPopupPanel.Children.Add(DropdownRow(entry));
        }
    }

    private static TextBlock SectionHeader(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["SectionHeader"] };

    /// <summary>A pill in the top bar that opens directly into a module (Chat, Programme,
    /// Options...). The active module's pill is filled yellow; every other one is a
    /// translucent white so it reads on the purple gradient without competing with it.</summary>
    private Button NavPill(MenuEntryDto entry, bool isActive)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = $"{Glyph(entry.Key)} {entry.Label}", FontSize = 13 },
            ToolTip = entry.Description,
            Cursor = Cursors.Hand,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(0),
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#1B1F27")!,
            Background = isActive
                ? (Brush)new BrushConverter().ConvertFromString("#D9A400")!
                : (Brush)new BrushConverter().ConvertFromString("#33FFFFFF")!,
            Template = (ControlTemplate)Resources["PillNavButtonTemplate"]!,
        };

        button.Click += (_, _) => Navigate(entry.Key, entry.Label);
        return button;
    }

    /// <summary>The "Gestion" pill. It never navigates on its own - there is no single
    /// screen behind it - it opens <see cref="GestionPopup"/> to list its sub-modules
    /// instead. It stays highlighted for as long as the open module belongs to Gestion.</summary>
    private Button GestionPill(MenuEntryDto entry, MenuResponse menu)
    {
        var isActive = menu.Gestion.Any(g => g.Key == _currentKey);

        var button = new Button
        {
            Content = new TextBlock { Text = $"{Glyph(entry.Key)} {entry.Label}  ▾", FontSize = 13 },
            ToolTip = entry.Description,
            Cursor = Cursors.Hand,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(0),
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#1B1F27")!,
            Background = isActive
                ? (Brush)new BrushConverter().ConvertFromString("#D9A400")!
                : (Brush)new BrushConverter().ConvertFromString("#33FFFFFF")!,
            Template = (ControlTemplate)Resources["PillNavButtonTemplate"]!,
        };

        button.Click += (_, _) =>
        {
            GestionPopup.PlacementTarget = button;
            GestionPopup.IsOpen = !GestionPopup.IsOpen;
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
            Width = 3,
            Background = accent,
            CornerRadius = new CornerRadius(2),
            Opacity = isCurrent ? 1 : 0.55,
        };

        var label = new TextBlock
        {
            Text = entry.Label,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
        };

        var row = new DockPanel { Height = 32 };
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
            Background = isCurrent ? (Brush)Application.Current.Resources["AccentLight"] : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = (ControlTemplate)Resources["DropdownRowTemplate"]!,
        };

        button.Click += (_, _) =>
        {
            GestionPopup.IsOpen = false;
            Navigate(entry.Key, entry.Label);
        };
        return button;
    }

    /// <summary>Small glyph per module, matching the icon each pill shows in the web app
    /// closely enough to tell them apart at a glance.</summary>
    private static string Glyph(string key) => key switch
    {
        "chat" => "💬",
        "program" => "🗓",
        "formulaire" => "📄",
        "prestations" => "🧾",
        "options" => "⚙",
        "espace/gestion" => "📊",
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
        BackButton.IsEnabled = _backStack.Count > 0;

        if (!_openModules.TryGetValue(key, out var view))
        {
            view = CreateModule(key, label);
            _openModules[key] = view;
        }

        ContentHost.Content = view;
        BuildTopNav(_session.Menu);
    }

    private UserControl CreateModule(string key, string label) => key switch
    {
        "gestion-de-stock" => new StockView(_session),
        "ventes" => new VentesView(_session),
        "options" => new MembersView(_session),
        "parametres" => new ParametresView(_session),
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
    private void Notifications_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, "Aucune notification pour le moment.", "Notifications",
            MessageBoxButton.OK, MessageBoxImage.Information);

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

        _session.SignOut();
        Application.Current.Shutdown();
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Lonnii Business — poste Windows\n\n" +
            $"Serveur : {_session.Api.BaseAddress}\n" +
            $"Espace : {_session.Groupe?.Nom}\n" +
            $"Utilisateur : {_session.DisplayName}\n\n" +
            "Les données sont conservées sur l'ordinateur hôte.",
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
