


using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

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

    /// <summary>French labels for the module keys, so the tabs read like the web app's sections.
    /// No "sales" entry: every privilege stored under that legacy module either has an alias
    /// in <see cref="PrivilegeAliases"/> (so <see cref="BuildTab"/> deduplicates it into its
    /// Caisse/Ventes/Stock row below) or is <c>can_process_returns</c>, resectioned into Ventes
    /// by <see cref="PrivilegeSections"/> - so no row is ever left under "sales".</summary>
    private static readonly Dictionary<string, string> ModuleLabels = new(StringComparer.Ordinal)
    {
        ["stock"] = "Stock",
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
        [PrivilegeSections.Caisse] = "Caisse",
    };

    /// <summary>Section order for the Gestion tab: the day-to-day till tasks first.
    /// Anything unlisted follows, alphabetically.</summary>
    private static readonly string[] SectionOrder =
        [PrivilegeSections.Caisse, PrivilegeSections.Ventes, PrivilegeSections.Stock];

    private static string SectionOf(PrivilegeDto privilege) => PrivilegeSections.Of(privilege);

    private static int SectionRank(string section) =>
        Array.IndexOf(SectionOrder, section) is var i and >= 0 ? i : SectionOrder.Length;

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

    /// <summary>Builds one tab, with a section per module. Where a privilege has an alias
    /// (<see cref="PrivilegeAliases"/>) only its canonical name gets a row - the server already
    /// resolves a grant of either name the same way, and <see cref="SaveAsync"/> writes both
    /// when it is toggled, so showing two boxes that must always agree would only be confusing.</summary>
    private TabItem BuildTab(string header, IReadOnlyList<PrivilegeDto> privileges, bool isGestion)
    {
        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 14) };
        var canonicalOnly = privileges.Where(p => PrivilegeAliases.Canonical(p.Name) == p.Name).ToList();

        foreach (var module in canonicalOnly.GroupBy(SectionOf)
                     .OrderBy(g => SectionRank(g.Key)).ThenBy(g => g.Key))
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

        if (isGestion && privilege.Name == Priv.Gestion.ProcessReturns)
            check.ToolTip = $"{privilege.Description}\n\nAucun effet sur l'application de bureau actuellement.";

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
            // Every alias, not just this one name: leaving a legacy alias granted underneath
            // an unchecked canonical box would mean unchecking it here does not actually take
            // the capability away, since the resolver still sees the other name granted.
            foreach (var name in PrivilegeAliases.GroupOf(privilege.Name))
            {
                var request = new SetPrivilegeRequest(_member.IdUser, name, granted);
                if (isGestion) await _session.Api.SetGestionPrivilegeAsync(request);
                else await _session.Api.SetOptionPrivilegeAsync(request);
            }

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
