using System.Diagnostics;
using System.Net;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Api.Features.Updates;

/// <summary>Starts an installer that will outlive this process. Replaced in the tests.</summary>
public interface IUpdateLauncher
{
    /// <summary>Null when it started; otherwise why not.</summary>
    string? Launch(string installerPath, string logPath);
}

/// <summary>
/// Runs the installer as SYSTEM through a one-off scheduled task, so no Windows prompt appears on
/// the shop's main computer. Not started as our own child: the installer stops this API before
/// replacing it, and a task of its own is not taken down with it.
/// </summary>
public sealed class ScheduledTaskLauncher : IUpdateLauncher
{
    private const string TaskName = "Lonnii Update";

    public string? Launch(string installerPath, string logPath)
    {
        var command = $"\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{logPath}\"";

        // /SC ONCE needs a start time; it is never reached - /Run starts it now.
        var created = Run("/Create", "/F", "/TN", TaskName, "/SC", "ONCE", "/ST", "23:59",
            "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", command);
        if (created is not null) return created;

        return Run("/Run", "/TN", TaskName);
    }

    private static string? Run(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info);
        if (process is null) return "Impossible de lancer le planificateur de tâches.";

        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return process.ExitCode == 0
            ? null
            : "Ce serveur n'a pas les droits pour installer la mise à jour (il ne tourne pas en tant que service Lonnii). " + error.Trim();
    }
}

/// <summary>
/// "Installer la mise à jour" on the shop's main computer. Only from that computer itself (its
/// tills install their own copy, with Windows asking for permission) and only by an admin.
/// </summary>
public static class UpdateInstallEndpoints
{
    public static IEndpointRouteBuilder MapUpdateInstallEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/updates/install", InstallAsync).RequireGroupScope().RequireGroupAdmin();
        return app;
    }

    private static async Task<IResult> InstallAsync(
        HttpContext http, UpdatePaths paths, IUpdateLauncher launcher, ILogger<UpdatePaths> logger, CancellationToken ct)
    {
        // Someone at a till must not restart the server under everyone else's feet.
        var remote = http.Connection.RemoteIpAddress;
        if (remote is null || !(IPAddress.IsLoopback(remote) || remote.Equals(http.Connection.LocalIpAddress)))
        {
            return Results.Json(
                new ApiError("La mise à jour du serveur se lance depuis l'ordinateur principal de la boutique."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (UpdateEndpoints.Read(paths) is not { } manifest)
            return Results.Json(new ApiError("Aucune mise à jour disponible."), statusCode: StatusCodes.Status404NotFound);

        var running = typeof(UpdateInstallEndpoints).Assembly.GetName().Version?.ToString(3);
        if (!manifest.IsNewerThan(running))
            return Results.Json(new ApiError("Ce serveur est déjà à jour."), statusCode: StatusCodes.Status409Conflict);

        // The updates folder sits in ProgramData, where an ordinary Windows account may be able to
        // write. The file SYSTEM runs is a copy beside this program, which only administrators can
        // change, checked again after copying - so nothing can be swapped in between.
        var runDirectory = Path.Combine(AppContext.BaseDirectory, "update-run");
        Directory.CreateDirectory(runDirectory);
        foreach (var old in Directory.EnumerateFiles(runDirectory, UpdateManifest.FilePrefix + "*")) File.Delete(old);

        var copy = Path.Combine(runDirectory, manifest.FileName);
        File.Copy(Path.Combine(paths.Directory, manifest.FileName), copy, overwrite: true);

        if (new FileInfo(copy).Length != manifest.Size
            || !string.Equals(await UpdateManifest.HashFileAsync(copy, ct), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(copy);
            return Results.Json(new ApiError("Le fichier de mise à jour est endommagé. Réessayez plus tard."),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (launcher.Launch(copy, Path.Combine(paths.Directory, "install.log")) is { } error)
        {
            logger.LogWarning("Mise à jour {Version} non lancée : {Error}", manifest.Version, error);
            return Results.Json(new ApiError(error), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Mise à jour {Version} lancée", manifest.Version);
        return Results.Accepted(value: manifest);
    }
}
