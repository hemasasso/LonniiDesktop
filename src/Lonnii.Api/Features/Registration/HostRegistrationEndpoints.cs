using System.Text.Json;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Registration;

/// <summary>A registration this host has confirmed by email and is waiting for us to approve.</summary>
public sealed record PendingRegistration(string GroupId, string ServerUrl, string Email, string ShopName);

/// <summary>
/// Remembers a confirmed-but-not-yet-approved registration between launches, in a small file
/// beside the database. Holds no password: approval can take days, and nothing secret should
/// sit on disk waiting for it.
/// </summary>
public sealed class PendingRegistrationStore(string dataDirectory)
{
    private string PathOf => Path.Combine(dataDirectory, "pending-registration.json");

    public PendingRegistration? Read()
    {
        try
        {
            return File.Exists(PathOf)
                ? JsonSerializer.Deserialize<PendingRegistration>(File.ReadAllText(PathOf))
                : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public void Write(PendingRegistration pending) =>
        File.WriteAllText(PathOf, JsonSerializer.Serialize(pending));

    public void Clear()
    {
        try { if (File.Exists(PathOf)) File.Delete(PathOf); }
        catch (IOException) { /* a leftover file only re-offers a registration that no longer applies */ }
    }
}

/// <summary>
/// The first-launch "create my shop" path, as this host offers it to its own client.
///
/// <para>
/// The host has no account and no workspace yet, so it cannot sign anyone in: it relays the
/// registration to the licence server (<see cref="RegistrationEndpoints"/>), remembers that the
/// shop is waiting, and - once we have approved it - activates against the server with the
/// owner's password and builds the local workspace exactly as the credentials-file path does.
/// Like <c>/api/setup/apply</c> it is anonymous and closes as soon as a workspace exists.
/// </para>
/// </summary>
public static class HostRegistrationEndpoints
{
    public static void MapHostRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/setup/register").WithTags("Setup");

        group.MapPost("/start", StartAsync);
        group.MapPost("/verify", VerifyAsync);
        group.MapGet("/pending", PendingAsync);
        group.MapPost("/activate", ActivateAsync);
        group.MapDelete("/pending", CancelAsync);

        // "J'ai déjà un compte": an existing account installs one of its espaces on this computer.
        var account = app.MapGroup("/api/setup/account").WithTags("Setup");
        account.MapPost("/espaces", AccountEspacesAsync);
        account.MapPost("/install", InstallAsync);
    }

    /// <summary>
    /// "J'ai déjà un compte" works on a fresh computer, and again to add more of the account's
    /// shops to one already set up. That second case grafts an espace onto a running host, so it
    /// needs someone signed in here as well as the shop's own admin password, which our server checks.
    /// </summary>
    private static async Task<IResult?> AccountGuardAsync(
        LonniiDbContext db, IConfiguration config, HttpContext http, CancellationToken ct)
    {
        if (await db.Groupes.AnyAsync(ct) && http.User.Identity?.IsAuthenticated != true)
            return Refuse("Connectez-vous d'abord pour ajouter un espace à cet ordinateur.", StatusCodes.Status401Unauthorized);

        if (string.IsNullOrWhiteSpace(ServerUrl(config)))
        {
            return Refuse(
                "Cette installation ne connaît pas l'adresse du serveur Lonnii.",
                StatusCodes.Status503ServiceUnavailable);
        }

        return null;
    }

    private static async Task<IResult> AccountEspacesAsync(
        AccountEspacesRequest request, LonniiDbContext db, ILicenceServer licences,
        IConfiguration config, HttpContext http, CancellationToken ct)
    {
        if (await AccountGuardAsync(db, config, http, ct) is { } refusal) return refusal;

        try
        {
            return Results.Ok(await licences.AccountEspacesAsync(ServerUrl(config), request, ct));
        }
        catch (ActivationRefusedException e)
        {
            return Refuse(e.Message, (int?)e.Status ?? StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Activates this computer for the chosen espace and builds it here, exactly as the other
    /// first-launch paths do. If the espace has an online backup the answer says so, and the
    /// window offers to restore it - which brings back its data, its staff and its real owner.
    /// </summary>
    private static async Task<IResult> InstallAsync(
        SetupInstallEspaceRequest request, LonniiDbContext db, ILicenceServer licences,
        CloudBackupClient backups, IConfiguration config, HttpContext http, CancellationToken ct)
    {
        if (await AccountGuardAsync(db, config, http, ct) is { } refusal) return refusal;

        if (await db.Groupes.AnyAsync(g => g.Id == request.GroupId, ct))
            return Refuse("Cet espace est déjà installé sur cet ordinateur.", StatusCodes.Status409Conflict);

        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.BadRequest(new ApiError("Identifiant de poste manquant."));

        var email = request.Email.Trim().ToLowerInvariant();

        ActivationResponse activation;
        try
        {
            activation = await licences.ActivateAsync(
                ServerUrl(config),
                new ActivationRequest(
                    GroupId: request.GroupId,
                    Email: email,
                    Password: request.Password,
                    DeviceId: request.DeviceId,
                    DeviceName: request.DeviceName,
                    Platform: DevicePlatforms.Windows,
                    AppVersion: request.AppVersion),
                ct);
        }
        catch (ActivationRefusedException e)
        {
            return Refuse(e.Message, (int?)e.Status ?? StatusCodes.Status403Forbidden);
        }

        // Our server's answer is the authority on the mode. A local-mode shop has no online copy
        // to pull, so nothing is built: it would only be an empty shop under the real one's name.
        if (!DeploymentModes.RequiresSubscription(activation.Mode))
        {
            return Refuse(
                "Cet espace est en mode local : ses données sont sur l'ordinateur de la boutique, pas en ligne. " +
                "Passez-le en mode en ligne dans le tableau de bord Lonnii pour l'installer ici.",
                StatusCodes.Status409Conflict);
        }

        return await SetupEndpoints.CreateWorkspaceAsync(
            db, backups, activation, email, request.Password, ServerUrl(config),
            request.DeviceId, request.DeviceName, request.AppVersion, ct);
    }

    private static IResult Refuse(string message, int status) =>
        Results.Json(new ApiError(message), statusCode: status);

    private static async Task<IResult?> GuardAsync(LonniiDbContext db, IConfiguration config, CancellationToken ct)
    {
        // Like /apply: once a workspace exists this would graft a second one onto a running shop.
        if (await db.Groupes.AnyAsync(ct))
            return Refuse("Cette installation est déjà configurée.", StatusCodes.Status409Conflict);

        if (string.IsNullOrWhiteSpace(ServerUrl(config)))
        {
            return Refuse(
                "Cette installation ne connaît pas l'adresse du serveur Lonnii. Utilisez le fichier d'identifiants.",
                StatusCodes.Status503ServiceUnavailable);
        }

        return null;
    }

    private static string ServerUrl(IConfiguration config) => config["Lonnii:LicenceServerUrl"]?.Trim() ?? string.Empty;

    private static async Task<IResult> StartAsync(
        RegistrationStartRequest request, LonniiDbContext db, ILicenceServer licences,
        IConfiguration config, CancellationToken ct)
    {
        if (await GuardAsync(db, config, ct) is { } refusal) return refusal;

        try
        {
            return Results.Ok(await licences.RegistrationStartAsync(ServerUrl(config), request, ct));
        }
        catch (ActivationRefusedException e)
        {
            return Refuse(e.Message, (int?)e.Status ?? StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> VerifyAsync(
        SetupRegisterVerifyRequest request, LonniiDbContext db, ILicenceServer licences,
        PendingRegistrationStore pending, IConfiguration config, CancellationToken ct)
    {
        if (await GuardAsync(db, config, ct) is { } refusal) return refusal;

        try
        {
            var result = await licences.RegistrationVerifyAsync(
                ServerUrl(config), new RegistrationVerifyRequest(request.RequestId, request.Code), ct);

            pending.Write(new PendingRegistration(
                result.GroupId, ServerUrl(config), request.Email.Trim().ToLowerInvariant(), request.ShopName));

            return Results.Ok(result);
        }
        catch (ActivationRefusedException e)
        {
            return Refuse(e.Message, (int?)e.Status ?? StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult PendingAsync(PendingRegistrationStore pending) =>
        pending.Read() is { } p
            ? Results.Ok(new PendingRegistrationDto(true, p.Email, p.ShopName))
            : Results.Ok(new PendingRegistrationDto(false, null, null));

    private static async Task<IResult> ActivateAsync(
        SetupActivatePendingRequest request, LonniiDbContext db, ILicenceServer licences,
        CloudBackupClient backups, PendingRegistrationStore pendingStore, IConfiguration config, CancellationToken ct)
    {
        if (await GuardAsync(db, config, ct) is { } refusal) return refusal;

        if (pendingStore.Read() is not { } pending)
            return Refuse("Aucune inscription en attente sur cet ordinateur.", StatusCodes.Status404NotFound);

        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.BadRequest(new ApiError("Identifiant de poste manquant."));

        ActivationResponse activation;
        try
        {
            activation = await licences.ActivateAsync(
                pending.ServerUrl,
                new ActivationRequest(
                    GroupId: pending.GroupId,
                    Email: pending.Email,
                    Password: request.Password,
                    DeviceId: request.DeviceId,
                    DeviceName: request.DeviceName,
                    Platform: DevicePlatforms.Windows,
                    AppVersion: request.AppVersion),
                ct);
        }
        catch (ActivationRefusedException e)
        {
            // Still waiting for us, or a wrong password: the server's own words say which.
            return Refuse(e.Message, (int?)e.Status ?? StatusCodes.Status403Forbidden);
        }

        var result = await SetupEndpoints.CreateWorkspaceAsync(
            db, backups, activation, pending.Email, request.Password, pending.ServerUrl,
            request.DeviceId, request.DeviceName, request.AppVersion, ct);

        pendingStore.Clear();
        return result;
    }

    private static async Task<IResult> CancelAsync(
        LonniiDbContext db, PendingRegistrationStore pending, CancellationToken ct)
    {
        // Only while nothing has been built: afterwards there is no registration to abandon.
        if (await db.Groupes.AnyAsync(ct))
            return Refuse("Cette installation est déjà configurée.", StatusCodes.Status409Conflict);

        pending.Clear();
        return Results.NoContent();
    }
}
