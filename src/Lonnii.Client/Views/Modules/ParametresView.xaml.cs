using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Views.Modules;

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

        CurrencyPanel.Visibility = _session.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        CurrencyBox.Text = groupe?.CurrencyLabel ?? Money.Label;

        PopulatePrivileges();
    }

    /// <summary>Saves the new currency label and applies it immediately across the app.</summary>
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
            var updated = await _session.Api.UpdateCurrencyAsync(label);
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
