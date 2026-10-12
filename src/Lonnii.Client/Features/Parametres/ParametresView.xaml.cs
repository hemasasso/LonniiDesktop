using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lonnii.Client.Features.Payments;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Features.Parametres;

/// <summary>
/// The Paramètres screen: the espace itself, its members and photo, and exactly which
/// privileges the signed-in user holds. The shop's running configuration and its servers
/// (connexion, devise, reçus, écran client, bilan, consommation, données) live in
/// <see cref="ConfigurationView"/>, which can be opened to a team section by section.
///
/// The privilege list is worth showing plainly. In Lonnii Business a user who cannot see
/// a module has no way to find out why; here the technical name is on screen, so an
/// administrator can be asked for that specific privilege.
/// </summary>
public partial class ParametresView : UserControl
{
    private readonly AppSession _session;

    /// <summary>Raised after this espace was deleted, so the window can leave it.</summary>
    public event EventHandler? EspaceDeleted;

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


        // Admin-only, as the API is.
        MembersPanel.Visibility = _session.IsAdmin ? Visibility.Visible : Visibility.Collapsed;

        // Deleting is the creator's alone, as on the API.
        DeleteEspacePanel.Visibility = _session.IsAdminGeneral ? Visibility.Visible : Visibility.Collapsed;

        // The API only lets an admin change the photo, so only an admin is offered it.
        PhotoPanel.Visibility = _session.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        PhotoInitial.Text = string.IsNullOrEmpty(groupe?.Nom) ? string.Empty : groupe!.Nom[..1].ToUpperInvariant();
        if (_session.IsAdmin && groupe is not null) _ = LoadPhotoAsync(groupe.Id);

        // Gated the same way: the API refuses these writes to anyone but an admin, so showing
        // the entry to a member only advertises a locked door.
        PaymentSettingsPanel.Visibility = _session.IsAdmin && PaymentProviderRegistry.SettingsVisible ? Visibility.Visible : Visibility.Collapsed;

        PopulatePrivileges();
    }

    private void OpenPaymentProviders_Click(object sender, RoutedEventArgs e) =>
        new PaymentProvidersDialog { Owner = Window.GetWindow(this) }.ShowDialog();

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

    // --- Espace photo ----------------------------------------------------------------

    /// <summary>Shows the espace's current photo, or its initial when it has none.</summary>
    private async Task LoadPhotoAsync(string groupId)
    {
        try
        {
            ShowPhoto(await _session.Api.GetEspacePhotoBytesAsync(groupId));
        }
        catch (ApiException)
        {
            ShowPhoto(null); // none uploaded yet
        }
    }

    private void ShowPhoto(byte[]? bytes)
    {
        PhotoImage.Source = bytes is null ? null : ToBitmap(bytes);
        PhotoInitial.Visibility = bytes is null ? Visibility.Visible : Visibility.Collapsed;
        RemovePhotoButton.Visibility = bytes is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static System.Windows.Media.Imaging.BitmapImage ToBitmap(byte[] bytes)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.StreamSource = new System.IO.MemoryStream(bytes);
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 128; // shown at 64
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void SetPhotoStatus(string text, bool error)
    {
        PhotoStatusText.Text = text;
        PhotoStatusText.Foreground = (Brush)FindResource(error ? "Danger" : "TextSecondary");
    }

    private async void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir une photo pour cet espace",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Tous les fichiers|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(dialog.FileName);
            await _session.Api.UploadEspacePhotoAsync(bytes, System.IO.Path.GetFileName(dialog.FileName));

            // Read it back the way the picker will, so what shows here is what is really saved.
            ShowPhoto(await _session.Api.GetEspacePhotoBytesAsync(_session.Groupe!.Id));
            SetPhotoStatus("Photo enregistrée.", error: false);
        }
        catch (System.IO.IOException ex)
        {
            SetPhotoStatus($"Impossible de lire le fichier : {ex.Message}", error: true);
        }
        catch (ApiException ex)
        {
            SetPhotoStatus(ex.Message, error: true);
        }
    }

    private async void RemovePhoto_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _session.Api.DeleteEspacePhotoAsync();
            ShowPhoto(null);
            SetPhotoStatus("Photo supprimée.", error: false);
        }
        catch (ApiException ex)
        {
            SetPhotoStatus(ex.Message, error: true);
        }
    }

    // --- Delete the espace -------------------------------------------------------------

    private async void DeleteEspace_Click(object sender, RoutedEventArgs e)
    {
        var groupe = _session.Groupe;
        if (groupe is null) return;

        // Two confirmations: typing the name stops a stray click, and the administrator's own password
        // stops anyone using a screen that was left signed in. The server checks the password itself.
        var typed = Lonnii.Client.Common.PromptDialog.Show(
            Window.GetWindow(this)!, "Supprimer l'espace",
            $"Toutes les données de « {groupe.Nom} » seront supprimées, définitivement.\n\n" +
            "Tapez le nom de l'espace pour confirmer :");
        if (typed is null) return;

        if (!string.Equals(typed.Trim(), groupe.Nom, StringComparison.Ordinal))
        {
            DeleteEspaceStatusText.Text = "Le nom ne correspond pas. Rien n'a été supprimé.";
            return;
        }

        var password = Lonnii.Client.Common.PasswordPromptDialog.Show(
            Window.GetWindow(this)!, "Supprimer l'espace",
            $"Dernière étape : entrez le mot de passe de l'administrateur pour supprimer « {groupe.Nom} ».");
        if (password is null)
        {
            DeleteEspaceStatusText.Text = "Suppression annulée. Rien n'a été supprimé.";
            return;
        }

        try
        {
            await _session.Api.DeleteGroupeAsync(groupe.Id, password);
            DeleteEspaceStatusText.Text = string.Empty;
            EspaceDeleted?.Invoke(this, EventArgs.Empty);
        }
        catch (ApiException ex)
        {
            DeleteEspaceStatusText.Text = ex.Message;
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
        if (privileges.Gestion.TryGetValue(Priv.Gestion.ViewConfiguration, out var config) && config)
            virtualRows.Add(("admin", "Accès Configuration", Priv.Gestion.ViewConfiguration));

        PrivilegeGrid.ItemsSource = rows
            .Select(r => new { r.Module, r.DisplayName, r.Name })
            .Concat(virtualRows.Select(v => new { Module = v.Module, DisplayName = v.Display, Name = v.Name }))
            .ToList();

        PrivilegeSummary.Text =
            $"{rows.Count + virtualRows.Count} privilège(s) accordé(s) dans cet espace. " +
            "Pour en obtenir d'autres, communiquez le privilège souhaité à un administrateur.";
    }
}
