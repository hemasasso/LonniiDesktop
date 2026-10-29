using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Auth;

/// <summary>
/// First launch for someone who already has a Lonnii account: sign in, pick one of the espaces
/// they administer, and this computer becomes its host - no credentials file, no new
/// registration. If the espace has an online backup, it is offered straight away.
///
/// <para>
/// As in <see cref="FirstLaunchWindow"/>, nothing is decided here: the host asks our licence
/// server, which checks the password and the account's right to the espace.
/// </para>
/// </summary>
public partial class ExistingAccountWindow : Window
{
    private readonly AppSession _session = App.Session;

    /// <summary>The address used, so sign-in can be pre-filled with it.</summary>
    public string? SignedInEmail { get; private set; }

    public ExistingAccountWindow(string? email = null)
    {
        InitializeComponent();
        EmailBox.Text = email ?? string.Empty;
        Loaded += (_, _) =>
        {
            if (EmailBox.Text.Length == 0) EmailBox.Focus();
            else PasswordBox.Focus();
        };
    }

    private sealed record EspaceItem(AccountEspaceDto Espace)
    {
        public string Display => Espace.IsOwner ? Espace.Name : $"{Espace.Name}  (administrateur)";
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (email.Length == 0 || PasswordBox.Password.Length == 0)
        {
            ShowError("Saisissez votre adresse e-mail et votre mot de passe.");
            return;
        }

        SetBusy(true, "Recherche de vos espaces…");
        try
        {
            var espaces = await _session.Api.GetAccountEspacesAsync(email, PasswordBox.Password);
            HideError();

            if (espaces.Count == 0)
            {
                EspacePanel.Visibility = Visibility.Collapsed;
                ShowError("Ce compte n'administre aucun espace approuvé. Un membre qui n'est pas administrateur " +
                          "demande à son administrateur d'installer l'espace.");
                return;
            }

            EspaceList.ItemsSource = espaces.Select(x => new EspaceItem(x)).ToList();
            EspaceList.SelectedIndex = 0;
            EspacePanel.Visibility = Visibility.Visible;
            EspaceList.Focus();
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void EspaceList_DoubleClick(object sender, MouseButtonEventArgs e) => Install_Click(sender, e);

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (EspaceList.SelectedItem is not EspaceItem item) return;

        var email = EmailBox.Text.Trim();

        SetBusy(true, "Installation en cours…");
        try
        {
            var result = await _session.Api.InstallAccountEspaceAsync(email, PasswordBox.Password, item.Espace.GroupId);
            SignedInEmail = email;

            MessageBox.Show(this,
                $"L'espace « {result.GroupName} » est installé sur cet ordinateur.\n\n" +
                $"Postes autorisés : {result.MaxDevices}.",
                "Installation réussie", MessageBoxButton.OK, MessageBoxImage.Information);

            if (result.CloudBackup is { } backup)
                await SetupRestore.OfferAsync(this, _session, backup, SetBusy);

            DialogResult = true;
        }
        catch (ApiException ex)
        {
            // Our licence server's own words: wrong password, machine limit reached, subscription expired.
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetBusy(bool busy, string? text)
    {
        if (text is not null) BusyText.Text = text;
        SearchButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy && EspaceList.SelectedItem is not null;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Cursor = busy ? Cursors.Wait : null;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorPanel.Visibility = Visibility.Collapsed;
}
