using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Features.CustomerDisplay;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Parametres;

/// <summary>
/// Administration → Configuration &amp; Serveurs: the shop's servers and running configuration -
/// connexion, devise, reçus et factures, écran client, bilan automatique, consommation des
/// données, données de l'espace - split out of Paramètres so it can be handed to a team.
///
/// Each section has its own privilege (configuration module of the catalogue), and shows only
/// to someone holding it; "Gérer Paramètres" and every admin role hold them all. The API
/// checks the same privilege behind each section.
/// </summary>
public partial class ConfigurationView : UserControl
{
    private readonly AppSession _session;

    public ConfigurationView(AppSession session)
    {
        _session = session;
        InitializeComponent();

        Show(ConnexionPanel, Priv.Gestion.ViewConnexion);
        Show(CurrencyPanel, Priv.Gestion.ManageDevise);
        Show(VentesSettingsPanel, Priv.Gestion.ManageRecuFacture);
        Show(CustomerDisplaySettingsPanel, Priv.Gestion.ManageEcranClient);
        Show(BilanSettingsPanel, Priv.Gestion.ManageBilanAutomatique);
        Show(ConsumptionPanel, Priv.Gestion.ViewConsommation);
        Show(EspaceDataPanel, Priv.Gestion.ManageDonneesEspace);

        // Whichever section comes first on screen should not open with a gap above it.
        var first = new FrameworkElement[]
        {
            ConnexionPanel, CurrencyPanel, VentesSettingsPanel, CustomerDisplaySettingsPanel,
            BilanSettingsPanel, ConsumptionPanel, EspaceDataPanel,
        }.FirstOrDefault(p => p.Visibility == Visibility.Visible);
        if (first is Panel { Children.Count: > 0 } panel && panel.Children[0] is FrameworkElement header)
            header.Margin = new Thickness(header.Margin.Left, 0, header.Margin.Right, header.Margin.Bottom);

        Populate();
        if (_session.Can(Priv.Gestion.ViewConsommation)) PopulateConsumptionYears();
        if (_session.Can(Priv.Gestion.ViewConnexion)) _ = LoadConnexionAsync();
    }

    private void Show(UIElement panel, string privilege) =>
        panel.Visibility = _session.Can(privilege) ? Visibility.Visible : Visibility.Collapsed;

    private void Populate()
    {
        var groupe = _session.Groupe;
        CurrencyBox.Text = groupe?.CurrencyLabel ?? Money.Label;
        (groupe?.CurrencyBefore == true ? CurrencyBeforeRadio : CurrencyAfterRadio).IsChecked = true;
        UpdateCurrencyPreview();
        if (_session.Can(Priv.Gestion.ManageBilanAutomatique)) _ = LoadCalculAutomatiqueAsync();
    }

    // --- Connexion ---

    private async void RefreshConnexion_Click(object sender, RoutedEventArgs e) => await LoadConnexionAsync();

