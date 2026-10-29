using System.Collections.Concurrent;

namespace Lonnii.Api.Features.Stock;

/// <summary>
/// One shop's stock-moving writes, one at a time. A sale reads each product's quantity, checks
/// it, then writes the new one; two tills doing that in the same instant would both read 5,
/// both sell 3 and both write 2. Taking turns here - a few milliseconds each - means the second
/// reads what the first wrote, and its sale number counts the first sale too.
///
/// <para>
/// Per shop, so one shop's rush never slows another on the same computer. In-process, which is
/// enough: every till of a shop talks to the one API on its main computer, and our server runs
/// a single instance too.
/// </para>
/// </summary>
public static class ShopWriteLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public static async Task<IDisposable> AcquireAsync(string groupId, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
