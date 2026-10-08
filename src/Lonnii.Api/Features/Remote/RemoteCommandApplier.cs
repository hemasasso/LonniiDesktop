using System.Collections.Concurrent;
using Lonnii.Api.Features.Backup;
using Lonnii.Api.Features.Live;
using Lonnii.Api.Features.Members;
using Lonnii.Data;
using Lonnii.Data.Services;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Remote;

/// <summary>
/// The shop host's side of the command queue: collects what administrators asked for from afar,
/// applies it to the shop's own database, and tells the server what became of each request.
///
/// <para>
/// OCI never pushes to the host. The host asks - with the machine token it already uses for its
/// backups - so a request can only arrive when the host chooses to look, and a shop that is offline
/// simply collects its requests when it is back.
/// </para>
///
/// <para>
/// The rule that keeps this safe: a request is applied only if its sender <em>still</em> holds an
/// administrator role in the shop's own data when the host gets to it. Roles are granted and removed
/// on the host, so an administrator removed after they asked cannot have their queued request carried
/// out, whatever the server says.
/// </para>
/// </summary>
public sealed class RemoteCommandApplier(
    LonniiDbContext db,
    CloudBackupClient client,
    PrivilegeResolver privileges,
    ShopChangeNotifier changes,
    CloudBackupRunner backups,
    ILogger<RemoteCommandApplier> logger)
{
    /// <summary>Requests already applied whose report has not reached the server yet. A lost reply
    /// must not make the next poll apply the same request twice.</summary>
    private static readonly ConcurrentDictionary<string, RemoteCommandResult> Unreported = new();

    /// <returns>How many requests were applied successfully on this pass.</returns>
    public async Task<int> PollAsync(string groupId, CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        var state = await db.CloudBackupStates.AsNoTracking().FirstOrDefaultAsync(s => s.GroupId == groupId, ct);

        if (CloudBackupRunner.WhyNotApplicable(groupe, state) is not null) return 0;

        client.For(groupe!.LicenceServerUrl!, groupId, state!.DeviceId!, state.DeviceToken!, state.Epoch);

        IReadOnlyList<RemoteCommandDto> pending;
        try
        {
            pending = await client.PendingCommandsAsync(ct);
        }
        catch (CloudBackupException)
        {
            return 0; // no internet or the server is busy: try again at the next pass
        }

        var applied = 0;

        foreach (var command in pending)
        {
            if (!Unreported.TryGetValue(command.Id, out var result))
            {
                result = await ApplyAsync(groupId, command, ct);
                Unreported[command.Id] = result;
                if (result.Status == RemoteCommandStatuses.Applied) applied++;
            }

            try
            {
                await client.ReportCommandAsync(command.Id, result, ct);
                Unreported.TryRemove(command.Id, out _);
            }
            catch (CloudBackupException e)
            {
                logger.LogWarning("Réponse à une demande à distance non transmise : {Message}", e.Message);
            }
        }

        if (applied > 0)
        {
            changes.Bump(groupId);

            // So the copy a phone reads shows the change soon, not at the next scheduled backup.
            try { await backups.RunAsync(groupId, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Sauvegarde après une demande à distance impossible");
            }
        }

        return applied;
    }

    /// <summary>Applies one request, or says why it was not. Never throws for a refused request.</summary>
    public async Task<RemoteCommandResult> ApplyAsync(string groupId, RemoteCommandDto command, CancellationToken ct)
    {
        var sender = await privileges.ResolveAsync(command.RequestedBy, groupId, ct);

        if (!sender.IsAdmin && !sender.IsAdminGeneral)
        {
            return new RemoteCommandResult(RemoteCommandStatuses.Failed,
                "Cette personne n'est plus administrateur de la boutique.");
        }

        PrivilegeChangeResult outcome;

        switch (command.Type)
        {
            case RemoteCommandTypes.Privilege when command.PrivilegeName is not null && command.Granted is not null:
                outcome = await PrivilegeChanges.SetPrivilegeAsync(
                    db, groupId, command.RequestedBy,
                    new SetPrivilegeRequest(command.UserId, command.PrivilegeName, command.Granted.Value),
                    isGestion: command.Catalog == "gestion", PrivilegeChanges.RemoteReasonPrefix.Trim(), ct);
                break;

            case RemoteCommandTypes.Role when command.Role is not null:
                outcome = await PrivilegeChanges.SetRoleAsync(
                    db, groupId, command.RequestedBy, sender.IsAdminGeneral,
                    new SetRoleRequest(command.UserId, command.Role),
                    PrivilegeChanges.RemoteReasonPrefix.Trim(), ct);
                break;

            default:
                return new RemoteCommandResult(RemoteCommandStatuses.Failed, "Demande incomplète ou inconnue.");
        }

        return outcome.Ok
            ? new RemoteCommandResult(RemoteCommandStatuses.Applied)
            : new RemoteCommandResult(RemoteCommandStatuses.Failed, outcome.Error);
    }
}

/// <summary>Looks for requests every few seconds for each shop that backs up to the server.</summary>
public sealed class RemoteCommandService(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<RemoteCommandService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(3, configuration.GetValue("Lonnii:RemoteCommands:IntervalSeconds", 10));

        // Let start-up (migrations, the first licence refresh) finish before the first look.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try { await PollAllAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Passage de lecture des demandes à distance interrompu");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    // Which shops back up to the server changes rarely, so it is looked up once a minute rather than
    // on every pass, which would put a database query in the log every few seconds.
    private List<string> _groupIds = [];
    private DateTime _groupIdsLoadedAt = DateTime.MinValue;

    private async Task<List<string>> GroupIdsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _groupIdsLoadedAt < TimeSpan.FromMinutes(1)) return _groupIds;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();
        _groupIds = await db.CloudBackupStates
            .Where(s => s.DeviceToken != null)
            .Select(s => s.GroupId)
            .ToListAsync(ct);
        _groupIdsLoadedAt = DateTime.UtcNow;
        return _groupIds;
    }

    private async Task PollAllAsync(CancellationToken ct)
    {
        foreach (var groupId in await GroupIdsAsync(ct))
        {
            using var inner = scopes.CreateScope();
            await inner.ServiceProvider.GetRequiredService<RemoteCommandApplier>().PollAsync(groupId, ct);
        }
    }
}
