using System.Collections.Concurrent;
using Lonnii.Api.Features.Auth;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Print;

/// <summary>A computer with Lonnii open that can print for the shop's phones.</summary>
public sealed record PrintPosteDto(string DeviceId, string Name, bool IsHost, DateTime LastSeenAt);

/// <summary>What a phone asks: print this sale on that computer, as a ticket or an A4 page.</summary>
public sealed record PrintJobRequest(string? DeviceId, string? VenteId, string? Format);

/// <summary>A job as the printing computer receives it, and as the phone follows it.</summary>
public sealed record PrintJobDto(
    string Id, string VenteId, string Format, string RequestedBy, DateTime CreatedAt,
    string Status, string? Message);

/// <summary>The computer's answer once it has tried.</summary>
public sealed record PrintJobResult(bool Ok, string? Message);

/// <summary>A poste open at the shop, as the shop's host tells our server about it.</summary>
public sealed record PrintRelayPoste(string DeviceId, string Name, bool IsHost);

/// <summary>The shop's host, every few seconds: these postes are open; anything to print?</summary>
public sealed record PrintRelayPoll(List<PrintRelayPoste>? Postes);

/// <summary>A job a phone left on our server, for the shop's host to hand to the named poste.</summary>
public sealed record PrintRelayJob(string Id, string DeviceId, string VenteId, string Format, string RequestedBy);

public sealed record PrintRelayJobs(List<PrintRelayJob> Jobs);

/// <summary>
/// Phones printing on a shop computer's printer.
///
/// <para>
/// Lives in the shop host's memory only: a print job is worth a few seconds, so nothing is stored
/// and a restart simply forgets jobs nobody picked up. Each desktop app asks
/// <c>GET /api/print/jobs/next</c> every few seconds while open; that ask is also how the host knows
/// which postes are open right now, so a closed or crashed till drops off the phone's list within
/// <see cref="OpenFor"/>.
/// </para>
/// <para>
/// The printing computer fetches the sale itself and prints it with the desktop's own receipt
/// layout and settings: the phone sends only which sale and which format, never a document, so a
/// phone cannot make a till print anything but a real sale of this espace.
/// </para>
/// <para>
/// On our server (a phone signed in online, in a remote session) the tills cannot be reached, so
/// the shop's host relays: it tells us every few seconds which postes are open
/// (<see cref="Relayed"/>), collects the jobs phones left here (<see cref="TakeRelayed"/>), hands
/// them to its own queue, and reports each result back (<see cref="FinishRelayed"/>). Nothing
/// reaches into the shop: the host only ever calls out, as it already does for backups.
/// </para>
/// </summary>
public sealed class PrintQueue
{
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan JobLife = TimeSpan.FromMinutes(5);

    private sealed record Poste(string GroupId, string DeviceId, string Name, bool IsHost, DateTime LastSeenAt);

    private sealed class Job
    {
        public required string Id { get; init; }
        public required string GroupId { get; init; }
        public required string DeviceId { get; init; }
        public required string VenteId { get; init; }
        public required string Format { get; init; }
        public required string RequestedBy { get; init; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public string Status { get; set; } = "pending"; // pending | sent | done | failed
        public string? Message { get; set; }

        public PrintJobDto ToDto() => new(Id, VenteId, Format, RequestedBy, CreatedAt, Status, Message);
    }

    private readonly ConcurrentDictionary<(string, string), Poste> _postes = new();
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public void Seen(string groupId, string deviceId, string name, bool isHost) =>
        _postes[(groupId, deviceId)] = new Poste(groupId, deviceId, name, isHost, DateTime.UtcNow);

    /// <summary>
    /// The shop's host's list of its open postes, as of now. Postes it no longer names drop off
    /// within <see cref="OpenFor"/>, like a till that stops polling.
    /// </summary>
    public void Relayed(string groupId, IEnumerable<PrintRelayPoste> postes)
    {
        foreach (var p in postes.Take(MaxRelayedPostes))
        {
            if (string.IsNullOrWhiteSpace(p.DeviceId) || p.DeviceId.Length > 128) continue;
            var name = string.IsNullOrWhiteSpace(p.Name) ? "Poste" : p.Name.Trim();
            Seen(groupId, p.DeviceId, name[..Math.Min(name.Length, 60)], p.IsHost);
        }
    }

    private const int MaxRelayedPostes = 50;

    /// <summary>Every job waiting for this shop, whichever poste it is for, marked as handed out.</summary>
    public IReadOnlyList<PrintRelayJob> TakeRelayed(string groupId)
    {
        Sweep();
        var taken = new List<PrintRelayJob>();
        foreach (var job in _jobs.Values.Where(j => j.GroupId == groupId && j.Status == "pending").OrderBy(j => j.CreatedAt))
        {
            lock (job)
            {
                if (job.Status != "pending") continue;
                job.Status = "sent";
            }
            taken.Add(new PrintRelayJob(job.Id, job.DeviceId, job.VenteId, job.Format, job.RequestedBy));
        }
        return taken;
    }

    /// <summary>The shop's host reporting for one of its postes: the shop is the check, not the device.</summary>
    public bool FinishRelayed(string groupId, string id, bool ok, string? message) =>
        _jobs.TryGetValue(id, out var j) && j.GroupId == groupId && Finish(groupId, j.DeviceId, id, ok, message);

