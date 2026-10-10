using System.IO.Compression;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Backup;

/// <summary>What a restore attempt did. Exactly one of the three is set.</summary>
public sealed record CloudRestoreOutcome(
    EspaceImportResultDto? Result = null, string? Error = null, int StatusCode = 400);

/// <summary>
/// Brings an online shop's data back from the licence server after its host lost the database:
/// downloads the newest snapshot and the photos it refers to, loads them into the (freshly
/// set-up, still empty) workspace, and adopts the backup's epoch so backups can then continue
/// on the same line.
///
/// <para>
/// Refuses unless the workspace holds no business data - see
/// <see cref="EspaceTransferService.RestoreAsync"/> - so a restore can never overwrite a shop
/// that has started trading again.
/// </para>
/// </summary>
public sealed class CloudRestoreService(
    LonniiDbContext db,
    EspaceTransferService transfers,
    EspaceTransferPaths paths,
    ImageStorageService images,
    CloudBackupClient client,
    ILogger<CloudRestoreService> logger)
{
    public async Task<CloudRestoreOutcome> RestoreAsync(string groupId, CancellationToken ct)
    {
        // Shared with the backup schedule: a backup of half-restored data must not be sent.
        if (!await CloudBackupRunner.Gate.WaitAsync(0, ct))
            return new CloudRestoreOutcome(Error: "Une sauvegarde est en cours. Réessayez dans un instant.", StatusCode: 409);

        var written = new List<string>();
        string? gz = null, dbPath = null;

        try
        {
            var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
            var state = await db.CloudBackupStates.FirstOrDefaultAsync(s => s.GroupId == groupId, ct);

            if (CloudBackupRunner.WhyNotApplicable(groupe, state) is { } reason)
                return new CloudRestoreOutcome(Error: reason, StatusCode: 409);

            client.For(groupe!.LicenceServerUrl!, groupId, state!.DeviceId!, state.DeviceToken!, state.Epoch);

            var info = await client.InfoAsync(ct);
            if (!info.Exists || info.Epoch is null)
                return new CloudRestoreOutcome(Error: "Aucune sauvegarde en ligne pour cet espace.", StatusCode: 404);

            gz = paths.NewFile("restore") + ".gz";
            dbPath = paths.NewFile("restore");

            await client.DownloadSnapshotAsync(gz, ct);

            await using (var source = File.OpenRead(gz))
            await using (var unzip = new GZipStream(source, CompressionMode.Decompress))
            await using (var target = File.Create(dbPath))
                await unzip.CopyToAsync(target, 1 << 20, ct);

            foreach (var image in await PhotosOfAsync(dbPath, ct))
            {
                if (images.PathFor(image.Folder, image.Name) is not { } path) continue;

                try
                {
                    // Already here (a restore retried after a failure): nothing to fetch.
                    if (!File.Exists(path))
                    {
                        await client.DownloadImageAsync(image, path, ct);
                        written.Add(path);
                    }
                }
                catch (CloudBackupException e)
                {
                    // One missing photo must not sink the whole restore; that row just has no photo.
                    logger.LogWarning("Photo {Name} introuvable dans la sauvegarde : {Message}", image.Name, e.Message);
                    TryDelete(path);
                }
            }

            var outcome = await transfers.RestoreAsync(groupId, dbPath, ct);
            dbPath = null; // the service deletes it

            if (outcome.Conflict is { } conflicts)
            {
                var found = string.Join(", ", conflicts.Select(c => $"{c.Label} ({c.Count})"));
                RemoveWritten(written);
                return new CloudRestoreOutcome(
                    Error: $"Cet espace contient déjà des données : {found}. Une restauration n'est possible que dans un espace vide.",
                    StatusCode: 409);
            }

            if (outcome.Error is { } error)
            {
                RemoveWritten(written);
                return new CloudRestoreOutcome(Error: error);
            }

            // Back on the shop's own line of backups, so the next scheduled one is accepted.
            db.ChangeTracker.Clear();
            var saved = await db.CloudBackupStates.FirstAsync(s => s.GroupId == groupId, ct);
            saved.Epoch = info.Epoch;
            saved.LastSuccessAt = info.SnapshotAt;
            saved.LastError = null;
            await db.SaveChangesAsync(ct);

            return new CloudRestoreOutcome(Result: outcome.Result);
        }
        catch (CloudBackupException e)
        {
            RemoveWritten(written);
            return new CloudRestoreOutcome(Error: e.Message, StatusCode: (int?)e.Status ?? 502);
        }
        finally
        {
            EspaceArchive.ReleaseFiles();
            TryDelete(gz);
            TryDelete(dbPath);
            CloudBackupRunner.Gate.Release();
        }
    }

    /// <summary>Every photo the snapshot's rows point at.</summary>
    private async Task<List<BackupImageRef>> PhotosOfAsync(string dbPath, CancellationToken ct)
    {
        await using var archive = EspaceArchive.Open(dbPath);

        var urls = new List<string?>();
        urls.AddRange(await archive.Db.Categories.Select(c => c.ImageUrl).ToListAsync(ct));
        urls.AddRange(await archive.Db.Products.Select(p => p.ImageUrl).ToListAsync(ct));
        urls.AddRange(await archive.Db.VentesParametres.Select(v => v.LogoPath).ToListAsync(ct));
        urls.AddRange(await archive.Db.VentesParametres.Select(v => v.QrCodePath).ToListAsync(ct));

        if (await archive.ReadManifestAsync(ct) is { } file) urls.Add(file.Settings.PhotoUrl);

        return urls
            .Select(EspaceImages.Parse)
            .Where(i => i is not null)
            .Select(i => new BackupImageRef(i!.Value.Folder, i.Value.FileName))
            .Distinct()
            .ToList();
    }

    private static void RemoveWritten(List<string> written)
    {
        foreach (var path in written) TryDelete(path);
        written.Clear();
    }

    private static void TryDelete(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a leftover file is not worth failing over */ }
    }
}
