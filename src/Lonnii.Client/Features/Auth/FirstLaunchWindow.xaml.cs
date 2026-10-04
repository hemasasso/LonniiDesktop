using System.IO;
using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;
using Microsoft.Win32;

namespace Lonnii.Client.Features.Auth;

/// <summary>
/// Shown once, on a host that has never been set up: turns the credentials file we issued
/// into a working shop.
///
/// <para>
/// Nothing here decides anything. The file is sent to the API, which decrypts it and asks
/// our licence server, and only a workspace we registered comes back. That is deliberate -
/// a check made in this window would run on a machine the customer controls.
/// </para>
/// </summary>
public partial class FirstLaunchWindow : Window
{
    private readonly AppSession _session = App.Session;
    private byte[]? _fileContent;

    public FirstLaunchWindow()
    {
        InitializeComponent();

        DeviceText.Text = $"{DeviceIdentity.FriendlyName} — {DeviceIdentity.Current[..12]}…";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Fichier d'identifiants Lonnii",
            Filter = "Fichier Lonnii (*.lonnii)|*.lonnii|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _fileContent = File.ReadAllBytes(dialog.FileName);
            FileBox.Text = dialog.FileName;
            HideError();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _fileContent = null;
            FileBox.Text = string.Empty;
            ShowError($"Impossible de lire le fichier : {ex.Message}");
        }
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_fileContent is null)
        {
            ShowError("Choisissez d'abord le fichier d'identifiants.");
            return;
        }

        if (PassphraseBox.Password.Length == 0)
        {
            ShowError("Saisissez la phrase secrète.");
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _session.Api.ApplyCredentialsAsync(_fileContent, PassphraseBox.Password);

            MessageBox.Show(this,
                $"L'espace « {result.GroupName} » est prêt.\n\n" +
                $"Connectez-vous avec {result.AdminEmail}.\n" +
                $"Postes autorisés : {result.MaxDevices}.",
                "Activation réussie", MessageBoxButton.OK, MessageBoxImage.Information);

            // A shop that already has an online backup is a replaced or reinstalled machine:
            // offer to bring it back now, before anyone trades on the blank workspace.
            if (result.CloudBackup is { } backup)
                await OfferRestoreAsync(backup);

            DialogResult = true;
        }
        catch (ApiException ex)
        {
            // The API passes our licence server's own wording through, which already
            // distinguishes an expired subscription from an exhausted machine allowance
            // better than anything this window could say.
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task OfferRestoreAsync(Lonnii.Shared.Contracts.BackupInfoDto backup)
    {
        var when = backup.SnapshotAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

        var answer = MessageBox.Show(this,
            $"Une sauvegarde en ligne de cet espace existe (du {when}, {backup.RecordCount} enregistrements).\n\n" +
            "La restaurer maintenant ? Vos produits, ventes, photos et comptes seront récupérés.\n\n" +
            "Si vous répondez Non, cette sauvegarde est conservée ; vous pourrez la restaurer plus tard " +
            "depuis Paramètres → Sauvegarde en ligne.",
            "Sauvegarde trouvée", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        BusyText.Text = "Restauration en cours… (peut durer quelques minutes)";
        SetBusy(true);
        try
        {
            var restored = await _session.Api.RestoreFromCloudAtSetupAsync();

            MessageBox.Show(this,
                $"Restauration terminée : {restored.RecordCount} enregistrements, {restored.ImageCount} images.",
                "Restauration", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ApiException ex)
        {
            MessageBox.Show(this,
                $"{ex.Message}\n\nVous pourrez réessayer depuis Paramètres → Sauvegarde en ligne.",
                "Restauration impossible", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SetBusy(bool busy)
    {
        ActivateButton.IsEnabled = !busy;
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
