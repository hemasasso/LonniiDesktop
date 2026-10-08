using System.Security.Claims;
using Lonnii.Api.Features.Auth;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Remote;

/// <summary>
/// Sends a request that carries a remote session token to the shop's copy of its data, and
/// makes sure it can only read it.
///
/// <para>
/// Runs after authentication, before any endpoint opens a database. It leaves the copy's path on
/// the request, which the <c>LonniiDbContext</c> registration reads when the context is created,
/// so every existing endpoint and screen works against the copy unchanged.
/// </para>
///
/// <para>
/// Only GET and HEAD are allowed. The copy is rebuilt from the shop's next backup, so a write to
/// it would be lost, and worse, would look as if it had worked. Changes a remote administrator is
/// allowed to make (privileges, members) will go through a command queue the shop's host picks
/// up; its endpoints under <c>/api/remote/commands</c> are the one exception.
/// </para>
/// </summary>
public sealed class RemoteRoutingMiddleware(RequestDelegate next)
{
    public const string UpdatedHeader = "X-Replica-Updated";

    public async Task InvokeAsync(HttpContext http, RemoteSessionTokens tokens, ReplicaStore replicas)
    {
        var header = http.Request.Headers[GroupSessionService.HeaderName].ToString();
        if (!RemoteSessionTokens.IsRemote(header))
        {
            await next(http);
            return;
        }

        var ct = http.RequestAborted;
        var session = tokens.Verify(header, DateTime.UtcNow);
        var userId = http.User.FindFirstValue(TokenService.UserIdClaim);

        if (session is null || string.IsNullOrEmpty(userId) || session.UserId != userId)
        {
            await Refuse(http, StatusCodes.Status401Unauthorized, "Session distante invalide ou expirée");
            return;
        }

        var readOnly = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
        var isCommand = http.Request.Path.StartsWithSegments("/api/remote/commands");

        if (!readOnly && !isCommand)
        {
            await Refuse(http, StatusCodes.Status405MethodNotAllowed,
                "Lecture seule : la copie en ligne d'un espace ne peut pas être modifiée à distance.");
            return;
        }

        var replica = await replicas.EnsureAsync(session.GroupId, ct);
        if (replica is null)
        {
            await Refuse(http, StatusCodes.Status404NotFound, "Aucune copie de cet espace n'est disponible en ligne.");
            return;
        }

        http.Items[RemoteKeys.ReplicaPath] = replica.Path;
        http.Items[RemoteKeys.SnapshotAt] = replica.SnapshotAt;

        http.Response.OnStarting(() =>
        {
            http.Response.Headers[UpdatedHeader] = replica.SnapshotAt.ToString("O");
            return Task.CompletedTask;
        });

        await next(http);
    }

    private static Task Refuse(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new ApiError(message));
    }
}
