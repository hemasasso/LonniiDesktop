using Lonnii.Api.Security;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Data.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Navigation;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// Privilege inspection and management, and the resolved menu.
/// <c>GET /api/privileges/me</c> is the desktop equivalent of the web app's
/// <c>GET /gestion/:sessionToken/my-privileges</c>.
/// </summary>
public static class PrivilegeEndpoints
{
    public static void MapPrivilegeEndpoints(this IEndpointRouteBuilder app)
    {
        var privileges = app.MapGroup("/api/privileges").WithTags("Privileges");

        privileges.MapGet("/me", MineAsync).RequireGroupScope();
        privileges.MapGet("/menu", MenuAsync).RequireGroupScope();

        privileges.MapGet("/catalog", CatalogAsync)
            .RequireGroupScope().RequireGroupAdmin();
        privileges.MapGet("/member/{userId}", MemberPrivilegesAsync)
            .RequireGroupScope().RequireGroupAdmin();
        privileges.MapPost("/gestion", SetGestionPrivilegeAsync)
            .RequireGroupScope().RequireGroupAdmin();
        privileges.MapPost("/option", SetOptionPrivilegeAsync)
            .RequireGroupScope().RequireGroupAdmin();
        privileges.MapPost("/role", SetRoleAsync)
            .RequireGroupScope().RequireGroupAdmin();
    }

    /// <summary>The caller's effective privileges in the current group.</summary>
    private static IResult MineAsync(GroupScope scope)
    {
        var p = scope.Privileges;
        return Results.Ok(new MyPrivilegesResponse(
            p.Role, p.IsAdmin, p.IsAdminGeneral, p.Option, p.Gestion));
    }

    /// <summary>
    /// The menu, filtered exactly as the React components filter their navigation cards.
    /// Returning it from the server keeps the desktop client from re-implementing the rules.
    /// </summary>
    private static async Task<IResult> MenuAsync(
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var groupe = await db.Groupes.FirstAsync(g => g.Id == scope.GroupId, ct);
        var all = scope.Privileges.All();

        var espace = AppMenu.Visible(
            AppMenu.Espace, all, groupe.GestionAccess, groupe.PrestationsEnabled, scope.IsAdmin);
        var gestion = AppMenu.Visible(
            AppMenu.Gestion, all, groupe.GestionAccess, groupe.PrestationsEnabled, scope.IsAdmin);

        // Gestion entries are only reachable at all when the group has gestion access.
        if (!groupe.GestionAccess) gestion = [];

        var sections = AppMenu.GestionSections
            .Select(s => new MenuSectionDto(
                s.Id, s.Label, s.Description, s.Accent,
                gestion.Where(e => s.Keys.Contains(e.Key)).Select(ToDto).ToList()))
            .Where(s => s.Entries.Count > 0)
            .ToList();

        // The shell navigates entirely through these sections, so an entry that belongs to
        // none of them would have no pill to open it. Rather than let it vanish silently,
        // collect the strays into a section of their own.
        var placed = AppMenu.GestionSections.SelectMany(s => s.Keys).ToHashSet(StringComparer.Ordinal);
        var strays = gestion.Where(e => !placed.Contains(e.Key)).Select(ToDto).ToList();
        if (strays.Count > 0)
            sections.Add(new MenuSectionDto("autres", "Autres", "Autres modules", "#64748b", strays));

        return Results.Ok(new MenuResponse(
            espace.Select(ToDto).ToList(),
            gestion.Select(ToDto).ToList(),
            sections));

        static MenuEntryDto ToDto(MenuEntry e) => new(e.Key, e.Label, e.Description, e.Accent);
    }

    /// <summary>The full privilege catalogue, for the Paramètres screen.</summary>
    private static async Task<IResult> CatalogAsync(LonniiDbContext db, CancellationToken ct)
    {
        var gestion = await db.GestionPrivileges
            .OrderBy(p => p.Module).ThenBy(p => p.DisplayName)
            .Select(p => new PrivilegeDto(p.Id, p.Name, p.DisplayName, p.Description, p.Module, p.IsAdminOnly, false))
            .ToListAsync(ct);

        var option = await db.OptionPrivileges
            .OrderBy(p => p.Module).ThenBy(p => p.DisplayName)
            .Select(p => new PrivilegeDto(p.Id, p.Name, p.DisplayName, p.Description, p.Module, p.IsAdminOnly, false))
            .ToListAsync(ct);

        return Results.Ok(new { Gestion = gestion, Option = option });
    }

