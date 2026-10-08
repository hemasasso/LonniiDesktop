using System.Security.Claims;
using Lonnii.Api.Features.Auth;
using Lonnii.Api.Features.Members;
using Lonnii.Data;
using Lonnii.Data.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Remote;

/// <summary>
/// Remote access for online-mode shops: an administrator signs in as usual, then opens a remote
/// session for their shop and reads its data from the copy OCI holds (see <see cref="ReplicaStore"/>).
/// </summary>
public static class RemoteEndpoints
{
    public static void MapRemoteEndpoints(this IEndpointRouteBuilder app)
    {
        var remote = app.MapGroup("/api/remote").WithTags("Remote");

        remote.MapPost("/{groupId}/session", OpenSessionAsync).RequireAuthorization();
        remote.MapGet("/info", Info).RequireGroupScope();

        // Changes asked for from afar are queued for the shop's host, not applied here: the
        // shop's own database is the one that counts, and the copy is rebuilt from it.
        remote.MapPost("/commands", QueueCommandAsync).RequireGroupScope();
        remote.MapGet("/commands", ListCommands).RequireGroupScope();
    }

    /// <summary>
    /// Opens a remote session. Unlike an ordinary session it asks for no registered machine - a
    /// phone has none - and asks instead for an administrator of an online shop whose
    /// subscription is current, because the machine check is what a remote caller cannot meet and
    /// the role and subscription are what make the access safe to give.
    /// </summary>
    private static async Task<IResult> OpenSessionAsync(
        string groupId,
        ClaimsPrincipal principal,
        LonniiDbContext db,
        RemoteSessionTokens tokens,
        ReplicaStore replicas,
        CancellationToken ct)
    {
        var userId = principal.FindFirstValue(TokenService.UserIdClaim)!;

        var groupe = await db.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (groupe is null || groupe.IsDeleted)
            return Results.Json(new ApiError("Accès refusé à cet espace"), statusCode: StatusCodes.Status403Forbidden);

        if (groupe.IsBlocked)
        {
            return Results.Json(new ApiError(groupe.BlockReason ?? "Cet espace est bloqué. Contactez le support."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!DeploymentModes.RequiresSubscription(groupe.Mode))
        {
            return Results.Json(
                new ApiError("L'accès à distance est réservé aux espaces en mode en ligne."),
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

        var replica = await replicas.EnsureAsync(groupId, ct);
        if (replica is null)
        {
            return Results.Json(
                new ApiError("Aucune copie de cet espace n'est encore disponible en ligne. " +
                             "Elle apparaît après la première sauvegarde du poste de la boutique."),
                statusCode: StatusCodes.Status404NotFound);
        }

        // Who is an administrator is decided by the shop's own data, not by the registration
        // record on this server: roles are granted and removed on the shop's host.
        //
        // The caller's account here and their account in the shop are two rows with two ids whenever
        // the shop's account was made on the shop's computer. They are matched by email, and only for
        // an account whose email this server has verified: otherwise anyone could sign up here with a
        // shop administrator's address and be taken for them.
        var account = await db.Users.AsNoTracking()
            .Where(u => u.IdUser == userId)
            .Select(u => new { u.Email, u.IsVerified })
            .FirstOrDefaultAsync(ct);

        if (account is null || !account.IsVerified || string.IsNullOrWhiteSpace(account.Email))
        {
            return Results.Json(
                new ApiError("Adresse e-mail non vérifiée : l'accès à distance n'est pas possible."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        await using var copy = new LonniiDbContext(ReplicaStore.OptionsFor(replica.Path));

        var email = account.Email.Trim().ToLowerInvariant();
        var shopUserId = await copy.Users.AsNoTracking()
            .Where(u => u.Email.ToLower() == email)
            .Select(u => u.IdUser)
            .FirstOrDefaultAsync(ct);

        if (shopUserId is null)
        {
            return Results.Json(
                new ApiError("Aucun compte avec cette adresse e-mail n'existe dans cette boutique."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var privileges = await new PrivilegeResolver(copy).ResolveAsync(shopUserId, groupId, ct);

        if (!privileges.IsAdminGeneral && !privileges.IsAdmin)
        {
            return Results.Json(
                new ApiError("L'accès à distance est réservé aux administrateurs de l'espace."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var shop = await copy.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct) ?? groupe;
        var memberCount = await copy.GroupMembers.CountAsync(m => m.IdGroupe == groupId, ct);

        var (token, expires) = tokens.Create(groupId, userId, shopUserId, DateTime.UtcNow);

        return Results.Ok(new RemoteSessionResponse(
            token,
            expires,
            replica.SnapshotAt,
            new GroupeDto(
                shop.Id, shop.Nom, privileges.IsAdminGeneral, privileges.Role, shop.GestionAccess,
                shop.PrestationsEnabled, shop.PrestationsLocation, memberCount, shop.CreatedAt,
                shop.CurrencyLabel, shop.CurrencyBefore, shop.PhotoUrl)));
    }

    /// <summary>What the screen needs to say how old what it shows is.</summary>
    private static IResult Info(HttpContext http) =>
        Results.Ok(new RemoteInfoResponse(
            IsCopy: http.Items.ContainsKey(RemoteKeys.ReplicaPath),
            SnapshotAt: http.Items[RemoteKeys.SnapshotAt] as DateTime?));

    /// <summary>
    /// Queues a privilege or role change for the shop's host.
    ///
    /// <para>
    /// The request is checked first against the copy, by really making the change inside a
    /// transaction and rolling it back - so it is refused now, with the same reason the shop would
    /// give (unknown member, admin-only privilege, only the creator may change admin roles),
    /// rather than sitting in the queue until the host rejects it. The copy itself is never
    /// changed.
    /// </para>
    /// </summary>
    private static async Task<IResult> QueueCommandAsync(
        RemoteCommandRequest request, GroupScope scope, LonniiDbContext db, HttpContext http,
        RemoteCommandStore commands, CancellationToken ct)
    {
        if (!http.Items.ContainsKey(RemoteKeys.ReplicaPath))
        {
            return Results.Json(
                new ApiError("Cette demande n'est possible que depuis une session à distance."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!scope.IsAdmin && !scope.IsAdminGeneral)
        {
            return Results.Json(
                new ApiError("L'accès à distance est réservé aux administrateurs de l'espace."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        PrivilegeChangeResult check;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            switch (request.Type)
            {
                case RemoteCommandTypes.Privilege:
                    if (string.IsNullOrWhiteSpace(request.PrivilegeName) || request.Granted is null
                        || request.Catalog is not ("gestion" or "option"))
                    {
                        return Results.BadRequest(new ApiError("Privilège, catalogue et action sont requis."));
                    }

                    check = await PrivilegeChanges.SetPrivilegeAsync(
                        db, scope.GroupId, scope.UserId,
                        new SetPrivilegeRequest(request.UserId, request.PrivilegeName, request.Granted.Value),
                        isGestion: request.Catalog == "gestion", PrivilegeChanges.RemoteReasonPrefix.Trim(), ct);
                    break;

                case RemoteCommandTypes.Role:
                    if (string.IsNullOrWhiteSpace(request.Role))
                        return Results.BadRequest(new ApiError("Le rôle est requis."));

                    check = await PrivilegeChanges.SetRoleAsync(
                        db, scope.GroupId, scope.UserId, scope.IsAdminGeneral,
                        new SetRoleRequest(request.UserId, request.Role),
                        PrivilegeChanges.RemoteReasonPrefix.Trim(), ct);
                    break;

                default:
                    return Results.BadRequest(new ApiError($"Type de demande inconnu : {request.Type}"));
            }

            await tx.RollbackAsync(ct);
        }

        if (!check.Ok) return check.ToResult();

        var names = await db.Users.AsNoTracking()
            .Where(u => u.IdUser == request.UserId || u.IdUser == scope.UserId)
            .Select(u => new { u.IdUser, u.FirstName, u.LastName, u.Username, u.Email })
            .ToListAsync(ct);

        string? NameOf(string id) => names.FirstOrDefault(n => n.IdUser == id) is { } n
            ? (string.Join(' ', new[] { n.FirstName, n.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } full
                ? full
                : n.Username ?? n.Email)
            : null;

        var command = commands.Enqueue(scope.GroupId, new RemoteCommandDto(
            Id: Guid.NewGuid().ToString(),
            Type: request.Type,
            UserId: request.UserId,
            TargetName: NameOf(request.UserId),
            PrivilegeName: request.PrivilegeName,
            Catalog: request.Catalog,
            Granted: request.Granted,
            Role: request.Role,
            RequestedBy: scope.UserId,
            RequestedByName: NameOf(scope.UserId),
            RequestedAt: DateTime.UtcNow,
            Status: RemoteCommandStatuses.Pending));

        return Results.Accepted($"/api/remote/commands/{command.Id}", command);
    }

    /// <summary>What was asked for recently and what became of it, newest first.</summary>
    private static IResult ListCommands(GroupScope scope, RemoteCommandStore commands) =>
        Results.Ok(new RemoteCommandsResponse(commands.Recent(scope.GroupId)));
}
