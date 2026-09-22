using System.Security.Claims;
using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Data.Services;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Security;

/// <summary>
/// Who is calling and in which group, resolved once per request by
/// <see cref="GroupScopeFilter"/> and injected into endpoints that need it.
/// </summary>
public class GroupScope
{
    public string UserId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string SessionToken { get; set; } = string.Empty;

    /// <summary>The caller's resolved privileges in <see cref="GroupId"/>.</summary>
    public PrivilegeSet Privileges { get; set; } = null!;

    /// <summary>True when the caller created this group.</summary>
    public bool IsAdminGeneral => Privileges.IsAdminGeneral;

    /// <summary>True when the caller holds an admin or sub_admin role.</summary>
    public bool IsAdmin => Privileges.IsAdmin;
}

/// <summary>
/// Establishes <see cref="GroupScope"/> for group-scoped endpoints.
///
/// Ports two checks from the web app's middleware chain: the blocked-user check that
/// backend/middleware/authMiddleware.js performs on every request, and the group session
/// validation that backend/routes/gestion.js applies before any group data is touched.
/// The blocked check fails closed here - the web app logs and continues on a database
/// error, which would let a blocked user through.
/// </summary>
public class GroupScopeFilter(
    LonniiDbContext db,
    GroupSessionService sessions,
    PrivilegeResolver privileges,
    GroupScope scope) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;

        var userId = http.User.FindFirstValue(TokenService.UserIdClaim);
        if (string.IsNullOrEmpty(userId))
            return Results.Json(new ApiError("Non authentifié"), statusCode: StatusCodes.Status401Unauthorized);

        var account = await db.Users
            .Where(u => u.IdUser == userId)
            .Select(u => new { u.IsBlocked, u.PasswordChangedAt })
            .FirstOrDefaultAsync(ct);

        if (account is null)
            return Results.Json(new ApiError("Compte introuvable"), statusCode: StatusCodes.Status401Unauthorized);

        if (account.IsBlocked)
            return Results.Json(new ApiError("Ce compte est bloqué."), statusCode: StatusCodes.Status403Forbidden);

        // A token issued before the password last changed belongs to a session that the
        // password change was meant to end.
        if (account.PasswordChangedAt is { } changedAt && IssuedAt(http) is { } issuedAt && issuedAt < changedAt)
        {
            return Results.Json(
                new ApiError("Votre mot de passe a été modifié. Reconnectez-vous."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var token = http.Request.Headers[GroupSessionService.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(token))
            return Results.Json(new ApiError("Session de groupe manquante"), statusCode: StatusCodes.Status400BadRequest);

        var session = await sessions.ResolveAsync(token, userId, ct);
        if (session is null)
            return Results.Json(new ApiError("Session de groupe invalide ou expirée"), statusCode: StatusCodes.Status401Unauthorized);

        scope.UserId = userId;
        scope.GroupId = session.GroupId;
        scope.SessionToken = token;
        scope.Privileges = await privileges.ResolveAsync(userId, session.GroupId, ct);

        return await next(context);
    }

    /// <summary>
    /// When the caller's token was issued, from its <c>iat</c> claim.
    /// Truncated to whole seconds, which is the resolution the claim carries.
    /// </summary>
    private static DateTime? IssuedAt(HttpContext http)
    {
        var raw = http.User.FindFirstValue(TokenService.IssuedAtClaim);
        if (!long.TryParse(raw, out var unixSeconds)) return null;
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
    }
}

/// <summary>Requires one named privilege before the endpoint runs.</summary>
public class RequirePrivilegeFilter(GroupScope scope, string privilege) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var all = scope.Privileges.All();
        if (!(all.TryGetValue(privilege, out var granted) && granted))
        {
            return ValueTask.FromResult<object?>(Results.Json(
                new ApiError("Privilège insuffisant", privilege),
                statusCode: StatusCodes.Status403Forbidden));
        }

        return next(context);
    }
}

/// <summary>Requires an admin or sub_admin role before the endpoint runs.</summary>
public class RequireAdminFilter(GroupScope scope) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!scope.IsAdmin)
        {
            return ValueTask.FromResult<object?>(Results.Json(
                new ApiError("Réservé aux administrateurs"),
                statusCode: StatusCodes.Status403Forbidden));
        }

        return next(context);
    }
}

/// <summary>Endpoint-building helpers for group scoping and privilege gates.</summary>
public static class GroupScopeExtensions
{
    /// <summary>
    /// Requires a valid JWT and a valid <c>x-group-session</c> header, and resolves
    /// <see cref="GroupScope"/> for the endpoint.
    /// </summary>
    public static RouteHandlerBuilder RequireGroupScope(this RouteHandlerBuilder builder)
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter<GroupScopeFilter>();
        return builder;
    }

    /// <summary>
    /// Adds a privilege gate. Apply after <see cref="RequireGroupScope"/>, so the scope
    /// this reads has already been resolved.
    /// </summary>
    public static RouteHandlerBuilder RequirePrivilege(this RouteHandlerBuilder builder, string privilege)
    {
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            var scope = invocation.HttpContext.RequestServices.GetRequiredService<GroupScope>();
            return await new RequirePrivilegeFilter(scope, privilege).InvokeAsync(invocation, next);
        });
        return builder;
    }

    /// <summary>Adds an admin-role gate. Apply after <see cref="RequireGroupScope"/>.</summary>
    public static RouteHandlerBuilder RequireGroupAdmin(this RouteHandlerBuilder builder)
    {
        builder.AddEndpointFilter<RequireAdminFilter>();
        return builder;
    }
}
