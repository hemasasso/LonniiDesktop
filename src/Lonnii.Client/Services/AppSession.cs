using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Services;

/// <summary>
/// Who is signed in, which group they are working in, and what they may do.
/// One instance for the lifetime of the app; the views read it to decide what to show.
/// </summary>
public class AppSession(LonniiApiClient api)
{
    public LonniiApiClient Api { get; } = api;

    public UserDto? User { get; private set; }
    public GroupeDto? Groupe { get; private set; }
    public MyPrivilegesResponse? Privileges { get; private set; }
    public MenuResponse? Menu { get; private set; }

    /// <summary>Raised after the group or privileges change, so open views can refresh.</summary>
    public event EventHandler? Changed;

    public bool IsSignedIn => User is not null;
    public bool HasGroup => Groupe is not null;

    /// <summary>True when the signed-in user created the current group.</summary>
    public bool IsAdminGeneral => Privileges?.IsAdminGeneral ?? false;

    /// <summary>True when the signed-in user holds an admin or sub_admin role here.</summary>
    public bool IsAdmin => Privileges?.IsAdmin ?? false;

    /// <summary>A display name for the signed-in user, falling back through the fields we have.</summary>
    public string DisplayName
    {
        get
        {
            if (User is null) return string.Empty;
            var full = $"{User.FirstName} {User.LastName}".Trim();
            if (!string.IsNullOrWhiteSpace(full)) return full;
            return User.Username ?? User.Email;
        }
    }

    /// <summary>Signs in and stores the bearer token for later calls.</summary>
    public async Task SignInAsync(string identifier, string password, CancellationToken ct = default)
    {
        var response = await Api.LoginAsync(identifier, password, ct);
        Api.SetAccessToken(response.AccessToken);
        User = response.User;
        Raise();
    }

    /// <summary>
    /// Enters a group: opens a group session, then loads privileges and the menu together
    /// so the shell never renders a menu that disagrees with what the API will allow.
    /// </summary>
    public async Task EnterGroupAsync(string groupId, CancellationToken ct = default)
    {
        var session = await Api.OpenGroupSessionAsync(groupId, ct);
        Api.SetGroupSession(session.SessionToken);
        SetGroupe(session.Groupe);

        Privileges = await Api.GetMyPrivilegesAsync(ct);
        Menu = await Api.GetMenuAsync(ct);
        Raise();
    }

    /// <summary>
    /// Changes the signed-in user's password and keeps them working.
    ///
    /// The server invalidates every existing token and group session, so the new token is
    /// stored and the current group is re-entered. Without that the user would be thrown
    /// back to the sign-in window immediately after succeeding.
    /// </summary>
    public async Task ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var response = await Api.ChangeOwnPasswordAsync(currentPassword, newPassword, ct);
        User = response.User;

        if (Groupe is { } groupe)
        {
            await EnterGroupAsync(groupe.Id, ct);
        }
        else
        {
            Raise();
        }
    }

    /// <summary>Re-reads privileges and the menu, after a grant changes or a group setting is edited.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!HasGroup) return;
        Privileges = await Api.GetMyPrivilegesAsync(ct);
        Menu = await Api.GetMenuAsync(ct);
        Raise();
    }

    /// <summary>Leaves the current group without signing out.</summary>
    public void LeaveGroup()
    {
        Api.SetGroupSession(null);
        SetGroupe(null);
        Privileges = null;
        Menu = null;
        Raise();
    }

    /// <summary>Clears everything, returning the app to the sign-in screen.</summary>
    public void SignOut()
    {
        Api.SetAccessToken(null);
        Api.SetGroupSession(null);
        User = null;
        SetGroupe(null);
        Privileges = null;
        Menu = null;
        Raise();
    }

    /// <summary>
    /// Updates the workspace's currency label after an admin changes it, so every open view
    /// reflects the new label the moment it takes effect - not just on the next sign-in.
    /// </summary>
    public void ApplyCurrencyChange(GroupeDto updated)
    {
        SetGroupe(updated);
        Raise();
    }

    /// <summary>Keeps <see cref="Money.Label"/> in step with whichever group is active, since
    /// the label is a workspace setting, not a per-client one.</summary>
    private void SetGroupe(GroupeDto? groupe)
    {
        Groupe = groupe;
        Money.Label = groupe?.CurrencyLabel ?? "FCFA";
    }

    /// <summary>True when the named privilege is granted in the current group.</summary>
    public bool Can(string privilege)
    {
        if (Privileges is null) return false;
        if (Privileges.Gestion.TryGetValue(privilege, out var gestion) && gestion) return true;
        return Privileges.Option.TryGetValue(privilege, out var option) && option;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
