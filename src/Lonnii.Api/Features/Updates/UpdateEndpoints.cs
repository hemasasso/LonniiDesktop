using System.Text.Json;
using Lonnii.Shared.Security;

namespace Lonnii.Api.Features.Updates;

/// <summary>Where published versions sit: the installer and the signed notice naming it.</summary>
public sealed class UpdatePaths(string dataDirectory, string publicKey = RepairCode.PublicKey)
{
    public string Directory { get; } = Path.Combine(dataDirectory, "updates");

    /// <summary>The key a notice must be signed with: ours, except in the tests.</summary>
    public string PublicKey { get; } = publicKey;

    public string Manifest => Path.Combine(Directory, "latest.json");
}

/// <summary>
/// Serves the newest version to the computers that ask. On our server the folder is filled by
/// hand when we publish (<c>lonnii-setup release</c>); later a shop's main computer fills its own
/// the same way for its tills, so both answer the same two addresses.
///
/// <para>
/// Open to anyone: the installer is the one we hand out to every customer anyway, and a computer
/// checks our signature before running it, so nothing here needs to be kept from strangers.
/// </para>
/// </summary>
public static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/updates").AllowAnonymous();

        group.MapGet("/latest", (UpdatePaths paths) =>
            Read(paths) is { } manifest ? Results.Ok(manifest) : Results.NoContent());

        group.MapGet("/files/{name}", (string name, UpdatePaths paths) =>
        {
            // Only the file the current notice names: never a path built from what was asked.
            if (Read(paths) is not { } manifest || !string.Equals(name, manifest.FileName, StringComparison.Ordinal))
                return Results.NotFound();

            return Results.File(
                Path.Combine(paths.Directory, manifest.FileName), "application/octet-stream",
                manifest.FileName, enableRangeProcessing: true);
        });

        return app;
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
            if (!File.Exists(paths.Manifest)) return null;

            var manifest = JsonSerializer.Deserialize<UpdateManifest>(
                File.ReadAllText(paths.Manifest), JsonSerializerOptions.Web);

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
