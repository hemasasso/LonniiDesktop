using System.Security.Cryptography;
using System.Text;

namespace Lonnii.Api.Features.Remote;

/// <summary>What a remote session token says, once its signature and expiry have been checked.</summary>
public sealed record RemoteSession(string GroupId, string UserId, DateTime ExpiresAt);

/// <summary>
/// Signed, stateless tokens for an administrator viewing an online shop from a phone or a laptop
/// away from the shop: <c>rs.{groupId}.{userId}.{expiresUnix}.{signature}</c>, sent in the same
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

    public (string Token, DateTime ExpiresAt) Create(string groupId, string userId, DateTime now)
    {
        var expires = now.Add(Lifetime);
        var unix = new DateTimeOffset(expires, TimeSpan.Zero).ToUnixTimeSeconds();
        var body = $"{groupId}.{userId}.{unix}";
        return ($"{Prefix}{body}.{Sign(body)}", expires);
    }

    public RemoteSession? Verify(string? token, DateTime now)
    {
        if (!IsRemote(token)) return null;

        var parts = token!.Split('.');
        if (parts.Length != 5) return null;

        var body = $"{parts[1]}.{parts[2]}.{parts[3]}";
        var expected = Encoding.ASCII.GetBytes(Sign(body));
        var given = Encoding.ASCII.GetBytes(parts[4]);
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return null;

        if (!long.TryParse(parts[3], out var unix)) return null;
        var expires = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return expires <= now ? null : new RemoteSession(parts[1], parts[2], expires);
    }

    private string Sign(string body) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}
