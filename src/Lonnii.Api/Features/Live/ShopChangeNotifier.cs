using System.Collections.Concurrent;

namespace Lonnii.Api.Features.Live;

/// <summary>
/// A per-shop change counter. Anything that alters what a till or a remote admin screen
/// shows - a privilege, a role, a member - bumps it, and clients that poll
/// <c>GET /api/live/version</c> reload as soon as the number moves.
///
/// Kept in memory on purpose: it only has to differ from what a client last saw. Seeding
/// from the clock means a restart changes every value, so clients reload once rather than
/// miss a change made while the API was down.
/// </summary>
public sealed class ShopChangeNotifier
{
    private readonly ConcurrentDictionary<string, long> _versions = new();
    private readonly long _seed = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

    public long Get(string groupId) => _versions.TryGetValue(groupId, out var v) ? v : _seed;

    public void Bump(string groupId) =>
        _versions.AddOrUpdate(groupId, _seed + 1, (_, current) => current + 1);
}
