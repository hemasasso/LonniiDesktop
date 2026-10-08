using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Members;

/// <summary>What became of a requested change: <see cref="Status"/> is the HTTP status it maps to.</summary>
public sealed record PrivilegeChangeResult(int Status, string? Error = null, string? Required = null)
{
    public bool Ok => Status is >= 200 and < 300;

    public static readonly PrivilegeChangeResult Done = new(StatusCodes.Status204NoContent);

    public static PrivilegeChangeResult Refused(int status, string error, string? required = null) =>
        new(status, error, required);

    public IResult ToResult() =>
        Ok ? Results.NoContent() : Results.Json(new ApiError(Error ?? "Refusé", Required), statusCode: Status);
}

/// <summary>
/// Granting, revoking and changing roles - the rules in one place, so a change made from the
/// Paramètres screen on the shop's own computer and one queued from a phone far away are held to
/// exactly the same ones.
/// </summary>
public static class PrivilegeChanges
{
    /// <summary>Marks a change made from a phone or laptop away from the shop, in the audit trail.</summary>
    public const string RemoteReasonPrefix = "À distance : ";

    /// <summary>
    /// Grants or revokes one privilege and records the change in the matching audit table.
    /// Admin-only privileges are refused: the web app forces them to false when resolving,
    /// so storing a grant would be a row that never takes effect.
    /// </summary>
    public static async Task<PrivilegeChangeResult> SetPrivilegeAsync(
        LonniiDbContext db, string groupId, string actorUserId, SetPrivilegeRequest request,
        bool isGestion, string? reason, CancellationToken ct)
    {
        var isMember = await db.GroupMembers
            .AnyAsync(m => m.IdGroupe == groupId && m.IdUser == request.UserId, ct);
        if (!isMember)
            return PrivilegeChangeResult.Refused(StatusCodes.Status404NotFound, "Ce membre est introuvable dans le groupe");

        var creatorId = await db.Groupes
            .Where(g => g.Id == groupId).Select(g => g.IdUserAdmin).FirstAsync(ct);

        if (request.UserId == creatorId)
        {
            return PrivilegeChangeResult.Refused(StatusCodes.Status400BadRequest,
                "Le créateur du groupe possède déjà tous les privilèges");
        }

        int privilegeId;
        bool isAdminOnly;
        string? module;

        if (isGestion)
        {
            var p = await db.GestionPrivileges.FirstOrDefaultAsync(x => x.Name == request.PrivilegeName, ct);
            if (p is null) return PrivilegeChangeResult.Refused(StatusCodes.Status404NotFound, "Privilège inconnu");
            (privilegeId, isAdminOnly, module) = (p.Id, p.IsAdminOnly, p.Module);
        }
        else
        {
            var p = await db.OptionPrivileges.FirstOrDefaultAsync(x => x.Name == request.PrivilegeName, ct);
            if (p is null) return PrivilegeChangeResult.Refused(StatusCodes.Status404NotFound, "Privilège inconnu");
            (privilegeId, isAdminOnly, module) = (p.Id, p.IsAdminOnly, p.Module);
        }

        if (isAdminOnly)
        {
            return PrivilegeChangeResult.Refused(StatusCodes.Status400BadRequest,
                "Ce privilège est réservé aux administrateurs et ne peut pas être accordé individuellement",
                request.PrivilegeName);
        }

        var action = request.Granted ? "grant" : "revoke";
        var why = request.Reason ?? reason;

        if (isGestion)
        {
            var existing = await db.GestionUserPrivileges.FirstOrDefaultAsync(
                up => up.UserId == request.UserId && up.GroupId == groupId && up.PrivilegeId == privilegeId, ct);

            if (existing is null)
            {
                if (request.Granted)
                {
                    db.GestionUserPrivileges.Add(new GestionUserPrivilege
                    {
                        UserId = request.UserId,
                        GroupId = groupId,
                        PrivilegeId = privilegeId,
                        GrantedBy = actorUserId,
                        IsActive = true,
                    });
                }
            }
            else
            {
                existing.IsActive = request.Granted;
                existing.GrantedBy = actorUserId;
                existing.GrantedAt = DateTime.UtcNow;
            }

            db.GestionPrivilegeAudits.Add(new GestionPrivilegeAudit
            {
                UserId = actorUserId,
                GroupId = groupId,
                TargetUserId = request.UserId,
                Action = action,
                PrivilegeId = privilegeId,
                Module = module,
                PerformedBy = actorUserId,
                Reason = why,
            });
        }
        else
        {
            var existing = await db.OptionUserPrivileges.FirstOrDefaultAsync(
                up => up.UserId == request.UserId && up.GroupId == groupId && up.PrivilegeId == privilegeId, ct);

            if (existing is null)
            {
                if (request.Granted)
                {
                    db.OptionUserPrivileges.Add(new OptionUserPrivilege
                    {
                        UserId = request.UserId,
                        GroupId = groupId,
                        PrivilegeId = privilegeId,
                        GrantedBy = actorUserId,
                        IsActive = true,
                    });
                }
            }
            else
            {
                existing.IsActive = request.Granted;
                existing.GrantedBy = actorUserId;
                existing.GrantedAt = DateTime.UtcNow;
            }

            db.OptionPrivilegeAudits.Add(new OptionPrivilegeAudit
            {
                UserId = actorUserId,
                GroupId = groupId,
                TargetUserId = request.UserId,
                Action = action,
                PrivilegeId = privilegeId,
                Module = module,
                PerformedBy = actorUserId,
                Reason = why,
            });
        }

        await db.SaveChangesAsync(ct);
        return PrivilegeChangeResult.Done;
    }

