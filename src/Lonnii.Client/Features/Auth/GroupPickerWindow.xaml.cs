using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Lonnii.Client.Features;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

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
        Icon = AppIcon.Current;
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

            await LoadPendingAsync();

            GroupList.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
            EmptyPanel.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
            OpenButton.IsEnabled = hasAny;

            if (!hasAny)
            {
                GroupList.ItemsSource = null;

                // With no espace left - the last one deleted, or a first sign-in - there is nobody to
                // ask to be added, so a way to create one must be offered or the person is stuck.
                // Who may actually create one is decided where espaces are registered: the licence
                // server accepts only the Admin Général of an approved shop and says so otherwise.
                CreateButton.Visibility = Visibility.Visible;
                return;
            }

            // Only someone who already runs an espace may open another one.
            CreateButton.Visibility = groupes.Any(g => g.IsAdminGeneral) ? Visibility.Visible : Visibility.Collapsed;

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
                var bytes = await _session.Api.GetEspacePhotoBytesAsync(item.Groupe.Id);
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
        image.DecodePixelWidth = 360; // shown ~180 wide on the card cover; 2x for sharpness
        image.EndInit();
        image.Freeze();
        return image;
    }

    // -----------------------------------------------------------------------
    // Selection

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
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Locked)
        {
            // Past the offline deadline: the host refuses the workspace until this shop has
            // reached the licence server. Offer the renewal here - there is no shell yet to
            // show it - and carry on opening if it worked.
            Cursor = null;

            if (Licensing.LicenceLockWindow.ShowLocked(this, item.Groupe.Id, ex.Message))
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

        // A new espace is registered with Lonnii before it exists, and Lonnii only knows the
        // owner by password - the host keeps nothing it could present instead.
        var password = PasswordPromptDialog.Show(this, "Créer un espace",
            "Saisissez le mot de passe de votre compte Lonnii en ligne (celui de Lonnii Business) pour " +
            "enregistrer ce nouvel espace. Il peut différer du mot de passe de ce poste.");
        if (password is null) return;

        Cursor = Cursors.Wait;
        try
        {
            var outcome = await _session.Api.CreateGroupeAsync(name.Trim(), password);
            HideError();

            if (outcome.Pending is { } waiting)
            {
                MessageBox.Show(this,
                    $"« {waiting.Name} » a été enregistré auprès de Lonnii.\n\n" +
                    "Il doit être approuvé avant de pouvoir être utilisé. Une fois approuvé, revenez ici " +
                    "et cliquez sur « Vérifier » à côté de son nom.",
                    "Espace en attente d'approbation", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            Cursor = null;
        }
    }

    // -----------------------------------------------------------------------
    // Waiting for approval

    private async Task LoadPendingAsync()
    {
        try
        {
            var pending = await _session.Api.GetPendingEspacesAsync();
            PendingList.ItemsSource = pending;
            PendingPanel.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (ApiException)
        {
            // An older host without this list: nothing to show.
            PendingPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Picks up an approved espace. While it still waits, Lonnii says so and nothing changes.</summary>
    private async void CheckPending_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string groupId) return;

        var password = PasswordPromptDialog.Show(this, "Vérifier l'approbation",
            "Saisissez le mot de passe de votre compte Lonnii en ligne (celui de Lonnii Business). " +
            "Il peut différer du mot de passe de ce poste.");
        if (password is null) return;

        Cursor = Cursors.Wait;
        try
        {
            await _session.Api.ActivatePendingEspaceAsync(groupId, password);
            HideError();
            await LoadAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            Cursor = null;
        }
    }

    private async void DismissPending_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string groupId) return;

        if (MessageBox.Show(this,
                "Retirer cette demande de ce poste ?\n\nLonnii garde sa trace ; vous pourrez lui demander de la refuser.",
                "Retirer la demande", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            await _session.Api.DismissPendingEspaceAsync(groupId);
            await LoadPendingAsync();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers

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
