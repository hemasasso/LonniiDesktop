using Lonnii.Data;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Remote;

/// <summary>The options for the server's own database, as opposed to a shop's copy.</summary>
public sealed record ControlDbOptions(DbContextOptions<LonniiDbContext> Options);

/// <summary>Keys under which <see cref="RemoteRoutingMiddleware"/> leaves what it found on the request.</summary>
public static class RemoteKeys
{
    public const string ReplicaPath = "lonnii.replica.path";
    public const string SnapshotAt = "lonnii.replica.snapshotAt";
}

/// <summary>
/// The server's own database - accounts, shops, subscriptions, devices - even while the request's
/// <see cref="LonniiDbContext"/> has been pointed at a shop's copy.
///
/// <para>
/// A remote session reads the shop's data from its copy, but who the caller is and whether the
/// shop's licence is current are facts that live in the server's database, not in the copy. For
/// an ordinary request this simply is the request's own context; only a remote request needs a
/// second one.
/// </para>
/// </summary>
public sealed class ControlDb(
    LonniiDbContext requestDb, ControlDbOptions options, IHttpContextAccessor accessor) : IAsyncDisposable
{
    private LonniiDbContext? _own;

    public LonniiDbContext Db =>
        accessor.HttpContext?.Items.ContainsKey(RemoteKeys.ReplicaPath) == true
            ? _own ??= new LonniiDbContext(options.Options)
            : requestDb;

    public async ValueTask DisposeAsync()
    {
        if (_own is not null) await _own.DisposeAsync();
    }
}
