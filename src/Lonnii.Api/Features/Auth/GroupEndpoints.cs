using Lonnii.Api.Features.Licensing;
using Lonnii.Api.Features.Live;
using System.Security.Claims;
using Lonnii.Api.Features.Images;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Data.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Auth;

/// <summary>Group listing and creation, membership, and opening a group session.</summary>
public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var groups = app.MapGroup("/api/groupes").WithTags("Groupes").RequireAuthorization();

        groups.MapGet("/", ListAsync);
        groups.MapPost("/", CreateAsync);
        groups.MapPost("/{groupId}/session", OpenSessionAsync);
        groups.MapDelete("/{groupId}", DeleteAsync).RequireAuthorization();
        groups.MapGet("/{groupId}/photo", GetPhotoAsync);

        var scoped = app.MapGroup("/api/groupe").WithTags("Groupes");
        scoped.MapGet("/members", ListMembersAsync).RequireGroupScope();
        scoped.MapPost("/members", AddMemberAsync).RequireGroupScope().RequireGroupAdmin();
        scoped.MapDelete("/members/{userId}", RemoveMemberAsync).RequireGroupScope().RequireGroupAdmin();
        scoped.MapPost("/members/{userId}/password", ResetMemberPasswordAsync)
            .RequireGroupScope().RequireGroupAdmin();
        scoped.MapPut("/currency", UpdateCurrencyAsync).RequireGroupScope().RequireGroupAdmin();
        scoped.MapDelete("/session", CloseSessionAsync).RequireGroupScope();

        scoped.MapPost("/photo", UploadPhotoAsync)
            .RequireGroupScope().RequireGroupAdmin().DisableAntiforgery();
        scoped.MapDelete("/photo", DeletePhotoAsync)
            .RequireGroupScope().RequireGroupAdmin();
    }

    /// <summary>Every group the caller created or belongs to.</summary>
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal, LonniiDbContext db, PrivilegeResolver privileges, CancellationToken ct)
    {
        var userId = principal.FindFirstValue(TokenService.UserIdClaim)!;

        var groupes = await db.Groupes
            .Where(g => g.IdUserAdmin == userId || g.Members.Any(m => m.IdUser == userId))
            .Select(g => new
            {
                Groupe = g,
                MemberCount = g.Members.Count,
            })
            .ToListAsync(ct);

        var result = new List<GroupeDto>(groupes.Count);
        foreach (var row in groupes)
        {
            var role = await privileges.GetRoleAsync(userId, row.Groupe.Id, ct);
            result.Add(ToDto(row.Groupe, role, row.Groupe.IdUserAdmin == userId, row.MemberCount));
        }

        return Results.Ok(result);
    }

    /// <summary>
    /// Creates a group. The caller becomes its Admin Général, which is what grants
    /// unconditional access - there is no separate privilege assignment step for them.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        CreateGroupeRequest request,
        ClaimsPrincipal principal,
        LonniiDbContext db,
        ILicenceServer licences,
        IConfiguration config,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = principal.FindFirstValue(TokenService.UserIdClaim)!;

        if (string.IsNullOrWhiteSpace(request.Nom))
            return Results.BadRequest(new ApiError("Le nom du groupe est requis"));

        var groupe = new Groupe
        {
            Nom = request.Nom.Trim(),
            IdUserAdmin = userId,
            GestionAccess = request.GestionAccess,
        };

        // Every espace is registered with Lonnii before it exists here, so we know all of them.
        // Skipped only when there is nowhere to register (no licence server configured) or the
        // switch is off - which is how development and the tests run.
        var serverUrl = await db.Groupes
            .Where(g => g.IdUserAdmin == userId && g.LicenceServerUrl != null && g.LicenceServerUrl != "")
            .Select(g => g.LicenceServerUrl)
            .FirstOrDefaultAsync(ct)
            ?? config["Lonnii:LicenceServerUrl"];

        Device? device = null;
        string? backupToken = null;

        if (config.GetValue("Lonnii:OnlineRegistration:Required", true) && !string.IsNullOrWhiteSpace(serverUrl))
        {
            var (refusal, registered) = await RegisterOnlineAsync(
                request, userId, serverUrl, db, licences, http, groupe, ct);
            if (refusal is not null) return refusal;

            device = registered.Device;
            backupToken = registered.BackupToken;
        }

        db.Groupes.Add(groupe);
        if (device is not null) db.Devices.Add(device);
        if (device is not null) LicenceGuard.StoreBackupToken(db, groupe.Id, device.DeviceId, backupToken);
        db.GroupMembers.Add(new GroupMember { IdGroupe = groupe.Id, IdUser = userId });
        db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            GroupId = groupe.Id,
            Role = GroupRoles.Admin,
            AssignedBy = userId,
        });

        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/groupes/{groupe.Id}",
            ToDto(groupe, GroupRoles.Admin, isAdminGeneral: true, memberCount: 1));
    }

    /// <summary>
    /// Registers a new espace with the licence server and fills <paramref name="groupe"/> with what
    /// it answers - its id above all, so the same espace is the same everywhere. Then activates
    /// this machine for it, which binds the machine and brings back the mode and device allowance.
    /// </summary>
    private static async Task<(IResult? Refusal, (Device? Device, string? BackupToken))> RegisterOnlineAsync(
        CreateGroupeRequest request,
        string userId,
        string serverUrl,
        LonniiDbContext db,
        ILicenceServer licences,
        HttpContext http,
        Groupe groupe,
        CancellationToken ct)
    {
        static IResult Refuse(string message, int status) =>
            Results.Json(new ApiError(message), statusCode: status);

        if (string.IsNullOrEmpty(request.Password))
        {
            return (Refuse("Saisissez votre mot de passe pour enregistrer le nouvel espace auprès de Lonnii.",
                StatusCodes.Status400BadRequest), default);
        }

        var deviceId = http.Request.Headers["x-device-id"].ToString();
        if (string.IsNullOrWhiteSpace(deviceId))
            return (Refuse("Identifiant de poste manquant.", StatusCodes.Status400BadRequest), default);

        var email = await db.Users.Where(u => u.IdUser == userId).Select(u => u.Email).FirstAsync(ct);

        try
        {
            var registered = await licences.RegisterEspaceAsync(
                serverUrl, new RegistrationEspaceRequest(email, request.Password, groupe.Nom, deviceId), ct);

            var activation = await licences.ActivateAsync(
                serverUrl,
                new ActivationRequest(registered.GroupId, email, request.Password, deviceId,
                    Environment.MachineName, DevicePlatforms.Windows),
                ct);

            groupe.Id = activation.GroupId;
            groupe.Nom = activation.GroupName;
            groupe.Mode = activation.Mode;
            groupe.MaxDevices = activation.MaxDevices;
            groupe.CurrencyLabel = activation.CurrencyLabel;
            groupe.LicenceServerUrl = serverUrl;
            groupe.LastLicenceCheckAt = activation.ActivatedAt;

            // This machine is one of the new espace's machines, recorded here too so the local
            // API can enforce the limit for the tills that have no internet of their own.
            var device = new Device { GroupId = groupe.Id, DeviceId = deviceId, DeviceName = Environment.MachineName };

            return (null, (device, activation.BackupToken));
        }
        catch (ActivationRefusedException e)
        {
            return (Refuse(e.Message, (int?)e.Status ?? StatusCodes.Status503ServiceUnavailable), default);
        }
    }

    /// <summary>Opens a group session and returns the token for the <c>x-group-session</c> header.</summary>
    private static async Task<IResult> OpenSessionAsync(
        string groupId,
        ClaimsPrincipal principal,
        LonniiDbContext db,
        GroupSessionService sessions,
        PrivilegeResolver privileges,
        LicenceGuard licence,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = principal.FindFirstValue(TokenService.UserIdClaim)!;

        var session = await sessions.OpenAsync(userId, groupId, ct);
        if (session is null)
            return Results.Json(new ApiError("Accès refusé à ce groupe"), statusCode: StatusCodes.Status403Forbidden);

        var groupe = await db.Groupes.FirstAsync(g => g.Id == groupId, ct);
        if (groupe.IsBlocked)
            return Results.Json(new ApiError(groupe.BlockReason ?? "Ce groupe est bloqué."),
                statusCode: StatusCodes.Status403Forbidden);

        var licenceStatus = await licence.CheckAsync(groupe, ct);
        if (licenceStatus.Expired)
        {
            return Results.Json(new ApiError(licenceStatus.Message ?? LicenceGuard.ExpiredMessage),
                statusCode: StatusCodes.Status423Locked);
        }

        // --- Machine check ---------------------------------------------------------
        // This is what stops a copied installation. A copy runs on different hardware, so
        // its fingerprint is one nobody bound, and it is refused here until it registers -
        // which reaches our server and is counted against the shop's allowance.
        //
        // Only enforced once the workspace has machines bound, which is the licensed state
        // a setup creates. A workspace made through POST /api/groupes has none, and locking
        // that out would break development and the first run of a self-built host.
        var bound = await db.Devices.AnyAsync(d => d.GroupId == groupId && d.RevokedAt == null, ct);

        if (bound)
        {
            var deviceId = http.Request.Headers["x-device-id"].ToString();

            var device = string.IsNullOrWhiteSpace(deviceId)
                ? null
                : await db.Devices.FirstOrDefaultAsync(
                    d => d.GroupId == groupId && d.DeviceId == deviceId && d.RevokedAt == null, ct);

            if (device is null)
            {
                return Results.Json(
                    new ApiError("Ce poste n'est pas autorisé pour cet espace. Enregistrez-le avec un compte administrateur."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            device.LastSeenAt = DateTime.UtcNow;
            device.LastIpAddress = http.Connection.RemoteIpAddress?.ToString();
            await db.SaveChangesAsync(ct);
        }

        var memberCount = await db.GroupMembers.CountAsync(m => m.IdGroupe == groupId, ct);
        var role = await privileges.GetRoleAsync(userId, groupId, ct);

        return Results.Ok(new GroupSessionResponse(
            session.SessionToken,
            session.ExpiresAt,
            ToDto(groupe, role, groupe.IdUserAdmin == userId, memberCount)));
    }

    private static async Task<IResult> CloseSessionAsync(
        GroupScope scope, GroupSessionService sessions, CancellationToken ct)
    {
        await sessions.CloseAsync(scope.SessionToken, ct);
        return Results.NoContent();
    }

    /// <summary>Members of the current group, each with their resolved role.</summary>
    private static async Task<IResult> ListMembersAsync(
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var creatorId = await db.Groupes
            .Where(g => g.Id == scope.GroupId)
            .Select(g => g.IdUserAdmin)
            .FirstAsync(ct);

        var members = await db.GroupMembers
            .Where(m => m.IdGroupe == scope.GroupId)
            .Join(db.Users, m => m.IdUser, u => u.IdUser, (m, u) => new { m, u })
            .GroupJoin(
                db.UserRoles.Where(r => r.GroupId == scope.GroupId && r.IsActive),
                x => x.u.IdUser,
                r => r.UserId,
                (x, roles) => new { x.m, x.u, Role = roles.Select(r => r.Role).FirstOrDefault() })
            .ToListAsync(ct);

        var result = members.Select(x => new GroupMemberDto(
            x.u.IdUser,
            x.u.Email,
            x.u.Username,
            x.u.FirstName,
            x.u.LastName,
            x.u.IdUser == creatorId ? GroupRoles.Admin : x.Role ?? GroupRoles.Member,
            x.u.IdUser == creatorId,
            x.m.JoinedAt,
            x.u.LastSeen))
            .OrderByDescending(m => m.IsAdminGeneral)
            .ThenBy(m => m.Email)
            .ToList();

        return Results.Ok(result);
    }

    /// <summary>
    /// Adds someone to the group and grants the baseline option privileges, reproducing
    /// what the web app does when a member joins.
    ///
    /// If no account matches the identifier and a password was supplied, the account is
    /// created here. That is the only way accounts come into being after the first one:
    /// self-service registration is closed on a local network, so an administrator
    /// onboarding a till operator does it in this single step.
    /// </summary>
    private static async Task<IResult> AddMemberAsync(
        AddMemberRequest request,
        GroupScope scope,
        LonniiDbContext db,
        DatabaseSeeder seeder,
        ShopChangeNotifier changes,
        CancellationToken ct)
    {
        var identifier = request.Identifier.Trim();
        var email = identifier.ToLowerInvariant();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email || u.Username == identifier, ct);

        if (user is null)
        {
            if (string.IsNullOrEmpty(request.Password))
            {
                return Results.NotFound(new ApiError(
                    "Aucun compte ne correspond. Indiquez un mot de passe pour créer le compte."));
            }

            var (created, error) = await AuthEndpoints.CreateAccountAsync(
                new RegisterRequest(
                    Email: identifier,
                    Password: request.Password,
                    Username: request.Username,
                    FirstName: request.FirstName,
                    LastName: request.LastName,
                    Phone: request.Phone),
                db, ct);

            if (error is not null) return error;
            user = created!;
        }
        else if (await db.GroupMembers.AnyAsync(m => m.IdGroupe == scope.GroupId && m.IdUser == user.IdUser, ct))
        {
            return Results.Conflict(new ApiError("Cet utilisateur est déjà membre du groupe"));
        }

        db.GroupMembers.Add(new GroupMember { IdGroupe = scope.GroupId, IdUser = user.IdUser });
        db.UserRoles.Add(new UserRole
        {
            UserId = user.IdUser,
            GroupId = scope.GroupId,
            Role = GroupRoles.Member,
            AssignedBy = scope.UserId,
        });

        await db.SaveChangesAsync(ct);
        await seeder.GrantDefaultOptionPrivilegesAsync(user.IdUser, scope.GroupId, scope.UserId, ct);
        changes.Bump(scope.GroupId);

        return Results.Ok(AuthEndpoints.ToDto(user));
    }

    /// <summary>
    /// Resets a member's password, for when they have forgotten it or have left and the
    /// account is being handed to someone else. There is no email reset on a local host,
    /// so this is the only recovery path.
    ///
    /// Two guards stop this becoming a way to climb: the group creator's password can
    /// only be changed by the creator themselves, and a sub_admin cannot reset another
    /// administrator's password. Without those, any sub_admin could take over the
    /// workspace by resetting the owner and signing in as them.
    /// </summary>
    private static async Task<IResult> ResetMemberPasswordAsync(
        string userId,
        ResetMemberPasswordRequest request,
        GroupScope scope,
        LonniiDbContext db,
        PrivilegeResolver privileges,
        CancellationToken ct)
    {
        if (request.NewPassword.Length < AuthEndpoints.MinimumPasswordLength)
        {
            return Results.BadRequest(new ApiError(
                $"Le mot de passe doit contenir au moins {AuthEndpoints.MinimumPasswordLength} caractères"));
        }

        var isMember = await db.GroupMembers
            .AnyAsync(m => m.IdGroupe == scope.GroupId && m.IdUser == userId, ct);
        if (!isMember) return Results.NotFound(new ApiError("Ce membre est introuvable dans le groupe"));

        var creatorId = await db.Groupes
            .Where(g => g.Id == scope.GroupId).Select(g => g.IdUserAdmin).FirstAsync(ct);

        if (userId == creatorId && scope.UserId != creatorId)
        {
            return Results.Json(
                new ApiError("Seul le créateur de l'espace peut modifier son propre mot de passe"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Only the creator may reset another administrator.
        var targetRole = await privileges.GetRoleAsync(userId, scope.GroupId, ct);
        if (GroupRoles.IsAdminRole(targetRole) && userId != scope.UserId && !scope.IsAdminGeneral)
        {
            return Results.Json(
                new ApiError("Seul le créateur de l'espace peut réinitialiser le mot de passe d'un administrateur"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.IdUser == userId, ct);
        if (user is null) return Results.NotFound(new ApiError("Utilisateur introuvable"));

        await AuthEndpoints.ApplyPasswordChangeAsync(user, request.NewPassword, db, ct);

        return Results.NoContent();
    }

    /// <summary>Removes a member. The group creator cannot be removed.</summary>
    private static async Task<IResult> RemoveMemberAsync(
        string userId, GroupScope scope, LonniiDbContext db, ShopChangeNotifier changes, CancellationToken ct)
    {
        var creatorId = await db.Groupes
            .Where(g => g.Id == scope.GroupId)
            .Select(g => g.IdUserAdmin)
            .FirstAsync(ct);

        if (userId == creatorId)
            return Results.BadRequest(new ApiError("Le créateur du groupe ne peut pas être retiré"));

        var removed = await db.GroupMembers
            .Where(m => m.IdGroupe == scope.GroupId && m.IdUser == userId)
            .ExecuteDeleteAsync(ct);

        if (removed == 0) return Results.NotFound(new ApiError("Ce membre est introuvable dans le groupe"));

        // Clear the member's role, grants and open sessions so access ends immediately.
        await db.UserRoles.Where(r => r.GroupId == scope.GroupId && r.UserId == userId).ExecuteDeleteAsync(ct);
        await db.OptionUserPrivileges.Where(p => p.GroupId == scope.GroupId && p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.GestionUserPrivileges.Where(p => p.GroupId == scope.GroupId && p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.GroupeSessions.Where(s => s.GroupId == scope.GroupId && s.UserId == userId).ExecuteDeleteAsync(ct);
        changes.Bump(scope.GroupId);

        return Results.NoContent();
    }

    /// <summary>
    /// Renames the currency shown after every amount in this workspace. Group-admin only,
    /// so a till operator cannot silently relabel prices.
    /// </summary>
    private static async Task<IResult> UpdateCurrencyAsync(
        UpdateCurrencyRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var label = request.CurrencyLabel.Trim();
        if (string.IsNullOrWhiteSpace(label) || label.Length > 12)
            return Results.BadRequest(new ApiError("La devise doit contenir entre 1 et 12 caractères."));

        var groupe = await db.Groupes.FirstAsync(g => g.Id == scope.GroupId, ct);
        groupe.CurrencyLabel = label;
        groupe.CurrencyBefore = request.CurrencyBefore;
        await db.SaveChangesAsync(ct);

        var memberCount = await db.GroupMembers.CountAsync(m => m.IdGroupe == scope.GroupId, ct);
        return Results.Ok(ToDto(groupe, scope.Privileges.Role, scope.IsAdminGeneral, memberCount));
    }

    // --- Espace deletion ---------------------------------------------------------

    /// <summary>
    /// Permanently deletes an espace and all its data. Restricted to the Admin Général
    /// (the creator): only someone with complete, unconditional ownership of the workspace
    /// can destroy it. A sub_admin cannot delete it even with every privilege granted.
    ///
    /// Cascade order matters on SQLite, which enforces FK constraints. Children are removed
    /// before their parents: vente items before ventes, amortissement schedules before
    /// immobilisations, caisse transactions before caisse sessions, etc.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        string groupId,
        ClaimsPrincipal principal,
        LonniiDbContext db,
        ImageStorageService images,
        CancellationToken ct)
    {
        var userId = principal.FindFirstValue(TokenService.UserIdClaim)!;

        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (groupe is null) return Results.NotFound(new ApiError("Espace introuvable."));

        if (groupe.IdUserAdmin != userId)
            return Results.Json(
                new ApiError("Seul le créateur de l'espace peut le supprimer."),
                statusCode: StatusCodes.Status403Forbidden);

        // --- Children first (FK order) ---

        // Ventes children - VenteItem and PaiementVente have no GroupId; they are joined
        // through VenteId, so we filter via a subquery on the parent.
        var venteIds = db.Ventes.Where(v => v.GroupId == groupId).Select(v => v.Id);
        await db.VenteItems.Where(x => venteIds.Contains(x.VenteId)).ExecuteDeleteAsync(ct);
        await db.PaiementsVentes.Where(x => venteIds.Contains(x.VenteId)).ExecuteDeleteAsync(ct);
        await db.VentesUserActivities.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GroupePayments.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.Ventes.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        // Caisse children
        await db.CaisseTransactions.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.Caisses.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        // Stock children
        await db.StockHistories.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.StockSnapshots.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.StockUserActivities.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.Products.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.Categories.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.Suppliers.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.StockSettings.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        // Charges
        await db.Charges.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.ChargeCategories.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        // Bilan / Comptabilite children
        await db.AmortissementEcheances.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.Immobilisations.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.BilanEcritures.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.BilanComptes.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.ResultatComptes.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.ComptabiliteParametres.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        // Programme
        await db.ProgrammeEntries.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.ProgrammeAnnouncements.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        // Audit
        await db.MemberWorkLogs.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        // Clients
        await db.Clients.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        // Receipt settings (images deleted below)
        var parametres = await db.VentesParametres.FirstOrDefaultAsync(p => p.GroupeId == groupId, ct);

        // Devices
        await db.Devices.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);

        // Memberships and privileges
        await db.OptionPrivilegeAudits.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GestionPrivilegeAudits.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.PrivilegeAudits.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.OptionUserPrivileges.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GestionUserPrivileges.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GestionUserRoles.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.UserPrivileges.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.UserRoles.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GroupeSessions.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.GroupMembers.Where(x => x.IdGroupe == groupId).ExecuteDeleteAsync(ct);

        // Receipt settings row
        if (parametres is not null) db.VentesParametres.Remove(parametres);

        // The espace itself
        db.Groupes.Remove(groupe);
        await db.SaveChangesAsync(ct);

        // Clean up all stored images for this espace (best-effort, after the DB commit).
        images.DeleteIfOwned(parametres?.LogoPath);
        images.DeleteIfOwned(parametres?.QrCodePath);
        images.DeleteIfOwned(groupe.PhotoUrl);

        return Results.NoContent();
    }

    // --- Espace photo ------------------------------------------------------------

    /// <summary>
    /// Stores a cover photo for this espace. Shown in the group picker to help a user
    /// with several espaces tell them apart at a glance. Admin-only so a till operator
    /// cannot silently rebrand the workspace.
    /// </summary>
    private static async Task<IResult> UploadPhotoAsync(
        IFormFile file, GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct)
    {
        if (file.Length == 0 || file.Length > ImageStorageService.MaxUploadBytes)
            return Results.BadRequest(new ApiError("Le fichier est vide ou trop volumineux (8 Mo max)."));

        var groupe = await db.Groupes.FirstAsync(g => g.Id == scope.GroupId, ct);

        string url;
        await using (var stream = file.OpenReadStream())
        {
            try
            {
                url = images.Save(
                    ImageStorageService.Folders.EspacePhotos,
                    scope.GroupId, stream, groupe.PhotoUrl, lossless: false);
            }
            catch (InvalidImageException)
            {
                return Results.BadRequest(new ApiError("Le fichier n'est pas une image valide."));
            }
        }

        groupe.PhotoUrl = url;
        await db.SaveChangesAsync(ct);

        var memberCount = await db.GroupMembers.CountAsync(m => m.IdGroupe == scope.GroupId, ct);
        return Results.Ok(ToDto(groupe, scope.Privileges.Role, scope.IsAdminGeneral, memberCount));
    }

    /// <summary>
    /// Streams an espace's cover photo to a signed-in member of that espace. This is what the
    /// group picker uses: it shows the photos before any espace is opened, so there is no group
    /// session yet and the ordinary <c>/api/images</c> route (which needs one) answers 401. Without
    /// this the photo showed right after an upload - which had opened a session - and vanished
    /// the next time the picker was shown. Same answer for "no such espace", "not yours" and
    /// "no photo", so it cannot be used to probe other workspaces.
    /// </summary>
    private static async Task<IResult> GetPhotoAsync(
        string groupId, ClaimsPrincipal principal, LonniiDbContext db, ImageStorageService images, CancellationToken ct)
    {
        var userId = principal.FindFirstValue(TokenService.UserIdClaim)!;

        var photoUrl = await db.Groupes
            .Where(g => g.Id == groupId && (g.IdUserAdmin == userId || g.Members.Any(m => m.IdUser == userId)))
            .Select(g => g.PhotoUrl)
            .FirstOrDefaultAsync(ct);

        var fileName = photoUrl?.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(fileName)) return Results.NotFound();

        var stream = images.OpenRead(ImageStorageService.Folders.EspacePhotos, fileName);
        return stream is null
            ? Results.NotFound()
            : Results.File(stream, ImageStorageService.ContentTypeFor(fileName));
    }

    /// <summary>Removes the espace cover photo.</summary>
    private static async Task<IResult> DeletePhotoAsync(
        GroupScope scope, LonniiDbContext db, ImageStorageService images, CancellationToken ct)
    {
        var groupe = await db.Groupes.FirstAsync(g => g.Id == scope.GroupId, ct);
        images.DeleteIfOwned(groupe.PhotoUrl);
        groupe.PhotoUrl = null;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static GroupeDto ToDto(Groupe g, string role, bool isAdminGeneral, int memberCount) => new(
        g.Id, g.Nom, isAdminGeneral, role, g.GestionAccess,
        g.PrestationsEnabled, g.PrestationsLocation, memberCount, g.CreatedAt, g.CurrencyLabel, g.CurrencyBefore,
        g.PhotoUrl);
}
