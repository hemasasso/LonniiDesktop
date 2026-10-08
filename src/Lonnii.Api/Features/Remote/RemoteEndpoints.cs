using System.Security.Claims;
using Lonnii.Api.Features.Auth;
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
        await using var copy = new LonniiDbContext(ReplicaStore.OptionsFor(replica.Path));
        var privileges = await new PrivilegeResolver(copy).ResolveAsync(userId, groupId, ct);

        if (!privileges.IsAdminGeneral && !privileges.IsAdmin)
        {
            return Results.Json(
                new ApiError("L'accès à distance est réservé aux administrateurs de l'espace."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var shop = await copy.Groupes.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct) ?? groupe;
        var memberCount = await copy.GroupMembers.CountAsync(m => m.IdGroupe == groupId, ct);

        var (token, expires) = tokens.Create(groupId, userId, DateTime.UtcNow);

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
}