    /// <summary>
    /// One member's privileges, with <c>IsGranted</c> filled in - what the privilege
    /// management dialog binds its checkboxes to.
    /// </summary>
    private static async Task<IResult> MemberPrivilegesAsync(
        string userId, GroupScope scope, LonniiDbContext db, PrivilegeResolver resolver, CancellationToken ct)
    {
        var isMember = await db.GroupMembers.AnyAsync(m => m.IdGroupe == scope.GroupId && m.IdUser == userId, ct);
        if (!isMember) return Results.NotFound(new ApiError("Ce membre est introuvable dans le groupe"));

        var resolved = await resolver.ResolveAsync(userId, scope.GroupId, ct);

        var gestion = await db.GestionPrivileges
            .OrderBy(p => p.Module).ThenBy(p => p.DisplayName)
            .ToListAsync(ct);

        var option = await db.OptionPrivileges
            .OrderBy(p => p.Module).ThenBy(p => p.DisplayName)
            .ToListAsync(ct);

        return Results.Ok(new MemberPrivilegesResponse(
            resolved.Role,
            resolved.IsAdminGeneral,
            gestion.Select(p => new PrivilegeDto(
                p.Id, p.Name, p.DisplayName, p.Description, p.Module, p.IsAdminOnly,
                resolved.HasGestion(p.Name))).ToList(),
            option.Select(p => new PrivilegeDto(
                p.Id, p.Name, p.DisplayName, p.Description, p.Module, p.IsAdminOnly,
                resolved.HasOption(p.Name))).ToList()));
    }

    private static Task<IResult> SetGestionPrivilegeAsync(
        SetPrivilegeRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct) =>
        SetPrivilegeAsync(request, scope, db, isGestion: true, ct);

    private static Task<IResult> SetOptionPrivilegeAsync(
        SetPrivilegeRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct) =>
        SetPrivilegeAsync(request, scope, db, isGestion: false, ct);

