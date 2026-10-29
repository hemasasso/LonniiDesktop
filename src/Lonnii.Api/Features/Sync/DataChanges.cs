using Lonnii.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lonnii.Api.Features.Sync;

/// <summary>
/// One saved change: which table, which row, and what happened to it. Today only the fact that
/// something changed is used (it wakes the cloud backup); live sync will send these rows themselves.
/// </summary>
public sealed record DataChange(string Table, string? Key, EntityState State, DateTime AtUtc);

/// <summary>
/// Where every saved change is reported. The one place the rest of the app writes to, so moving from
/// snapshot backups to live sync means changing what listens here, not every endpoint that saves.
///
/// <para>
/// Step 2 (live sync) plan: add a listener that writes each change to an outbox table in the same
/// transaction, and a sender that pushes the outbox to the licence server within seconds. Snapshots
/// then drop to a daily safety copy.
/// </para>
/// </summary>
public sealed class DataChangeSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _next = NewSource();

    /// <summary>When the oldest change not yet backed up was saved; null when there is none.</summary>
    public DateTime? FirstPendingUtc { get; private set; }

    /// <summary>When the most recent change was saved.</summary>
    public DateTime? LastChangeUtc { get; private set; }

    /// <summary>Raised for every save that changed something. Listeners must be quick and must not throw.</summary>
    public event Action<IReadOnlyList<DataChange>>? Changed;

    public void Report(IReadOnlyList<DataChange> changes)
    {
        if (changes.Count == 0) return;

        TaskCompletionSource toRelease;
        lock (_gate)
        {
            var now = changes[^1].AtUtc;
            FirstPendingUtc ??= now;
            LastChangeUtc = now;
            toRelease = _next;
            _next = NewSource();
        }

        toRelease.TrySetResult();
        Changed?.Invoke(changes);
    }

    /// <summary>Completes at the next change, or when <paramref name="timeout"/> passes.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task next;
        lock (_gate) next = _next.Task;
        await Task.WhenAny(next, Task.Delay(timeout, ct));
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>The pending changes up to <paramref name="takenAtUtc"/> are backed up; later ones still wait.</summary>
    public void MarkSent(DateTime takenAtUtc)
    {
        lock (_gate)
        {
            if (LastChangeUtc is null || LastChangeUtc <= takenAtUtc) FirstPendingUtc = null;
            else FirstPendingUtc = takenAtUtc;
        }
    }

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Reports every change saved through EF Core to <see cref="DataChangeSignal"/>. Writes made with raw
/// SQL bypass it; the backup's periodic check on the database file still catches those.
/// </summary>
public sealed class DataChangeInterceptor(DataChangeSignal signal) : SaveChangesInterceptor
{
    // Collected before the save (states are reset after it) and reported only once it succeeded.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, List<DataChange>> Pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        Flush(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Discard(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken ct = default)
    {
        Discard(eventData.Context);
        return Task.CompletedTask;
    }

    private static void Discard(DbContext? context)
    {
        if (context is not null) Pending.Remove(context);
    }

    private static void Collect(DbContext? context)
    {
        if (context is null) return;

        var now = DateTime.UtcNow;
        Pending.AddOrUpdate(context, context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            // The backup's own bookkeeping is not shop data: counting it would make every backup ask for another.
            .Where(e => e.Entity is not CloudBackupState)
            .Select(e => new DataChange(
                e.Metadata.GetTableName() ?? e.Metadata.ClrType.Name,
                KeyOf(e),
                e.State,
                now))
            .ToList());
    }

    private void Flush(DbContext? context)
    {
        if (context is null || !Pending.TryGetValue(context, out var changes)) return;
        Pending.Remove(context);
        if (changes.Count > 0) signal.Report(changes);
    }

    private static string? KeyOf(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null) return null;
        // An added row with a store-generated key has only a temporary value at this point; live sync
        // will need the outbox row written after the insert, which is why it belongs in the same save.
        return string.Join("|", key.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString()));
    }
}
