using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Ventes;

/// <summary>
/// The shop's serveurs (waiters, agents… - the name is the shop's own, set in Paramètres Reçu
/// et Facture). A plain name list the till picks from, so a cashier never retypes a name and
/// "Koffi" cannot split into "koffi" and "Kofi" in the stats. Serveurs are not accounts.
/// </summary>
public static class ServeursEndpoints
{
    public static void MapServeursEndpoints(this IEndpointRouteBuilder app)
    {
        var serveurs = app.MapGroup("/api/ventes/serveurs").WithTags("Serveurs");

        // The till fills its picker from this, so selling is enough to read it.
        serveurs.MapGet("/", ListAsync)
            .RequireGroupScope().RequireAnyPrivilege(
                Priv.Gestion.CreateVente, Priv.Gestion.ViewVentes, Priv.Gestion.ManageClients);

        // The till's "+" adds a new serveur mid-sale, so a cashier may create one; renaming
        // or deactivating is managed like the clients.
        serveurs.MapPost("/", CreateAsync)
            .RequireGroupScope().RequireAnyPrivilege(Priv.Gestion.CreateVente, Priv.Gestion.ManageClients);
        serveurs.MapPut("/{id}", UpdateAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageClients);
    }

    private static async Task<IResult> ListAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var rows = await db.Serveurs.AsNoTracking()
            .Where(s => s.GroupId == scope.GroupId)
            .ToListAsync(ct);

        return Results.Ok(rows
            .OrderByDescending(s => s.IsActive)
            .ThenBy(s => s.Nom, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToDto)
            .ToList());
    }

    private static async Task<IResult> CreateAsync(
        SaveServeurRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } error) return error;

        if (await FindSameNameAsync(db, scope.GroupId, request.Nom, exceptId: null, ct) is { } existing)
        {
            // Typed again from the till: hand back the one that exists (reactivated) rather
            // than refuse, so the "+" button never blocks a sale.
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                existing.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(ToDto(existing));
        }

        var serveur = new Serveur { GroupId = scope.GroupId };
        Apply(serveur, request);
        db.Serveurs.Add(serveur);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/ventes/serveurs/{serveur.Id}", ToDto(serveur));
    }

    private static async Task<IResult> UpdateAsync(
        string id, SaveServeurRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (Validate(request) is { } error) return error;

        var serveur = await db.Serveurs.FirstOrDefaultAsync(s => s.Id == id && s.GroupId == scope.GroupId, ct);
        if (serveur is null) return Results.NotFound(new ApiError("Introuvable"));

        if (await FindSameNameAsync(db, scope.GroupId, request.Nom, exceptId: id, ct) is not null)
            return Results.Conflict(new ApiError("Ce nom existe déjà"));

        Apply(serveur, request);
        serveur.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(serveur));
    }

    private static IResult? Validate(SaveServeurRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Nom))
            return Results.BadRequest(new ApiError("Le nom est requis"));
        if (request.Nom.Trim().Length > 255)
            return Results.BadRequest(new ApiError("Le nom ne peut pas dépasser 255 caractères."));
        if (request.Telephone is { Length: > 0 } tel && tel.Trim().Length > 100)
            return Results.BadRequest(new ApiError("Le téléphone ne peut pas dépasser 100 caractères."));
        return null;
    }

    /// <summary>Same name ignoring case and spacing - names are the only key a serveur has.</summary>
    private static async Task<Serveur?> FindSameNameAsync(
        LonniiDbContext db, string groupId, string nom, string? exceptId, CancellationToken ct)
    {
        var key = ClientsEndpoints.NameKey(nom);
        var rows = await db.Serveurs.Where(s => s.GroupId == groupId && s.Id != exceptId).ToListAsync(ct);
        return rows.FirstOrDefault(s => ClientsEndpoints.NameKey(s.Nom) == key);
    }

    private static void Apply(Serveur serveur, SaveServeurRequest request)
    {
        serveur.Nom = string.Join(' ', request.Nom.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        serveur.Telephone = string.IsNullOrWhiteSpace(request.Telephone) ? null : request.Telephone.Trim();
        serveur.IsActive = request.IsActive;
    }

    private static ServeurDto ToDto(Serveur s) => new(s.Id, s.Nom, s.Telephone, s.IsActive, s.CreatedAt);
}
