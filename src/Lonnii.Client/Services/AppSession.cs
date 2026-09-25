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

    /// <summary>Cached by <see cref="GetReceiptSettingsAsync"/>; see it for why.</summary>
    private ReceiptSettingsDto? _receiptSettings;

    private byte[]? _receiptLogo;
    private byte[]? _receiptQrCode;

    /// <summary>Raised after the group or privileges change, so open views can refresh.</summary>
    public event EventHandler? Changed;

    public bool IsSignedIn => User is not null;
    public bool HasGroup => Groupe is not null;

    /// <summary>Set by <see cref="SignInAsync"/>, read by the sign-in window when
    /// "Rester connecté" is checked so it has something to hand <see cref="SessionStore"/>.</summary>
    public string? AccessToken { get; private set; }

    public DateTime AccessTokenExpiresAt { get; private set; }

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
        AccessToken = response.AccessToken;
        AccessTokenExpiresAt = response.ExpiresAt;
        User = response.User;
        Raise();
    }

    /// <summary>
    /// Re-enters a session "Rester connecté" saved earlier: validates the bearer token
    /// against the server (rather than trusting the locally-recorded expiry alone, since a
    /// password change or a revoked account invalidates it early) and, when a group id is
    /// given, re-opens that group too. Throws <see cref="ApiException"/> on any failure -
    /// callers should fall back to the ordinary sign-in screen rather than treat this as fatal.
    /// </summary>
    public async Task RestoreAsync(string accessToken, DateTime expiresAt, string? groupId, CancellationToken ct = default)
    {
        Api.SetAccessToken(accessToken);
        AccessToken = accessToken;
        AccessTokenExpiresAt = expiresAt;
        User = await Api.MeAsync(ct);

        if (groupId is not null)
        {
            await EnterGroupAsync(groupId, ct);
        }
        else
        {
            Raise();
        }
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
        ClearReceiptSettings();

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

    /// <summary>
    /// The workspace's receipt and invoice configuration, plus the logo and QR code bytes,
    /// fetched once per group session and kept.
    ///
    /// <para>
    /// Cached because of where it is needed: every receipt printed from the till reads it,
    /// and a sale finishing is the one moment the cashier should not be waiting on the
    /// network. It is also small, and changes about as often as the shop's name does.
    /// </para>
    /// <para>
    /// The cost is a stale reading on the other tills until they re-enter the workspace,
    /// after an admin changes the settings on this one. That is accepted:
    /// <see cref="ApplyReceiptSettingsChange"/> keeps the machine that made the change
    /// correct immediately, and the alternative - refetching before each print - trades a
    /// rare, cosmetic staleness for a delay on every single sale.
    /// </para>
    /// </summary>
    public async Task<ReceiptSettingsDto> GetReceiptSettingsAsync(CancellationToken ct = default)
    {
        if (_receiptSettings is { } cached) return cached;

        var settings = await Api.GetReceiptSettingsAsync(ct);
        _receiptLogo = await TryLoadImageAsync(settings.LogoUrl, ct);
        _receiptQrCode = await TryLoadImageAsync(settings.QrCodeUrl, ct);
        _receiptSettings = settings;

        return settings;
    }

    /// <summary>The cached logo bytes, or null when the shop has not set one. Only meaningful
    /// after <see cref="GetReceiptSettingsAsync"/> has run.</summary>
    public byte[]? ReceiptLogo => _receiptLogo;

    public byte[]? ReceiptQrCode => _receiptQrCode;

    /// <summary>
    /// Replaces the cache after the settings editor saves, so the next receipt printed on
    /// this machine shows the new wording and images without a reload. The two images are
    /// passed in rather than refetched: the editor already holds the bytes the user picked.
    /// </summary>
    public void ApplyReceiptSettingsChange(ReceiptSettingsDto settings, byte[]? logo, byte[]? qrCode)
    {
        _receiptSettings = settings;
        _receiptLogo = logo;
        _receiptQrCode = qrCode;
        Raise();
    }

    /// <summary>
    /// A configured image's bytes, or null if it cannot be fetched. A missing logo must not
    /// stop a receipt printing - the sale is already made, and the document is still valid
    /// without it.
    /// </summary>
    private async Task<byte[]?> TryLoadImageAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            return await Api.GetImageBytesAsync(url, ct);
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>Leaves the current group without signing out.</summary>
    public void LeaveGroup()
    {
        Api.SetGroupSession(null);
        SetGroupe(null);
        Privileges = null;
        Menu = null;
        ClearReceiptSettings();
        Raise();
    }

    /// <summary>Clears everything, returning the app to the sign-in screen.</summary>
    public void SignOut()
    {
        Api.SetAccessToken(null);
        Api.SetGroupSession(null);
        AccessToken = null;
        User = null;
        SetGroupe(null);
        Privileges = null;
        Menu = null;
        ClearReceiptSettings();
        SessionStore.Clear();
        Raise();
    }

    /// <summary>Drops the cached receipt configuration. Called on every change of workspace,
    /// since the settings - and the images - belong to the group, not to the user.</summary>
    private void ClearReceiptSettings()
    {
        _receiptSettings = null;
        _receiptLogo = null;
        _receiptQrCode = null;
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
