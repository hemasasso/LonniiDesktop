using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Lonnii.Client.Features.CustomerDisplay;

/// <summary>Parametres → Écran client: turns the customer display on or off and chooses which
/// kinds of display to drive on this machine.</summary>
public partial class CustomerDisplaySettingsDialog : Window
{
    private static readonly (string Label, int Columns, int Rows)[] Sizes =
    [
        ("20 colonnes × 2 lignes", 20, 2),
        ("16 colonnes × 2 lignes", 16, 2),
        ("20 colonnes × 4 lignes", 20, 4),
        ("16 colonnes × 4 lignes", 16, 4),
        ("40 colonnes × 2 lignes", 40, 2),
    ];

    private static readonly string[] FontChoices =
        ["Segoe UI", "Segoe UI Variable Display", "Arial", "Calibri", "Verdana", "Tahoma", "Trebuchet MS", "Georgia", "Century Gothic", "Consolas"];

    private static readonly int[] ScaleChoices = [40, 50, 60, 70, 80, 90, 100, 110, 120, 130];

    private readonly CustomerDisplayService _service = CustomerDisplayService.Instance;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<MonitorInfo> _monitors = [];

    public CustomerDisplaySettingsDialog()
    {
        InitializeComponent();

        foreach (var combo in new[] { SerialSizeCombo, NetSizeCombo })
            foreach (var size in Sizes) combo.Items.Add(new ComboBoxItem { Content = size.Label, Tag = size });
        foreach (var combo in new[] { SerialProtocolCombo, NetProtocolCombo })
            foreach (var (key, label) in DisplayProtocols.All) combo.Items.Add(new ComboBoxItem { Content = label, Tag = key });

        foreach (var (key, label) in DisplayThemes.All) ThemeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        foreach (var (hex, label) in DisplayThemes.Accents) AccentCombo.Items.Add(new ComboBoxItem { Content = label, Tag = hex });

        // Only the typefaces the shop's machine really has; the list shows each in its own face.
        var installed = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in FontChoices.Where(installed.Contains))
            FontCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name, FontFamily = new System.Windows.Media.FontFamily(name) });
        foreach (var percent in ScaleChoices)
            ScaleCombo.Items.Add(new ComboBoxItem { Content = $"{percent} %{(percent == 80 ? " (par défaut)" : string.Empty)}", Tag = percent });

        LoadPorts();
        LoadMonitors();
        Load(_service.Settings);

        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
    }

    // --- Form <-> settings ---

    private void Load(CustomerDisplaySettings s)
    {
        EnabledCheck.IsChecked = s.Enabled;
        ThemeCombo.SelectedItem = ThemeCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Theme)
            ?? ThemeCombo.Items.Cast<ComboBoxItem>().First();
        AccentBox.Text = s.AccentColor;
        FontCombo.SelectedItem = FontCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.FontFamily)
            ?? FontCombo.Items.Cast<ComboBoxItem>().FirstOrDefault();
        ScaleCombo.SelectedItem = ScaleCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == s.TextScale)
            ?? ScaleCombo.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == 80);
        ScreenCheck.IsChecked = s.ScreenEnabled;
        AutoScreenCheck.IsChecked = s.AutoDetectScreen;
        var items = ScreenCombo.Items.Cast<ComboBoxItem>().ToList();
        ScreenCombo.SelectedItem =
            items.FirstOrDefault(i => ((MonitorInfo)i.Tag).DeviceName == s.ScreenDevice)
            ?? items.FirstOrDefault(i => !((MonitorInfo)i.Tag).IsPrimary)
            ?? items.FirstOrDefault();

        SerialCheck.IsChecked = s.Serial.Enabled;
        PortCombo.Text = s.Serial.Port ?? string.Empty;
        BaudCombo.SelectedItem = BaudCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Content == s.Serial.BaudRate.ToString())
            ?? BaudCombo.Items.Cast<ComboBoxItem>().First(i => (string)i.Content == "9600");
        Select(SerialProtocolCombo, s.Serial.Protocol);
        SelectSize(SerialSizeCombo, s.Serial);

        NetworkCheck.IsChecked = s.Network.Enabled;
        HostBox.Text = s.Network.Host ?? string.Empty;
        NetPortBox.Text = s.Network.Port.ToString();
        Select(NetProtocolCombo, s.Network.Protocol);
        SelectSize(NetSizeCombo, s.Network);

        UpdateEnabledState();
        RefreshStatus();
    }

    private bool TryRead(out CustomerDisplaySettings settings)
    {
        settings = new CustomerDisplaySettings
        {
            Enabled = EnabledCheck.IsChecked == true,
            ScreenEnabled = ScreenCheck.IsChecked == true,
            AutoDetectScreen = AutoScreenCheck.IsChecked == true,
            Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? DisplayThemes.Light,
            AccentColor = TryAccent(AccentBox.Text, out var accent) ? accent : CustomerDisplaySettings.DefaultAccent,
            FontFamily = (FontCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? CustomerDisplaySettings.DefaultFont,
            TextScale = (ScaleCombo.SelectedItem as ComboBoxItem)?.Tag is int percent ? percent : 80,
            ScreenDevice = (ScreenCombo.SelectedItem as ComboBoxItem)?.Tag is MonitorInfo monitor ? monitor.DeviceName : null,
        };

        settings.Serial.Enabled = SerialCheck.IsChecked == true;
        settings.Serial.Port = PortCombo.Text.Trim();
        settings.Serial.BaudRate = int.TryParse((BaudCombo.SelectedItem as ComboBoxItem)?.Content as string, out var baud) ? baud : 9600;
        settings.Serial.Protocol = ProtocolOf(SerialProtocolCombo);
        ReadSize(SerialSizeCombo, settings.Serial);

        settings.Network.Enabled = NetworkCheck.IsChecked == true;
        settings.Network.Host = HostBox.Text.Trim();
        settings.Network.Protocol = ProtocolOf(NetProtocolCombo);
        ReadSize(NetSizeCombo, settings.Network);

        if (!int.TryParse(NetPortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            if (settings.Enabled && settings.Network.Enabled)
            {
                MessageBox.Show(this, "Le port TCP doit être un nombre entre 1 et 65535.", "Écran client",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            port = 9100;
        }
        settings.Network.Port = port;

        if (settings.Enabled && settings.Serial.Enabled && string.IsNullOrWhiteSpace(settings.Serial.Port))
        {
            MessageBox.Show(this, "Choisissez le port de l'afficheur USB / série.", "Écran client",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (settings.Enabled && settings.Network.Enabled && string.IsNullOrWhiteSpace(settings.Network.Host))
        {
            MessageBox.Show(this, "Saisissez l'adresse de l'afficheur réseau.", "Écran client",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    /// <summary>A usable <c>#RRGGBB</c>: typed with or without the leading #.</summary>
    private static bool TryAccent(string text, out string hex)
    {
        hex = "#" + text.Trim().TrimStart('#');
        return hex.Length == 7 && hex[1..].All(Uri.IsHexDigit);
    }

    private bool _syncingAccent;

    private void AccentPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAccent || AccentCombo.SelectedItem is not ComboBoxItem { Tag: string hex }) return;
        AccentBox.Text = hex;
    }

    private void Accent_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!TryAccent(AccentBox.Text, out var hex))
        {
            AccentSwatch.Background = null;
            return;
        }

        AccentSwatch.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;

        // Keep the preset list in step with a typed colour.
        _syncingAccent = true;
        AccentCombo.SelectedItem = AccentCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string)i.Tag, hex, StringComparison.OrdinalIgnoreCase));
        _syncingAccent = false;
    }

    private static string ProtocolOf(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? DisplayProtocols.Text;

    private static void Select(ComboBox combo, string protocol) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == protocol)
            ?? combo.Items.Cast<ComboBoxItem>().First();

    private static void SelectSize(ComboBox combo, CharacterDisplaySettings s) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag is ValueTuple<string, int, int> size && size.Item2 == s.Columns && size.Item3 == s.Rows)
            ?? combo.Items.Cast<ComboBoxItem>().First();

    private static void ReadSize(ComboBox combo, CharacterDisplaySettings s)
    {
        var (_, cols, rows) = combo.SelectedItem is ComboBoxItem { Tag: ValueTuple<string, int, int> size } ? size : Sizes[0];
        s.Columns = cols;
        s.Rows = rows;
    }

    // --- Detection ---

    private void LoadPorts()
    {
        var current = PortCombo.Text;
        PortCombo.Items.Clear();
        foreach (var name in SerialPort.GetPortNames().Order()) PortCombo.Items.Add(name);
        PortCombo.Text = current;
    }

    private void LoadMonitors()
    {
        _monitors = _service.Monitors;
        ScreenCombo.Items.Clear();
        foreach (var monitor in _monitors)
            ScreenCombo.Items.Add(new ComboBoxItem { Content = monitor.Label, Tag = monitor });
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => LoadPorts();

    // --- State ---

    private void Master_Changed(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void Screen_Changed(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void Serial_Changed(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void Network_Changed(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void UpdateEnabledState()
    {
        if (!IsInitialized || Body is null) return;

        Body.IsEnabled = EnabledCheck.IsChecked == true;
        ScreenBody.IsEnabled = ScreenCheck.IsChecked == true;
        ScreenCombo.IsEnabled = ScreenCheck.IsChecked == true && AutoScreenCheck.IsChecked != true;
        SerialBody.IsEnabled = SerialCheck.IsChecked == true;
        NetworkBody.IsEnabled = NetworkCheck.IsChecked == true;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (!IsInitialized || ScreenStatus is null) return;

        var secondary = _monitors.Count(m => !m.IsPrimary);
        ScreenStatus.Text = _monitors.Count switch
        {
            0 => "Aucun écran détecté.",
            1 => "Un seul écran détecté : branchez le second écran, l'affichage démarrera tout seul.",
            _ => $"{_monitors.Count} écrans détectés ({secondary} secondaire{(secondary > 1 ? "s" : string.Empty)}).",
        };
        if (_service.IsScreenActive) ScreenStatus.Text += " Écran client affiché.";

        SerialStatus.Text = _service.SerialStatus is { } serial ? $"État : {serial}" : string.Empty;
        NetworkStatus.Text = _service.NetworkStatus is { } net ? $"État : {net}" : string.Empty;
    }

    // --- Actions ---

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var settings)) return;
        _service.Apply(settings);
        DialogResult = true;
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var settings)) return;
        _service.Apply(settings);

        if (!_service.ShowTest())
        {
            MessageBox.Show(this,
                "Aucun afficheur n'est actif : activez l'écran client, branchez un second écran " +
                "ou choisissez un afficheur USB / réseau.",
                "Écran client", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        RefreshStatus();
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => _service.OpenPreview();
}
