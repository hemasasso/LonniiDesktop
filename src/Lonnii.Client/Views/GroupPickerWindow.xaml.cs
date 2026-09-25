using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Views;

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
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var groupes = await _session.Api.GetGroupesAsync();
            GroupList.ItemsSource = groupes;

            var hasAny = groupes.Count > 0;
            GroupList.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
            EmptyPanel.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
            OpenButton.IsEnabled = hasAny;

            if (!hasAny) return;

            // Reopen whatever was last used, so a till lands where it left off.
            var remembered = groupes.FirstOrDefault(g => g.Id == App.Settings.LastGroupId);
            GroupList.SelectedItem = remembered ?? groupes[0];
            GroupList.Focus();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    private async void List_DoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();

    private async Task OpenSelectedAsync()
    {
        if (GroupList.SelectedItem is not GroupeDto groupe) return;

        OpenButton.IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            await _session.EnterGroupAsync(groupe.Id);

            App.Settings.LastGroupId = groupe.Id;
            App.Settings.Save();

            DialogResult = true;
        }
        catch (ApiException ex) when (IsUnknownDevice(ex))
        {
            // A till being set up for the first time, or one that was replaced. Offer to
            // authorise it rather than leaving the person at a refusal they cannot act on.
            Cursor = null;

            if (Dialogs.RegisterDeviceDialog.Show(this, App.Settings.LastIdentifier))
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
