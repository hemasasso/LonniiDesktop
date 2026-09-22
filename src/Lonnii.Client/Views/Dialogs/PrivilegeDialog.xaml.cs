


using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views.Dialogs;

/// <summary>
/// Grants and revokes one member's privileges, grouped by module in the same way the web
/// app's privilege screen groups them.
///
/// Each checkbox saves on the spot rather than on a Save button. That matches the web
/// app's behaviour and means a half-finished session cannot leave the two disagreeing.
/// Admin-only privileges are shown but locked: the resolver forces them to false for a
/// non-creator, so offering them would promise something that never takes effect.
/// </summary>
public partial class PrivilegeDialog : Window
{
    private readonly AppSession _session;
    private readonly GroupMemberDto _member;

    /// <summary>
    /// Set while a checkbox is being reverted after a failed save. Without it, restoring
    /// the box would raise Checked/Unchecked again and start a second save.
    /// </summary>
    private bool _suppressSave;

    /// <summary>French labels for the module keys, so the tabs read like the web app's sections.</summary>
    private static readonly Dictionary<string, string> ModuleLabels = new(StringComparer.Ordinal)
    {
        ["stock"] = "Stock",
        ["sales"] = "Ventes (caisse)",
        ["ventes"] = "Ventes",
        ["charges"] = "Charges",
        ["marges"] = "Marges",
        ["amortissement"] = "Amortissement",
        ["bilan"] = "Bilan",
        ["prestations"] = "Prestations",
        ["finance"] = "Finance",
        ["analytics"] = "Analyses",
        ["admin"] = "Administration",
        ["programme"] = "Programme",
        ["formulaire"] = "Formulaire",
        ["chat"] = "Chat",
    };

    public PrivilegeDialog(AppSession session, GroupMemberDto member)
    {
        _session = session;
        _member = member;
        InitializeComponent();

        MemberName.Text = member.Email;
        MemberHint.Text = $"Rôle : {member.Role}";

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var data = await _session.Api.GetMemberPrivilegesAsync(_member.IdUser);

            if (data.IsAdminGeneral)
            {
                StatusText.Text = "Ce membre a créé l'espace et possède tous les privilèges.";
                Tabs.IsEnabled = false;
                return;
            }

            Tabs.Items.Clear();
            Tabs.Items.Add(BuildTab("Gestion", data.Gestion, isGestion: true));
            Tabs.Items.Add(BuildTab("Espace", data.Option, isGestion: false));
            Tabs.SelectedIndex = 0;

            var granted = data.Gestion.Count(p => p.IsGranted) + data.Option.Count(p => p.IsGranted);
            StatusText.Text = $"{granted} privilège(s) accordé(s).";
        }
        catch (ApiException ex)
        {
            StatusText.Text = ex.Message;
            StatusText.Foreground = (Brush)Application.Current.Resources["Danger"];
        }
    }

    /// <summary>Builds one tab, with a section per module.</summary>
    private TabItem BuildTab(string header, IReadOnlyList<PrivilegeDto> privileges, bool isGestion)
    {
        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 14) };

        foreach (var module in privileges.GroupBy(p => p.Module).OrderBy(g => g.Key))
        {
            panel.Children.Add(new TextBlock
            {
                Text = ModuleLabels.TryGetValue(module.Key, out var label) ? label : module.Key,
                Style = (Style)Application.Current.Resources["SectionHeader"],
                Margin = new Thickness(0, 12, 0, 6),
            });

            foreach (var privilege in module.OrderBy(p => p.DisplayName))
                panel.Children.Add(BuildRow(privilege, isGestion));
        }

        return new TabItem
        {
            Header = header,
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };
    }

    private CheckBox BuildRow(PrivilegeDto privilege, bool isGestion)
    {
        var check = new CheckBox
        {
            Content = privilege.DisplayName,
            ToolTip = privilege.Description,
            IsChecked = privilege.IsGranted,
            Margin = new Thickness(0, 3, 0, 3),
            Tag = privilege,
        };

        if (privilege.IsAdminOnly)
        {
            check.IsEnabled = false;
            check.Content = $"{privilege.DisplayName}  (réservé aux administrateurs)";
            check.ToolTip = "Ce privilège est réservé aux administrateurs et ne peut pas être accordé individuellement.";
            return check;
        }

        check.Checked += async (_, _) => await SaveAsync(check, privilege, true, isGestion);
        check.Unchecked += async (_, _) => await SaveAsync(check, privilege, false, isGestion);
        return check;
    }

    private async Task SaveAsync(CheckBox check, PrivilegeDto privilege, bool granted, bool isGestion)
    {
        if (_suppressSave) return;

        check.IsEnabled = false;
        try
        {
            var request = new SetPrivilegeRequest(_member.IdUser, privilege.Name, granted);

            if (isGestion) await _session.Api.SetGestionPrivilegeAsync(request);
            else await _session.Api.SetOptionPrivilegeAsync(request);

            StatusText.Foreground = (Brush)Application.Current.Resources["TextSecondary"];
            StatusText.Text = granted
                ? $"« {privilege.DisplayName} » accordé."
                : $"« {privilege.DisplayName} » retiré.";
        }
        catch (ApiException ex)
        {
            // Put the box back where it was, so the screen never claims a change that failed.
            _suppressSave = true;
            check.IsChecked = !granted;
            _suppressSave = false;

            StatusText.Foreground = (Brush)Application.Current.Resources["Danger"];
            StatusText.Text = ex.Message;
        }
        finally
        {
            check.IsEnabled = true;
        }
    }
}
