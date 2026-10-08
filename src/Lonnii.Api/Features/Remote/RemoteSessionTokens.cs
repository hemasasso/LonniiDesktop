using System.Security.Cryptography;
using System.Text;

namespace Lonnii.Api.Features.Remote;

/// <summary>
/// What a remote session token says, once its signature and expiry have been checked.
/// <paramref name="UserId"/> is the caller's account on the licence server (what their sign-in token
/// names); <paramref name="LocalUserId"/> is the same person's account inside the shop's own data,
/// matched by email when the session was opened. The two ids differ whenever the shop's account was
/// created on the shop's computer rather than on the server, and everything inside the shop - roles,
/// privileges, the audit trail - uses the shop's own.
/// </summary>
public sealed record RemoteSession(string GroupId, string UserId, string LocalUserId, DateTime ExpiresAt);

/// <summary>
/// Signed, stateless tokens for an administrator viewing an online shop from a phone or a laptop
/// away from the shop: <c>rs.{groupId}.{accountId}.{shopUserId}.{expiresUnix}.{signature}</c>, sent in the same
/// <c>x-group-session</c> header an ordinary group session uses.
///
/// <para>
/// Stateless on purpose. The request has to be routed to the shop's copy of its data before any
/// database is opened, and the token is what says which shop that is. A random token stored in a
/// table could not do that, and the licence server's PostgreSQL is not the place to keep sessions
/// for shops whose data does not live there.
/// </para>
/// </summary>
public sealed class RemoteSessionTokens(string secret)
{
    public const string Prefix = "rs.";

    /// <summary>Shorter than an ordinary session: a lapsed subscription or a removed administrator
    /// is only noticed when a session is opened, so the window is kept small.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);

    private readonly byte[] _key = Encoding.UTF8.GetBytes("lonnii-remote-session|" + secret);

    public static bool IsRemote(string? token) =>
        token is not null && token.StartsWith(Prefix, StringComparison.Ordinal);

    public (string Token, DateTime ExpiresAt) Create(string groupId, string accountId, string shopUserId, DateTime now)
    {
        var expires = now.Add(Lifetime);
        var unix = new DateTimeOffset(expires, TimeSpan.Zero).ToUnixTimeSeconds();
        var body = $"{groupId}.{accountId}.{shopUserId}.{unix}";
        return ($"{Prefix}{body}.{Sign(body)}", expires);
    }

    public RemoteSession? Verify(string? token, DateTime now)
    {
        if (!IsRemote(token)) return null;

        var parts = token!.Split('.');
        if (parts.Length != 6) return null;

        var body = $"{parts[1]}.{parts[2]}.{parts[3]}.{parts[4]}";
        var expected = Encoding.ASCII.GetBytes(Sign(body));
        var given = Encoding.ASCII.GetBytes(parts[5]);
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return null;

        if (!long.TryParse(parts[4], out var unix)) return null;
        var expires = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return expires <= now ? null : new RemoteSession(parts[1], parts[2], parts[3], expires);
    }

    private string Sign(string body) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
