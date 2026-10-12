using System.Security.Cryptography;
using System.Text;
using Lonnii.Api.Features.Auth;
using Lonnii.Api.Features.Licensing;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Registration;

/// <summary>A reset code we emailed and are waiting to see typed back.</summary>
internal sealed record PendingPasswordReset(string Email, string CodeHash, DateTime ExpiresAt)
{
    public int Attempts { get; set; }
}

/// <summary>
/// Reset codes, in memory on the licence server. A restart forgets them, which only means asking
/// for another code; a table would be one more thing to create on the live database for nothing.
/// </summary>
public sealed class PasswordResetStore
{
    private readonly Dictionary<string, PendingPasswordReset> _pending = [];
    private readonly Lock _gate = new();

    internal void Add(string requestId, PendingPasswordReset reset)
    {
        lock (_gate)
        {
            foreach (var expired in _pending.Where(p => p.Value.ExpiresAt < DateTime.UtcNow).Select(p => p.Key).ToList())
                _pending.Remove(expired);

            _pending[requestId] = reset;
        }
    }

    internal PendingPasswordReset? Find(string requestId)
    {
        lock (_gate) return _pending.GetValueOrDefault(requestId);
    }

    internal void Remove(string requestId)
    {
        lock (_gate) _pending.Remove(requestId);
    }
}

/// <summary>
/// "Mot de passe oublié" for a shop owner - the Admin Général, whose password nobody else in the
/// shop may reset (see <c>GroupEndpoints.ResetMemberPasswordAsync</c>). Other members are reset by
/// their administrator, as before.
///
/// <para>
/// The proof is the owner's email, which we confirmed at registration: the licence server emails a
/// six-digit code, and only a correct code changes the password - on the licence server and, through
/// the host that relayed it, in the shop's own database. A shop that cannot reach the internet cannot
/// use this; it needs Lonnii's help.
/// </para>
/// </summary>
public static class PasswordResetEndpoints
{
    private const int CodeLifetimeMinutes = 15;
    private const int MaxAttempts = 5;

    public static void MapPasswordResetEndpoints(this IEndpointRouteBuilder app)
    {
        // Licence server: behind the same switch as registration, so a shop's host does not offer it.
        var server = app.MapGroup("/api/registration/password").WithTags("Registration")
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()
                    .GetValue("Lonnii:Registration:Enabled", false)
                    ? await next(context)
                    : Results.NotFound());

        server.MapPost("/start", StartAsync);
        server.MapPost("/verify", VerifyAsync);

        // The shop's host, for its own login screen. Anonymous: whoever uses it has forgotten
        // how to sign in.
        var host = app.MapGroup("/api/auth/forgot").WithTags("Auth");

        host.MapPost("/start", HostStartAsync);
        host.MapPost("/verify", HostVerifyAsync);

