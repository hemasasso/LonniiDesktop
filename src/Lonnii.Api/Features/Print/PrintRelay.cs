using System.Collections.Concurrent;
using Lonnii.Api.Features.Backup;
using Lonnii.Api.Features.Remote;
using Lonnii.Data;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Print;

/// <summary>
/// The shop host's side of printing for phones signed in online: tells our server which postes are
/// open here, takes the jobs phones left there into this host's own <see cref="PrintQueue"/> (the
/// tills print them exactly like a job sent over the Wi-Fi), and reports each result back.
///
/// <para>
/// Calls out only, with the machine token the backup already uses; nothing on our server can reach
/// the shop. It asks only while a poste is open or a relayed job is unfinished, so a closed shop
/// costs our server nothing.
/// </para>
/// </summary>
public sealed class PrintRelay(
    LonniiDbContext db,
    CloudBackupClient client,
    PrintQueue queue,
    RemoteCommandApplier commands,
    ILogger<PrintRelay> logger)
{
    /// <summary>Jobs taken from our server and not yet reported, per shop.</summary>
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> InFlight = new();

    public async Task PassAsync(string groupId, CancellationToken ct)
    {
        var open = queue.Open(groupId);
        var inFlight = InFlight.GetOrAdd(groupId, _ => new());
        if (open.Count == 0 && inFlight.IsEmpty) return;

        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        var state = await db.CloudBackupStates.AsNoTracking().FirstOrDefaultAsync(s => s.GroupId == groupId, ct);
        if (CloudBackupRunner.WhyNotApplicable(groupe, state) is not null) return;

        client.For(groupe!.LicenceServerUrl!, groupId, state!.DeviceId!, state.DeviceToken!, state.Epoch);

        try
        {
            await ReportFinishedAsync(groupId, inFlight, ct);

            var jobs = await client.PollPrintAsync(
                open.Select(p => new PrintRelayPoste(p.DeviceId, p.Name, p.IsHost)), ct);
            if (jobs.Count == 0) return;

            // A sale made on the phone online reaches this shop as a queued request: apply those first,
            // so the poste finds the sale it is asked to print.
            var found = await ResolveAsync(groupId, jobs, ct);
            if (jobs.Any(j => !found.ContainsKey(j.Id)))
            {
                await commands.PollAsync(groupId, ct);
                found = await ResolveAsync(groupId, jobs, ct);
            }

            foreach (var job in jobs)
            {
                if (!found.TryGetValue(job.Id, out var venteId))
                {
                    await client.ReportPrintAsync(job.Id, new PrintJobResult(false,
                        "Vente introuvable à la boutique : réessayez dans un instant."), ct);
                    continue;
                }
                if (!queue.IsOpen(groupId, job.DeviceId))
                {
                    await client.ReportPrintAsync(job.Id, new PrintJobResult(false,
                        "Ce poste vient de se fermer. Choisissez-en un autre."), ct);
                    continue;
                }
                queue.Add(groupId, job.DeviceId, venteId, job.Format, job.RequestedBy, job.Id);
                inFlight[job.Id] = 0;
            }
        }
        catch (CloudBackupException e)
        {
            logger.LogDebug("Relais d'impression : {Message}", e.Message); // no internet: next pass
        }
    }

    /// <summary>Each job's sale id here, by id or by the key a sale made online was sent with.</summary>
    private async Task<Dictionary<string, string>> ResolveAsync(string groupId, IReadOnlyList<PrintRelayJob> jobs, CancellationToken ct)
    {
        var ids = jobs.Where(j => j.SaleKey is null).Select(j => j.VenteId).Distinct().ToList();
        var keys = jobs.Where(j => j.SaleKey is not null).Select(j => j.SaleKey!).Distinct().ToList();

        var knownIds = (await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && ids.Contains(v.Id)).Select(v => v.Id).ToListAsync(ct)).ToHashSet();
        var byKey = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && v.IdempotencyKey != null && keys.Contains(v.IdempotencyKey))
            .ToDictionaryAsync(v => v.IdempotencyKey!, v => v.Id, ct);

        var found = new Dictionary<string, string>();
        foreach (var job in jobs)
        {
            if (job.SaleKey is not null) { if (byKey.TryGetValue(job.SaleKey, out var id)) found[job.Id] = id; }
            else if (knownIds.Contains(job.VenteId)) found[job.Id] = job.VenteId;
        }
        return found;
    }

    private async Task ReportFinishedAsync(string groupId, ConcurrentDictionary<string, byte> inFlight, CancellationToken ct)
    {
        foreach (var id in inFlight.Keys)
        {
            var job = queue.Get(groupId, id);
            PrintJobResult? result = job switch
            {
                null => new PrintJobResult(false, "Le poste n'a pas imprimé à temps."),
                { Status: "done" } => new PrintJobResult(true, job.Message),
                { Status: "failed" } => new PrintJobResult(false, job.Message),
                _ => null,
            };
            if (result is null) continue;
            await client.ReportPrintAsync(id, result, ct);
            inFlight.TryRemove(id, out _);
        }
    }
}

/// <summary>Runs <see cref="PrintRelay"/> every few seconds for each shop that backs up to our server.</summary>
public sealed class PrintRelayService(IServiceScopeFactory scopes, ILogger<PrintRelayService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        List<string> groupIds = [];
        var loadedAt = DateTime.MinValue;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (DateTime.UtcNow - loadedAt > TimeSpan.FromMinutes(1))
                {
                    using var s = scopes.CreateScope();
                    groupIds = await s.ServiceProvider.GetRequiredService<LonniiDbContext>().CloudBackupStates
                        .Where(x => x.DeviceToken != null).Select(x => x.GroupId).ToListAsync(stoppingToken);
                    loadedAt = DateTime.UtcNow;
                }
                foreach (var groupId in groupIds)
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<PrintRelay>().PassAsync(groupId, stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Passage du relais d'impression interrompu");
            }
        }
        while (await Wait(timer, stoppingToken));
    }

    private static async Task<bool> Wait(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
