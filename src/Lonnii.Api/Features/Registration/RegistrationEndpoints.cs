using System.Security.Cryptography;
using System.Text;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Registration;

/// <summary>
/// How a new shop and its Admin Général come to exist: on the licence server, first, and only
/// after the owner has proved the email address is theirs.
///
/// <para>
/// Two steps. <c>start</c> takes the shop name, email and password, stores them (password
/// hashed) and emails a six-digit code. <c>verify</c> checks the code and only then creates the
/// user and the workspace - as <see cref="ApprovalStatuses.Pending"/>. Activation and licence
/// refresh refuse a pending shop, so nobody uses the app before we have talked to them; moving
/// <c>groupes.approval_status</c> to <c>approved</c> (from the dashboard) is what lets it in.
/// Every shop that registers is therefore on our server from the first minute, which is the point.
/// </para>
/// <para>
/// Switched on by <c>Lonnii:Registration:Enabled</c>, which only the OCI server sets. A shop's
/// own host also runs this program, and registering a workspace into <em>its</em> database
/// would register nothing we can see.
/// </para>
/// </summary>
public static class RegistrationEndpoints
{
    private const int CodeLifetimeMinutes = 15;
    private const int MaxAttempts = 5;

    public static void MapRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/registration").WithTags("Registration")
            .AddEndpointFilter(RequireEnabledAsync);

