using System.Security.Cryptography;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Services;

/// <summary>
/// Issues and validates the per-group session tokens, ported from the web app's
/// <c>groupe_sessions</c> table. A signed-in user picks one group, receives a token,
/// and sends it as <c>x-group-session</c> on every group-scoped request. This keeps
/// the group out of the URL and makes a request that forgets it fail closed.
/// </summary>
public class GroupSessionService(LonniiDbContext db)
{
    /// <summary>Header carrying the group session token, matching the web client.</summary>
    public const string HeaderName = "x-group-session";

    /// <summary>How long a group session lasts before the client must pick its group again.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>
    /// Opens a session for a user in a group they belong to.
    /// Returns null when the user is neither the creator nor a member.
    /// </summary>
    public async Task<GroupeSession?> OpenAsync(string userId, string groupId, CancellationToken ct = default)
    {
        var groupe = await db.Groupes.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (groupe is null) return null;

        var isMember = groupe.IdUserAdmin == userId ||
                       await db.GroupMembers.AnyAsync(m => m.IdGroupe == groupId && m.IdUser == userId, ct);
        if (!isMember) return null;

        var session = new GroupeSession
        {
            SessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            GroupId = groupId,
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime),
        };

        db.GroupeSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// Resolves a session token, refreshing its last-accessed stamp.
    /// Returns null when the token is unknown, expired, or belongs to another user.
    /// </summary>
    public async Task<GroupeSession?> ResolveAsync(string token, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var session = await db.GroupeSessions
            .FirstOrDefaultAsync(s => s.SessionToken == token, ct);

        if (session is null) return null;
        if (session.UserId != userId) return null;
        if (session.ExpiresAt <= DateTime.UtcNow) return null;

        session.LastAccessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>Closes one session, used when the client switches group or signs out.</summary>
    public async Task CloseAsync(string token, CancellationToken ct = default)
    {
        await db.GroupeSessions.Where(s => s.SessionToken == token).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deletes expired sessions. Called on start and after each sign-in rather than on a
    /// timer, which is enough for a handful of tills and keeps the host free of background work.
    /// </summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default) =>
        await db.GroupeSessions.Where(s => s.ExpiresAt <= DateTime.UtcNow).ExecuteDeleteAsync(ct);
}
