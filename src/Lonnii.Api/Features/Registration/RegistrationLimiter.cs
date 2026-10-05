namespace Lonnii.Api.Features.Registration;

/// <summary>
/// Caps how often one address, and one network address, can ask for a confirmation code.
///
/// <para>
/// Registration is open to the internet and each request sends an email, so unchecked it is
/// both a way to fill our list with junk and a way to make us mail strangers. In memory on
/// purpose: a restart forgives everyone, which costs nothing, while a shared store would be
/// one more thing to run. The limits are per hour.
/// </para>
/// </summary>
public sealed class RegistrationLimiter(TimeProvider? clock = null)
{
    public const int PerEmailPerHour = 3;
    public const int PerIpPerHour = 10;

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, List<DateTimeOffset>> _hits = [];
    private readonly Lock _gate = new();

    /// <summary>Records an attempt and says whether it is allowed.</summary>
    public bool TryAcquire(string email, string? ip)
    {
        var now = _clock.GetUtcNow();

        lock (_gate)
        {
            var emailKey = "e:" + email.Trim().ToLowerInvariant();
            var ipKey = "i:" + (ip ?? "?");

            if (Count(emailKey, now) >= PerEmailPerHour || Count(ipKey, now) >= PerIpPerHour)
                return false;

            _hits[emailKey].Add(now);
            _hits[ipKey].Add(now);
            return true;
        }
    }

    private int Count(string key, DateTimeOffset now)
    {
        if (!_hits.TryGetValue(key, out var list)) _hits[key] = list = [];

        list.RemoveAll(t => now - t > Window);
        return list.Count;
    }
}
