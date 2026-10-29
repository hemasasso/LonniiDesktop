using System.IO.Compression;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Backup;

/// <summary>Where the host's database lives, so the background service can tell whether it changed.</summary>
public sealed record CloudBackupPaths(string DatabasePath)
{
    /// <summary>
    /// A cheap change marker: the newest write time of the database or its write-ahead log.
    /// Any write moves it - including the sign-in bookkeeping, so it errs towards backing up
    /// once too often rather than once too few.
    /// </summary>
    public DateTime LastWriteUtc()
    {
        var latest = DateTime.MinValue;
        foreach (var path in new[] { DatabasePath, DatabasePath + "-wal" })
        {
            if (File.Exists(path)) latest = new[] { latest, File.GetLastWriteTimeUtc(path) }.Max();
        }
        return latest;
    }
}

/// <summary>What one backup attempt did.</summary>
public sealed record CloudBackupOutcome(bool Ran, bool Succeeded, string? Message);

/// <summary>
/// Takes one workspace's backup: builds the snapshot, sends the photos the server lacks, then
/// the snapshot itself - in that order, so a snapshot never refers to a photo the server does
/// not hold yet. All state (epoch, last result) is recorded in the database.
/// </summary>
public sealed class CloudBackupRunner(
    LonniiDbContext db,
    EspaceTransferService transfers,
    EspaceTransferPaths paths,
    ImageStorageService images,
    CloudBackupClient client,
    ILogger<CloudBackupRunner> logger)
{
    /// <summary>One backup at a time on this host: the schedule and the "Sauvegarder maintenant"
    /// button must not both be building a snapshot.</summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsRunning => Gate.CurrentCount == 0;

    /// <summary>Why this workspace cannot be backed up, or null when it can.</summary>
    public static string? WhyNotApplicable(Groupe? groupe, CloudBackupState? state)
    {
        if (groupe is null) return "Espace introuvable.";
        if (!Lonnii.Shared.Security.DeploymentModes.RequiresSubscription(groupe.Mode))
            return "La sauvegarde en ligne est réservée aux espaces en mode en ligne.";
        if (string.IsNullOrWhiteSpace(groupe.LicenceServerUrl))
            return "Aucun serveur Lonnii configuré pour cet espace.";
        if (string.IsNullOrEmpty(state?.DeviceToken) || string.IsNullOrEmpty(state.DeviceId))
            return "En attente de la première connexion au serveur Lonnii (renouvellement de licence).";
        return null;
    }

    public async Task<CloudBackupOutcome> RunAsync(string groupId, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct))
            return new CloudBackupOutcome(false, false, "Une sauvegarde est déjà en cours.");

        try
        {
            return await RunCoreAsync(groupId, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<CloudBackupOutcome> RunCoreAsync(string groupId, CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        var state = await db.CloudBackupStates.FirstOrDefaultAsync(s => s.GroupId == groupId, ct);

        if (WhyNotApplicable(groupe, state) is { } reason) return new CloudBackupOutcome(false, false, reason);

        // The people first, and even for a shop with no sales yet: a member added on this computer
        // must be able to sign in elsewhere (another computer, from afar) as soon as possible.
        await PushAccountsAsync(groupe!, state!, ct);

        // An empty workspace has nothing worth keeping, and uploading it can only do harm: on a
        // freshly installed computer whose restore failed or was declined, it would replace the
        // shop's real copy - or start a line that stops the server building one from live data.
        if (!await transfers.HasBusinessDataAsync(groupId, ct))
            return new CloudBackupOutcome(false, false, "Rien à sauvegarder : l'espace est encore vide.");

        state!.LastAttemptAt = DateTime.UtcNow;

        string? snapshot = null, gz = null;
        try
        {
            client.For(groupe!.LicenceServerUrl!, groupId, state.DeviceId!, state.DeviceToken!, state.Epoch);

            var begun = await client.BeginAsync(ct);
            state.Epoch = begun.Epoch;

            // Catches up a rename made while the server could not be reached. Never fails the
            // backup: an older server has no such address.
            try { await client.PutNameAsync(groupe.Nom, ct); }
            catch (CloudBackupException e)
            {
                logger.LogWarning("Nom de {GroupId} non envoyé au serveur Lonnii : {Message}", groupId, e.Message);
            }

            snapshot = paths.NewFile("backup");
            var export = await transfers.ExportSnapshotAsync(groupId, snapshot, ct)
                ?? throw new CloudBackupException("Espace introuvable.");

            gz = snapshot + ".gz";
            await using (var source = File.OpenRead(snapshot))
            await using (var target = File.Create(gz))
            await using (var zip = new GZipStream(target, CompressionLevel.Optimal))
            {
                await source.CopyToAsync(zip, 1 << 20, ct);
            }

            var wanted = export.Images.Select(i => new BackupImageRef(i.Folder, i.Name)).ToList();
            var missing = await client.MissingImagesAsync(wanted, ct);

            foreach (var image in missing)
            {
                if (images.ExistingPath(image.Folder, image.Name) is { } path)
                    await client.PutImageAsync(image, path, ct);
            }

            await client.PutSnapshotAsync(gz, export.Manifest.RecordCount, wanted.Count, ct);

            state.LastSuccessAt = DateTime.UtcNow;
            state.LastError = null;
            state.LastRecordCount = export.Manifest.RecordCount;
            state.LastImageCount = wanted.Count;

            logger.LogInformation(
                "Sauvegarde en ligne de {GroupId} : {Records} enregistrements, {Images} images ({Sent} envoyées)",
                groupId, export.Manifest.RecordCount, wanted.Count, missing.Count);

            return new CloudBackupOutcome(true, true, null);
        }
        catch (CloudBackupException e)
        {
            state.LastError = e.Message;
            logger.LogWarning("Sauvegarde en ligne de {GroupId} échouée : {Message}", groupId, e.Message);
            return new CloudBackupOutcome(true, false, e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            state.LastError = "Erreur inattendue pendant la sauvegarde.";
            logger.LogError(e, "Sauvegarde en ligne de {GroupId} : erreur", groupId);
            return new CloudBackupOutcome(true, false, state.LastError);
        }
        finally
        {
            EspaceArchive.ReleaseFiles();
            Delete(snapshot);
            Delete(gz);

            // Even on failure: the error and the attempt time are what Paramètres shows.
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Sends this shop's members to our server (see <see cref="AccountSync"/>). Never fails the
    /// backup: an older server has no such address, and the next pass simply tries again.
    /// </summary>
    private async Task PushAccountsAsync(Groupe groupe, CloudBackupState state, CancellationToken ct)
    {
        try
        {
            var request = await AccountSync.BuildAsync(db, groupe.Id, ct);
            client.For(groupe.LicenceServerUrl!, groupe.Id, state.DeviceId!, state.DeviceToken!, state.Epoch);
            await client.PutAccountsAsync(request, ct);
        }
        catch (CloudBackupException e)
        {
            logger.LogWarning("Membres de {GroupId} non envoyés au serveur Lonnii : {Message}", groupe.Id, e.Message);
        }
    }

    /// <summary>
    /// Sends the shop's name to the Lonnii server straight away, for an online shop just renamed.
    /// False when it is not an online shop or the server cannot be reached; the next backup then
    /// carries the name instead.
    /// </summary>
    public async Task<bool> PushNameAsync(string groupId, CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        var state = await db.CloudBackupStates.AsNoTracking().FirstOrDefaultAsync(s => s.GroupId == groupId, ct);
        if (WhyNotApplicable(groupe, state) is not null) return false;

        try
        {
            // The backup client waits up to half an hour for a snapshot; a rename must not.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            client.For(groupe!.LicenceServerUrl!, groupId, state!.DeviceId!, state.DeviceToken!, state.Epoch);
            await client.PutNameAsync(groupe.Nom, timeout.Token);
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Nom de {GroupId} non envoyé au serveur Lonnii : {Message}", groupId, e.Message);
            return false;
        }
    }

    /// <summary>
    /// Tells the Lonnii server an online shop is being deleted, so the dashboard shows it as deleted
    /// rather than as a shop gone quiet. Must run before the shop's rows go. False when it is not an
    /// online shop or the server cannot be reached.
    /// </summary>
    public async Task<bool> PushDeletedAsync(string groupId, CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        var state = await db.CloudBackupStates.AsNoTracking().FirstOrDefaultAsync(s => s.GroupId == groupId, ct);
        if (WhyNotApplicable(groupe, state) is not null) return false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            client.For(groupe!.LicenceServerUrl!, groupId, state!.DeviceId!, state.DeviceToken!, state.Epoch);
            await client.MarkDeletedAsync(timeout.Token);
            return true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Suppression de {GroupId} non signalée au serveur Lonnii : {Message}", groupId, e.Message);
            return false;
        }
    }

    /// <summary>Starts a fresh line of backups, setting the old one aside on the server.
    /// For a shop that deliberately begins again from an empty installation.</summary>
    public async Task<CloudBackupOutcome> RestartAsync(string groupId, CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        var state = await db.CloudBackupStates.FirstOrDefaultAsync(s => s.GroupId == groupId, ct);
        if (WhyNotApplicable(groupe, state) is { } reason) return new CloudBackupOutcome(false, false, reason);

        try
        {
            var begun = await client
                .For(groupe!.LicenceServerUrl!, groupId, state!.DeviceId!, state.DeviceToken!, state.Epoch)
                .ResetAsync(ct);

            state.Epoch = begun.Epoch;
            state.LastError = null;
            await db.SaveChangesAsync(ct);
            return new CloudBackupOutcome(true, true, null);
        }
        catch (CloudBackupException e)
        {
            return new CloudBackupOutcome(true, false, e.Message);
        }
    }

    private static void Delete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a leftover temporary file is not worth failing a backup over */ }
    }
}
