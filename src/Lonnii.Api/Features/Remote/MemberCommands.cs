using Lonnii.Api.Features.Auth;
using Lonnii.Api.Features.Live;
using Lonnii.Api.Features.Members;
using Lonnii.Data;
using Lonnii.Data.Services;
using Lonnii.Shared.Contracts;

namespace Lonnii.Api.Features.Remote;

/// <summary>
/// Members added or removed from afar - someone hired while the owner is away - run through the very
/// handlers the Espace screen uses, so the same rules hold: an existing account is simply added, a new
/// one needs a password, the creator can never be removed. Used twice, like <see cref="ProductCommands"/>:
/// on the server inside a transaction that is rolled back, to refuse a bad request at once; on the
/// shop's computer, for real.
/// </summary>
internal static class MemberCommands
{
    /// <summary>What is missing from the request for its type, or null when it is complete.</summary>
    public static string? Incomplete(string type, string? userId, AddMemberRequest? member) => type switch
    {
        RemoteCommandTypes.MemberAdd when string.IsNullOrWhiteSpace(member?.Identifier) =>
            "L'email ou le nom d'utilisateur du membre est requis.",
        RemoteCommandTypes.MemberRemove when string.IsNullOrWhiteSpace(userId) => "Le membre à retirer est requis.",
        _ => null,
    };

    /// <param name="passwordHash">For a new account on the shop: the password the server checked and hashed.</param>
    public static async Task<PrivilegeChangeResult> RunAsync(
        string type, string? userId, AddMemberRequest? member, string? passwordHash,
        GroupScope scope, LonniiDbContext db, DatabaseSeeder seeder, ShopChangeNotifier changes, CancellationToken ct)
    {
        // The Espace screen's own rule: only administrators manage members.
        if (!scope.IsAdmin && !scope.IsAdminGeneral)
            return PrivilegeChangeResult.Refused(StatusCodes.Status403Forbidden, "Seuls les administrateurs gèrent les membres.");

        if (Incomplete(type, userId, member) is { } missing)
            return PrivilegeChangeResult.Refused(StatusCodes.Status400BadRequest, missing);

        var result = type == RemoteCommandTypes.MemberAdd
            ? await GroupEndpoints.AddMemberCoreAsync(member!, passwordHash, scope, db, seeder, changes, ct)
            : await GroupEndpoints.RemoveMemberAsync(userId!, scope, db, changes, ct);

        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;

        return status is >= 200 and < 300
            ? PrivilegeChangeResult.Done
            : PrivilegeChangeResult.Refused(status, ((result as IValueHttpResult)?.Value as ApiError)?.Error ?? "Modification refusée.");
    }

    /// <summary>The name a queued request shows for the person being added.</summary>
    public static string DisplayName(AddMemberRequest member) =>
        string.Join(' ', new[] { member.FirstName, member.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } full
            ? full
            : member.Username ?? member.Identifier;
}
