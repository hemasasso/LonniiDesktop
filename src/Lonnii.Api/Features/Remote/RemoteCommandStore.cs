using System.Text.Json;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Remote;

/// <summary>
/// The queue of changes administrators have asked for from afar, per shop, waiting for the shop's
/// host to collect and apply them.
///
/// <para>
/// A file per shop (<c>remote-commands/{groupId}.json</c>) rather than a table: it holds a
/// handful of small rows that are meaningful only until the host has acted on them, and the live
/// PostgreSQL is shared with Lonnii Business and changed by hand-written scripts only. Every
/// operation holds a per-shop lock and writes through a temp file, so a crash cannot leave a
/// half-written queue.
/// </para>
/// </summary>
public sealed class RemoteCommandStore
{
    /// <summary>How long a request waits for a shop that stays offline before it is dropped.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private const int KeepPerShop = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>How long an uploaded photo waits for the request that should carry it.</summary>
    private static readonly TimeSpan UnclaimedPhotoLifetime = TimeSpan.FromDays(1);

    private readonly string _root;
    private readonly Dictionary<string, object> _locks = [];

    public RemoteCommandStore(string dataDirectory)
    {
        _root = Path.Combine(dataDirectory, "remote-commands");
        Directory.CreateDirectory(_root);
    }

    // --- Photos travelling with a product request ---------------------------------------

    /// <summary>
    /// Keeps a photo sent from afar until the shop's host collects the request it goes with
    /// (<c>remote-commands/photos/{groupId}/{photoId}</c>). The bytes are stored as sent: the host
    /// decodes, resizes and re-encodes them exactly as it does a photo picked at the shop.
    /// </summary>
    public string SavePhoto(string groupId, byte[] content)
    {
        var folder = PhotoFolder(groupId);
        Directory.CreateDirectory(folder);

        lock (LockFor(groupId))
        {
            var waiting = Read(groupId)
                .Where(c => c.Status == RemoteCommandStatuses.Pending && c.PhotoId is not null)
                .Select(c => c.PhotoId!)
                .ToHashSet();
            SweepUnclaimedPhotos(folder, waiting);
        }

        var id = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(folder, id), content);
        return id;
    }

    public bool HasPhoto(string groupId, string photoId) =>
        PhotoPath(groupId, photoId) is { } path && File.Exists(path);

    /// <summary>The photo a still-pending request carries, for the shop's host to download.</summary>
    public Stream? OpenCommandPhoto(string groupId, string commandId)
    {
        string? photoId;
        lock (LockFor(groupId))
            photoId = Read(groupId).FirstOrDefault(c => c.Id == commandId && c.Status == RemoteCommandStatuses.Pending)?.PhotoId;

        return photoId is not null && PhotoPath(groupId, photoId) is { } path && File.Exists(path)
            ? File.OpenRead(path)
            : null;
    }

    private string PhotoFolder(string groupId) => Path.Combine(_root, "photos", Path.GetFileNameWithoutExtension(PathFor(groupId)));

    /// <summary>Null for anything that is not one of our own ids, so a request value never becomes an arbitrary path.</summary>
    private string? PhotoPath(string groupId, string photoId) =>
        Guid.TryParseExact(photoId, "N", out _) ? Path.Combine(PhotoFolder(groupId), photoId) : null;

    private void DeletePhoto(string groupId, string? photoId)
    {
        if (photoId is null || PhotoPath(groupId, photoId) is not { } path) return;
        try { File.Delete(path); }
        catch (IOException) { }
    }

    /// <summary>A photo uploaded but never sent with a request is not kept. One a request is still
    /// waiting with stays, however long the shop is offline.</summary>
    private static void SweepUnclaimedPhotos(string folder, HashSet<string> waiting)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (waiting.Contains(Path.GetFileName(file))) continue;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) <= UnclaimedPhotoLifetime) continue;
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }

    public RemoteCommandDto Enqueue(string groupId, RemoteCommandDto command)
    {
        lock (LockFor(groupId))
        {
            var all = Read(groupId);
            all.Add(command);
            Write(groupId, all.OrderByDescending(c => c.RequestedAt).Take(KeepPerShop).ToList());
            return command;
        }
    }

    /// <summary>What the shop's host still has to do, oldest first. A request older than
    /// <see cref="Lifetime"/> is marked expired instead of being handed out.</summary>
    public IReadOnlyList<RemoteCommandDto> Pending(string groupId)
    {
        lock (LockFor(groupId))
        {
            var all = Read(groupId);
            var now = DateTime.UtcNow;
            var changed = false;

            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].Status == RemoteCommandStatuses.Pending && now - all[i].RequestedAt > Lifetime)
                {
                    all[i] = all[i] with
                    {
                        Status = RemoteCommandStatuses.Expired,
                        PasswordHash = null,
                        Message = "La boutique n'est pas restée connectée à temps : demande abandonnée.",
                    };
                    DeletePhoto(groupId, all[i].PhotoId);
                    changed = true;
                }
            }

            if (changed) Write(groupId, all);

            return all.Where(c => c.Status == RemoteCommandStatuses.Pending).OrderBy(c => c.RequestedAt).ToList();
        }
    }

    /// <summary>Records what the host did with a request. False when it is unknown or already settled.</summary>
    public bool Complete(string groupId, string id, string status, string? message)
    {
        if (status is not (RemoteCommandStatuses.Applied or RemoteCommandStatuses.Failed)) return false;

        lock (LockFor(groupId))
        {
            var all = Read(groupId);
            var index = all.FindIndex(c => c.Id == id && c.Status == RemoteCommandStatuses.Pending);
            if (index < 0) return false;

            // A new member's password hash was only ever for the shop: once it has answered, it goes.
            all[index] = all[index] with { Status = status, Message = message, AppliedAt = DateTime.UtcNow, PasswordHash = null };
            Write(groupId, all);

            // The shop has dealt with it; the photo, if any, is now in the shop's own data.
            DeletePhoto(groupId, all[index].PhotoId);
            return true;
        }
    }

    /// <summary>The most recent requests, newest first - what a screen shows as "waiting for the shop".</summary>
    public IReadOnlyList<RemoteCommandDto> Recent(string groupId, int take = 30)
    {
        lock (LockFor(groupId))
            return Read(groupId).OrderByDescending(c => c.RequestedAt).Take(take)
                .Select(c => c with { PasswordHash = null }).ToList();
    }

    // --- Storage ---------------------------------------------------------------------

    private object LockFor(string groupId)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(groupId, out var gate)) _locks[groupId] = gate = new object();
            return gate;
        }
    }

    private string PathFor(string groupId)
    {
        // The one place a request value becomes a path, so it is checked rather than trusted.
        if (!Guid.TryParse(groupId, out _))
            throw new ArgumentException("Identifiant d'espace invalide.", nameof(groupId));

        return Path.Combine(_root, groupId + ".json");
    }

    private List<RemoteCommandDto> Read(string groupId)
    {
        var path = PathFor(groupId);
        if (!File.Exists(path)) return [];

        try { return JsonSerializer.Deserialize<List<RemoteCommandDto>>(File.ReadAllText(path)) ?? []; }
        catch (JsonException) { return []; }
    }

    private void Write(string groupId, List<RemoteCommandDto> commands)
    {
        var path = PathFor(groupId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(commands, Json));
        File.Move(temp, path, overwrite: true);
    }
}
