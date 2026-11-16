using System.Text.Json;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Licensing;

/// <summary>
/// The shop computer's side of SecurityEventKinds: what it noticed, kept in
/// <c>security-events.jsonl</c> beside its data until the next check-in carries it to our server.
///
/// <para>A file, not a table: nothing here is the shop's business data, and the schema stays as it
/// is. Deleting the file hides what was not sent yet - but the attempt itself usually leaves a trace
/// our server sees anyway (a shop that goes quiet, an old licence, a refused machine).</para>
/// </summary>
public sealed class HostSecurityLog(string dataDirectory)
{
    private const int MaxDetail = 300;
    private const int MaxPending = 200;

    private readonly string _path = Path.Combine(dataDirectory, "security-events.jsonl");
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What was already noted today, so a condition seen on every request (a clock set back)
    /// does not read the file every time.</summary>
    private readonly HashSet<string> _notedToday = new(StringComparer.Ordinal);
    private DateTime _notedDay;

    private sealed record Entry(string GroupId, string Kind, DateTime At, string? Detail);

    /// <summary>Records <paramref name="kind"/> for <paramref name="groupId"/>, once a day.</summary>
    public void Record(string groupId, string kind, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var now = DateTime.UtcNow;
        if (detail is { Length: > MaxDetail }) detail = detail[..MaxDetail];

        lock (_gate)
        {
            if (_notedDay != now.Date)
            {
                _notedDay = now.Date;
                _notedToday.Clear();
            }
            if (!_notedToday.Add($"{groupId}|{kind}")) return;

            var entries = ReadAll();
            if (entries.Any(e => e.GroupId == groupId && e.Kind == kind && e.At.Date == now.Date)) return;
            if (entries.Count >= MaxPending) return;
            entries.Add(new Entry(groupId, kind, now, detail));
            WriteAll(entries);
        }
    }

    /// <summary>What is waiting to be sent for <paramref name="groupId"/>.</summary>
    public IReadOnlyList<SecurityEventDto> Pending(string groupId)
    {
        lock (_gate)
            return ReadAll().Where(e => e.GroupId == groupId).Select(e => new SecurityEventDto(e.Kind, e.At, e.Detail)).ToList();
    }

    /// <summary>Forgets <paramref name="sent"/>, once our server has received them.</summary>
    public void Sent(string groupId, IReadOnlyCollection<SecurityEventDto> sent)
    {
        if (sent.Count == 0) return;
        lock (_gate)
        {
            var entries = ReadAll();
            var removed = entries.RemoveAll(e => e.GroupId == groupId && sent.Any(s => s.Kind == e.Kind && s.At == e.At));
            if (removed > 0) WriteAll(entries);
        }
    }

    private List<Entry> ReadAll()
    {
        var entries = new List<Entry>();
        try
        {
            if (!File.Exists(_path)) return entries;
            foreach (var line in File.ReadAllLines(_path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<Entry>(line, Json) is { } entry) entries.Add(entry);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        return entries;
    }

    private void WriteAll(List<Entry> entries)
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllLines(temp, entries.Select(e => JsonSerializer.Serialize(e, Json)));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>One attempt as our server keeps it.</summary>
public sealed record StoredSecurityEvent(
    string Kind, DateTime At, DateTime ReceivedAt, string? Detail, string? DeviceId, string? IpAddress, string? AppVersion,
    bool FromServer);

/// <summary>
/// Our server's record of SecurityEventKinds per espace: <c>{data}/security/{groupId}.jsonl</c>, one
/// line per attempt, read by the dashboard and the morning report. Appended to by licence refreshes,
/// whatever their outcome - a refused machine's report is the most interesting one.
/// </summary>
public sealed class SecurityEventStore(string dataDirectory)
{
    private const int MaxPerRequest = 50;
    private const int MaxDetail = 300;

    private readonly string _directory = Path.Combine(dataDirectory, "security");
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Lines already written, per espace, so a report sent twice (an answer lost on the way
    /// back) is kept once. Loaded from the file the first time an espace is written to.</summary>
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.Ordinal);

    /// <summary>Only an espace id names a file: anything else could climb out of the folder.</summary>
    private string? PathFor(string groupId) =>
        Guid.TryParse(groupId, out var id) ? Path.Combine(_directory, id.ToString("D") + ".jsonl") : null;

    private static string Key(StoredSecurityEvent e) => $"{e.Kind}|{e.At:o}|{e.DeviceId}|{e.FromServer}";

    /// <summary>Keeps what a shop's computer reported.</summary>
    public void AddReported(string groupId, IReadOnlyList<SecurityEventDto>? events, string? deviceId, string? ip, string? appVersion)
    {
        if (events is null || events.Count == 0) return;
        var now = DateTime.UtcNow;
        Append(groupId, events.Take(MaxPerRequest).Where(e => !string.IsNullOrWhiteSpace(e.Kind)).Select(e => new StoredSecurityEvent(
            Trim(e.Kind, 40)!, e.At, now, Trim(e.Detail, MaxDetail), Trim(deviceId, 80), ip, Trim(appVersion, 20), FromServer: false)));
    }

    /// <summary>Keeps what our server noticed itself; once a day per kind and machine.</summary>
    public void AddFromServer(string groupId, string kind, string? deviceId, string? ip, string? appVersion, string? detail = null)
    {
        var now = DateTime.UtcNow;
        Append(groupId, [new StoredSecurityEvent(kind, now.Date, now, Trim(detail, MaxDetail), Trim(deviceId, 80), ip, Trim(appVersion, 20), FromServer: true)]);
    }

    /// <summary>The espace's attempts since <paramref name="since"/>, newest first.</summary>
    public IReadOnlyList<StoredSecurityEvent> Read(string groupId, DateTime since)
    {
        var path = PathFor(groupId);
        if (path is null || !File.Exists(path)) return [];
        lock (_gate)
            return ReadFile(path).Where(e => e.ReceivedAt >= since).OrderByDescending(e => e.ReceivedAt).ToList();
    }

    private void Append(string groupId, IEnumerable<StoredSecurityEvent> events)
    {
        var path = PathFor(groupId);
        if (path is null) return;

        lock (_gate)
        {
            if (!_seen.TryGetValue(groupId, out var seen))
                _seen[groupId] = seen = ReadFile(path).Select(Key).ToHashSet(StringComparer.Ordinal);

            var lines = new List<string>();
            foreach (var e in events)
                if (seen.Add(Key(e))) lines.Add(JsonSerializer.Serialize(e, Json));
            if (lines.Count == 0) return;

            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllLines(path, lines);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static List<StoredSecurityEvent> ReadFile(string path)
    {
        var events = new List<StoredSecurityEvent>();
        try
        {
            if (!File.Exists(path)) return events;
            foreach (var line in File.ReadAllLines(path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<StoredSecurityEvent>(line, Json) is { } e) events.Add(e);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        return events;
    }

    private static string? Trim(string? text, int max) =>
        text is null ? null : text.Length > max ? text[..max] : text;
}
