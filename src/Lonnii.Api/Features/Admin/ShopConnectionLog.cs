using System.Text.Json;

namespace Lonnii.Api.Features.Admin;

/// <summary>How someone reached an espace through our server. One of <see cref="ShopConnectionKinds"/>.</summary>
public static class ShopConnectionKinds
{
    /// <summary>Away from the shop, reading the shop's copy on our server (a phone or a computer).</summary>
    public const string Remote = "remote";

    /// <summary>A phone-only shop: our server is its host, the phone works on it directly.</summary>
    public const string PhoneOnly = "phone_only";

    public static string Label(string kind) => kind switch
    {
        Remote => "À distance",
        PhoneOnly => "Téléphone (boutique hébergée ici)",
        _ => kind,
    };
}

/// <summary>One sign-in to an espace through our server, as kept for the dashboard.</summary>
public sealed record StoredShopConnection(
    string Kind,
    DateTime At,
    string? UserId,
    string? UserEmail,
    string? UserName,
    string? DeviceId,
    string? DeviceName,
    string? Platform,
    string? IpAddress,
    string? AppVersion,
    string? UserAgent = null);

/// <summary>
/// Keeps, per espace, who signed in to it through our server: phones and computers away from the shop,
/// and phones of a phone-only shop. The devices table only knows the machines bound at the shop, so until
/// now none of these appeared in the dashboard. Same shape as <see cref="Licensing.SecurityEventStore"/>:
/// one JSON-lines file per espace under the data directory, no table on the shared database.
/// </summary>
public sealed class ShopConnectionLog(string dataDirectory)
{
    /// <summary>The same person on the same device again within this window is the app re-opening its
    /// session (foreground, network change), not a new visit.</summary>
    private static readonly TimeSpan Dedupe = TimeSpan.FromMinutes(10);

    /// <summary>A file is trimmed to this many lines once it grows past twice that.</summary>
    private const int KeepLines = 2000;

    private readonly string _directory = Path.Combine(dataDirectory, "connections");
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Only an espace id names a file: anything else could climb out of the folder.</summary>
    private string? PathFor(string groupId) =>
        Guid.TryParse(groupId, out var id) ? Path.Combine(_directory, id.ToString("D") + ".jsonl") : null;

    public void Record(string groupId, StoredShopConnection connection)
    {
        var path = PathFor(groupId);
        if (path is null) return;

        lock (_gate)
        {
            var existing = File.Exists(path) ? ReadFile(path) : [];
            var last = existing.LastOrDefault(e =>
                e.Kind == connection.Kind && e.UserId == connection.UserId && e.DeviceId == connection.DeviceId);
            if (last is not null && connection.At - last.At < Dedupe) return;

            try
            {
                Directory.CreateDirectory(_directory);
                if (existing.Count >= KeepLines * 2)
                {
                    File.WriteAllLines(path, existing.Skip(existing.Count - KeepLines).Select(e => JsonSerializer.Serialize(e, Json)));
                }
                File.AppendAllText(path, JsonSerializer.Serialize(connection, Json) + Environment.NewLine);
            }
            catch (IOException)
            {
                // A full disk must not stop anyone signing in: the dashboard just misses this one.
            }
        }
    }

    /// <summary>The espace's connections since <paramref name="since"/>, newest first.</summary>
    public IReadOnlyList<StoredShopConnection> Read(string groupId, DateTime since)
    {
        var path = PathFor(groupId);
        if (path is null || !File.Exists(path)) return [];
        lock (_gate)
            return ReadFile(path).Where(e => e.At >= since).OrderByDescending(e => e.At).ToList();
    }

    /// <summary>Every espace's connections since <paramref name="since"/>, newest first, with the espace id.</summary>
    public IReadOnlyList<(string GroupId, StoredShopConnection Connection)> ReadAll(DateTime since, int max)
    {
        if (!Directory.Exists(_directory)) return [];
        lock (_gate)
        {
            return Directory.EnumerateFiles(_directory, "*.jsonl")
                .SelectMany(path =>
                {
                    var groupId = Path.GetFileNameWithoutExtension(path);
                    return ReadFile(path).Where(e => e.At >= since).Select(e => (groupId, e));
                })
                .OrderByDescending(x => x.e.At)
                .Take(max)
                .ToList();
        }
    }

    private static List<StoredShopConnection> ReadFile(string path)
    {
        var entries = new List<StoredShopConnection>();
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<StoredShopConnection>(line, Json) is { } entry) entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A line cut short by a crash mid-write: skipped, the rest still reads.
                }
            }
        }
        catch (IOException)
        {
        }
        return entries;
    }

    /// <summary>What kind of device sent the request: the app says so in a header; older phone apps are
    /// told apart by what their HTTP stack calls itself, and the desktop app sends no user agent at all.</summary>
    public static string PlatformOf(HttpRequest request)
    {
        var declared = request.Headers["x-device-platform"].ToString().Trim().ToLowerInvariant();
        if (declared is "android" or "ios" or "windows") return declared;

        var agent = request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(agent)) return "windows";
        if (agent.Contains("okhttp", StringComparison.OrdinalIgnoreCase)
            || agent.Contains("Dalvik", StringComparison.OrdinalIgnoreCase)
            || agent.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "android";
        if (agent.Contains("CFNetwork", StringComparison.OrdinalIgnoreCase)
            || agent.Contains("Darwin", StringComparison.OrdinalIgnoreCase)
            || agent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || agent.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "ios";
        return "unknown";
    }

    /// <summary>Builds the entry for the request that just opened a session.</summary>
    public static StoredShopConnection From(HttpRequest request, string kind, string? userId, string? email, string? name)
    {
        static string? Trim(string? value, int max)
        {
            value = value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value.Length > max ? value[..max] : value;
        }

        return new StoredShopConnection(
            kind,
            DateTime.UtcNow,
            userId,
            Trim(email, 200),
            Trim(name, 120),
            Trim(request.Headers["x-device-id"].ToString(), 80),
            Trim(request.Headers["x-device-name"].ToString(), 80),
            PlatformOf(request),
            request.HttpContext.Connection.RemoteIpAddress?.ToString(),
            Trim(request.Headers["x-app-version"].ToString(), 20),
            // Kept as sent, so a device the guess above calls "unknown" can still be identified.
            Trim(request.Headers.UserAgent.ToString(), 200));
    }
}
