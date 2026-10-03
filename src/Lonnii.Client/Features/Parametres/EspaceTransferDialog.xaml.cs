using System.IO;
using System.Windows;
using System.Windows.Media;
using Lonnii.Client.Services;
using Microsoft.Win32;

namespace Lonnii.Client.Features.Parametres;

/// <summary>
/// Paramètres → Données de l'espace: downloads the whole espace as one <c>.db</c> file, and
/// loads such a file into an espace that has none of its own yet.
///
/// <para>
/// The two halves are deliberately asymmetric. Downloading is routine - a shop should be able
/// to take its own data away whenever it likes. Loading one in rewrites an entire espace from
/// a file that came from somewhere else, so it is the Admin Général's alone, it asks first,
/// and the host refuses it outright if the espace already holds anything.
/// </para>
/// </summary>
public partial class EspaceTransferDialog : Window
{
    private readonly AppSession _session;

    public EspaceTransferDialog(AppSession session)
    {
        _session = session;
        InitializeComponent();

        var nom = session.Groupe?.Nom ?? "cet espace";
        ExportTitleText.Text = $"Télécharger « {nom} »";

        // Import is the Admin Général's alone, and the API says the same: showing the button
        // to anyone else would only advertise a locked door.
        var canImport = session.IsAdminGeneral;
        ImportPanel.Visibility = canImport ? Visibility.Visible : Visibility.Collapsed;
        ImportDeniedPanel.Visibility = canImport ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- Export ---

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer les données de l'espace",
            Filter = "Sauvegarde d'espace Lonnii (*.db)|*.db",
            DefaultExt = ".db",
            FileName = SuggestedFileName(),
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        Busy("Téléchargement en cours…");

        try
        {
            var (records, images) = await _session.Api.DownloadEspaceAsync(
                dialog.FileName, Reporting("Téléchargement"));

            var size = new FileInfo(dialog.FileName).Length;
            Done($"Enregistré : {Money.FormatPlain(records)} enregistrements, " +
                 $"{Money.FormatPlain(images)} image(s), {FormatBytes(size)}.");
        }
        catch (ApiException ex)
        {
            Failed(ex.Message);
        }
        catch (IOException ex)
        {
            Failed($"Le fichier n'a pas pu être écrit : {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Failed($"Le fichier n'a pas pu être écrit : {ex.Message}");
        }
    }

    /// <summary>Espace name and date, so a shop recognises the file months later.</summary>
    private string SuggestedFileName()
    {
        var nom = _session.Groupe?.Nom ?? "espace";

        var safe = new string(nom.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray())
            .Trim('-');

        if (safe.Length == 0) safe = "espace";
        if (safe.Length > 40) safe = safe[..40].Trim('-');

        return $"lonnii-{safe}-{DateTime.Now:yyyy-MM-dd}.db";
    }

    // --- Import ---

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choisir la sauvegarde à importer",
            Filter = "Sauvegarde d'espace Lonnii (*.db)|*.db|Tous les fichiers|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        var nom = _session.Groupe?.Nom ?? "cet espace";
        var confirm = MessageBox.Show(this,
            $"Charger le contenu de ce fichier dans « {nom} » ?\n\n" +
            "L'import n'est accepté que si cet espace ne contient encore aucune donnée. " +
            "Les membres et leurs privilèges ne sont pas transférés.",
            "Importer des données", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK) return;

        Busy("Import en cours…");

        try
        {
            var result = await _session.Api.UploadEspaceAsync(
                dialog.FileName, Reporting("Envoi", "Import en cours sur le serveur…"));

            ResultHeaderText.Text = $"IMPORTÉ DEPUIS « {result.SourceGroupName.ToUpperInvariant()} »";
            ResultGrid.ItemsSource = result.Sections.Where(s => s.Count > 0).ToList();
            ResultPanel.Visibility = Visibility.Visible;

            Done($"{Money.FormatPlain(result.RecordCount)} enregistrements et " +
                 $"{Money.FormatPlain(result.ImageCount)} image(s) importés.");

            // The espace's currency and receipt settings came with the file, and the shell is
            // still showing the ones it read when the espace was opened.
            try { await _session.RefreshAsync(); }
            catch (ApiException) { /* the import itself succeeded; a stale label is not worth an error */ }

            MessageBox.Show(this,
                $"Import terminé : {Money.FormatPlain(result.RecordCount)} enregistrements.\n\n" +
                "Rouvrez l'espace (ou redémarrez l'application) pour que tous les écrans " +
                "affichent les données importées.",
                "Importer des données", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ApiException ex)
        {
            Failed(ex.Message);

            // The refusals worth reading in full - "the espace is not empty", with the list
            // of what is in it - are longer than the status line can hold.
            MessageBox.Show(this, ex.Message, "Importer des données",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (IOException ex)
        {
            Failed($"Le fichier n'a pas pu être lu : {ex.Message}");
        }
    }

    // --- Status line ---

    /// <summary>
    /// Updates the status line as the bytes move. A transfer can run for an hour at the sizes
    /// allowed, so a bar with nothing behind it would be indistinguishable from a hang.
    ///
    /// <paramref name="afterMessage"/> is shown once everything is on the wire but the host
    /// has not answered yet - which, for an import, is the whole copy happening server-side.
    /// </summary>
    private IProgress<LonniiApiClient.TransferProgress> Reporting(string verb, string? afterMessage = null) =>
        // Progress<T> posts back to the thread it was created on - the UI one - so the
        // callback may touch these controls directly.
        new Progress<LonniiApiClient.TransferProgress>(p =>
        {
            if (p.Total <= 0)
            {
                StatusText.Text = $"{verb} : {FormatBytes(p.Transferred)}";
                return;
            }

            if (p.Transferred >= p.Total && afterMessage is not null)
            {
                Progress.IsIndeterminate = true;
                StatusText.Text = afterMessage;
                return;
            }

            Progress.IsIndeterminate = false;
            Progress.Value = (double)p.Transferred / p.Total;
            StatusText.Text = $"{verb} : {FormatBytes(p.Transferred)} / {FormatBytes(p.Total)}";
        });

    private void Busy(string message)
    {
        ExportButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        Progress.Value = 0;
        StatusText.Foreground = (Brush)FindResource("TextSecondary");
        StatusText.Text = message;
    }

    private void Done(string message)
    {
        Idle();
        StatusText.Foreground = (Brush)FindResource("TextSecondary");
        StatusText.Text = message;
    }

    private void Failed(string message)
    {
        Idle();
        StatusText.Foreground = (Brush)FindResource("Danger");
        StatusText.Text = message;
    }

    private void Idle()
    {
        Progress.Visibility = Visibility.Collapsed;
        ExportButton.IsEnabled = true;
        ImportButton.IsEnabled = true;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} o",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} Ko",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} Mo",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} Go",
    };

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
