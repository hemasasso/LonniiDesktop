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
/// The copy itself is only ever read: it is rebuilt from the shop's next backup, so a write to it
/// would be lost, and worse, would look as if it had worked. A change made from afar on one of the
/// shop's screens (<see cref="IsReplayable"/>) is kept instead, exactly as sent, and answered 202:
/// the shop's host collects it and replays it on itself as the sender (see
/// <see cref="RemoteRequestReplayer"/>). The typed requests under <c>/api/remote/commands</c> go
/// through as before.
/// </para>
/// </summary>
public sealed class RemoteRoutingMiddleware(RequestDelegate next)
{
    public const string UpdatedHeader = "X-Replica-Updated";

    /// <summary>The largest change body kept for a shop; a sale or a settings form is a few kilobytes.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// The shop's own screens, whose changes can be replayed on its host. Everything about accounts,
    /// machines, licences, backups and the espace's very existence stays out: those are not a shop
    /// screen's change, and some would let a remote session reach past the shop.
    /// </summary>
    private static readonly string[] ReplayablePrefixes =
    [
        "/api/stock", "/api/ventes", "/api/caisse", "/api/charges", "/api/marges", "/api/amortissement",
        "/api/bilan", "/api/programme", "/api/parametres", "/api/privileges", "/api/groupe",
        ProfilePath,
    ];

    /// <summary>The person's own profile: the one change made in both places at once (see below).</summary>
    private const string ProfilePath = "/api/auth/profile";

    private static readonly string[] NeverReplayed =
    [
        "/api/parametres/cloud-backup", "/api/parametres/espace", "/api/groupe/session",
    ];

    public static bool IsReplayable(string method, string path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return false;
        var p = new PathString(path.Split('?')[0]);
        return ReplayablePrefixes.Any(prefix => p.StartsWithSegments(prefix))
               && !NeverReplayed.Any(prefix => p.StartsWithSegments(prefix));
    }

    public async Task InvokeAsync(HttpContext http, RemoteSessionTokens tokens, ReplicaStore replicas,
        RemoteCommandStore commands)
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

        // Leaving the shop from afar: there is no session row to close.
        if (HttpMethods.IsDelete(http.Request.Method) && http.Request.Path.StartsWithSegments("/api/groupe/session"))
        {
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        // The list of your shops is the server's own, not the shop's copy: inside one shop, "Changer
        // d'espace" still has to find them all. It reads nothing from the copy.
        if (readOnly && http.Request.Path.Equals("/api/groupes", StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        // Your own profile lives on both servers: saved here on the account you signed in with, and
        // kept for the shop, which applies it to your account there.
        if (HttpMethods.IsPut(http.Request.Method) && http.Request.Path.StartsWithSegments(ProfilePath))
        {
            http.Request.EnableBuffering();
            if (!await QueueAsync(http, session, commands, ct, answer: false)) return;
            http.Request.Body.Position = 0;
            await next(http);
            return;
        }

        if (!readOnly && !isCommand)
        {
            await QueueAsync(http, session, commands, ct);
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

    /// <summary>Keeps a change for the shop's host, or says why it cannot be made from afar.</summary>
    /// <returns>False when refused (the refusal is already written).</returns>
    private static async Task<bool> QueueAsync(HttpContext http, RemoteSession session, RemoteCommandStore commands,
        CancellationToken ct, bool answer = true)
    {
        var request = http.Request;
        if (!IsReplayable(request.Method, request.Path))
        {
            await Refuse(http, StatusCodes.Status405MethodNotAllowed,
                "Cette action n'est pas possible à distance : faites-la depuis la boutique.");
            return false;
        }

        // A photo or a file is not kept: only the JSON a screen sends.
        if (request.ContentLength > 0 && request.ContentType is { } type
            && !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(http, StatusCodes.Status405MethodNotAllowed,
                "L'envoi de photos ou de fichiers n'est pas possible à distance : faites-le depuis la boutique.");
            return false;
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            await Refuse(http, StatusCodes.Status413PayloadTooLarge, "Demande trop volumineuse pour être envoyée à distance.");
            return false;
        }

        string? body;
        using (var reader = new StreamReader(request.Body, leaveOpen: true))
            body = await reader.ReadToEndAsync(ct);
        if (body.Length > MaxBodyBytes)
        {
            await Refuse(http, StatusCodes.Status413PayloadTooLarge, "Demande trop volumineuse pour être envoyée à distance.");
            return false;
        }

        var path = request.Path + request.QueryString;
        var queued = commands.Enqueue(session.GroupId, new RemoteCommandDto(
            Id: Guid.NewGuid().ToString("N"),
            Type: RemoteCommandTypes.Request,
            UserId: session.LocalUserId,
            TargetName: $"{request.Method} {request.Path}",
            PrivilegeName: null, Catalog: null, Granted: null, Role: null,
            RequestedBy: session.LocalUserId,
            RequestedByName: null,
            RequestedAt: DateTime.UtcNow,
            Status: RemoteCommandStatuses.Pending,
            Method: request.Method,
            Path: path,
            Body: body.Length == 0 ? null : body));

        if (!answer) return true;

        http.Response.StatusCode = StatusCodes.Status202Accepted;
        await http.Response.WriteAsJsonAsync(new RemoteQueuedResponse(queued.Id,
            "Demande envoyée à la boutique : elle sera appliquée dès que son serveur sera connecté."), ct);
        return true;
    }

    private static Task Refuse(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new ApiError(message));
    }
}
