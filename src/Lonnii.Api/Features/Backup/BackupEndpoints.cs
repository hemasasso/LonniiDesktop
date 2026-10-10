using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Backup;

/// <summary>
/// The licence server's side of the cloud backup: what an online-mode shop's host uploads to,
/// and what it restores from after losing its database.
///
/// <para>
/// Why this exists: an online shop's sales live in the host's <c>lonnii.db</c>, and the OCI
/// server held only the licence. Delete that file and the shop's history was gone, which is
/// the main thing the paid tier is bought to prevent. This is a <em>backup and restore</em>,
/// not a live sync - the host stays the one place the shop writes to, and the cloud holds a
/// recent copy (at most one backup interval behind).
/// </para>
/// <para>
/// Authenticated by workspace id + device id + a per-device token (see <see cref="BackupTokens"/>),
/// not by user login: the caller is a machine, and on a replaced laptop no user has signed in
/// yet. Every call also re-checks that the device is still bound and the subscription is
/// current, so revoking a machine or letting a subscription lapse cuts access immediately.
/// </para>
/// <para>
/// Writes are guarded by an <em>epoch</em>. The server remembers which line of backups it is
/// holding; a host may only add to the line it started or restored from. A freshly installed,
/// empty host has no epoch, so it cannot overwrite the shop's real backup with a blank one -
/// it must restore first, or explicitly start again with <c>/reset</c>.
/// </para>
/// </summary>
public static class BackupEndpoints
{
    private const string CallerKey = "backup.caller";
    public const string EpochHeader = "x-backup-epoch";
    public const string TokenHeader = "x-backup-token";
    public const string GroupHeader = "x-group-id";

    private sealed record Caller(string GroupId);

    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backup").WithTags("Backup")
            .AddEndpointFilter(AuthenticateAsync);

        group.MapGet("/info", (HttpContext http, BackupStore store) =>
            Results.Ok(store.Info(GroupOf(http))));

        group.MapPost("/begin", BeginAsync);
        group.MapPost("/reset", ResetAsync);

        group.MapPost("/images/missing", MissingImagesAsync);
        group.MapPut("/images/{folder}/{name}", PutImageAsync);
        group.MapGet("/images/{folder}/{name}", GetImage);

        group.MapPut("/snapshot", PutSnapshotAsync);
        group.MapGet("/snapshot", GetSnapshot);

        // What administrators asked for from afar, for the shop's host to collect and apply.
        group.MapGet("/commands", (HttpContext http, Lonnii.Api.Features.Remote.RemoteCommandStore commands) =>
            Results.Ok(new RemoteCommandsResponse(commands.Pending(GroupOf(http)))));

        // The photo a product request carries, sent from afar (see RemoteEndpoints.UploadPhotoAsync).
        group.MapGet("/commands/{id}/photo", (string id, HttpContext http, Lonnii.Api.Features.Remote.RemoteCommandStore commands) =>
            commands.OpenCommandPhoto(GroupOf(http), id) is { } photo
                ? Results.File(photo, "application/octet-stream")
                : Results.NotFound(new ApiError("Photo introuvable.")));

