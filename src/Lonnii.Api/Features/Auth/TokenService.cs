using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Lonnii.Data.Entities;
using Microsoft.IdentityModel.Tokens;

namespace Lonnii.Api.Features.Auth;

/// <summary>Signing and lifetime settings for access tokens.</summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Signing key. Left empty in configuration on purpose: the host generates one on
    /// first run and stores it beside the database, so no secret is committed.
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    public string Issuer { get; set; } = "LonniiDesktop";
    public string Audience { get; set; } = "LonniiDesktop";

    /// <summary>
    /// Token lifetime. A working day by default, so a till does not sign out mid-shift.
    /// </summary>
    public int LifetimeHours { get; set; } = 12;
}

/// <summary>Issues and describes the JWTs the desktop clients authenticate with.</summary>
public class TokenService(JwtOptions options)
{
    /// <summary>Claim carrying the Lonnii user id, matching the web app's <c>iduser</c> field.</summary>
    public const string UserIdClaim = "iduser";

    /// <summary>
    /// Claim carrying when the token was issued, as Unix seconds. Compared against the
    /// user's <c>password_changed_at</c> so a password reset invalidates older tokens.
    /// </summary>
    public const string IssuedAtClaim = JwtRegisteredClaimNames.Iat;

    private readonly SymmetricSecurityKey _key =
        new(Encoding.UTF8.GetBytes(options.Secret));

    /// <summary>
    /// A stable identity for this host, safe to show to anyone: a one-way hash of the signing
    /// key, so it survives restarts and reinstalls that keep the data folder, yet reveals nothing
    /// of the key. Tills remember it and refuse a different host.
    /// </summary>
    public string HostId { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("lonnii-host-id|" + options.Secret)))[..32].ToLowerInvariant();

    /// <summary>The validation rules the API applies to incoming tokens.</summary>
    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ValidateLifetime = true,
        // Local network, one clock source. No tolerance for a stale token.
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    /// <summary>Issues an access token for a signed-in user.</summary>
    public (string Token, DateTime ExpiresAt) Issue(User user)
    {
        var issuedAt = DateTime.UtcNow;
        var expiresAt = issuedAt.AddHours(options.LifetimeHours);

        var claims = new List<Claim>
        {
            new(UserIdClaim, user.IdUser),
            new(JwtRegisteredClaimNames.Sub, user.IdUser),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),

            // Set explicitly: the JwtSecurityToken constructor does not add "iat" on its
            // own, and the password-change check depends on it being present.
            new(IssuedAtClaim,
                new DateTimeOffset(issuedAt).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
        };

        if (!string.IsNullOrWhiteSpace(user.Username))
            claims.Add(new Claim("username", user.Username));

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    /// <summary>
    /// Reads the signing secret from <paramref name="keyFilePath"/>, creating a random
    /// one on first run. Keeping the key on the host laptop rather than in configuration
    /// means the repository never carries a usable secret.
    /// </summary>
    public static string LoadOrCreateSecret(string keyFilePath)
    {
        if (File.Exists(keyFilePath))
        {
            var existing = File.ReadAllText(keyFilePath).Trim();
            if (existing.Length >= 32)
            {
                // A key made by an earlier build may be readable by every user on the server. It is the root of every
                // token this server signs and every cloud-backup token it derives, so close it on the way past.
                RestrictToOwner(keyFilePath);
                return existing;
            }
        }

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);

        // Created readable by its owner only, from the first byte - not written world-readable and tightened after.
        // The mode is set on Linux only: on Windows, UnixCreateMode throws (it is NOT ignored), and the file there
        // simply inherits the per-user ACL of its folder.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(keyFilePath, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(secret);
        }

        return secret;
    }

    /// <summary>Owner-only permissions on Linux. A no-op on Windows, which has no such mode bits.</summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not ours to change (another owner): the key still works, and the operator can fix the mode.
        }
    }
}
