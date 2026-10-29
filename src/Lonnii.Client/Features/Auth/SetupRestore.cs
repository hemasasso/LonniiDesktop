using System.Windows;
using Lonnii.Client.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Client.Features.Auth;

/// <summary>
/// After a first-launch activation: a shop that already has an online backup is a replaced or
/// reinstalled machine, or one being added for a shop that already trades. Offer to bring the
/// backup back now, before anyone trades on the blank workspace.
/// </summary>
internal static class SetupRestore
{
    public static async Task OfferAsync(
        Window owner, AppSession session, BackupInfoDto backup, Action<bool, string?> setBusy, string? groupId = null)
    {
        var when = backup.SnapshotAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

        var answer = MessageBox.Show(owner,
            $"Une sauvegarde en ligne de cet espace existe (du {when}, {backup.RecordCount} enregistrements).\n\n" +
            "La restaurer maintenant ? Vos produits, ventes, photos et comptes seront récupérés.\n\n" +
            "Si vous répondez Non, cette sauvegarde est conservée ; vous pourrez la restaurer plus tard " +
            "depuis Paramètres → Sauvegarde en ligne.",
            "Sauvegarde trouvée", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        setBusy(true, "Restauration en cours… (peut durer quelques minutes)");
        try
        {
            var restored = await session.Api.RestoreFromCloudAtSetupAsync(groupId);

            MessageBox.Show(owner,
                $"Restauration terminée : {restored.RecordCount} enregistrements, {restored.ImageCount} images.",
                "Restauration", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ApiException ex)
        {
            MessageBox.Show(owner,
                $"{ex.Message}\n\nVous pourrez réessayer depuis Paramètres → Sauvegarde en ligne.",
                "Restauration impossible", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            setBusy(false, null);
        }
    }
}
