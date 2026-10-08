using Lonnii.Api.Features.Live;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Data.Services;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Navigation;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Members;

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
        SetPrivilegeRequest request, GroupScope scope, LonniiDbContext db, ShopChangeNotifier changes, CancellationToken ct) =>
        SetPrivilegeAsync(request, scope, db, changes, isGestion: true, ct);

    private static Task<IResult> SetOptionPrivilegeAsync(
        SetPrivilegeRequest request, GroupScope scope, LonniiDbContext db, ShopChangeNotifier changes, CancellationToken ct) =>
        SetPrivilegeAsync(request, scope, db, changes, isGestion: false, ct);

    private static async Task<IResult> SetPrivilegeAsync(
        SetPrivilegeRequest request, GroupScope scope, LonniiDbContext db, ShopChangeNotifier changes, bool isGestion, CancellationToken ct)
    {
        var result = await PrivilegeChanges.SetPrivilegeAsync(db, scope.GroupId, scope.UserId, request, isGestion, null, ct);
        if (result.Ok) changes.Bump(scope.GroupId);
        return result.ToResult();
    }

    /// <summary>Changes a member's group role, recording the change in the audit log.</summary>
    private static async Task<IResult> SetRoleAsync(
        SetRoleRequest request, GroupScope scope, LonniiDbContext db, ShopChangeNotifier changes, CancellationToken ct)
    {
        var result = await PrivilegeChanges.SetRoleAsync(
            db, scope.GroupId, scope.UserId, scope.IsAdminGeneral, request, null, ct);
        if (result.Ok) changes.Bump(scope.GroupId);
        return result.ToResult();
    }
}
