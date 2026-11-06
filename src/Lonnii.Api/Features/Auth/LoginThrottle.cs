using System.Collections.Concurrent;

namespace Lonnii.Api.Features.Auth;

/// <summary>
/// Stops password guessing at sign-in: after <see cref="Allowed"/> failures the account is locked for
/// 5 minutes, and every further round of failures locks it for 30 minutes, until a sign-in succeeds.
///
/// <para>
/// Counted twice - per account and per caller's address - and either lock refuses the attempt: one
/// phone on a shop's Wi-Fi cannot try a thousand passwords on one account, nor one password on a
/// thousand accounts. The lock answers the same whatever the password, so it cannot be used to test
/// one either.
/// </para>
/// <para>
/// In memory: a restart clears it, which costs an attacker a computer restart they cannot cause from
/// the Wi-Fi. A side effect accepted on purpose: someone guessing a known email also locks its owner
/// out for those minutes; the owner signs in again once it ends.
/// </para>
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int Allowed = 5;
    public static readonly TimeSpan FirstLock = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LaterLock = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Forget = TimeSpan.FromHours(24);

    private sealed class Entry
    {
        public int Failures;
        public int Locks;
        public DateTime? LockedUntil;
        public DateTime LastFailure;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public LoginThrottle() : this(TimeProvider.System) { }

    private static string AccountKey(string identifier) => "a:" + identifier.Trim().ToLowerInvariant();
    private static string AddressKey(string? address) => "ip:" + (address ?? "?");

    /// <summary>How long the caller must still wait, or null when it may try.</summary>
    public TimeSpan? Remaining(string identifier, string? address)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        TimeSpan? longest = null;
        foreach (var key in new[] { AccountKey(identifier), AddressKey(address) })
        {
            if (_entries.TryGetValue(key, out var e))
            {
                lock (e)
                {
                    if (e.LockedUntil is { } until && until > now && (longest is null || until - now > longest))
                        longest = until - now;
                }
            }
        }
        return longest;
    }

    /// <summary>Counts a wrong password; returns the lock it caused, if this failure caused one.</summary>
    public TimeSpan? Failed(string identifier, string? address)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        TimeSpan? caused = null;
        foreach (var key in new[] { AccountKey(identifier), AddressKey(address) })
        {
            var e = _entries.GetOrAdd(key, _ => new Entry());
            lock (e)
            {
                if (now - e.LastFailure > Forget) { e.Failures = 0; e.Locks = 0; }
                e.LastFailure = now;
                if (++e.Failures < Allowed) continue;

                var length = e.Locks == 0 ? FirstLock : LaterLock;
                e.Locks++;
                e.Failures = 0;
                e.LockedUntil = now + length;
                if (caused is null || length > caused) caused = length;
            }
        }
        return caused;
    }

    /// <summary>A right password: this account and this address start again from zero.</summary>
    public void Succeeded(string identifier, string? address)
    {
        _entries.TryRemove(AccountKey(identifier), out _);
        _entries.TryRemove(AddressKey(address), out _);
    }

    public static string Message(TimeSpan wait)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return $"Trop de tentatives incorrectes. Réessayez dans {minutes} minute{(minutes > 1 ? "s" : "")}.";
    }
}
