using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Client.Views;
using Lonnii.Client.Views.Modules;
using Lonnii.Client.Views.Dialogs;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client;

/// <summary>
/// The application shell: menu bar, privilege-filtered navigation, content host and
/// status bar.
///
/// The navigation is built from the menu the API returns rather than from a list held
/// here, so the desktop shows exactly what Lonnii Business would show the same user. A
/// module the user cannot open never appears.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSession _session = App.Session;
    private readonly Dictionary<string, UserControl> _openModules = new(StringComparer.Ordinal);
    private string? _currentKey;

    public MainWindow()
    {
        InitializeComponent();

        _session.Changed += (_, _) => Dispatcher.Invoke(BuildShell);

        InputBindings.Add(new KeyBinding(
            new RelayCommand(async () => await RefreshAsync()), Key.F5, ModifierKeys.None));

        Loaded += (_, _) => BuildShell();
    }

    /// <summary>Rebuilds every part of the shell that depends on the current group and privileges.</summary>
    private void BuildShell()
    {
        var groupe = _session.Groupe;
        var menu = _session.Menu;

        Title = groupe is null ? "Lonnii Business" : $"Lonnii Business — {groupe.Nom}";
        GroupName.Text = groupe?.Nom ?? "Aucun espace";
        GroupRole.Text = DescribeRole();

        StatusUser.Text = _session.DisplayName;
        StatusHost.Text = _session.Api.BaseAddress ?? string.Empty;

        BuildNavigation(menu);
        BuildMenuBar(menu);

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

    /// <summary>A short description of the user's standing, shown under the group name.</summary>
    private string DescribeRole() =>
        _session.Privileges is null
            ? string.Empty
            : GroupRoles.DisplayName(_session.Privileges.Role, _session.IsAdminGeneral);

    private void BuildNavigation(MenuResponse? menu)
    {
        NavPanel.Children.Clear();
        if (menu is null) return;

        if (menu.Espace.Count > 0)
        {
            NavPanel.Children.Add(SectionHeader("ESPACE"));
            foreach (var entry in menu.Espace)
            {
                // Gestion is the sidebar's own section below; no need for a duplicate entry.
                if (entry.Key == "espace/gestion") continue;
                NavPanel.Children.Add(NavButton(entry));
            }
        }

        // Admins get the same Opérations / Finance / Administration grouping as the web app.
        if (_session.IsAdmin && menu.GestionSections.Count > 0)
        {
            foreach (var section in menu.GestionSections)
            {
                NavPanel.Children.Add(SectionHeader(section.Label.ToUpperInvariant()));
                foreach (var entry in section.Entries) NavPanel.Children.Add(NavButton(entry));
            }
        }
        else if (menu.Gestion.Count > 0)
        {
            NavPanel.Children.Add(SectionHeader("GESTION"));
            foreach (var entry in menu.Gestion) NavPanel.Children.Add(NavButton(entry));
        }
    }

    private void BuildMenuBar(MenuResponse? menu)
    {
        MenuEspace.Items.Clear();
        MenuGestion.Items.Clear();

        foreach (var entry in menu?.Espace ?? [])
        {
            if (entry.Key == "espace/gestion") continue;
            MenuEspace.Items.Add(MenuEntry(entry));
        }

        foreach (var entry in menu?.Gestion ?? [])
            MenuGestion.Items.Add(MenuEntry(entry));

        MenuEspace.IsEnabled = MenuEspace.Items.Count > 0;
        MenuGestion.IsEnabled = MenuGestion.Items.Count > 0;

        MenuItem MenuEntry(MenuEntryDto entry)
        {
            var item = new MenuItem { Header = entry.Label, ToolTip = entry.Description };
            item.Click += (_, _) => Navigate(entry.Key, entry.Label);
            return item;
        }
    }

    private static TextBlock SectionHeader(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["SectionHeader"] };

    private Button NavButton(MenuEntryDto entry)
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

        var row = new DockPanel { Height = 34 };
        DockPanel.SetDock(stripe, Dock.Left);
        row.Children.Add(stripe);
        row.Children.Add(label);

        var button = new Button
        {
            Content = row,
            ToolTip = entry.Description,
            Cursor = Cursors.Hand,
            Padding = new Thickness(11, 0, 11, 0),
            Margin = new Thickness(6, 1, 6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = isCurrent
                ? (Brush)Application.Current.Resources["AccentLight"]
                : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = (ControlTemplate)Resources["NavButtonTemplate"]!,
        };

        button.Click += (_, _) => Navigate(entry.Key, entry.Label);
        return button;
    }

    /// <summary>
    /// Opens a module, reusing the instance if it is already open so grids keep their
    /// scroll position and filters when the user moves between screens.
    /// </summary>
    private void Navigate(string key, string label)
    {
        _currentKey = key;
        StatusText.Text = label;

        if (!_openModules.TryGetValue(key, out var view))
        {
            view = CreateModule(key, label);
            _openModules[key] = view;
        }

        ContentHost.Content = view;
        BuildNavigation(_session.Menu);
    }

    private UserControl CreateModule(string key, string label) => key switch
    {
        "gestion-de-stock" => new StockView(_session),
        "options" => new MembersView(_session),
        "parametres" => new ParametresView(_session),
        _ => PlaceholderView.For(label, key),
    };

    private void ShowPlaceholder(string title, string message)
    {
        ContentHost.Content = PlaceholderView.Message(title, message);
        StatusText.Text = title;
    }

    // --- Menu bar commands ---

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
                Navigate(entry.Key, entry.Label);
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