    /// <summary>One row per server the espace depends on: a coloured dot for its state, its
    /// name, what it is for, and when it last answered.</summary>
    private async Task LoadConnexionAsync()
    {
        ConnexionList.Children.Clear();
        ConnexionResponse connexion;
        try
        {
            connexion = await _session.Api.GetConnexionAsync();
        }
        catch (ApiException ex)
        {
            ConnexionList.Children.Add(ConnexionRow("Danger", "Serveur de la boutique", "Injoignable", ex.Message));
            return;
        }

        MachineText.Text = $"Ce poste : {Environment.MachineName}";
        ConnexionList.Children.Add(new TextBlock
        {
            Text = connexion.Mode == DeploymentModes.Online ? "Mode en ligne" : "Mode local",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
        });

        foreach (var serveur in connexion.Serveurs)
        {
            var (colorKey, etat) = serveur.Etat switch
            {
                ConnexionEtats.Connecte => ("Success", "Connecté"),
                ConnexionEtats.HorsLigne => ("Danger", "Hors ligne"),
                _ => ("TextMuted", "Inactif"),
            };

            // The shop's own server is reached at the address this client is using; the
            // central server is never addressed on screen.
            var nom = serveur.Role == ConnexionRoles.Boutique
                ? $"{serveur.Nom} ({serveur.Hote}{(_session.Api.BaseAddress is { } address ? $" · {address.TrimEnd('/')}" : string.Empty)})"
                : serveur.Nom;
            var contact = serveur.DernierContact is { } at && serveur.Role != ConnexionRoles.Boutique
                ? $"Dernier contact : {at.ToLocalTime():dd/MM/yyyy à HH:mm}"
                : null;
            ConnexionList.Children.Add(ConnexionRow(colorKey, nom, etat,
                string.Join(" · ", new[] { serveur.Detail, contact }.Where(t => !string.IsNullOrEmpty(t)))));
        }

        if (!connexion.Active)
        {
            var note = new TextBlock
            {
                Text = "Cet espace n'est pas relié au serveur central Lonnii.", FontSize = 12, TextWrapping = TextWrapping.Wrap,
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            ConnexionList.Children.Add(note);
        }
    }

    private UIElement ConnexionRow(string colorKey, string nom, string etat, string? detail)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        var state = new TextBlock { Text = etat, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top };
        state.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        DockPanel.SetDock(state, Dock.Right);
        row.Children.Add(state);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 9, Height = 9, Margin = new Thickness(0, 5, 10, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, colorKey);
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = nom, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
        {
            var detailText = new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            detailText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            text.Children.Add(detailText);
        }
        row.Children.Add(text);
        return row;
    }

    // --- Devise ---

    /// <summary>Fires while the view is still being built (the radio's IsChecked in XAML), before
    /// every control exists - hence the null check.</summary>
    private void CurrencyInput_Changed(object sender, RoutedEventArgs e)
    {
        if (CurrencyPreviewText is null || CurrencyBox is null) return;
        UpdateCurrencyPreview();
        if (CurrencyStatusText is not null) CurrencyStatusText.Text = string.Empty;
    }

    private void UpdateCurrencyPreview()
    {
        var label = CurrencyBox.Text.Trim();
        CurrencyPreviewText.Text = label.Length == 0
            ? "—"
            : Money.WithLabel(1500, Money.FormatPlain(1500m), label, CurrencyBeforeRadio.IsChecked == true);
    }

    /// <summary>Saves the new currency label and position and applies them immediately
    /// across the app.</summary>
    private async void SaveCurrency_Click(object sender, RoutedEventArgs e)
    {
        var label = CurrencyBox.Text.Trim();
        if (label.Length == 0)
        {
            CurrencyStatusText.Text = "La devise ne peut pas être vide.";
            CurrencyStatusText.Foreground = (Brush)FindResource("Danger");
            return;
        }

        try
        {
            var updated = await _session.Api.UpdateCurrencyAsync(label, CurrencyBeforeRadio.IsChecked == true);
            _session.ApplyCurrencyChange(updated);
            CurrencyBox.Text = updated.CurrencyLabel;
            CurrencyStatusText.Text = "Enregistré.";
            CurrencyStatusText.Foreground = (Brush)FindResource("TextSecondary");
        }
        catch (ApiException ex)
        {
            CurrencyStatusText.Text = ex.Message;
            CurrencyStatusText.Foreground = (Brush)FindResource("Danger");
        }
    }

    // --- Reçus et factures ---

    /// <summary>Opens the reçu/facture editor. Its own Enregistrer does the saving and
    /// refreshes the session's cached settings, so there is nothing to do on return.</summary>
    private void OpenReceiptSettings_Click(object sender, RoutedEventArgs e) =>
        new ReceiptSettingsDialog(_session) { Owner = Window.GetWindow(this) }.ShowDialog();

    // --- Écran client ---

    private void OpenCustomerDisplay_Click(object sender, RoutedEventArgs e) =>
        new CustomerDisplaySettingsDialog { Owner = Window.GetWindow(this) }.ShowDialog();

    // --- Bilan ---

