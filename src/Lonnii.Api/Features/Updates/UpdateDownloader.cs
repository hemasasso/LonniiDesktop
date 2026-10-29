using System.Net;
using System.Text.Json;
using Lonnii.Api.Features.Backup;
using Lonnii.Data;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Updates;

/// <summary>What one check against our server ended with.</summary>
public enum UpdateCheckResult
{
    /// <summary>This computer has no online espace: updates are for online shops only.</summary>
    NotEntitled,
    /// <summary>The server could not be reached; whatever was here is kept.</summary>
    Unreachable,
    /// <summary>Nothing is published right now.</summary>
    NothingPublished,
    /// <summary>The newest version was already here.</summary>
    UpToDate,
    Downloaded,
    /// <summary>The server offered a file that failed our signature, size or hash.</summary>
    Rejected,
}

/// <summary>
/// The shop's main computer fetching the newest version from our server, for itself and for its
/// tills (served to them by <see cref="UpdateEndpoints"/>). Asked as one of its online espaces,
/// with that espace's backup credentials, so our server gives it out only to a shop that is
/// online, unblocked and paid up. A computer whose espaces are all in local mode keeps nothing.
///
/// <para>
/// Nothing is trusted on the server's word: the notice must carry our signature, and the file
/// must have exactly the size and SHA-256 it names, before it is put where the tills look.
/// </para>
/// </summary>
public sealed class UpdateDownloader(
    LonniiDbContext db,
    CloudBackupClient client,
    UpdatePaths paths,
    ILogger<UpdateDownloader> logger)
{
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct)
    {
        var candidates = await (
            from g in db.Groupes.AsNoTracking()
            join s in db.CloudBackupStates.AsNoTracking() on g.Id equals s.GroupId
            where s.DeviceToken != null && !g.IsDeleted
            select new { Groupe = g, State = s }).ToListAsync(ct);

        var entitled = candidates.Where(c => CloudBackupRunner.WhyNotApplicable(c.Groupe, c.State) is null).ToList();

        foreach (var c in entitled)
        {
            UpdateManifest? manifest;
            try
            {
                manifest = await client
                    .For(c.Groupe.LicenceServerUrl!, c.Groupe.Id, c.State.DeviceId!, c.State.DeviceToken!, epoch: null)
                    .LatestUpdateAsync(ct);
            }
            catch (CloudBackupException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.PaymentRequired)
            {
                // This espace is not entitled (blocked, unpaid, gone local on our side); another may be.
                continue;
            }
            catch (CloudBackupException e)
            {
                logger.LogInformation("Recherche de mise à jour impossible : {Message}", e.Message);
                return UpdateCheckResult.Unreachable;
            }

            if (manifest is null)
            {
                // Withdrawn on our side: the tills must stop being offered it.
                Clear();
                return UpdateCheckResult.NothingPublished;
            }

            return await KeepAsync(manifest, ct);
        }

        Clear();
        return UpdateCheckResult.NotEntitled;
    }

    private async Task<UpdateCheckResult> KeepAsync(UpdateManifest manifest, CancellationToken ct)
    {
        if (!manifest.IsGenuine(paths.PublicKey))
        {
            logger.LogWarning("Mise à jour {Version} refusée : signature invalide", manifest.Version);
            return UpdateCheckResult.Rejected;
        }

        if (UpdateEndpoints.Read(paths) is { } current && current.Version == manifest.Version && current.Sha256 == manifest.Sha256)
            return UpdateCheckResult.UpToDate;

        Directory.CreateDirectory(paths.Directory);
        var final = Path.Combine(paths.Directory, manifest.FileName);
        var part = final + ".part";

        try
        {
            await client.DownloadUpdateAsync(manifest.FileName, part, ct);

            if (new FileInfo(part).Length != manifest.Size
                || !string.Equals(await UpdateManifest.HashFileAsync(part, ct), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Mise à jour {Version} refusée : fichier différent de celui signé", manifest.Version);
                return UpdateCheckResult.Rejected;
            }

            // The notice goes last: until it names the new file, the tills keep seeing the old one.
            File.Move(part, final, overwrite: true);
            var notice = paths.Manifest + ".part";
            await File.WriteAllTextAsync(notice, JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web), ct);
            File.Move(notice, paths.Manifest, overwrite: true);

            foreach (var old in Directory.EnumerateFiles(paths.Directory, UpdateManifest.FilePrefix + "*"))
            {
                if (!string.Equals(Path.GetFileName(old), manifest.FileName, StringComparison.Ordinal)) TryDelete(old);
            }

            logger.LogInformation("Mise à jour {Version} téléchargée", manifest.Version);
            return UpdateCheckResult.Downloaded;
        }
        catch (CloudBackupException e)
        {
            logger.LogInformation("Téléchargement de la mise à jour {Version} interrompu : {Message}", manifest.Version, e.Message);
            return UpdateCheckResult.Unreachable;
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>Removes the notice first, so a till never sees one naming a file being deleted.</summary>
    private void Clear()
    {
        if (!Directory.Exists(paths.Directory)) return;

        TryDelete(paths.Manifest);
        foreach (var file in Directory.EnumerateFiles(paths.Directory, UpdateManifest.FilePrefix + "*")) TryDelete(file);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException e)
        {
            logger.LogWarning(e, "Fichier de mise à jour non supprimé : {Path}", path);
        }
    }
}

/// <summary>Runs <see cref="UpdateDownloader"/> a few minutes after start, then every six hours.</summary>
public sealed class UpdateService(IServiceScopeFactory scopes, ILogger<UpdateService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<UpdateDownloader>().CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                logger.LogError(e, "Recherche de mise à jour interrompue");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
