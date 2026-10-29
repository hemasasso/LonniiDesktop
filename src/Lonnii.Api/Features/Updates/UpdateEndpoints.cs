using System.Text.Json;
using Lonnii.Shared.Security;

namespace Lonnii.Api.Features.Updates;

/// <summary>Where published versions sit: the installer and the signed notice naming it.</summary>
public sealed class UpdatePaths(string dataDirectory, string? publicKey = null)
{
    public string Directory { get; } = Path.Combine(dataDirectory, "updates");

    /// <summary>The key a notice must be signed with; null for ours (main or spare), set by the tests.</summary>
    public string? PublicKey { get; } = publicKey;

    public string Manifest => Path.Combine(Directory, "latest.json");
}

/// <summary>
/// Hands out the newest version. Only online shops get updates, so the paths differ by side:
/// <list type="bullet">
/// <item>On our server the folder is filled when we publish (<c>lonnii-setup release</c>) and is
/// given out only through <c>/api/backup/update/...</c>, behind the backup's checks: an online,
/// unblocked, paid-up shop's own computer. Its <c>/api/updates</c> is never routed outside.</item>
/// <item>On a shop's main computer, <see cref="UpdateDownloader"/> fills the folder from our
/// server, and <c>/api/updates</c> serves it to the shop's tills, which often have no internet.
/// A local-mode shop's computer never downloads one, so its tills are offered nothing.</item>
/// </list>
/// <para>
/// <c>/api/updates</c> is open on the shop's network: a till checks our signature before running
/// the file, so the address needs no more protection than that.
/// </para>
/// </summary>
public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/updates").AllowAnonymous();

        group.MapGet("/latest", (UpdatePaths paths) => Latest(paths));
        group.MapGet("/files/{name}", (string name, UpdatePaths paths) => File(name, paths));

        return app;
    }

    internal static IResult Latest(UpdatePaths paths) =>
        Read(paths) is { } manifest ? Results.Ok(manifest) : Results.NoContent();

    internal static IResult File(string name, UpdatePaths paths)
    {
        // Only the file the current notice names: never a path built from what was asked.
        if (Read(paths) is not { } manifest || !string.Equals(name, manifest.FileName, StringComparison.Ordinal))
            return Results.NotFound();

        return Results.File(
            Path.Combine(paths.Directory, manifest.FileName), "application/octet-stream",
            manifest.FileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// The published notice, if it is ours and its installer is really there. A notice that fails
    /// our signature is never served: the computers would refuse it anyway, and an empty answer
    /// is easier to read in a log than a stream of rejected downloads.
    /// </summary>
    internal static UpdateManifest? Read(UpdatePaths paths)
    {
        try
        {
            if (!System.IO.File.Exists(paths.Manifest)) return null;

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(
                System.IO.File.ReadAllText(paths.Manifest), JsonSerializerOptions.Web);

            if (manifest is null || !manifest.IsGenuine(paths.PublicKey)) return null;

            var installer = new FileInfo(Path.Combine(paths.Directory, manifest.FileName));
            return installer.Exists && installer.Length == manifest.Size ? manifest : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }
}
