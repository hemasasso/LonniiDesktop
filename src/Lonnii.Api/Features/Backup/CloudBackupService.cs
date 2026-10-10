using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Backup;

/// <summary>
/// Backs every online-mode workspace on this host up to the licence server on a schedule, and
/// serves the status and "back up now" controls shown in Paramètres.
///
/// <para>
/// Idle on the OCI server itself and on local-mode installs: a workspace is only backed up when
/// it has a licence server to answer to and a backup token from it. Skips a round when nothing
/// has been written since the last successful one.
/// </para>
/// </summary>
public sealed class CloudBackupService(
    IServiceScopeFactory scopes,
    CloudBackupPaths dbPaths,
    IConfiguration configuration,
    ILogger<CloudBackupService> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTime> _lastSeenWrite = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = Math.Max(1, configuration.GetValue("Lonnii:CloudBackup:IntervalMinutes", 15));

        // Let start-up (migrations, the first licence refresh) finish before the first pass.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try { await RunAllAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Passage de sauvegarde en ligne interrompu");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task RunAllAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LonniiDbContext>();

        var groupIds = await db.CloudBackupStates
            .Where(s => s.DeviceToken != null)
            .Select(s => s.GroupId)
            .ToListAsync(ct);

        foreach (var groupId in groupIds)
        {
            var written = dbPaths.LastWriteUtc();
            if (_lastSeenWrite.TryGetValue(groupId, out var seen) && written <= seen) continue;

            using var inner = scopes.CreateScope();
            var outcome = await inner.ServiceProvider.GetRequiredService<CloudBackupRunner>().RunAsync(groupId, ct);

            // Taken after the run, which itself writes its result: only later writes count.
            if (outcome.Succeeded) _lastSeenWrite[groupId] = dbPaths.LastWriteUtc();
        }
    }
}

/// <summary>The controls in Paramètres → Données de l'espace.</summary>
public static class CloudBackupEndpoints
{
    public static void MapCloudBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/parametres/cloud-backup").WithTags("Paramètres");

        group.MapGet("/", StatusAsync).RequireGroupScope().RequireGroupAdmin();
        group.MapPost("/run", RunAsync).RequireGroupScope().RequireGroupAdmin();
        group.MapPost("/restore", RestoreAsync).RequireGroupScope().RequireGroupAdmin();
        group.MapPost("/restart", RestartAsync).RequireGroupScope().RequireGroupAdmin();
    }

    private static async Task<IResult> StatusAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == scope.GroupId, ct);
        var state = await db.CloudBackupStates.AsNoTracking().FirstOrDefaultAsync(s => s.GroupId == scope.GroupId, ct);

        var reason = CloudBackupRunner.WhyNotApplicable(groupe, state);
        var applicable = groupe is not null
            && Lonnii.Shared.Security.DeploymentModes.RequiresSubscription(groupe.Mode)
            && !string.IsNullOrWhiteSpace(groupe.LicenceServerUrl);

        return Results.Ok(new CloudBackupStatusDto(
            Applicable: applicable,
            Running: CloudBackupRunner.IsRunning,
            LastSuccessAt: state?.LastSuccessAt,
            LastAttemptAt: state?.LastAttemptAt,
            LastError: state?.LastError,
            LastRecordCount: state?.LastRecordCount ?? 0,
            LastImageCount: state?.LastImageCount ?? 0,
            Reason: reason));
    }

    private static async Task<IResult> RunAsync(GroupScope scope, CloudBackupRunner runner, CancellationToken ct)
    {
        var outcome = await runner.RunAsync(scope.GroupId, ct);

        return outcome.Succeeded
            ? Results.NoContent()
            : Results.Json(new ApiError(outcome.Message ?? "Sauvegarde impossible."), statusCode: StatusCodes.Status409Conflict);
    }

    /// <summary>Admin Général only: it writes a whole workspace from the cloud copy.</summary>
    private static async Task<IResult> RestoreAsync(GroupScope scope, CloudRestoreService restore, CancellationToken ct)
    {
        if (!scope.IsAdminGeneral)
        {
            return Results.Json(
                new ApiError("Réservé à l'Administrateur Général de l'espace"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        return ToResult(await restore.RestoreAsync(scope.GroupId, ct));
    }

    /// <summary>Shared with the first-launch endpoint.</summary>
    public static IResult ToResult(CloudRestoreOutcome outcome) =>
        outcome.Result is { } result
            ? Results.Ok(result)
            : Results.Json(new ApiError(outcome.Error ?? "Restauration impossible."), statusCode: outcome.StatusCode);

    /// <summary>Admin Général only: it sets the shop's existing cloud backup aside.</summary>
    private static async Task<IResult> RestartAsync(GroupScope scope, CloudBackupRunner runner, CancellationToken ct)
    {
        if (!scope.IsAdminGeneral)
        {
            return Results.Json(
                new ApiError("Réservé à l'Administrateur Général de l'espace"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var outcome = await runner.RestartAsync(scope.GroupId, ct);

        return outcome.Succeeded
            ? Results.NoContent()
            : Results.Json(new ApiError(outcome.Message ?? "Impossible."), statusCode: StatusCodes.Status409Conflict);
    }
}