    public IReadOnlyList<PrintPosteDto> Open(string groupId)
    {
        var since = DateTime.UtcNow - OpenFor;
        return _postes.Values
            .Where(p => p.GroupId == groupId && p.LastSeenAt >= since)
            .OrderByDescending(p => p.IsHost).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PrintPosteDto(p.DeviceId, p.Name, p.IsHost, p.LastSeenAt))
            .ToList();
    }

    public bool IsOpen(string groupId, string deviceId) =>
        _postes.TryGetValue((groupId, deviceId), out var p) && p.LastSeenAt >= DateTime.UtcNow - OpenFor;

    public PrintJobDto Add(string groupId, string deviceId, string venteId, string format, string requestedBy)
    {
        Sweep();
        var job = new Job
        {
            Id = Guid.NewGuid().ToString("N"), GroupId = groupId, DeviceId = deviceId,
            VenteId = venteId, Format = format, RequestedBy = requestedBy,
        };
        _jobs[job.Id] = job;
        return job.ToDto();
    }

    /// <summary>The oldest job waiting for this computer, marked as handed out.</summary>
    public PrintJobDto? Next(string groupId, string deviceId)
    {
        Sweep();
        var job = _jobs.Values
            .Where(j => j.GroupId == groupId && j.DeviceId == deviceId && j.Status == "pending")
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefault();
        if (job is null) return null;
        lock (job)
        {
            if (job.Status != "pending") return null;
            job.Status = "sent";
        }
        return job.ToDto();
    }

    public PrintJobDto? Get(string groupId, string id) =>
        _jobs.TryGetValue(id, out var j) && j.GroupId == groupId ? j.ToDto() : null;

    /// <summary>Only the computer the job was for may report on it.</summary>
    public bool Finish(string groupId, string deviceId, string id, bool ok, string? message)
    {
        if (!_jobs.TryGetValue(id, out var j) || j.GroupId != groupId || j.DeviceId != deviceId) return false;
        lock (j)
        {
            j.Status = ok ? "done" : "failed";
            j.Message = message is { Length: > 300 } ? message[..300] : message;
        }
        return true;
    }

    private void Sweep()
    {
        var old = DateTime.UtcNow - JobLife;
        foreach (var (id, job) in _jobs)
            if (job.CreatedAt < old) _jobs.TryRemove(id, out _);
    }
}

public static class PrintEndpoints
{
    public static readonly string[] Formats = ["ticket", "a4"];

    /// <summary>
    /// The routes a phone uses (list postes, send a job, follow it): the only ones a remote session
    /// may reach. Polling for jobs and reporting results are for a poste at the shop, never for an
    /// account signed in from afar, which could otherwise pose as a till.
    /// </summary>
    public static bool IsPhoneRoute(string method, PathString path)
    {
        if (!path.StartsWithSegments("/api/print", out var rest)) return false;
        var parts = rest.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return (HttpMethods.IsGet(method), HttpMethods.IsPost(method), parts) switch
        {
            (true, _, ["postes"]) => true,
            (_, true, ["jobs"]) => true,
            (true, _, ["jobs", var id]) => id != "next",
            _ => false,
        };
    }

    public static void MapPrintEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/print").WithTags("Print");

        // Phone side.
        group.MapGet("/postes", (GroupScope scope, PrintQueue queue) => Results.Ok(queue.Open(scope.GroupId))).RequireGroupScope();
        group.MapPost("/jobs", (PrintJobRequest request, GroupScope scope, PrintQueue queue) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.VenteId))
                return Results.BadRequest(new ApiError("Poste et vente obligatoires."));
            var format = Formats.Contains(request.Format) ? request.Format! : "ticket";
            if (!queue.IsOpen(scope.GroupId, request.DeviceId))
                return Results.Conflict(new ApiError("Ce poste n'est plus ouvert. Choisissez-en un autre."));
            return Results.Ok(queue.Add(scope.GroupId, request.DeviceId, request.VenteId, format, scope.UserId));
        }).RequireGroupScope();
        group.MapGet("/jobs/{id}", (string id, GroupScope scope, PrintQueue queue) =>
            queue.Get(scope.GroupId, id) is { } job ? Results.Ok(job) : Results.NotFound()).RequireGroupScope();

        // Desktop side: the open app asks every few seconds, which also keeps it on the list.
        group.MapGet("/jobs/next", (HttpContext http, GroupScope scope, PrintQueue queue, string? name) =>
        {
            var deviceId = http.Request.Headers["x-device-id"].ToString();
            if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 128)
                return Results.BadRequest(new ApiError("Identifiant du poste manquant."));
            var isHost = deviceId == Infrastructure.HostMachineIdentity.DeviceId;
            var label = string.IsNullOrWhiteSpace(name) ? "Poste" : name.Trim()[..Math.Min(name.Trim().Length, 60)];
            queue.Seen(scope.GroupId, deviceId, label, isHost);
            return queue.Next(scope.GroupId, deviceId) is { } job ? Results.Ok(job) : Results.NoContent();
        }).RequireGroupScope();
        group.MapPost("/jobs/{id}/result", (string id, PrintJobResult result, HttpContext http, GroupScope scope, PrintQueue queue) =>
            queue.Finish(scope.GroupId, http.Request.Headers["x-device-id"].ToString(), id, result.Ok, result.Message)
                ? Results.Ok(new { success = true }) : Results.NotFound()).RequireGroupScope();
    }
}
