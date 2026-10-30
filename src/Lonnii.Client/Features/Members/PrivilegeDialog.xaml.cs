


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
        ["programme"] = "Programme",
        [PrivilegeSections.Caisse] = "Caisse",
        [GestionModules.Configuration] = "Configuration & Serveurs",
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
        // Of the legacy "admin" set, only Gérer Paramètres does something here: every section
        // of Configuration & Serveurs at once. Resectioned there by PrivilegeSections.
        GestionModules.Admin => p.Name == Priv.Gestion.ManageGestionSettings,
        GestionModules.Analytics => false,
        // Prestations was removed from the desktop; its rows remain in the shared catalogue.
        GestionModules.Prestations => false,
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

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Foreground = (Brush)Application.Current.Resources["TextSecondary"];
        try
        {
            var data = await _session.Api.GetMemberPrivilegesAsync(_member.IdUser);

            if (data.IsAdminGeneral)
            {
                StatusText.Text = "Ce membre a créé l'espace et possède tous les privilèges.";
                Tabs.IsEnabled = false;
                return;
            }

            // IsUsedInDesktop drops whole modules the desktop has no feature for - Lonnii
            // Business's chat, formulaires and Prestations, and the legacy "finance"/"analytics"
            // sets superseded by each module's own privileges: ticking them would do nothing.
            var gestion = data.Gestion.Where(IsUsedInDesktop).ToList();
            var option = data.Option.Where(IsUsedInDesktop).ToList();

            var waiting = new HashSet<string>(StringComparer.Ordinal);
            if (_session.IsRemote)
            {
                var requested = await RequestedSinceCopyAsync();
                gestion = WithRequested(gestion, requested, "gestion", waiting);
                option = WithRequested(option, requested, "option", waiting);
            }

            var tab = Math.Max(0, Tabs.SelectedIndex);
            Tabs.Items.Clear();
            Tabs.Items.Add(BuildTab("Gestion", gestion, isGestion: true, waiting));
            Tabs.Items.Add(BuildTab("Espace", option, isGestion: false, waiting));
            Tabs.SelectedIndex = tab;

            var granted = gestion.Count(p => p.IsGranted) + option.Count(p => p.IsGranted);
            StatusText.Text = $"{granted} privilège(s) accordé(s)."
                + (waiting.Count > 0 ? $" {waiting.Count} en attente de la boutique (en gris)." : string.Empty);
        }
        catch (ApiException ex)
        {
            StatusText.Text = ex.Message;
            StatusText.Foreground = (Brush)Application.Current.Resources["Danger"];
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    // --- From afar ---------------------------------------------------------------------

    /// <summary>How long a change made from afar is followed before saying it is on its way. The
    /// shop's server collects requests every 10 seconds, so a shorter wait would rarely see one land.</summary>
    private static readonly TimeSpan ShopWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// From afar the privileges are read from the shop's last backup, which can be minutes behind.
    /// Requests for this member made after it are laid over it, oldest first, so the screen shows
    /// what was asked for: applied ones as they are now, pending ones as such.
    /// </summary>
    private async Task<IReadOnlyList<RemoteCommandDto>> RequestedSinceCopyAsync()
    {
        try
        {
            var copyAt = (await _session.Api.GetRemoteInfoAsync()).SnapshotAt;
            return (await _session.Api.GetRemoteCommandsAsync())
                .Where(c => c.Type == RemoteCommandTypes.Privilege && c.UserId == _member.IdUser
                            && c.Status is RemoteCommandStatuses.Pending or RemoteCommandStatuses.Applied
                            && (copyAt is null || c.RequestedAt > copyAt))
                .OrderBy(c => c.RequestedAt)
                .ToList();
        }
        catch (ApiException)
        {
            // Without the list, the copy alone is still a true picture of the last backup.
            return [];
        }
    }

    private static List<PrivilegeDto> WithRequested(List<PrivilegeDto> privileges,
        IReadOnlyList<RemoteCommandDto> requested, string catalog, HashSet<string> waiting)
    {
        var result = privileges.ToList();
        foreach (var command in requested.Where(c => c.Catalog == catalog && c.PrivilegeName is not null))
        {
            var name = PrivilegeAliases.Canonical(command.PrivilegeName!);
            var index = result.FindIndex(p => p.Name == name);
            if (index < 0) continue;

            result[index] = result[index] with { IsGranted = command.Granted == true };
            if (command.Status == RemoteCommandStatuses.Pending) waiting.Add(catalog + ":" + name);
            else waiting.Remove(catalog + ":" + name);
        }
        return result;
    }

    private static void ShowWaiting(CheckBox check, bool waiting)
    {
        check.Opacity = waiting ? 0.5 : 1;
        check.ToolTip = waiting
            ? "Demande envoyée à la boutique, pas encore appliquée. « Actualiser » pour vérifier."
            : (check.Tag as PrivilegeDto)?.Description;
    }

    /// <summary>Follows the requests just sent until the shop has applied them all, one has failed,
    /// or <see cref="ShopWait"/> has passed.</summary>
    /// <returns>Applied or Failed, with the shop's message; Pending when still on its way.</returns>
    private async Task<(string Status, string? Message)> WaitForShopAsync(IReadOnlyCollection<string> ids)
    {
        var until = DateTime.UtcNow + ShopWait;
        while (DateTime.UtcNow < until && IsLoaded)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try
            {
                var mine = (await _session.Api.GetRemoteCommandsAsync()).Where(c => ids.Contains(c.Id)).ToList();

                var refused = mine.FirstOrDefault(c => c.Status is RemoteCommandStatuses.Failed or RemoteCommandStatuses.Expired);
                if (refused is not null) return (RemoteCommandStatuses.Failed, refused.Message);

                if (mine.Count == ids.Count && mine.All(c => c.Status == RemoteCommandStatuses.Applied))
                    return (RemoteCommandStatuses.Applied, null);
            }
            catch (ApiException)
            {
                break;
            }
        }
        return (RemoteCommandStatuses.Pending, null);
    }

    /// <summary>Builds one tab, with a section per module. Where a privilege has an alias
    /// (<see cref="PrivilegeAliases"/>) only its canonical name gets a row - the server already
    /// resolves a grant of either name the same way, and <see cref="SaveAsync"/> writes both
    /// when it is toggled, so showing two boxes that must always agree would only be confusing.</summary>
    private TabItem BuildTab(string header, IReadOnlyList<PrivilegeDto> privileges, bool isGestion,
        IReadOnlySet<string> waiting)
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
            {
                var row = BuildRow(privilege, isGestion);
                if (waiting.Contains((isGestion ? "gestion:" : "option:") + privilege.Name)) ShowWaiting(row, true);
                panel.Children.Add(row);
            }
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
        var done = granted ? $"« {privilege.DisplayName} » accordé." : $"« {privilege.DisplayName} » retiré.";
        try
        {
            // Every alias, not just this one name: leaving a legacy alias granted underneath
            // an unchecked canonical box would mean unchecking it here does not actually take
            // the capability away, since the resolver still sees the other name granted.
            // From afar each one is kept for the shop rather than made, so all are sent first.
            var sent = new List<string>();
            var queued = false;
            foreach (var name in PrivilegeAliases.GroupOf(privilege.Name))
            {
                var request = new SetPrivilegeRequest(_member.IdUser, name, granted);
                try
                {
                    if (isGestion) await _session.Api.SetGestionPrivilegeAsync(request);
                    else await _session.Api.SetOptionPrivilegeAsync(request);
                }
                catch (RemoteQueuedException kept)
                {
                    queued = true;
                    if (kept.CommandId is not null) sent.Add(kept.CommandId);
                }
            }

            StatusText.Foreground = (Brush)Application.Current.Resources["TextSecondary"];
            if (!queued)
            {
                StatusText.Text = done;
                return;
            }

            // Grey while the shop has not answered: the box shows what was asked, not yet what is.
            ShowWaiting(check, true);
            StatusText.Text = $"{done} Envoi à la boutique…";

            var (status, message) = sent.Count > 0
                ? await WaitForShopAsync(sent)
                : (RemoteCommandStatuses.Pending, null);

            if (status == RemoteCommandStatuses.Applied)
            {
                ShowWaiting(check, false);
                StatusText.Text = $"{done} Appliqué par la boutique.";
            }
            else if (status == RemoteCommandStatuses.Failed)
            {
                ShowWaiting(check, false);
                _suppressSave = true;
                check.IsChecked = !granted;
                _suppressSave = false;
                StatusText.Foreground = (Brush)Application.Current.Resources["Danger"];
                StatusText.Text = message ?? "La boutique n'a pas pu appliquer ce changement.";
            }
            else
            {
                StatusText.Text = $"{done} Demande envoyée à la boutique, appliquée dès qu'elle la reçoit.";
            }
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