        group.MapPost("/commands/{id}/result", (
            string id, RemoteCommandResult result, HttpContext http,
            Lonnii.Api.Features.Remote.RemoteCommandStore commands) =>
            commands.Complete(GroupOf(http), id, result.Status, result.Message)
                ? Results.NoContent()
                : Results.NotFound(new ApiError("Demande introuvable ou déjà traitée.")));
    }

    private static string GroupOf(HttpContext http) => ((Caller)http.Items[CallerKey]!).GroupId;

    // --- Authentication ----------------------------------------------------------------

    private static async ValueTask<object?> AuthenticateAsync(
        EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var http = invocation.HttpContext;
        var db = http.RequestServices.GetRequiredService<LonniiDbContext>();
        var tokens = http.RequestServices.GetRequiredService<BackupTokens>();
        var ct = http.RequestAborted;

        var groupId = http.Request.Headers[GroupHeader].ToString();
        var deviceId = http.Request.Headers["x-device-id"].ToString();
        var token = http.Request.Headers[TokenHeader].ToString();

        // One message for a wrong token, an unknown workspace and a revoked device: the
        // caller learns nothing about which of the three it was.
        var refused = Results.Json(
            new ApiError("Sauvegarde refusée : ce poste n'est pas autorisé."),
            statusCode: StatusCodes.Status403Forbidden);

        if (!Guid.TryParse(groupId, out _) || string.IsNullOrEmpty(deviceId) || !tokens.Verify(groupId, deviceId, token))
            return refused;

        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (groupe is null || groupe.IsDeleted) return refused;

        var bound = await db.Devices.AnyAsync(
            d => d.GroupId == groupId && d.DeviceId == deviceId && d.RevokedAt == null, ct);
        if (!bound) return refused;

        if (groupe.IsBlocked)
        {
            return Results.Json(
                new ApiError(groupe.BlockReason ?? "Cet espace est bloqué. Contactez le support."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        // The backup is part of the paid tier: a local-mode shop has none to reach.
        if (!DeploymentModes.RequiresSubscription(groupe.Mode))
        {
            return Results.Json(
                new ApiError("La sauvegarde en ligne est réservée aux espaces en mode en ligne."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var subscription = await db.DashboardSubscriptions
            .Where(s => s.GroupId == groupId)
            .OrderByDescending(s => s.ContractStartDate)
            .ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (subscription is null || !subscription.IsCurrentAt(DateTime.UtcNow))
        {
            return Results.Json(
                new ApiError("Abonnement inactif ou expiré. Contactez le support pour réactiver cet espace."),
                statusCode: StatusCodes.Status402PaymentRequired);
        }

        http.Items[CallerKey] = new Caller(groupId);
        return await next(invocation);
    }

    /// <summary>
    /// The write guard. Null when the caller is on the current line of backups; otherwise the
    /// 409 to answer with, which tells the client to restore (or start again) rather than retry.
    /// </summary>
    private static IResult? RequireCurrentEpoch(HttpContext http, BackupStore store)
    {
        var current = store.CurrentEpoch(GroupOf(http));
        var presented = http.Request.Headers[EpochHeader].ToString();

        if (current is not null && string.Equals(current, presented, StringComparison.Ordinal))
            return null;

        return StaleEpoch();
    }

    private static IResult StaleEpoch() =>
        Results.Json(
            new ApiError(
                "Une sauvegarde existe déjà pour cet espace et cet ordinateur n'en fait pas partie. " +
                "Restaurez la sauvegarde, ou choisissez de repartir de zéro."),
            statusCode: StatusCodes.Status409Conflict);

    // --- Epoch -------------------------------------------------------------------------

    private static async Task<IResult> BeginAsync(
        BackupBeginRequest request, HttpContext http, BackupStore store, CancellationToken ct)
    {
        var groupId = GroupOf(http);
        using var _ = await store.LockAsync(groupId, ct);

        var (epoch, created) = store.Begin(groupId, request.Epoch);

        return epoch is null
            ? StaleEpoch()
            : Results.Ok(new BackupBeginResponse(epoch, created));
    }

    private static async Task<IResult> ResetAsync(HttpContext http, BackupStore store, ILogger<BackupStore> log, CancellationToken ct)
    {
        var groupId = GroupOf(http);
        using var _ = await store.LockAsync(groupId, ct);

        var epoch = store.Reset(groupId);

        // Loud on purpose: this sets a shop's real backup aside.
        log.LogWarning("Sauvegarde de l'espace {GroupId} mise de côté ; nouvelle ligne {Epoch}", groupId, epoch);

        return Results.Ok(new BackupBeginResponse(epoch, Created: true));
    }

    // --- Images ------------------------------------------------------------------------

    private static IResult MissingImagesAsync(BackupImagesRequest request, HttpContext http, BackupStore store)
    {
        if (RequireCurrentEpoch(http, store) is { } stale) return stale;

        return Results.Ok(new BackupImagesMissingDto(store.MissingImages(GroupOf(http), request.Images)));
    }

    private static async Task<IResult> PutImageAsync(
        string folder, string name, HttpContext http, BackupStore store, CancellationToken ct)
    {
        if (RequireCurrentEpoch(http, store) is { } stale) return stale;

        var saved = await store.SaveImageAsync(GroupOf(http), folder, name, http.Request.Body, ct);

        return saved
            ? Results.NoContent()
            : Results.BadRequest(new ApiError("Image refusée."));
    }

    private static IResult GetImage(string folder, string name, HttpContext http, BackupStore store)
    {
        var stream = store.OpenImage(GroupOf(http), folder, name);

        return stream is null
            ? Results.NotFound()
            : Results.Stream(stream, Images.ImageStorageService.ContentTypeFor(name));
    }

    // --- Snapshot ----------------------------------------------------------------------

    private static async Task<IResult> PutSnapshotAsync(
        HttpContext http, BackupStore store, int records = 0, int images = 0, CancellationToken ct = default)
    {
        if (RequireCurrentEpoch(http, store) is { } stale) return stale;

        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = BackupStore.MaxSnapshotBytes;

        var groupId = GroupOf(http);
        using var _ = await store.LockAsync(groupId, ct);

        var at = await store.SaveSnapshotAsync(groupId, http.Request.Body, records, images, ct);

        return at is null
            ? Results.BadRequest(new ApiError("Sauvegarde refusée : fichier invalide ou trop volumineux."))
            : Results.Ok(new { SnapshotAt = at.Value });
    }

    private static IResult GetSnapshot(HttpContext http, BackupStore store)
    {
        var stream = store.OpenLatestSnapshot(GroupOf(http));

        return stream is null
            ? Results.NotFound(new ApiError("Aucune sauvegarde pour cet espace."))
            : Results.Stream(stream, "application/gzip");
    }
}
