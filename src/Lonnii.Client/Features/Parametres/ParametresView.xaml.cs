using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Features.CustomerDisplay;
using Lonnii.Client.Features.Payments;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Parametres;

/// <summary>
/// The Paramètres screen: what this workspace is configured to do, where this machine is
/// connected, and exactly which privileges the signed-in user holds.
///
/// The privilege list is worth showing plainly. In Lonnii Business a user who cannot see
/// a module has no way to find out why; here the technical name is on screen, so an
/// administrator can be asked for that specific privilege.
/// </summary>
public partial class ParametresView : UserControl
{
    private readonly AppSession _session;

    public ParametresView(AppSession session)
    {
        _session = session;
        InitializeComponent();
        Populate();
    }

    private void Populate()
    {
        var groupe = _session.Groupe;
        var privileges = _session.Privileges;

        GroupNameText.Text = groupe?.Nom ?? "—";
        MemberCountText.Text = groupe is null ? "—" : $"{groupe.MemberCount}";

        RoleText.Text = _session.IsAdminGeneral
            ? "Administrateur Général (créateur de l'espace)"
            : GroupRoles.DisplayName(privileges?.Role);

        GestionAccessText.Text = groupe?.GestionAccess == true ? "Activé" : "Désactivé";

        PrestationsText.Text = groupe?.PrestationsEnabled == true
            ? $"Activé ({groupe.PrestationsLocation ?? "gestion"})"
            : "Désactivé";

        HostText.Text = _session.Api.BaseAddress ?? "—";
        MachineText.Text = Environment.MachineName;

        // Gated the same way as Currency/Ventes below, and for the same reason.
        MembersPanel.Visibility = _session.IsAdmin ? Visibility.Visible : Visibility.Collapsed;

        CurrencyPanel.Visibility = _session.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        CurrencyBox.Text = groupe?.CurrencyLabel ?? Money.Label;
        (groupe?.CurrencyBefore == true ? CurrencyBeforeRadio : CurrencyAfterRadio).IsChecked = true;
        UpdateCurrencyPreview();

        // Gated the same way, and for the same reason: the API refuses these writes to
        // anyone but an admin, so showing the entry to a member only advertises a locked door.
        VentesSettingsPanel.Visibility = _session.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        CustomerDisplaySettingsPanel.Visibility = _session.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        PaymentSettingsPanel.Visibility = _session.IsAdmin && PaymentProviderRegistry.SettingsVisible ? Visibility.Visible : Visibility.Collapsed;

        BilanSettingsPanel.Visibility = _session.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        if (_session.IsAdmin) _ = LoadCalculAutomatiqueAsync();

        PopulatePrivileges();
        PopulateConsumptionYears();
    }

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
        if (!_session.IsAdmin) return;
        ConsumptionPanel.Visibility = Visibility.Visible;

        var items = new List<ComboBoxItem> { new() { Content = "Toutes les années", Tag = null } };
        for (var year = DateTime.Today.Year; year > DateTime.Today.Year - 5; year--)
            items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year });

        ConsumptionYearCombo.ItemsSource = items;
        ConsumptionYearCombo.SelectedIndex = 0;
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

    /// <summary>Opens the reçu/facture editor. Its own Enregistrer does the saving and
    /// refreshes the session's cached settings, so there is nothing to do on return.</summary>
    private void OpenReceiptSettings_Click(object sender, RoutedEventArgs e) =>
        new ReceiptSettingsDialog(_session) { Owner = Window.GetWindow(this) }.ShowDialog();

    private void OpenPaymentProviders_Click(object sender, RoutedEventArgs e) =>
        new PaymentProvidersDialog { Owner = Window.GetWindow(this) }.ShowDialog();

    private void OpenCustomerDisplay_Click(object sender, RoutedEventArgs e) =>
        new CustomerDisplaySettingsDialog { Owner = Window.GetWindow(this) }.ShowDialog();

    /// <summary>Opens the members/roles/privileges screen. Refreshes afterwards so a member
    /// added or removed there is reflected in this screen's own "Membres" count above, and so
    /// the shell picks up a change to the signed-in user's own privileges immediately.</summary>
    private async void OpenMembers_Click(object sender, RoutedEventArgs e)
    {
        new MembresDialog(_session) { Owner = Window.GetWindow(this) }.ShowDialog();

        try { await _session.RefreshAsync(); }
        catch (ApiException) { /* best-effort refresh; the dialog already reported any error */ }
        Populate();
    }

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

    private void PopulatePrivileges()
    {
        // Pointless for the Admin Général: they already know everything is granted
        // automatically, so the section is hidden outright rather than shown for nothing.
        if (_session.IsAdminGeneral)
        {
            PrivilegeSection.Visibility = Visibility.Collapsed;
            return;
        }

        if (_session.Privileges is not { } privileges)
        {
            PrivilegeSummary.Text = "Aucun privilège résolu.";
            return;
        }

        // Match each granted name back to the catalogue, so the list carries readable labels.
        var granted = privileges.Gestion.Where(p => p.Value).Select(p => p.Key)
            .Concat(privileges.Option.Where(p => p.Value).Select(p => p.Key))
            .ToHashSet(StringComparer.Ordinal);

        var rows = PrivilegeCatalog.Gestion
            .Where(p => granted.Contains(p.Name))
            .Select(p => new { p.Module, p.DisplayName, p.Name })
            .Concat(PrivilegeCatalog.Option
                .Where(p => granted.Contains(p.Name))
                .Select(p => new { p.Module, p.DisplayName, p.Name }))
            .GroupBy(p => p.Name)
            .Select(g => g.First())
            .OrderBy(p => p.Module)
            .ThenBy(p => p.DisplayName)
            .ToList();

        // The two virtual privileges have no catalogue row, so they are added by hand.
        var virtualRows = new List<(string Module, string Display, string Name)>();
        if (privileges.Gestion.TryGetValue(Priv.Gestion.ViewAudit, out var audit) && audit)
            virtualRows.Add(("admin", "Accès Audit", Priv.Gestion.ViewAudit));
        if (privileges.Gestion.TryGetValue(Priv.Gestion.ViewParametres, out var param) && param)
            virtualRows.Add(("admin", "Accès Paramètres", Priv.Gestion.ViewParametres));

        PrivilegeGrid.ItemsSource = rows
            .Select(r => new { r.Module, r.DisplayName, r.Name })
            .Concat(virtualRows.Select(v => new { Module = v.Module, DisplayName = v.Display, Name = v.Name }))
            .ToList();

        PrivilegeSummary.Text =
            $"{rows.Count + virtualRows.Count} privilège(s) accordé(s) dans cet espace. " +
            "Pour en obtenir d'autres, communiquez le privilège souhaité à un administrateur.";
    }
}
