


using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Members;

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
    /// by <see cref="PrivilegeSections"/> - so no row is ever left under "sales". Likewise no
    /// "finance", "analytics", "admin", "chat" or "formulaire" entry: <see cref="LoadAsync"/>
    /// filters those out before a tab is ever built - see its comment for why.</summary>
    private static readonly Dictionary<string, string> ModuleLabels = new(StringComparer.Ordinal)
    {
        ["stock"] = "Stock",
        ["ventes"] = "Ventes",
        ["charges"] = "Charges",
        ["marges"] = "Marges",
        ["amortissement"] = "Amortissement",
        ["bilan"] = "Bilan",
        ["prestations"] = "Prestations",
        ["programme"] = "Programme",
        [PrivilegeSections.Caisse] = "Caisse",
    };

    /// <summary>Section order for the Gestion tab: the day-to-day till tasks first.
    /// Anything unlisted follows, alphabetically.</summary>
    private static readonly string[] SectionOrder =
        [PrivilegeSections.Caisse, PrivilegeSections.Ventes, PrivilegeSections.Stock];

    private static string SectionOf(PrivilegeDto privilege) => PrivilegeSections.Of(privilege);

    private static int SectionRank(string section) =>
        Array.IndexOf(SectionOrder, section) is var i and >= 0 ? i : SectionOrder.Length;

    /// <summary>Whole modules the desktop app has no feature for at all: Lonnii Business's
    /// chat and formulaires were never ported, and "finance"/"analytics"/"admin" are a legacy
    /// privilege set superseded by each module's own (can_view_expenses etc. by Charges and
    /// Bilan, can_view_basic_analytics etc. by can_view_stock_analytics/ventes_analytics/
    /// charges_analytics/marges). can_manage_suppliers is the one "finance" privilege that
    /// still does something (Stock's Fournisseurs manager) - kept, and resectioned into Stock
    /// by <see cref="PrivilegeSections"/>.</summary>
    private static bool IsUsedInDesktop(PrivilegeDto p) => p.Module switch
    {
        GestionModules.Finance => p.Name == Priv.Gestion.ManageSuppliers,
        GestionModules.Analytics or GestionModules.Admin => false,
        OptionModules.Chat or OptionModules.Formulaire => false,
        _ => true,
    };

    public PrivilegeDialog(AppSession session, GroupMemberDto member)
    {
        _session = session;
        _member = member;
        InitializeComponent();

        MemberName.Text = member.Email;
        MemberHint.Text = $"Rôle : {GroupRoles.DisplayName(member.Role, member.IsAdminGeneral)}";

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

            // Two kinds of privilege never get a row here. Prestations has ~19 of its own -
            // clutter for the common case, since the module itself is hidden everywhere else
            // in the app until an admin turns the group's Prestations toggle on (see AppMenu's
            // RequiresPrestationsEnabled); left out entirely rather than just unchecked, so the
            // list an admin who does not use Prestations sees matches what their shop actually
            // has. Separately, IsUsedInDesktop drops whole modules the desktop never wired to
            // anything at all - not a toggle away like Prestations, just genuinely absent
            // (Lonnii Business's chat/formulaires, or a legacy "finance"/"analytics"/"admin"
            // set superseded by each module's own privileges) - ticking them would do nothing.
            var prestationsOn = _session.Groupe?.PrestationsEnabled == true;
            var gestionUsed = data.Gestion.Where(IsUsedInDesktop).ToList();
            var gestion = prestationsOn
                ? gestionUsed : gestionUsed.Where(p => p.Module != GestionModules.Prestations).ToList();
            var option = data.Option.Where(IsUsedInDesktop).ToList();

            Tabs.Items.Clear();
            Tabs.Items.Add(BuildTab("Gestion", gestion, isGestion: true));
            Tabs.Items.Add(BuildTab("Espace", option, isGestion: false));
            Tabs.SelectedIndex = 0;

            var granted = gestion.Count(p => p.IsGranted) + option.Count(p => p.IsGranted);
            var hiddenPrestations = prestationsOn ? 0 : gestionUsed.Count(p => p.Module == GestionModules.Prestations);
            StatusText.Text = $"{granted} privilège(s) accordé(s)."
                + (hiddenPrestations > 0
                    ? $" ({hiddenPrestations} privilège(s) Prestations masqué(s) - module non activé pour cet espace.)"
                    : string.Empty);
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

        if (isGestion && privilege.Name == Priv.Gestion.ProcessReturns
            || !isGestion && privilege.Name == Priv.Option.ManageEventCategories)
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
