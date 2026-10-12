using System.Security.Claims;
using System.Security.Cryptography;
using Lonnii.Shared.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Lonnii.Api.Features.Admin;

/// <summary>Settings for accepting the admin dashboard's tokens. Section <c>DashboardAuth</c>.</summary>
public sealed class DashboardAuthOptions
{
    public const string SectionName = "DashboardAuth";

    /// <summary>
    /// The dashboard login service's public key (PEM, ES256). Only the public half lives here:
    /// this server can check a dashboard token but never issue one. Unset means no dashboard
    /// token is accepted, which is right for every shop's own host.
    /// </summary>
    public string? PublicKeyFile { get; set; }

    /// <summary>Sites allowed to call the admin routes from a browser.</summary>
    public string[] AllowedOrigins { get; set; } =
        ["https://dashboard-lonniib.com", "https://www.dashboard-lonniib.com"];
}

/// <summary>
/// Lets the admin dashboard call this API. Its tokens are signed by the dashboard login service on
/// AWS (dashboard-auth), which keeps the admin accounts in its own database so the dashboard opens
/// even when this server is down.
///
/// <para>
/// A separate authentication scheme from the shops' own tokens, and the admin routes accept only
/// this one: a shop's token can never reach an admin route, and a dashboard token never opens a shop.
/// </para>
/// </summary>
public static class DashboardAdminAuth
{
    public const string Scheme = "DashboardAdmin";
    public const string Policy = "DashboardAdmin";
    public const string CorsPolicy = "DashboardAdmin";

    private const string Issuer = "lonnii-dashboard-auth";
    private const string Audience = "lonnii-dashboard";

    public static void AddDashboardAdminAuth(
        this WebApplicationBuilder builder, Microsoft.AspNetCore.Authentication.AuthenticationBuilder auth)
    {
        var options = new DashboardAuthOptions();
        builder.Configuration.GetSection(DashboardAuthOptions.SectionName).Bind(options);
        builder.Services.AddSingleton(options);

        var keys = LoadKeys(options.PublicKeyFile);

        auth.AddJwtBearer(Scheme, jwt =>
        {
            // Claim names exactly as the dashboard writes them (role, permissions, adminId).
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Audience,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                IssuerSigningKeys = keys,
                ValidateIssuerSigningKey = true,
                RoleClaimType = "role",
                NameClaimType = "email",
                ClockSkew = TimeSpan.FromMinutes(1),
            };
        });

        builder.Services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser());

        builder.Services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy => policy
            .WithOrigins(options.AllowedOrigins)
            .WithHeaders("Authorization", "Content-Type")
            .WithMethods("GET", "PUT", "DELETE")));
    }

    private static SecurityKey[] LoadKeys(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return [];

        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(file));
        return [new ECDsaSecurityKey(ecdsa)];
    }

    /// <summary>Owner and super_admin hold every permission, as in the dashboard itself.</summary>
    public static bool HasAllPermissions(ClaimsPrincipal user) =>
        user.FindFirstValue("role") is "owner" or "super_admin"
        || user.FindAll("permissions").Any(c => c.Value == "all");

    public static bool HasPermission(ClaimsPrincipal user, string permission) =>
        HasAllPermissions(user) || user.FindAll("permissions").Any(c => c.Value == permission);

    public static string AdminId(ClaimsPrincipal user) => user.FindFirstValue("adminId") ?? "inconnu";

    /// <summary>Refuses the request unless the dashboard admin holds <paramref name="permission"/>.</summary>
    public static RouteHandlerBuilder RequireDashboardPermission(this RouteHandlerBuilder route, string permission) =>
        route.AddEndpointFilter(async (context, next) =>
            HasPermission(context.HttpContext.User, permission)
                ? await next(context)
                : Results.Json(new ApiError("Permission refusée", permission), statusCode: StatusCodes.Status403Forbidden));

    /// <summary>Refuses the request unless the dashboard admin is owner or super_admin.</summary>
    public static RouteHandlerBuilder RequireDashboardSuperAdmin(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter(async (context, next) =>
            context.HttpContext.User.FindFirstValue("role") is "owner" or "super_admin"
                ? await next(context)
                : Results.Json(new ApiError("Permission super admin requise"), statusCode: StatusCodes.Status403Forbidden));
}