    /// <summary>Set while <see cref="CalculAutomatiqueCheck"/> is being populated from the
    /// server, so that does not itself fire a save.</summary>
    private bool _loadingCalculAutomatique;

    private async Task LoadCalculAutomatiqueAsync()
    {
        try
        {
            _loadingCalculAutomatique = true;
            var parametres = await _session.Api.GetComptabiliteParametresAsync();
            CalculAutomatiqueCheck.IsChecked = parametres.CalculAutomatique;
        }
        catch (ApiException)
        {
            // A convenience toggle - a failed read just leaves it at its default (checked).
        }
        finally
        {
            _loadingCalculAutomatique = false;
        }
    }

    private async void CalculAutomatique_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCalculAutomatique) return;

        try
        {
            await _session.Api.SaveComptabiliteParametresAsync(
                new SaveComptabiliteParametresRequest(CalculAutomatiqueCheck.IsChecked == true));
        }
        catch (ApiException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Bilan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Consommation données ---

    /// <summary>"Toutes les années" plus the last five, as the web app's picker offers. The
    /// selection fires the first load.</summary>
    private void PopulateConsumptionYears()
    {
        var items = new List<ComboBoxItem> { new() { Content = "Toutes les années", Tag = null } };
        for (var year = DateTime.Today.Year; year > DateTime.Today.Year - 5; year--)
            items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });

        ConsumptionYearCombo.ItemsSource = items;
        ConsumptionYearCombo.SelectedIndex = 0;
    }

    private void ConsumptionHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var open = ConsumptionBody.Visibility != Visibility.Visible;
        ConsumptionBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ConsumptionHeader.Text = (open ? "▾  " : "▸  ") + "CONSOMMATION DONNÉES";
    }

    private async void ConsumptionYear_Changed(object sender, SelectionChangedEventArgs e)
    {
        var year = (ConsumptionYearCombo.SelectedItem as ComboBoxItem)?.Tag as int?;
        try
        {
            var data = await _session.Api.GetDataConsumptionAsync(year);

            ConsumptionRecordsText.Text = Money.FormatPlain(data.TotalRecords);
            ConsumptionVolumeText.Text = FormatBytes(data.TotalBytes);
            ConsumptionSectionsText.Text = $"{data.SectionsWithData} / {data.Sections}";
            ConsumptionFilesText.Text = Money.FormatPlain(data.TotalFiles);

            ConsumptionGrid.ItemsSource = data.Rows
                .OrderByDescending(r => r.Bytes)
                .Select(r => new
                {
                    r.Section,
                    r.Label,
                    CountDisplay = Money.FormatPlain(r.Count),
                    BytesDisplay = FormatBytes(r.Bytes),
                })
                .ToList();

            ConsumptionNoteText.Foreground = (Brush)FindResource("TextSecondary");
        }
        catch (ApiException ex)
        {
            ConsumptionNoteText.Text = ex.Message;
            ConsumptionNoteText.Foreground = (Brush)FindResource("Danger");
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} o",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} Ko",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} Mo",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} Go",
    };

    // --- Données de l'espace ---

    /// <summary>Opens the espace export/import screen. Refreshes afterwards: an import brings
    /// the file's currency and module settings with it.</summary>
    private async void OpenEspaceTransfer_Click(object sender, RoutedEventArgs e)
    {
        new EspaceTransferDialog(_session) { Owner = Window.GetWindow(this) }.ShowDialog();

        try { await _session.RefreshAsync(); }
        catch (ApiException) { /* best-effort refresh; the dialog already reported any error */ }
        Populate();
    }

    /// <summary>Opens the cloud backup status and its controls. Refreshes afterwards: a restore
    /// brings the espace's settings and staff back with it.</summary>
    private async void OpenCloudBackup_Click(object sender, RoutedEventArgs e)
    {
        new CloudBackupDialog(_session) { Owner = Window.GetWindow(this) }.ShowDialog();

        try { await _session.RefreshAsync(); }
        catch (ApiException) { /* best-effort refresh; the dialog already reported any error */ }
        Populate();
    }
}
