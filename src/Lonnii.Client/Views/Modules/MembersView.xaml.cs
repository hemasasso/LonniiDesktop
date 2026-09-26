using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Client.Views.Dialogs;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Views.Modules;

/// <summary>
/// The Options screen: who is in the group, what role they hold, and which privileges
/// they have been granted. Mirrors the web app's Option and privilege-management screens.
/// </summary>
public partial class MembersView : UserControl
{
    private readonly AppSession _session;
    private List<GroupMemberDto> _members = [];

    public MembersView(AppSession session)
    {
        _session = session;
        InitializeComponent();

        // Membership and privilege changes are administrative acts in Lonnii Business too.
        var isAdmin = _session.IsAdmin;
        AddButton.IsEnabled = isAdmin;
        PrivilegesButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        RoleButton.IsEnabled = false;

        if (!isAdmin)
        {
            AddButton.ToolTip = "Réservé aux administrateurs";
            PrivilegesButton.ToolTip = "Réservé aux administrateurs";
        }

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _members = await _session.Api.GetMembersAsync();
            MemberGrid.ItemsSource = _members;
            SummaryText.Text = $"{_members.Count} membre(s) dans « {_session.Groupe?.Nom} »";
            HideMessage();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            Grid_SelectionChanged(this, null!);
        }
    }

    private GroupMemberDto? Selected => MemberGrid.SelectedItem as GroupMemberDto;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var member = Selected;
        var canManage = _session.IsAdmin && member is not null && !member.IsAdminGeneral;

        PrivilegesButton.IsEnabled = canManage;
        RemoveButton.IsEnabled = canManage;

        // Only the group creator hands out roles, as in the web app.
        RoleButton.IsEnabled = canManage && _session.IsAdminGeneral;

        // Resetting an administrator's password is the creator's call alone, so that a
        // sub_admin cannot take over the workspace by resetting the owner.
        PasswordButton.IsEnabled = _session.IsAdmin
                                   && member is not null
                                   && (member.IdUser == _session.User?.IdUser
                                       || (!member.IsAdminGeneral
                                           && (!GroupRoles.IsAdminRole(member.Role) || _session.IsAdminGeneral)));

        PasswordButton.ToolTip = member switch
        {
            { IsAdminGeneral: true } when member.IdUser != _session.User?.IdUser =>
                "Seul le créateur de l'espace peut modifier son propre mot de passe",
            not null when GroupRoles.IsAdminRole(member.Role) && !_session.IsAdminGeneral
                          && member.IdUser != _session.User?.IdUser =>
                "Seul le créateur de l'espace peut réinitialiser le mot de passe d'un administrateur",
            _ => null,
        };

        if (member?.IsAdminGeneral == true)
        {
            PrivilegesButton.ToolTip = "Le créateur de l'espace possède déjà tous les privilèges";
            RemoveButton.ToolTip = "Le créateur de l'espace ne peut pas être retiré";
        }
        else
        {
            PrivilegesButton.ToolTip = null;
            RemoveButton.ToolTip = null;
        }
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PrivilegesButton.IsEnabled) Privileges_Click(sender, e);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this)!;

        var dialog = new AddMemberDialog { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var user = await _session.Api.AddMemberAsync(dialog.Result!);
            await LoadAsync();

            if (dialog.CreatedPassword is { } password)
            {
                // Shown once, here, because the password is not recoverable afterwards.
                MessageBox.Show(owner,
                    $"Compte créé pour {user.Email}.\n\n" +
                    $"Identifiant : {user.Username ?? user.Email}\n" +
                    $"Mot de passe : {password}\n\n" +
                    "Communiquez-les à la personne concernée. Ce mot de passe ne sera plus affiché.",
                    "Compte créé", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private async void Privileges_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } member) return;

        var dialog = new PrivilegeDialog(_session, member) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();

        // The edited member may be the signed-in user; refresh so the shell agrees with the API.
        if (member.IdUser == _session.User?.IdUser) await _session.RefreshAsync();
        await LoadAsync();
    }

    private async void Role_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } member) return;

        var dialog = new RoleDialog(member) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (dialog.SelectedRole != member.Role)
                await _session.Api.SetRoleAsync(new SetRoleRequest(member.IdUser, dialog.SelectedRole!));

            var profile = dialog.SelectedProfile;
            var changed = await PrivilegeProfiles.ApplyAsync(_session, member.IdUser, profile);

            if (member.IdUser == _session.User?.IdUser) await _session.RefreshAsync();
            await LoadAsync();

            if (!ReferenceEquals(profile, PrivilegeProfiles.None))
            {
                MessageBox.Show(Window.GetWindow(this),
                    $"Profil « {profile.Name} » appliqué à {member.Email} : {changed} privilège(s) modifié(s).",
                    "Profil appliqué", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    /// <summary>
    /// Resets a member's password, or opens the self-service dialog when the admin has
    /// selected their own row - changing your own password should ask for the old one.
    /// </summary>
    private async void Password_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } member) return;

        var owner = Window.GetWindow(this)!;

        if (member.IdUser == _session.User?.IdUser)
        {
            var own = new ChangePasswordDialog(_session) { Owner = owner };
            if (own.ShowDialog() == true)
            {
                MessageBox.Show(owner, "Votre mot de passe a été modifié.",
                    "Mot de passe", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        var dialog = new ResetPasswordDialog(member) { Owner = owner };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await _session.Api.ResetMemberPasswordAsync(member.IdUser, dialog.NewPassword!);

            // Shown once: the password is not recoverable afterwards.
            MessageBox.Show(owner,
                $"Mot de passe réinitialisé pour {member.Email}.\n\n" +
                $"Identifiant : {member.Username ?? member.Email}\n" +
                $"Nouveau mot de passe : {dialog.NewPassword}\n\n" +
                "Communiquez-le à la personne concernée. Ses sessions ouvertes ont été fermées.",
                "Mot de passe réinitialisé", MessageBoxButton.OK, MessageBoxImage.Information);

            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } member) return;

        var confirm = MessageBox.Show(Window.GetWindow(this)!,
            $"Retirer « {member.Email} » de l'espace ?\n\n" +
            "Ses privilèges et ses sessions ouvertes seront supprimés immédiatement.",
            "Retirer le membre", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.RemoveMemberAsync(member.IdUser);
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowMessage(ex.Message);
        }
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void HideMessage() => MessagePanel.Visibility = Visibility.Collapsed;
}

