using System.Security.Claims;
using Lonnii.Api.Services;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>Sign-in, registration and the current-user lookup.</summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapGet("/setup-state", SetupStateAsync);
        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);
        group.MapGet("/me", MeAsync).RequireAuthorization();
        group.MapPost("/password", ChangeOwnPasswordAsync).RequireAuthorization();
    }

    /// <summary>
    /// Changes the signed-in user's own password, after proving they know the current one.
    ///
    /// A new token comes back, because applying the change invalidates the one they are
    /// holding. Without this the only way to change a password would be to ask an
    /// administrator: there is no email reset on a host with no internet.
    /// </summary>
    private static async Task<IResult> ChangeOwnPasswordAsync(
        ChangePasswordRequest request,
        ClaimsPrincipal principal,
        LonniiDbContext db,
        TokenService tokens,
        CancellationToken ct)
    {
        var userId = principal.FindFirstValue(TokenService.UserIdClaim);
        var user = await db.Users.FirstOrDefaultAsync(u => u.IdUser == userId, ct);

        if (user?.Password is null)
            return Results.Json(new ApiError("Compte introuvable"), statusCode: StatusCodes.Status401Unauthorized);

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.Password))
            return Results.BadRequest(new ApiError("Le mot de passe actuel est incorrect"));

        if (request.NewPassword.Length < MinimumPasswordLength)
            return Results.BadRequest(new ApiError(
                $"Le nouveau mot de passe doit contenir au moins {MinimumPasswordLength} caractères"));

        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, user.Password))
            return Results.BadRequest(new ApiError("Le nouveau mot de passe doit être différent de l'actuel"));

        await ApplyPasswordChangeAsync(user, request.NewPassword, db, ct);

        // Issued after the change, so it survives the invalidation it just caused.
        var (token, expiresAt) = tokens.Issue(user);
        return Results.Ok(new LoginResponse(token, expiresAt, ToDto(user)));
    }

    /// <summary>
    /// Sets a new password, records it in the history, and ends the account's existing
    /// sessions - both the group sessions in the database and, through
    /// <see cref="User.PasswordChangedAt"/>, any access token already issued.
    ///
    /// The timestamp is truncated to whole seconds because that is the resolution of the
    /// token's <c>iat</c> claim; storing sub-second precision would make a token issued
    /// in the same second look older than the change and reject itself.
    /// </summary>
    internal static async Task ApplyPasswordChangeAsync(
        User user, string newPassword, LonniiDbContext db, CancellationToken ct)
    {
        user.Password = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.PasswordChangedAt = TruncateToSeconds(DateTime.UtcNow);

        db.PasswordHistories.Add(new PasswordHistory
        {
            IdUser = user.IdUser,
            PasswordHash = user.Password,
        });

        await db.SaveChangesAsync(ct);

        // Drop open group sessions so the next request has to start from a fresh sign-in.
        await db.GroupeSessions.Where(s => s.UserId == user.IdUser).ExecuteDeleteAsync(ct);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    /// <summary>
    /// Says whether this host has any account yet, so a fresh installation can offer to
    /// create the first administrator rather than presenting an unusable sign-in form.
    /// </summary>
    private static async Task<IResult> SetupStateAsync(LonniiDbContext db, CancellationToken ct) =>
        Results.Ok(new SetupStateResponse(await db.Users.AnyAsync(ct)));

    /// <summary>
    /// Creates the first account on a fresh host, which becomes the owner of whatever
    /// workspace it creates.
    ///
    /// Open only while the database has no accounts. Lonnii Business allows anyone to
    /// register because it is on the public internet behind an email verification step;
    /// here every machine on the office network can reach this endpoint and there is no
    /// email to verify against, so leaving it open would let any device on the LAN mint
    /// accounts. Afterwards an administrator creates accounts from Options, which also
    /// puts the new person in a group - registering alone never granted any access anyway.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        RegisterRequest request, LonniiDbContext db, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct))
        {
            return Results.Json(
                new ApiError("Ce serveur est déjà configuré. Demandez à un administrateur de vous créer un compte."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        var (user, error) = await CreateAccountAsync(request, db, ct);
        if (error is not null) return error;

        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/users/{user!.IdUser}", ToDto(user));
    }

    /// <summary>
    /// Shortest password accepted. Long enough to be worth having, short enough that a
    /// shop owner setting up a till will not work around it by choosing something worse.
    /// </summary>
    public const int MinimumPasswordLength = 8;

    /// <summary>
    /// Validates and builds a new account, adding it to the change tracker without saving
    /// so a caller can commit it alongside other work in one transaction.
    /// Returns the account, or the failure to return to the client.
    /// </summary>
    internal static async Task<(User? User, IResult? Error)> CreateAccountAsync(
        RegisterRequest request, LonniiDbContext db, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return (null, Results.BadRequest(new ApiError("Adresse email invalide")));

        if (request.Password.Length < MinimumPasswordLength)
            return (null, Results.BadRequest(new ApiError(
                $"Le mot de passe doit contenir au moins {MinimumPasswordLength} caractères")));

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return (null, Results.Conflict(new ApiError("Cette adresse email est déjà utilisée")));

        var username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();
        if (username is not null && await db.Users.AnyAsync(u => u.Username == username, ct))
            return (null, Results.Conflict(new ApiError("Ce nom d'utilisateur est déjà utilisé")));

        var user = new User
        {
            Email = email,
            Username = username,
            Password = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            Phone = request.Phone,
            // No email delivery on a local network, so an account is usable immediately.
            IsVerified = true,
        };

        db.Users.Add(user);
        db.PasswordHistories.Add(new PasswordHistory { IdUser = user.IdUser, PasswordHash = user.Password });

        return (user, null);
    }

    /// <summary>Signs a user in with an email or username.</summary>
    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        LonniiDbContext db,
        TokenService tokens,
        GroupSessionService sessions,
        HttpContext http,
        CancellationToken ct)
    {
        var identifier = request.Identifier.Trim();
        var email = identifier.ToLowerInvariant();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Email == email || u.Username == identifier, ct);

        // Same message either way, so a failed sign-in does not reveal which accounts exist.
        const string failure = "Identifiants incorrects";

        if (user?.Password is null) return Results.Json(new ApiError(failure), statusCode: StatusCodes.Status401Unauthorized);
        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.Password))
            return Results.Json(new ApiError(failure), statusCode: StatusCodes.Status401Unauthorized);

        if (user.IsBlocked)
            return Results.Json(new ApiError("Ce compte est bloqué."), statusCode: StatusCodes.Status403Forbidden);

        user.LastLogin = DateTime.UtcNow;
        user.LastSeen = DateTime.UtcNow;

        db.UserSessions.Add(new UserSession
        {
            UserId = user.IdUser,
            DeviceName = request.DeviceName,
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            LoginSource = "desktop",
        });

        await db.SaveChangesAsync(ct);
        await sessions.PurgeExpiredAsync(ct);

        var (token, expiresAt) = tokens.Issue(user);
        return Results.Ok(new LoginResponse(token, expiresAt, ToDto(user)));
    }

    /// <summary>Returns the signed-in user, so a client can restore its session on start.</summary>
    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, LonniiDbContext db, CancellationToken ct)
    {
        var userId = principal.FindFirstValue(TokenService.UserIdClaim);
        var user = await db.Users.FirstOrDefaultAsync(u => u.IdUser == userId, ct);
        return user is null
            ? Results.Json(new ApiError("Compte introuvable"), statusCode: StatusCodes.Status401Unauthorized)
            : Results.Ok(ToDto(user));
    }

    internal static UserDto ToDto(User u) => new(
        u.IdUser, u.Email, u.Username, u.FirstName, u.LastName, u.Phone, u.Poste, u.IsVerified, u.LastLogin);
}
