using System.IO.Compression;
using Lonnii.Api.Features.Parametres;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Backup;

/// <summary>
/// On the licence server: builds a shop's backup from the server's own live data, for a shop that
/// has never had a host to upload one - created online, members added from afar, used from phones.
/// A new computer installing it then restores this exactly as it would a host's backup.
///
/// <para>
/// The snapshot is the same archive a host uploads (<see cref="EspaceTransferService.ExportSnapshotAsync"/>,
/// staff accounts and real owner included), read through the same model, which on the server sits on
/// the live PostgreSQL. Photos are copied from the server's image store when it has them; one it lacks
/// is simply not restored.
/// </para>
/// <para>
/// Switched by <c>Lonnii:Backup:BuildFromLiveData</c>, on by default only where the database is
/// PostgreSQL - that is, on our server, never on a shop's host.
/// </para>
/// </summary>
public sealed class LiveSnapshotBuilder(
    Lonnii.Data.LonniiDbContext db,
    EspaceTransferService transfers,
    EspaceTransferPaths paths,
    ImageStorageService images,
    BackupStore store,
    IConfiguration config,
    ILogger<LiveSnapshotBuilder> logger)
{
    public bool Enabled => config.GetValue<bool?>("Lonnii:Backup:BuildFromLiveData") ?? db.Database.IsNpgsql();

    /// <summary>
    /// Builds and stores the snapshot when this shop's backup may come from live data. Never throws:
    /// a failure leaves the shop with whatever backup it had, and the caller answers from that.
    /// Call with the store's lock held for the group.
    /// </summary>
    public async Task TryBuildAsync(string groupId, CancellationToken ct)
    {
        if (!Enabled || !store.MayBuildFromLive(groupId)) return;

        string? snapshot = null, gz = null;
        try
        {
            snapshot = paths.NewFile("live");
            var export = await transfers.ExportSnapshotAsync(groupId, snapshot, ct);
            if (export is null) return;

            gz = snapshot + ".gz";
            await using (var source = File.OpenRead(snapshot))
            await using (var target = File.Create(gz))
            await using (var zip = new GZipStream(target, CompressionLevel.Optimal))
            {
                await source.CopyToAsync(zip, 1 << 20, ct);
            }

            var copied = 0;
            foreach (var (folder, name) in export.Value.Images)
            {
                if (images.ExistingPath(folder, name) is not { } path) continue;
                await using var photo = File.OpenRead(path);
                if (await store.SaveImageAsync(groupId, folder, name, photo, ct)) copied++;
            }

            // A line must exist before a snapshot can join it; a shop with none starts one here.
            store.Begin(groupId, store.CurrentEpoch(groupId));

            await using (var body = File.OpenRead(gz))
            {
                await store.SaveSnapshotAsync(groupId, body, export.Value.Manifest.RecordCount, copied, ct, builtFromLive: true);
            }

            logger.LogInformation(
                "Sauvegarde de {GroupId} construite depuis les données en ligne : {Records} enregistrements, {Images} images",
                groupId, export.Value.Manifest.RecordCount, copied);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Sauvegarde de {GroupId} depuis les données en ligne impossible", groupId);
        }
        finally
        {
            EspaceArchive.ReleaseFiles();
            if (snapshot is not null && File.Exists(snapshot)) File.Delete(snapshot);
            if (gz is not null && File.Exists(gz)) File.Delete(gz);
        }
    }
}