        // Without the internet: a code Lonnii signs, read out over the phone (see ResetCode).
        host.MapPost("/offline/tag", OfflineTagAsync);
        host.MapPost("/offline/apply", OfflineApplyAsync);
    }

    // --- Licence server ---

    private static async Task<IResult> StartAsync(
        PasswordResetStartRequest request,
        LonniiDbContext db,
        IEmailSender email,
        RegistrationLimiter limiter,
        PasswordResetStore store,
        HttpContext http,
        ILogger<PasswordResetStore> log,
        CancellationToken ct)
    {
        var address = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (address.Length is 0 or > 200 || !address.Contains('@'))
            return Results.BadRequest(new ApiError("Adresse email invalide"));

        if (!email.IsConfigured)
        {
            log.LogError("Réinitialisation demandée mais aucun envoi d'email n'est configuré (Lonnii:Email)");
            return Results.Json(
                new ApiError("La réinitialisation est momentanément indisponible. Contactez le support."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!limiter.TryAcquire(address, http.Connection.RemoteIpAddress?.ToString()))
        {
            return Results.Json(
                new ApiError("Trop de tentatives. Réessayez dans une heure."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var requestId = Guid.NewGuid().ToString();
        var expiresAt = DateTime.UtcNow.AddMinutes(CodeLifetimeMinutes);

        // Only the owner of a shop that still exists gets a code. Anyone else gets the same answer
        // and no email, so the endpoint does not reveal which addresses are our customers.
        var isOwner = await db.Users
            .Where(u => u.Email == address && !u.IsBlocked)
            .AnyAsync(u => db.Groupes.Any(g => g.IdUserAdmin == u.IdUser && !g.IsDeleted), ct);

        if (!isOwner) return Results.Ok(new PasswordResetStartResponse(requestId, expiresAt));

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        store.Add(requestId, new PendingPasswordReset(address, HashCode(requestId, code), expiresAt));

        try
        {
            await email.SendAsync(
                address,
                RegistrationEmail.PasswordResetSubject,
                RegistrationEmail.PasswordResetHtml(code, CodeLifetimeMinutes),
                ct);
        }
        catch (EmailSendException e)
        {
            store.Remove(requestId);
            log.LogError(e, "Email de réinitialisation non envoyé à {Email}", address);

            return Results.Json(
                new ApiError("Impossible d'envoyer l'email. Réessayez plus tard."),
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new PasswordResetStartResponse(requestId, expiresAt));
    }

    private static async Task<IResult> VerifyAsync(
        PasswordResetVerifyRequest request,
        LonniiDbContext db,
        PasswordResetStore store,
        ILogger<PasswordResetStore> log,
        CancellationToken ct)
    {
        if ((request.NewPassword?.Length ?? 0) < AuthEndpoints.MinimumPasswordLength)
        {
            return Results.BadRequest(new ApiError(
                $"Le mot de passe doit contenir au moins {AuthEndpoints.MinimumPasswordLength} caractères"));
        }

        // One message for unknown, expired and wrong, so the endpoint reveals nothing.
        var refused = Results.BadRequest(new ApiError("Code invalide ou expiré."));

        var reset = store.Find(request.RequestId ?? string.Empty);
        var email = request.Email?.Trim().ToLowerInvariant();
        if (reset is null || reset.Email != email) return refused;

        if (reset.ExpiresAt < DateTime.UtcNow || reset.Attempts >= MaxAttempts)
        {
            store.Remove(request.RequestId!);
            return Results.BadRequest(new ApiError("Code expiré ou trop d'essais. Demandez un nouveau code."));
        }

        var presented = HashCode(request.RequestId!, request.Code?.Trim() ?? string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(presented), Encoding.ASCII.GetBytes(reset.CodeHash)))
        {
            reset.Attempts++;
            return refused;
        }

        store.Remove(request.RequestId!);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == reset.Email, ct);
        if (user is null || user.IsBlocked) return refused;

        await AuthEndpoints.ApplyPasswordChangeAsync(user, request.NewPassword!, db, ct);
        log.LogInformation("Mot de passe réinitialisé par email pour {Email}", reset.Email);

        return Results.Ok(new PasswordResetVerifyResponse(true));
    }

    // --- The shop's host ---

    /// <summary>
    /// Finds the account typed on the login screen and the licence server its shop belongs to.
    /// Only the owner of an espace activated with Lonnii qualifies: that is the only account the
    /// licence server knows by email, and the only one nobody in the shop can reset.
    /// </summary>
    private static async Task<(Data.Entities.User User, string ServerUrl)?> FindOwnerAsync(
        string? identifier, LonniiDbContext db, CancellationToken ct)
    {
        var typed = identifier?.Trim() ?? string.Empty;
        if (typed.Length == 0) return null;

        var email = typed.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email || u.Username == typed, ct);
        if (user?.Email is null) return null;

        var serverUrl = await db.Groupes
            .Where(g => g.IdUserAdmin == user.IdUser && !g.IsDeleted &&
                        g.LicenceServerUrl != null && g.LicenceServerUrl != "")
            .Select(g => g.LicenceServerUrl)
            .FirstOrDefaultAsync(ct);

        return serverUrl is null ? null : (user, serverUrl);
    }

    private static IResult NotAnOwner() => Results.Json(
        new ApiError("Seul l'administrateur général d'un espace peut réinitialiser son mot de passe ici. " +
                     "Les autres membres demandent à leur administrateur."),
        statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> HostStartAsync(
        ForgotPasswordStartRequest request, LonniiDbContext db, ILicenceServer licences, CancellationToken ct)
    {
        if (await FindOwnerAsync(request.Identifier, db, ct) is not { } owner) return NotAnOwner();
        var (user, serverUrl) = owner;

        try
        {
            var started = await licences.PasswordResetStartAsync(
                serverUrl, new PasswordResetStartRequest(user.Email!), ct);

            return Results.Ok(new ForgotPasswordStartResponse(started.RequestId, started.ExpiresAt, MaskEmail(user.Email!)));
        }
        catch (ActivationRefusedException e)
        {
            return Results.Json(new ApiError(e.Message),
                statusCode: (int?)e.Status ?? StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> HostVerifyAsync(
        ForgotPasswordVerifyRequest request, LonniiDbContext db, ILicenceServer licences, CancellationToken ct)
    {
        if (await FindOwnerAsync(request.Identifier, db, ct) is not { } owner) return NotAnOwner();
        var (user, serverUrl) = owner;

        if ((request.NewPassword?.Length ?? 0) < AuthEndpoints.MinimumPasswordLength)
        {
            return Results.BadRequest(new ApiError(
                $"Le mot de passe doit contenir au moins {AuthEndpoints.MinimumPasswordLength} caractères"));
        }

        try
        {
            // The licence server checks the code and changes its own copy first; only then this one.
            await licences.PasswordResetVerifyAsync(
                serverUrl,
                new PasswordResetVerifyRequest(request.RequestId, user.Email!, request.Code, request.NewPassword!),
                ct);
        }
        catch (ActivationRefusedException e)
        {
            return Results.Json(new ApiError(e.Message),
                statusCode: (int?)e.Status ?? StatusCodes.Status503ServiceUnavailable);
        }

        await AuthEndpoints.ApplyPasswordChangeAsync(user, request.NewPassword!, db, ct);
        return Results.NoContent();
    }

    // --- The shop's host, without the internet ---

    /// <summary>
    /// The owner of any espace on this host, activated or not. Unlike the emailed code this needs
    /// no licence server, so it is the way back for a shop that runs offline.
    /// </summary>
    private static async Task<Data.Entities.User?> FindAnyOwnerAsync(
        string? identifier, LonniiDbContext db, CancellationToken ct)
    {
        var typed = identifier?.Trim() ?? string.Empty;
        if (typed.Length == 0) return null;

        var email = typed.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email || u.Username == typed, ct);
        if (user is null) return null;

        var owns = await db.Groupes.AnyAsync(g => g.IdUserAdmin == user.IdUser && !g.IsDeleted, ct);
        return owns ? user : null;
    }

    private static async Task<IResult> OfflineTagAsync(
        ForgotPasswordStartRequest request, LonniiDbContext db, TokenService tokens, CancellationToken ct)
    {
        if (await FindAnyOwnerAsync(request.Identifier, db, ct) is not { } user) return NotAnOwner();

        return Results.Ok(new OfflineResetTagResponse(ResetCode.AccountTag(tokens.HostId, user.IdUser, user.Password)));
    }

    private static async Task<IResult> OfflineApplyAsync(
        OfflineResetApplyRequest request, LonniiDbContext db, TokenService tokens,
        ILogger<PasswordResetStore> log, CancellationToken ct)
    {
        if (await FindAnyOwnerAsync(request.Identifier, db, ct) is not { } user) return NotAnOwner();

        if ((request.NewPassword?.Length ?? 0) < AuthEndpoints.MinimumPasswordLength)
        {
            return Results.BadRequest(new ApiError(
                $"Le mot de passe doit contenir au moins {AuthEndpoints.MinimumPasswordLength} caractères"));
        }

        var tag = ResetCode.AccountTag(tokens.HostId, user.IdUser, user.Password);
        var result = ResetCode.Check(request.Code, tag, DateTimeOffset.UtcNow);
        if (result != ResetCodeResult.Valid)
        {
            return Results.BadRequest(new ApiError(result switch
            {
                ResetCodeResult.Expired => "Ce code a expiré. Demandez-en un nouveau à Lonnii.",
                ResetCodeResult.WrongAccount => "Ce code a été émis pour un autre compte, un autre serveur, ou a déjà servi.",
                _ => "Code invalide. Vérifiez qu'il est recopié en entier.",
            }));
        }

        await AuthEndpoints.ApplyPasswordChangeAsync(user, request.NewPassword!, db, ct);
        log.LogInformation("Mot de passe réinitialisé par code Lonnii pour {UserId}", user.IdUser);

        return Results.NoContent();
    }

    /// <summary>"caissier@exemple.com" becomes "ca******@exemple.com".</summary>
    internal static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return email;

        var shown = Math.Min(2, at);
        return email[..shown] + new string('*', Math.Max(3, at - shown)) + email[at..];
    }

    private static string HashCode(string requestId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId}:{code}"))).ToLowerInvariant();
}
