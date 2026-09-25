using System.Security.Claims;
using Lonnii.Api.Security;
using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The machines bound to a workspace.
///
/// <para>
/// This is what closes the copying hole. An installation copied to another shop runs on
/// different hardware, so every machine there is one nobody bound - each has to register,
/// registering reaches our server, and the attempt either exceeds the allowance and fails
/// or consumes the original shop's own slots. Either way it surfaces.
/// </para>
/// </summary>
public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").WithTags("Devices");

        // Anonymous: a machine that is not yet bound cannot open a group session, so it has
        // no way to reach an authenticated endpoint. The administrator's credentials in the
        // request are what authorise it.
        group.MapPost("/register", RegisterAsync);

        var scoped = app.MapGroup("/api/devices")
            .WithTags("Devices")
            .RequireAuthorization();

        scoped.MapGet("/", ListAsync).RequireGroupScope();
        scoped.MapDelete("/{id}", RevokeAsync).RequireGroupScope().RequireGroupAdmin();
    }

    private static async Task<IResult> RegisterAsync(
        RegisterDeviceRequest request,
        LonniiDbContext db,
        ILicenceServer licences,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.BadRequest(new ApiError("Identifiant de poste manquant."));

        var groupe = await db.Groupes.FirstOrDefaultAsync(ct);
        if (groupe is null)
            return Results.BadRequest(new ApiError("Cette installation n'est pas encore configurée."));

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        var refusal = Results.Json(
            new ApiError("Identifiants incorrects."),
            statusCode: StatusCodes.Status403Forbidden);

        if (user?.Password is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.Password))
            return refusal;

        // Only an administrator adds a machine. A cashier who could would make the allowance
        // meaningless - anyone could bring their own laptop.
        var isAdmin = groupe.IdUserAdmin == user.IdUser
            || await db.UserRoles.AnyAsync(
                r => r.UserId == user.IdUser && r.GroupId == groupe.Id
                     && r.IsActive && r.Role == Lonnii.Shared.Security.GroupRoles.Admin, ct);

        if (!isAdmin) return refusal;

        var existing = await db.Devices
            .FirstOrDefaultAsync(d => d.GroupId == groupe.Id && d.DeviceId == request.DeviceId, ct);

        if (existing is { RevokedAt: null })
            return Results.Ok(ToDto(existing, request.DeviceId));

        // The count that decides is our server's, never this one's: a shop adding rows to
        // its own database would otherwise grant itself machines.
        try
        {
            var activation = await licences.ActivateAsync(
                groupe.LicenceServerUrl ?? string.Empty,
                new ActivationRequest(
                    GroupId: groupe.Id,
                    Email: email,
                    Password: request.Password,
                    DeviceId: request.DeviceId,
                    DeviceName: request.DeviceName,
                    Platform: DevicePlatforms.Windows,
                    AppVersion: request.AppVersion),
                ct);

            // Our server is the authority for the allowance too, so a change made there
            // lands here as a side effect of adding a machine.
            groupe.MaxDevices = activation.MaxDevices;
        }
        catch (ActivationRefusedException e)
        {
            return Results.Json(
                new ApiError(e.Message),
                statusCode: (int?)e.Status ?? StatusCodes.Status403Forbidden);
        }

        var device = existing ?? new Device { GroupId = groupe.Id, DeviceId = request.DeviceId };
        device.DeviceName = request.DeviceName ?? device.DeviceName;
        device.AppVersion = request.AppVersion;
        device.LastSeenAt = DateTime.UtcNow;
        device.RevokedAt = null;
        device.RevokedReason = null;

        if (existing is null) db.Devices.Add(device);

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(device, request.DeviceId));
    }

    private static async Task<IResult> ListAsync(
        GroupScope scope, LonniiDbContext db, HttpContext http, CancellationToken ct)
    {
        var groupe = await db.Groupes.FirstAsync(g => g.Id == scope.GroupId, ct);

        var current = http.Request.Headers["x-device-id"].ToString();

        var devices = await db.Devices
            .Where(d => d.GroupId == scope.GroupId && d.RevokedAt == null)
            .OrderBy(d => d.RegisteredAt)
            .ToListAsync(ct);

        return Results.Ok(new DeviceListResponse(
            groupe.MaxDevices,
            devices.Count,
            [.. devices.Select(d => ToDto(d, current))]));
    }

    /// <summary>
    /// Unbinds a machine, freeing its slot - normally a till that broke and was replaced.
    ///
    /// <para>
    /// The shop's own administrator may do this, and safely: it can never take them above
    /// the number we granted, so it removes a support call without weakening anything.
    /// Raising <see cref="Groupe.MaxDevices"/> stays with us.
    /// </para>
    /// </summary>
    private static async Task<IResult> RevokeAsync(
        string id,
        GroupScope scope,
        ClaimsPrincipal principal,
        LonniiDbContext db,
        CancellationToken ct)
    {
        var device = await db.Devices
            .FirstOrDefaultAsync(d => d.Id == id && d.GroupId == scope.GroupId, ct);

        if (device is null) return Results.NotFound(new ApiError("Poste introuvable"));

        device.RevokedAt = DateTime.UtcNow;
        device.RevokedReason = "Retiré par un administrateur";

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static DeviceDto ToDto(Device d, string? currentDeviceId) => new(
        d.Id, d.DeviceId, d.DeviceName, d.Platform, d.AppVersion,
        d.RegisteredAt, d.LastSeenAt,
        IsCurrent: d.DeviceId == currentDeviceId);
}
