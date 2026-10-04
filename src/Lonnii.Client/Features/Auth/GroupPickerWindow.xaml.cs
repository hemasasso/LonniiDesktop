using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;
using Microsoft.Win32;

namespace Lonnii.Client.Features.Auth;

/// <summary>
/// Picks the group to work in. Shown after sign-in because every privilege the shell
/// reads is scoped to one group.
/// </summary>
public partial class GroupPickerWindow : Window
{
    private readonly AppSession _session = App.Session;

    public GroupPickerWindow()
    {
        InitializeComponent();
        GroupList.SelectionChanged += GroupList_SelectionChanged;
        Loaded += async (_, _) => await LoadAsync();
    }

    // -----------------------------------------------------------------------
    // Loading

    private async Task LoadAsync()
    {
        try
        {
            var groupes = await _session.Api.GetGroupesAsync();
            var hasAny = groupes.Count > 0;

            GroupList.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
            EmptyPanel.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
            OpenButton.IsEnabled = hasAny;

            if (!hasAny)
            {
                GroupList.ItemsSource = null;
                UpdateAdminButtons(null);
                return;
            }

            // Wrap each DTO in a lightweight VM so photos can be pushed in async.
            var items = groupes.Select(g => new GroupeItem(g)).ToList();
            GroupList.ItemsSource = items;

            // Reopen whatever was last used, so a till lands where it left off.
            var remembered = items.FirstOrDefault(i => i.Groupe.Id == App.Settings.LastGroupId);
            GroupList.SelectedItem = remembered ?? items[0];
            GroupList.Focus();

            // Load photos in the background - the cards appear immediately with initials
            // and update once each photo arrives.
            _ = LoadPhotosAsync(items);
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>Fetches each espace's cover photo and pushes it into the card VM so WPF
    /// updates the card without a full reload.</summary>
    private async Task LoadPhotosAsync(IEnumerable<GroupeItem> items)
    {
        foreach (var item in items)
        {
            if (item.Groupe.PhotoUrl is null) continue;

            try
            {
                var bytes = await _session.Api.GetImageBytesAsync(item.Groupe.PhotoUrl);
                item.Photo = ToBitmapImage(bytes);
            }
            catch
            {
                // A missing or broken photo should never block opening the picker.
            }
        }
    }

    private static BitmapImage ToBitmapImage(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 80; // small thumbnail - only shown at 40px
        image.EndInit();
        image.Freeze();
        return image;
    }

    // -----------------------------------------------------------------------
    // Selection

    private void GroupList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var selected = GroupList.SelectedItem as GroupeItem;
        UpdateAdminButtons(selected?.Groupe);
    }

    /// <summary>Shows the photo and delete buttons only when the selected espace was created
    /// by the signed-in user — the API enforces the same check server-side.</summary>
    private void UpdateAdminButtons(GroupeDto? groupe)
    {
        var isOwner = groupe?.IsAdminGeneral == true;
        PhotoButton.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = isOwner ? Visibility.Visible : Visibility.Collapsed;
    }

    // -----------------------------------------------------------------------
    // Open

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    private async void List_DoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();

    private async Task OpenSelectedAsync()
    {
        if (GroupList.SelectedItem is not GroupeItem item) return;

        OpenButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            await _session.EnterGroupAsync(item.Groupe.Id);

            App.Settings.LastGroupId = item.Groupe.Id;
            App.Settings.Save();

            DialogResult = true;
        }
        catch (ApiException ex) when (IsUnknownDevice(ex))
        {
            // A till being set up for the first time, or one that was replaced. Offer to
            // authorise it rather than leaving the person at a refusal they cannot act on.
            Cursor = null;

            if (RegisterDeviceDialog.Show(this, App.Settings.LastIdentifier))
            {
                HideError();
                await OpenSelectedAsync();
                return;
            }

            ShowError(ex.Message);
            OpenButton.IsEnabled = true;
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
            OpenButton.IsEnabled = true;
        }
        finally
        {
            Cursor = null;
        }
    }

    // -----------------------------------------------------------------------
    // Create

