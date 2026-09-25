using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// First launch: turns the credentials file we issued into a working installation.
///
/// <para>
/// The order matters. The licence server is asked <em>before</em> anything is written, so a
/// refused activation leaves the database exactly as empty as it was and the shopkeeper can
/// simply try again. Seeding first and validating afterwards would leave a half-built
/// workspace behind on every failed attempt.
/// </para>
/// </summary>
public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/setup").WithTags("Setup");

        // Anonymous, and open only while the database has no workspace - see the guard
        // below. There is no account to authenticate against yet; creating one is the point.
        group.MapPost("/apply", ApplyAsync);
    }

    private static async Task<IResult> ApplyAsync(
        ApplyCredentialsRequest request,
        LonniiDbContext db,
        ILicenceServer licences,
        CancellationToken ct)
    {
        // Once a workspace exists this endpoint would be a way to graft a second one onto a
        // running shop, so it closes as soon as setup has happened once.
        if (await db.Groupes.AnyAsync(ct))
        {
            return Results.Json(
                new ApiError("Cette installation est déjà configurée."),
                statusCode: StatusCodes.Status409Conflict);
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.BadRequest(new ApiError("Identifiant de poste manquant."));

        byte[] fileContent;
        try
        {
            fileContent = Convert.FromBase64String(request.CredentialsFile);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new ApiError("Fichier d'identifiants illisible."));
        }

        StoreCredentials credentials;
        try
        {
            credentials = CredentialsFile.Unprotect(fileContent, request.Passphrase);
        }
        catch (CredentialsFileException e)
        {
            // Wrong passphrase, or a file someone has edited. Its own message says which is
            // possible without saying which happened.
            return Results.BadRequest(new ApiError(e.Message));
        }

        ActivationResponse activation;
        try
        {
            activation = await licences.ActivateAsync(
                credentials.ServerUrl ?? string.Empty,
                new ActivationRequest(
                    GroupId: credentials.GroupId,
                    Email: credentials.AdminEmail,
                    Password: credentials.AdminPassword,
                    DeviceId: request.DeviceId,
                    DeviceName: request.DeviceName,
                    Platform: DevicePlatforms.Windows,
                    AppVersion: request.AppVersion),
                ct);
        }
        catch (ActivationRefusedException e)
        {
            return Results.Json(
                new ApiError(e.Message),
                statusCode: (int?)e.Status ?? StatusCodes.Status403Forbidden);
        }

        // Everything below is local. The server's answer is the authority for mode,
        // max_devices and the currency - never the file, which the customer holds and
        // could have edited before we started checking signatures on it.
        var admin = new User
        {
            Email = credentials.AdminEmail,
            Password = BCrypt.Net.BCrypt.HashPassword(credentials.AdminPassword),
            IsVerified = true,
        };

        var groupe = new Groupe
        {
            Id = activation.GroupId,
            Nom = activation.GroupName,
            IdUserAdmin = admin.IdUser,
            Mode = activation.Mode,
            MaxDevices = activation.MaxDevices,
            CurrencyLabel = activation.CurrencyLabel,
            GestionAccess = true,
            // Kept so later licence refreshes and till registrations know where to call.
            LicenceServerUrl = credentials.ServerUrl,
            LastLicenceCheckAt = activation.ActivatedAt,
        };

        db.Users.Add(admin);
        db.PasswordHistories.Add(new PasswordHistory { IdUser = admin.IdUser, PasswordHash = admin.Password! });
        db.Groupes.Add(groupe);
        db.GroupMembers.Add(new GroupMember { IdGroupe = groupe.Id, IdUser = admin.IdUser });
        db.UserRoles.Add(new UserRole
        {
            UserId = admin.IdUser,
            GroupId = groupe.Id,
            Role = GroupRoles.Admin,
            AssignedBy = admin.IdUser,
        });

        // The machine that set the shop up is one of its machines. Recorded locally too, so
        // the local API can enforce the limit for the tills on the shop's own network - they
        // have no internet of their own to be counted over.
        db.Devices.Add(new Device
        {
            GroupId = groupe.Id,
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName,
            AppVersion = request.AppVersion,
        });

        await db.SaveChangesAsync(ct);

        return Results.Ok(new ApplyCredentialsResponse(
            GroupId: groupe.Id,
            GroupName: groupe.Nom,
            AdminEmail: admin.Email,
            Mode: groupe.Mode,
            MaxDevices: groupe.MaxDevices,
            DevicesUsed: activation.DevicesUsed));
    }
}