        group.MapPost("/start", StartAsync);
        group.MapPost("/verify", VerifyAsync);
        group.MapPost("/espace", NewEspaceAsync);
    }

    private static async ValueTask<object?> RequireEnabledAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();

        // 404, not 403: on a shop's own host this feature should look like it does not exist.
        return config.GetValue("Lonnii:Registration:Enabled", false)
            ? await next(context)
            : Results.NotFound();
    }

    private static async Task<IResult> StartAsync(
        RegistrationStartRequest request,
        LonniiDbContext db,
        IEmailSender email,
        RegistrationLimiter limiter,
        HttpContext http,
        ILogger<RegistrationRequest> log,
        CancellationToken ct)
    {
        var address = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var shop = request.ShopName?.Trim() ?? string.Empty;

        if (shop.Length is < 2 or > 100)
            return Results.BadRequest(new ApiError("Le nom de la boutique doit contenir entre 2 et 100 caractères"));

        if (address.Length > 200 || !address.Contains('@') || address.StartsWith('@') || address.EndsWith('@'))
            return Results.BadRequest(new ApiError("Adresse email invalide"));

        if ((request.Password?.Length ?? 0) < AuthEndpoints.MinimumPasswordLength)
        {
            return Results.BadRequest(new ApiError(
                $"Le mot de passe doit contenir au moins {AuthEndpoints.MinimumPasswordLength} caractères"));
        }

        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.BadRequest(new ApiError("Identifiant de poste manquant."));

        if (!email.IsConfigured)
        {
            log.LogError("Inscription demandée mais aucun envoi d'email n'est configuré (Lonnii:Email)");
            return Results.Json(
                new ApiError("L'inscription est momentanément indisponible. Contactez le support."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var ip = http.Connection.RemoteIpAddress?.ToString();
        if (!limiter.TryAcquire(address, ip))
        {
            return Results.Json(
                new ApiError("Trop de tentatives. Réessayez dans une heure."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (await db.Users.AnyAsync(u => u.Email == address, ct))
            return Results.Conflict(new ApiError("Cette adresse email est déjà utilisée"));

        // A second attempt for the same address replaces the first.
        await db.RegistrationRequests.Where(r => r.Email == address).ExecuteDeleteAsync(ct);

        var pending = new RegistrationRequest
        {
            ShopName = shop,
            Email = address,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName,
            CodeExpiresAt = DateTime.UtcNow.AddMinutes(CodeLifetimeMinutes),
            IpAddress = ip,
        };

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        pending.CodeHash = HashCode(pending.Id, code);

        db.RegistrationRequests.Add(pending);
        await db.SaveChangesAsync(ct);

        try
        {
            await email.SendAsync(
                address,
                RegistrationEmail.Subject,
                RegistrationEmail.Html(shop, code, CodeLifetimeMinutes),
                ct);
        }
        catch (EmailSendException e)
        {
            // A code nobody received is worse than an error: remove it and say so.
            db.RegistrationRequests.Remove(pending);
            await db.SaveChangesAsync(CancellationToken.None);
            log.LogError(e, "Email de confirmation non envoyé à {Email}", address);

            return Results.Json(
                new ApiError("Impossible d'envoyer l'email de confirmation. Vérifiez l'adresse puis réessayez."),
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(new RegistrationStartResponse(pending.Id, pending.CodeExpiresAt));
    }

    private static async Task<IResult> VerifyAsync(
        RegistrationVerifyRequest request,
        LonniiDbContext db,
        ILogger<RegistrationRequest> log,
        CancellationToken ct)
    {
        // One message for unknown, expired and wrong, so the endpoint reveals nothing.
        var refused = Results.BadRequest(new ApiError("Code invalide ou expiré."));

        var pending = await db.RegistrationRequests.FirstOrDefaultAsync(r => r.Id == request.RequestId, ct);
        if (pending is null) return refused;

        if (pending.CodeExpiresAt < DateTime.UtcNow || pending.Attempts >= MaxAttempts)
        {
            db.RegistrationRequests.Remove(pending);
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new ApiError("Code expiré ou trop d'essais. Recommencez l'inscription."));
        }

        var presented = HashCode(pending.Id, request.Code?.Trim() ?? string.Empty);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(presented), Encoding.ASCII.GetBytes(pending.CodeHash)))
        {
            pending.Attempts++;
            await db.SaveChangesAsync(ct);
            return refused;
        }

        // Someone may have taken the address between start and verify.
        if (await db.Users.AnyAsync(u => u.Email == pending.Email, ct))
        {
            db.RegistrationRequests.Remove(pending);
            await db.SaveChangesAsync(ct);
            return Results.Conflict(new ApiError("Cette adresse email est déjà utilisée"));
        }

        var admin = new User
        {
            Email = pending.Email,
            Password = pending.PasswordHash,
            IsVerified = true,
        };

        var groupe = new Groupe
        {
            Nom = pending.ShopName,
            IdUserAdmin = admin.IdUser,
            GestionAccess = true,
            Mode = DeploymentModes.Local,
            ApprovalStatus = ApprovalStatuses.Pending,
        };

        // Inserted in dependency order, in one transaction. The live PostgreSQL schema has foreign keys
        // the model does not describe (password_history -> users, and others), and EF cannot order
        // rows by a constraint it does not know about - one batch put the history ahead of the user.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        db.Groupes.Add(groupe);
        await db.SaveChangesAsync(ct);

        db.PasswordHistories.Add(new PasswordHistory { IdUser = admin.IdUser, PasswordHash = admin.Password });
        db.GroupMembers.Add(new GroupMember { IdGroupe = groupe.Id, IdUser = admin.IdUser });
        db.RegistrationRequests.Remove(pending);
        await db.SaveChangesAsync(ct);

        await AddAdminRoleAsync(db, admin.IdUser, groupe.Id, ct);

        await transaction.CommitAsync(ct);

        log.LogInformation("Nouvelle boutique en attente d'approbation : {Shop} ({GroupId})", groupe.Nom, groupe.Id);

        return Results.Ok(new RegistrationVerifyResponse(groupe.Id, groupe.ApprovalStatus));
    }

    /// <summary>
    /// Another espace for an owner we have already approved. Instant: they are a known customer,
    /// and the espace is on our list from this moment, which is what matters - it can still be
    /// blocked or deleted from the dashboard like any other.
    ///
    /// <para>
    /// Authenticated by email and password, and only an Admin Général of an <em>approved</em> shop
    /// qualifies: someone whose only shop is pending, blocked or declined gets nothing here, so this
    /// cannot be used to get around the approval step.
    /// </para>
    /// </summary>
    private static async Task<IResult> NewEspaceAsync(
        RegistrationEspaceRequest request,
        LonniiDbContext db,
        RegistrationLimiter limiter,
        HttpContext http,
        ILogger<RegistrationRequest> log,
        CancellationToken ct)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var shop = request.ShopName?.Trim() ?? string.Empty;

        if (shop.Length is < 2 or > 100)
            return Results.BadRequest(new ApiError("Le nom de l'espace doit contenir entre 2 et 100 caractères"));

        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.BadRequest(new ApiError("Identifiant de poste manquant."));

        // The same cap as sign-up: this checks passwords, so it must not be a way to guess them.
        if (!limiter.TryAcquire(email, http.Connection.RemoteIpAddress?.ToString()))
        {
            return Results.Json(
                new ApiError("Trop de tentatives. Réessayez dans une heure."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var refused = Results.Json(
            new ApiError("Création refusée. Vérifiez votre mot de passe."),
            statusCode: StatusCodes.Status403Forbidden);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user?.Password is null || user.IsBlocked || !BCrypt.Net.BCrypt.Verify(request.Password, user.Password))
            return refused;

        var approvedShop = await db.Groupes.AnyAsync(g =>
            g.IdUserAdmin == user.IdUser && g.ApprovalStatus == ApprovalStatuses.Approved &&
            !g.IsBlocked && !g.IsDeleted, ct);

        if (!approvedShop)
        {
            return Results.Json(
                new ApiError("Seul l'administrateur d'une boutique déjà approuvée peut créer un espace."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Every espace waits for our approval, including another one for an owner we already know:
        // nobody uses the app before we have agreed to it (decided 2026-10-08).
        var groupe = new Groupe
        {
            Nom = shop,
            IdUserAdmin = user.IdUser,
            GestionAccess = true,
            Mode = DeploymentModes.Local,
            ApprovalStatus = ApprovalStatuses.Pending,
        };

        // The shop first, then what points at it - see the note in VerifyAsync about ordering.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.Groupes.Add(groupe);
        await db.SaveChangesAsync(ct);

        db.GroupMembers.Add(new GroupMember { IdGroupe = groupe.Id, IdUser = user.IdUser });
        await db.SaveChangesAsync(ct);

        await AddAdminRoleAsync(db, user.IdUser, groupe.Id, ct);

        await transaction.CommitAsync(ct);

        log.LogInformation("Nouvel espace en attente d'approbation : {Shop} ({GroupId}) par {Email}", shop, groupe.Id, email);

        return Results.Ok(new RegistrationVerifyResponse(groupe.Id, groupe.ApprovalStatus));
    }

    /// <summary>
    /// Gives a user the Admin Général role in a shop. On PostgreSQL this is written with an explicit
    /// cast, because the live <c>user_roles.role</c> is the enum <c>role_type</c>: Npgsql sends the
    /// role as text however the model names the column, and Postgres refuses text for an enum. (Naming
    /// the column type in the model was tried and changed nothing.) On SQLite - a shop's own file, and
    /// the tests - the role is plain text and goes through the model as usual; the cast would not be
    /// safe there, where an unknown type name is read as a number.
    /// </summary>
    private static async Task AddAdminRoleAsync(LonniiDbContext db, string userId, string groupId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            var role = GroupRoles.Admin;

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO user_roles (user_id, group_id, role, assigned_by, assigned_at, is_active)
                VALUES ({userId}, {groupId}, CAST({role} AS role_type), {userId}, NOW(), TRUE)
                """, ct);

            return;
        }

        db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            GroupId = groupId,
            Role = GroupRoles.Admin,
            AssignedBy = userId,
        });
        await db.SaveChangesAsync(ct);
    }

    private static string HashCode(string requestId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId}:{code}"))).ToLowerInvariant();
}