/// <summary>Picks a group role for a member, and optionally a job profile (Caissier, Vendeur,
/// Stock...) that sets their Caisse/Ventes/Stock privileges - see <see cref="PrivilegeProfiles"/>.</summary>
public class RoleDialog : Window
{
    private readonly ComboBox _combo;
    private readonly ComboBox _profileCombo;

    /// <summary>The role chosen, once the dialog has been accepted.</summary>
    public string? SelectedRole => (_combo.SelectedItem as RoleOption)?.Value;

    public PrivilegeProfile SelectedProfile => _profileCombo.SelectedItem as PrivilegeProfile ?? PrivilegeProfiles.None;

    private sealed record RoleOption(string Value, string Label);

    public RoleDialog(GroupMemberDto member)
    {
        Title = "Changer le rôle";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.Resources["Surface"];

        var options = new[]
        {
            new RoleOption(GroupRoles.Member, $"{GroupRoles.DisplayName(GroupRoles.Member)} — accès de base"),
            new RoleOption(GroupRoles.Moderator, $"{GroupRoles.DisplayName(GroupRoles.Moderator)} — modère le chat et les événements"),
            new RoleOption(GroupRoles.SubAdmin, $"{GroupRoles.DisplayName(GroupRoles.SubAdmin)} — gère les membres"),
            new RoleOption(GroupRoles.Admin, $"{GroupRoles.DisplayName(GroupRoles.Admin)} — tous les droits"),
        };

        _combo = new ComboBox
        {
            ItemsSource = options,
            DisplayMemberPath = nameof(RoleOption.Label),
            SelectedItem = options.FirstOrDefault(o => o.Value == member.Role) ?? options[0],
            Margin = new Thickness(0, 0, 0, 14),
        };

        _profileCombo = new ComboBox
        {
            ItemsSource = PrivilegeProfiles.All,
            SelectedItem = PrivilegeProfiles.None,
            Margin = new Thickness(0, 0, 0, 6),
        };

        var ok = new Button
        {
            Content = "Valider",
            IsDefault = true,
            Style = (Style)Application.Current.Resources["PrimaryButton"],
        };
        ok.Click += (_, _) => DialogResult = true;

        var cancel = new Button
        {
            Content = "Annuler",
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.Resources["SecondaryButton"],
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Rôle de {member.Email}",
            Style = (Style)Application.Current.Resources["FieldLabel"],
        });
        panel.Children.Add(_combo);
        panel.Children.Add(new TextBlock
        {
            Text = "Profil de tâches (optionnel)",
            Style = (Style)Application.Current.Resources["FieldLabel"],
        });
        panel.Children.Add(_profileCombo);
        panel.Children.Add(new TextBlock
        {
            Text = "Coche les privilèges Caisse, Ventes et Stock correspondants et retire les autres de ces trois sections. " +
                   "Les autres privilèges ne sont pas modifiés. Ajustez ensuite au besoin dans « Privilèges ».",
            Style = (Style)Application.Current.Resources["PageSubtitle"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });
        panel.Children.Add(buttons);

        Content = panel;
    }
}