    /// <summary>Changes a member's group role, recording the change in the audit log.</summary>
    public static async Task<PrivilegeChangeResult> SetRoleAsync(
        LonniiDbContext db, string groupId, string actorUserId, bool actorIsAdminGeneral,
        SetRoleRequest request, string? reason, CancellationToken ct)
    {
        if (!GroupRoles.All.Contains(request.Role))
            return PrivilegeChangeResult.Refused(StatusCodes.Status400BadRequest, $"Rôle inconnu: {request.Role}");

        var creatorId = await db.Groupes
            .Where(g => g.Id == groupId).Select(g => g.IdUserAdmin).FirstAsync(ct);

        if (request.UserId == creatorId)
        {
            return PrivilegeChangeResult.Refused(StatusCodes.Status400BadRequest,
                "Le rôle du créateur du groupe ne peut pas être modifié");
        }

        // Only the group creator may hand out or take away an admin role.
        if ((GroupRoles.IsAdminRole(request.Role) || actorUserId != creatorId) && !actorIsAdminGeneral)
        {
            return PrivilegeChangeResult.Refused(StatusCodes.Status403Forbidden, "Réservé au créateur du groupe");
        }

        var existing = await db.UserRoles
            .FirstOrDefaultAsync(r => r.UserId == request.UserId && r.GroupId == groupId, ct);

        var roleFrom = existing?.Role ?? GroupRoles.Member;

        if (existing is null)
        {
            db.UserRoles.Add(new UserRole
            {
                UserId = request.UserId,
                GroupId = groupId,
                Role = request.Role,
                AssignedBy = actorUserId,
            });
        }
        else
        {
            existing.Role = request.Role;
            existing.AssignedBy = actorUserId;
            existing.AssignedAt = DateTime.UtcNow;
            existing.IsActive = true;
        }

        db.PrivilegeAudits.Add(new PrivilegeAudit
        {
            UserId = actorUserId,
            GroupId = groupId,
            TargetUserId = request.UserId,
            Action = RankOf(request.Role) > RankOf(roleFrom) ? "promote" : "demote",
            RoleFrom = roleFrom,
            RoleTo = request.Role,
            PerformedBy = actorUserId,
            Reason = request.Reason ?? reason,
        });

        await db.SaveChangesAsync(ct);
        return PrivilegeChangeResult.Done;
    }

    /// <summary>Orders roles so the audit log can record a change as a promotion or a demotion.</summary>
    private static int RankOf(string role) => role switch
    {
        GroupRoles.Admin => 3,
        GroupRoles.SubAdmin => 2,
        GroupRoles.Moderator => 1,
        _ => 0,
    };
}
