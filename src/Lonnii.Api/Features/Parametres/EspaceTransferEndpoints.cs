using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Http.Features;

namespace Lonnii.Api.Features.Parametres;

/// <summary>
/// Paramètres → Données de l'espace: download this espace as one <c>.db</c> file, images
/// included, and load such a file into an espace that has no data of its own yet.
///
/// <para>
/// Nothing in Lonnii Business corresponds to this. It exists because the desktop product is
/// deployed per shop: a shop changing host laptop, splitting a workspace in two, or moving
/// from a trial espace to the real one has no server-side admin to do it for them.
/// </para>
/// <para>
/// Export is admin, like every other screen in Paramètres. Import is Admin Général only -
/// it writes the whole content of an espace in one go, from a file that came from outside,
/// and that is not a settings change.
/// </para>
/// </summary>
public static class EspaceTransferEndpoints
{
    public static void MapEspaceTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var espace = app.MapGroup("/api/parametres/espace").WithTags("Paramètres");

        espace.MapGet("/export", ExportAsync).RequireGroupScope().RequireGroupAdmin();
        espace.MapPost("/import", ImportAsync).RequireGroupScope().RequireGroupAdmin();
    }

    /// <summary>
    /// Streams the archive. It is built on the host's disk first - an espace can be hundreds
    /// of megabytes with its photos, which is not something to hold in memory - and deleted
    /// as soon as the download closes.
    /// </summary>
    private static async Task<IResult> ExportAsync(
        HttpContext http, GroupScope scope, EspaceTransferService transfers, CancellationToken ct)
    {
        var export = await transfers.ExportAsync(scope.GroupId, ct);
        if (export is null) return Results.NotFound(new ApiError("Espace introuvable"));

        // So the client can report what it just saved without reopening the file.
        http.Response.Headers["x-lonnii-records"] = export.Manifest.RecordCount.ToString();
        http.Response.Headers["x-lonnii-images"] = export.Manifest.ImageCount.ToString();

        var stream = new FileStream(
            export.FilePath, FileMode.Open, FileAccess.Read, FileShare.None, 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        return Results.Stream(stream, "application/octet-stream", export.FileName);
    }

    /// <summary>
    /// Takes the archive as the raw request body rather than as a multipart form: there is
    /// one file and no fields, and a plain body streams to disk without the 128 MB multipart
    /// limit getting in the way.
    /// </summary>
    private static async Task<IResult> ImportAsync(
        HttpContext http,
        GroupScope scope,
        EspaceTransferService transfers,
        EspaceTransferPaths paths,
        CancellationToken ct)
    {
        if (!scope.IsAdminGeneral)
        {
            return Results.Json(
                new ApiError("Réservé à l'Administrateur Général de l'espace"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Kestrel's default body limit is 30 MB, far below an espace with photos.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = EspaceTransferService.MaxImportBytes;

        if (http.Request.ContentLength > EspaceTransferService.MaxImportBytes)
            return TooLarge();

        var path = paths.NewFile("import");

        try
        {
            if (!await SaveBodyAsync(http.Request.Body, path, ct)) return TooLarge();
        }
        catch
        {
            Delete(path);
            throw;
        }

        // From here the service owns the file, including deleting it.
        var outcome = await transfers.ImportAsync(scope.GroupId, path, ct);

        if (outcome.Conflict is { } conflicts)
        {
            var found = string.Join(", ", conflicts.Select(c => $"{c.Label} ({c.Count})"));

            return Results.Json(
                new ApiError(
                    $"Cet espace contient déjà des données : {found}. " +
                    "L'import n'est possible que dans un espace vide : créez un nouvel espace, " +
                    "puis importez le fichier dans celui-là."),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (outcome.Error is { } error) return Results.BadRequest(new ApiError(error));

        return Results.Ok(outcome.Result);
    }

    /// <summary>
    /// Writes the body to <paramref name="path"/>, stopping if it runs past the cap. False
    /// means it did - a chunked upload sends no Content-Length, so the size cannot always be
    /// checked before reading.
    /// </summary>
    private static async Task<bool> SaveBodyAsync(Stream body, string path, CancellationToken ct)
    {
        // 1 MB chunks rather than the usual 64 KB: at the sizes allowed here that is tens of
        // thousands of fewer round trips through the socket and the disk.
        const int ChunkBytes = 1024 * 1024;

        await using var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes, useAsync: true);

        var buffer = new byte[ChunkBytes];
        long total = 0;

        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0) break;

            total += read;
            if (total > EspaceTransferService.MaxImportBytes)
            {
                await file.DisposeAsync();
                Delete(path);
                return false;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return true;
    }

    private static IResult TooLarge()
    {
        // Quoted in gigabytes: the limit is measured in them, and "5120 Mo" reads as a
        // stranger number than the one actually configured.
        var maxGb = EspaceTransferService.MaxImportBytes / (1024.0 * 1024 * 1024);
        return Results.BadRequest(new ApiError($"Le fichier dépasse la taille maximale de {maxGb:0.#} Go"));
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file is not worth failing the request over.
        }
    }
}