    /// <summary>Creates a group. The creator becomes its Admin Général.</summary>
    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Show(this, "Créer un espace", "Nom de l'espace :");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _session.Api.CreateGroupeAsync(name.Trim());
            HideError();
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Photo

    /// <summary>
    /// Opens a file picker and uploads the chosen image as the espace cover photo.
    /// Only enabled when the selected espace was created by the current user.
    /// </summary>
    private async void Photo_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupeItem item) return;

        var dialog = new OpenFileDialog
        {
            Title = "Choisir une photo pour cet espace",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Tous les fichiers|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        var fileName = Path.GetFileName(dialog.FileName);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(dialog.FileName);
        }
        catch (IOException ex)
        {
            ShowError($"Impossible de lire le fichier : {ex.Message}");
            return;
        }

        // The API requires a group session for /api/groupe/photo, so we need to be
        // inside the espace to update its photo. If the session is already open for
        // this espace we call directly; otherwise we open a temporary session.
        var needsSession = _session.Groupe?.Id != item.Groupe.Id;
        if (needsSession) await _session.EnterGroupAsync(item.Groupe.Id);

        try
        {
            var updated = await _session.Api.UploadEspacePhotoAsync(bytes, fileName);
            HideError();

            // Push the new photo into the card without a full reload.
            item.Groupe = updated;
            if (updated.PhotoUrl is not null)
            {
                try { item.Photo = ToBitmapImage(await _session.Api.GetImageBytesAsync(updated.PhotoUrl)); }
                catch { /* ignore - the picker still works without a fresh thumbnail */ }
            }
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            if (needsSession) await CloseTemporarySessionAsync();
        }
    }

    // -----------------------------------------------------------------------
    // Delete

    /// <summary>
    /// Asks for confirmation then permanently deletes the selected espace and all its data.
    /// Only enabled when the selected espace was created by the current user.
    /// </summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupeItem item) return;

        var nom = item.Groupe.Nom;
        var confirm = MessageBox.Show(
            this,
            $"Voulez-vous vraiment supprimer l'espace « {nom} » et toutes ses données ?\n\n" +
            "Cette action est irréversible.",
            "Supprimer l'espace",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        // A second confirmation with the name typed is the gold standard for destructive
        // actions, but for a local POS app a double-click confirm is already rare and
        // sufficient. The API enforces admin-général on its end regardless.
        try
        {
            await _session.Api.DeleteGroupeAsync(item.Groupe.Id);
            HideError();
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers

    /// <summary>Closes the group session quietly after a photo upload that required opening
    /// one temporarily. Swallows errors - the session will expire on its own.</summary>
    private async Task CloseTemporarySessionAsync()
    {
        try { await _session.Api.CloseGroupSessionAsync(); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Whether the server refused because it does not recognise this machine, as opposed to
    /// refusing the account. Matched on the message because the API returns 403 for both,
    /// deliberately - distinguishing them in the status code would tell a copied
    /// installation which of the two it had got wrong.
    /// </summary>
    private static bool IsUnknownDevice(ApiException ex) =>
        ex.Message.Contains("poste n'est pas autorisé", StringComparison.OrdinalIgnoreCase);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorPanel.Visibility = Visibility.Collapsed;
}

// ---------------------------------------------------------------------------

/// <summary>
/// Thin ViewModel wrapping one <see cref="GroupeDto"/> entry in the picker list.
/// Implements <see cref="INotifyPropertyChanged"/> so cover-photo bytes pushed in
/// after the initial load update the card without a full ItemsSource reset.
/// </summary>
internal sealed class GroupeItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private GroupeDto _groupe;
    private BitmapImage? _photo;

    public GroupeItem(GroupeDto groupe) => _groupe = groupe;

    public GroupeDto Groupe
    {
        get => _groupe;
        set { _groupe = value; Notify(); Notify(nameof(HasPhoto)); }
    }

    public BitmapImage? Photo
    {
        get => _photo;
        set { _photo = value; Notify(); Notify(nameof(HasPhoto)); }
    }

    /// <summary>True when a photo has been loaded. Used by the XAML BoolToVis converters
    /// to switch between the photo and the initial-letter circle.</summary>
    public bool HasPhoto => _photo is not null;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