    /// <summary>
    /// Grants or revokes one privilege and records the change in the matching audit table.
    /// Admin-only privileges are refused: the web app forces them to false when resolving,
    /// so storing a grant would be a row that never takes effect.
    /// </summary>
    private static async Task<IResult> SetPrivilegeAsync(
        SetPrivilegeRequest request, GroupScope scope, LonniiDbContext db, bool isGestion, CancellationToken ct)
    {
        var isMember = await db.GroupMembers
            .AnyAsync(m => m.IdGroupe == scope.GroupId && m.IdUser == request.UserId, ct);
        if (!isMember) return Results.NotFound(new ApiError("Ce membre est introuvable dans le groupe"));

        var creatorId = await db.Groupes
            .Where(g => g.Id == scope.GroupId).Select(g => g.IdUserAdmin).FirstAsync(ct);

        if (request.UserId == creatorId)
            return Results.BadRequest(new ApiError("Le créateur du groupe possède déjà tous les privilèges"));

        int privilegeId;
        bool isAdminOnly;
        string? module;

        if (isGestion)
        {
            var p = await db.GestionPrivileges.FirstOrDefaultAsync(x => x.Name == request.PrivilegeName, ct);
            if (p is null) return Results.NotFound(new ApiError("Privilège inconnu"));
            (privilegeId, isAdminOnly, module) = (p.Id, p.IsAdminOnly, p.Module);
        }
        else
        {
            var p = await db.OptionPrivileges.FirstOrDefaultAsync(x => x.Name == request.PrivilegeName, ct);
            if (p is null) return Results.NotFound(new ApiError("Privilège inconnu"));
            (privilegeId, isAdminOnly, module) = (p.Id, p.IsAdminOnly, p.Module);
        }

        if (isAdminOnly)
        {
            return Results.BadRequest(new ApiError(
                "Ce privilège est réservé aux administrateurs et ne peut pas être accordé individuellement",
                request.PrivilegeName));
        }

        var action = request.Granted ? "grant" : "revoke";

        if (isGestion)
        {
            var existing = await db.GestionUserPrivileges.FirstOrDefaultAsync(
                up => up.UserId == request.UserId && up.GroupId == scope.GroupId && up.PrivilegeId == privilegeId, ct);

            if (existing is null)
            {
                if (request.Granted)
                {
                    db.GestionUserPrivileges.Add(new GestionUserPrivilege
                    {
                        UserId = request.UserId,
                        GroupId = scope.GroupId,
                        PrivilegeId = privilegeId,
                        GrantedBy = scope.UserId,
                        IsActive = true,
                    });
                }
            }
            else
            {
                existing.IsActive = request.Granted;
                existing.GrantedBy = scope.UserId;
                existing.GrantedAt = DateTime.UtcNow;
            }

            db.GestionPrivilegeAudits.Add(new GestionPrivilegeAudit
            {
                UserId = scope.UserId,
                GroupId = scope.GroupId,
                TargetUserId = request.UserId,
                Action = action,
                PrivilegeId = privilegeId,
                Module = module,
                PerformedBy = scope.UserId,
                Reason = request.Reason,
            });
        }
        else
        {
            var existing = await db.OptionUserPrivileges.FirstOrDefaultAsync(
                up => up.UserId == request.UserId && up.GroupId == scope.GroupId && up.PrivilegeId == privilegeId, ct);

            if (existing is null)
            {
                if (request.Granted)
                {
                    db.OptionUserPrivileges.Add(new OptionUserPrivilege
                    {
                        UserId = request.UserId,
                        GroupId = scope.GroupId,
                        PrivilegeId = privilegeId,
                        GrantedBy = scope.UserId,
                        IsActive = true,
                    });
                }
            }
            else
            {
                existing.IsActive = request.Granted;
                existing.GrantedBy = scope.UserId;
                existing.GrantedAt = DateTime.UtcNow;
            }

            db.OptionPrivilegeAudits.Add(new OptionPrivilegeAudit
            {
                UserId = scope.UserId,
                GroupId = scope.GroupId,
                TargetUserId = request.UserId,
                Action = action,
                PrivilegeId = privilegeId,
                Module = module,
                PerformedBy = scope.UserId,
                Reason = request.Reason,
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Changes a member's group role, recording the change in the audit log.</summary>
    private static async Task<IResult> SetRoleAsync(
        SetRoleRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (!GroupRoles.All.Contains(request.Role))
            return Results.BadRequest(new ApiError($"Rôle inconnu: {request.Role}"));

        var creatorId = await db.Groupes
            .Where(g => g.Id == scope.GroupId).Select(g => g.IdUserAdmin).FirstAsync(ct);

        if (request.UserId == creatorId)
            return Results.BadRequest(new ApiError("Le rôle du créateur du groupe ne peut pas être modifié"));

        // Only the group creator may hand out or take away an admin role.
        if ((GroupRoles.IsAdminRole(request.Role) || scope.UserId != creatorId) && !scope.IsAdminGeneral)
            return Results.Json(new ApiError("Réservé au créateur du groupe"), statusCode: StatusCodes.Status403Forbidden);

        var existing = await db.UserRoles
            .FirstOrDefaultAsync(r => r.UserId == request.UserId && r.GroupId == scope.GroupId, ct);

        var roleFrom = existing?.Role ?? GroupRoles.Member;

        if (existing is null)
        {
            db.UserRoles.Add(new UserRole
            {
                UserId = request.UserId,
                GroupId = scope.GroupId,
                Role = request.Role,
                AssignedBy = scope.UserId,
            });
        }
        else
        {
            existing.Role = request.Role;
            existing.AssignedBy = scope.UserId;
            existing.AssignedAt = DateTime.UtcNow;
            existing.IsActive = true;
        }

        db.PrivilegeAudits.Add(new PrivilegeAudit
        {
            UserId = scope.UserId,
            GroupId = scope.GroupId,
            TargetUserId = request.UserId,
            Action = RankOf(request.Role) > RankOf(roleFrom) ? "promote" : "demote",
            RoleFrom = roleFrom,
            RoleTo = request.Role,
            PerformedBy = scope.UserId,
            Reason = request.Reason,
        });

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
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
