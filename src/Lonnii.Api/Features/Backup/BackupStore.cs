using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Lonnii.Api.Features.Images;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Backup;

/// <summary>
/// Where the licence server keeps each workspace's backup: plain files under
/// <c>{data}/backups/{groupId}/</c>.
///
/// <list type="bullet">
/// <item><c>meta.json</c> - the current epoch and what the latest snapshot holds.</item>
/// <item><c>snapshots/snapshot-{utc}.db.gz</c> - the shop's data as an espace archive, gzipped.
/// Several are kept (see <see cref="Prune"/>): a corrupt or mistaken upload must never be the
/// only copy.</item>
/// <item><c>images/{folder}/{name}</c> - every photo ever uploaded. Filenames are never
/// reused (each upload carries a fresh timestamp), so a name present means its bytes are
/// final, and uploads can be skipped by name alone.</item>
/// </list>
///
/// Files rather than PostgreSQL on purpose. The live tables use integer keys (caisses,
/// charges, écritures...) that are unique per <em>database</em>, so two shops' rows cannot
/// share one table without being renumbered, and the shared database is also Lonnii
/// Business's. An opaque per-shop snapshot touches none of that.
/// </summary>
public sealed partial class BackupStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Largest snapshot accepted. A shop's data is megabytes; this is a ceiling against abuse.</summary>
    public const long MaxSnapshotBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Largest single photo. Stored photos are already capped at 1024px.</summary>
    public const long MaxImageBytes = 16L * 1024 * 1024;

    /// <summary>How many of the newest snapshots are always kept.</summary>
    private const int KeepNewest = 5;

    /// <summary>Beyond the newest, one snapshot per day is kept for this many days.</summary>
    private const int KeepDailyDays = 30;

    private const string SnapshotPrefix = "snapshot-";

    private readonly string _root;
    private readonly Dictionary<string, SemaphoreSlim> _locks = [];

    public BackupStore(string dataDirectory)
    {
        _root = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(_root);
    }

    private sealed record Meta(
        string Epoch, DateTime? SnapshotAt, int RecordCount, int ImageCount, long SizeBytes);

    // --- Locking ---------------------------------------------------------------------

    /// <summary>One writer at a time per workspace: two uploads racing would interleave a
    /// snapshot with its own pruning.</summary>
    public async Task<IDisposable> LockAsync(string groupId, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_locks)
        {
            if (!_locks.TryGetValue(groupId, out gate!))
                _locks[groupId] = gate = new SemaphoreSlim(1, 1);
        }

        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    // --- Paths -----------------------------------------------------------------------

    private string Dir(string groupId)
    {
        // Group ids are GUIDs we issued, but this is the one place a request value becomes a
        // path, so it is checked rather than trusted.
        if (!Guid.TryParse(groupId, out _))
            throw new ArgumentException("Identifiant d'espace invalide.", nameof(groupId));

        return Path.Combine(_root, groupId);
    }

    private string MetaPath(string groupId) => Path.Combine(Dir(groupId), "meta.json");
    private string SnapshotsDir(string groupId) => Path.Combine(Dir(groupId), "snapshots");

    // --- Epoch -----------------------------------------------------------------------

    private Meta? ReadMeta(string groupId)
    {
        var path = MetaPath(groupId);
        if (!File.Exists(path)) return null;

        try { return JsonSerializer.Deserialize<Meta>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    private void WriteMeta(string groupId, Meta meta)
    {
        Directory.CreateDirectory(Dir(groupId));
        var path = MetaPath(groupId);
        var temp = path + ".tmp";

        File.WriteAllText(temp, JsonSerializer.Serialize(meta, Json));
        File.Move(temp, path, overwrite: true);
    }

    public string? CurrentEpoch(string groupId) => ReadMeta(groupId)?.Epoch;

    /// <summary>
    /// Opens the line of backups for a host. A workspace with no backup yet starts one; one
    /// that has a backup only lets in a host that presents its current epoch - a freshly
    /// installed, empty machine does not, which is the whole point.
    /// </summary>
    /// <returns>The epoch, or null when the caller's epoch is not the current one.</returns>
    public (string? Epoch, bool Created) Begin(string groupId, string? presented)
    {
        var meta = ReadMeta(groupId);

        if (meta is null)
        {
            var fresh = NewEpoch();
            WriteMeta(groupId, new Meta(fresh, null, 0, 0, 0));
            return (fresh, true);
        }

        // A line that never received a snapshot holds nothing worth protecting, so a host that
        // lost its state before its first backup completed may simply pick it up again.
        return meta.SnapshotAt is null || string.Equals(meta.Epoch, presented, StringComparison.Ordinal)
            ? (meta.Epoch, false)
            : (null, false);
    }

    /// <summary>
    /// Starts a new line of backups, setting the old one aside rather than deleting it. Used
    /// when the shop deliberately starts again from an empty installation. Support can still
    /// put the retired folder back.
    /// </summary>
    public string Reset(string groupId)
    {
        var dir = Dir(groupId);

        if (Directory.Exists(dir))
        {
            var retired = Path.Combine(_root, $"{groupId}.retired-{DateTime.UtcNow:yyyyMMddHHmmssfff}");
            Directory.Move(dir, retired);
        }

        var fresh = NewEpoch();
        WriteMeta(groupId, new Meta(fresh, null, 0, 0, 0));
        return fresh;
    }

    private static string NewEpoch() => Guid.NewGuid().ToString("N");

    // --- Info ------------------------------------------------------------------------

    public BackupInfoDto Info(string groupId)
    {
        var meta = ReadMeta(groupId);
        if (meta is null || meta.SnapshotAt is null)
            return new BackupInfoDto(false, meta?.Epoch, null, 0, 0, 0);

        return new BackupInfoDto(true, meta.Epoch, meta.SnapshotAt, meta.RecordCount, meta.ImageCount, meta.SizeBytes);
    }

    // --- Images ----------------------------------------------------------------------

    /// <summary>The server-side path for a photo, or null when the folder or name is not one
    /// a client is allowed to name. The name must already be a bare file name.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,200}$")]
    private static partial System.Text.RegularExpressions.Regex SafeName();

    private string? ImagePath(string groupId, string folder, string name)
    {
        if (!ImageStorageService.Folders.All.Contains(folder)) return null;

        // Stored photos are named {entityId}_{ticks}.jpg|png, so anything outside that
        // alphabet is not a photo of ours and is refused outright.
        if (!SafeName().IsMatch(name)) return null;

        return Path.Combine(Dir(groupId), "images", folder, name);
    }

    public IReadOnlyList<BackupImageRef> MissingImages(string groupId, IEnumerable<BackupImageRef> wanted) =>
        wanted
            .Where(i => ImagePath(groupId, i.Folder, i.Name) is { } path && !File.Exists(path))
            .ToList();

    /// <summary>Stores one photo. False when the folder or name is not acceptable, or it is too large.</summary>
    public async Task<bool> SaveImageAsync(
        string groupId, string folder, string name, Stream content, CancellationToken ct)
    {
        if (ImagePath(groupId, folder, name) is not { } path) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                if (!await CopyCappedAsync(content, file, MaxImageBytes, ct)) return false;
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public FileStream? OpenImage(string groupId, string folder, string name) =>
        ImagePath(groupId, folder, name) is { } path && File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;

    // --- Snapshots -------------------------------------------------------------------

    /// <summary>
    /// Stores a snapshot and makes it the current one. The body must be gzip of a SQLite
    /// database - checked from the first bytes, so garbage or a truncated upload can never
    /// replace a good backup.
    /// </summary>
    /// <returns>The snapshot's time, or null when the body was refused.</returns>
    public async Task<DateTime?> SaveSnapshotAsync(
        string groupId, Stream body, int recordCount, int imageCount, CancellationToken ct)
    {
        var dir = SnapshotsDir(groupId);
        Directory.CreateDirectory(dir);

        var at = DateTime.UtcNow;
        var final = Path.Combine(dir, $"{SnapshotPrefix}{at:yyyyMMddHHmmssfff}.db.gz");
        var temp = final + ".tmp";

        try
        {
            long size;
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            {
                if (!await CopyCappedAsync(body, file, MaxSnapshotBytes, ct)) return null;
                size = file.Length;
            }

            if (!LooksLikeGzippedSqlite(temp)) return null;

            File.Move(temp, final, overwrite: true);

            var epoch = ReadMeta(groupId)?.Epoch ?? NewEpoch();
            WriteMeta(groupId, new Meta(epoch, at, recordCount, imageCount, size));

            Prune(groupId);
            return at;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>The newest snapshot's file and the time it was taken, or null when there is none.
    /// The time comes from the file name, which is how the store orders snapshots.</summary>
    public (string Path, DateTime At)? LatestSnapshotFile(string groupId)
    {
        var dir = SnapshotsDir(groupId);
        if (!Directory.Exists(dir)) return null;

        var latest = Directory.EnumerateFiles(dir, SnapshotPrefix + "*.db.gz").OrderDescending().FirstOrDefault();
        if (latest is null) return null;

        var stamp = System.IO.Path.GetFileName(latest)[SnapshotPrefix.Length..][..17];
        var at = DateTime.TryParseExact(stamp, "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : File.GetLastWriteTimeUtc(latest);

        return (latest, at);
    }

    /// <summary>The newest snapshot, opened for reading, or null when there is none.</summary>
    public FileStream? OpenLatestSnapshot(string groupId)
    {
        var dir = SnapshotsDir(groupId);
        if (!Directory.Exists(dir)) return null;

        var latest = Directory.EnumerateFiles(dir, SnapshotPrefix + "*.db.gz").OrderDescending().FirstOrDefault();

        return latest is null
            ? null
            : new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous);
    }

    /// <summary>
    /// Keeps the newest few snapshots and, behind them, one per day for a month. A shop that
    /// notices on day ten that something was wrong can still reach a copy from before it.
    /// </summary>
    private void Prune(string groupId)
    {
        var all = Directory.EnumerateFiles(SnapshotsDir(groupId), SnapshotPrefix + "*.db.gz")
            .OrderDescending()
            .ToList();

        var cutoff = DateTime.UtcNow.AddDays(-KeepDailyDays);
        var seenDays = new HashSet<string>();

        for (var i = KeepNewest; i < all.Count; i++)
        {
            var stamp = Path.GetFileName(all[i])[SnapshotPrefix.Length..][..8];

            var day = DateTime.TryParseExact(
                stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.MinValue;

            // Newest first, so the first one met for a day is that day's latest.
            if (day >= cutoff && seenDays.Add(stamp)) continue;

            try { File.Delete(all[i]); }
            catch (IOException) { /* an old snapshot that will not delete is not worth failing an upload */ }
        }
    }

    private static bool LooksLikeGzippedSqlite(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);

            var header = new byte[16];
            var read = 0;
            while (read < header.Length)
            {
                var n = gzip.Read(header, read, header.Length - read);
                if (n == 0) return false;
                read += n;
            }

            return Encoding.ASCII.GetString(header) == "SQLite format 3\0";
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static async Task<bool> CopyCappedAsync(Stream from, Stream to, long cap, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long total = 0;

        while (true)
        {
            var read = await from.ReadAsync(buffer, ct);
            if (read == 0) return true;

            total += read;
            if (total > cap) return false;

            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
