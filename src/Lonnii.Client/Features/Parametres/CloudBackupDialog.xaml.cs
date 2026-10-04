using System.Windows;
using System.Windows.Input;
using Lonnii.Client.Services;

namespace Lonnii.Client.Features.Parametres;

/// <summary>
/// Paramètres → Sauvegarde en ligne: when the last backup reached the licence server, a button
/// to take one now, and the two ways out of a machine that has lost its place on the shop's
/// line of backups - restore the copy, or deliberately start again.
/// </summary>
public partial class CloudBackupDialog : Window
{
    private readonly AppSession _session;

    public CloudBackupDialog(AppSession session)
    {
        _session = session;
        InitializeComponent();

        // Restoring and starting again rewrite a whole espace's history: Admin Général only,
        // as the API enforces.
        RestoreButton.IsEnabled = RestartButton.IsEnabled = session.IsAdminGeneral;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var status = await _session.Api.GetCloudBackupStatusAsync();

            if (!status.Applicable)
            {
                StatusText.Text = "Sauvegarde en ligne non disponible";
                DetailText.Text = status.Reason ?? "Cet espace n'est pas en mode en ligne.";
                RunButton.IsEnabled = RestoreButton.IsEnabled = RestartButton.IsEnabled = false;
                return;
            }

            StatusText.Text = status.LastSuccessAt is { } at
                ? $"Dernière sauvegarde : {at.ToLocalTime():dd/MM/yyyy HH:mm}"
                : "Aucune sauvegarde envoyée pour l'instant";

            DetailText.Text = status.LastSuccessAt is null
                ? status.Reason ?? string.Empty
                : $"{status.LastRecordCount} enregistrements, {status.LastImageCount} images.";

            ShowError(status.LastError);
            RunButton.IsEnabled = status.Reason is null && !status.Running;
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e) =>
        await DoAsync("Sauvegarde en cours…", () => _session.Api.RunCloudBackupAsync());

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "Recharger la copie en ligne dans cet espace ?\n\nCela n'est possible que si l'espace est encore vide.",
            "Restaurer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        await DoAsync("Restauration en cours… (peut durer quelques minutes)", async () =>
        {
            var result = await _session.Api.RestoreFromCloudAsync();
            MessageBox.Show(this,
                $"Restauration terminée : {result.RecordCount} enregistrements, {result.ImageCount} images.\n\n" +
                "Reconnectez-vous pour retrouver vos comptes.",
                "Restaurer", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "Repartir de zéro ?\n\nLa copie en ligne actuelle est mise de côté (le support peut la récupérer) " +
            "et les prochaines sauvegardes la remplaceront.",
            "Repartir de zéro", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        await DoAsync("…", () => _session.Api.RestartCloudBackupAsync());
    }

    private async Task DoAsync(string busy, Func<Task> action)
    {
        ShowError(null);
        BusyText.Text = busy;
        BusyText.Visibility = Visibility.Visible;
        Cursor = Cursors.Wait;
        RunButton.IsEnabled = RestoreButton.IsEnabled = RestartButton.IsEnabled = false;

        try { await action(); }
        catch (ApiException ex) { ShowError(ex.Message); }
        finally
        {
            BusyText.Visibility = Visibility.Collapsed;
            Cursor = null;
            RestoreButton.IsEnabled = RestartButton.IsEnabled = _session.IsAdminGeneral;
            await LoadAsync();
        }
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
