using System.IO.Compression;
using Lonnii.Api.Features.Backup;
using Lonnii.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Remote;

/// <summary>A shop's working copy: where it is, and when the snapshot it came from was taken.</summary>
public sealed record ReplicaInfo(string Path, DateTime SnapshotAt);

/// <summary>
/// Keeps, for each online shop, a ready-to-read copy of its newest cloud-backup snapshot.
///
/// <para>
/// The host uploads its whole database to OCI every few minutes (<see cref="BackupStore"/>). A
/// snapshot is a gzipped SQLite file in the Lonnii schema, so the same API code that serves a
/// host can serve it: this class unpacks it to <c>replicas/{groupId}/{stamp}.db</c> and brings it
/// up to the current schema, so a shop whose host runs an older version still reads correctly.
/// A new snapshot gets a new file name, so a request still reading the previous copy is never
/// pulled out from under it.
/// </para>
///
/// <para>
/// The copy is OCI's own scratch file, rebuilt from the next snapshot, so it is not treated as
/// precious. Requests against it are limited to reads by <see cref="RemoteRoutingMiddleware"/>.
/// </para>
/// </summary>
public sealed class ReplicaStore
{
    private readonly BackupStore _backups;
    private readonly string _root;
    private readonly Dictionary<string, SemaphoreSlim> _gates = [];

    public ReplicaStore(BackupStore backups, string dataDirectory)
    {
        _backups = backups;
        _root = Path.Combine(dataDirectory, "replicas");
        Directory.CreateDirectory(_root);
    }

    public static DbContextOptions<LonniiDbContext> OptionsFor(string path) =>
        new DbContextOptionsBuilder<LonniiDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True")
            .Options;

    /// <summary>The shop's current copy, unpacked if the newest snapshot has not been yet; null
    /// when the shop has never uploaded one.</summary>
    public async Task<ReplicaInfo?> EnsureAsync(string groupId, CancellationToken ct)
    {
        if (!Guid.TryParse(groupId, out _)) return null;

        var latest = _backups.LatestSnapshotFile(groupId);
        if (latest is null) return null;

        var dir = Path.Combine(_root, groupId);
        var target = Path.Combine(dir, $"{latest.Value.At:yyyyMMddHHmmssfff}.db");
        if (File.Exists(target)) return new ReplicaInfo(target, latest.Value.At);

        SemaphoreSlim gate;
        lock (_gates)
        {
            if (!_gates.TryGetValue(groupId, out gate!))
                _gates[groupId] = gate = new SemaphoreSlim(1, 1);
        }

        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target)) return new ReplicaInfo(target, latest.Value.At);

            Directory.CreateDirectory(dir);
            var temp = target + ".tmp";

            try
            {
                await using (var source = File.OpenRead(latest.Value.Path))
                await using (var gzip = new GZipStream(source, CompressionMode.Decompress))
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
                {
                    await gzip.CopyToAsync(output, ct);
                }

                // Same migrations a host runs: brings the copy to the schema this server's model
                // expects, whatever version the host that made the snapshot was on.
                await using (var db = new LonniiDbContext(OptionsFor(temp)))
                {
                    await db.Database.MigrateAsync(ct);
                }

                SqliteConnection.ClearAllPools();
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) TryDelete(temp);
            }

            // Older copies: possibly still in use by a request that started before this one.
            foreach (var old in Directory.EnumerateFiles(dir, "*.db").Where(f => f != target))
                TryDelete(old);

            return new ReplicaInfo(target, latest.Value.At);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* in use or locked: the next refresh clears it */ }
        catch (UnauthorizedAccessException) { }
    }
}
