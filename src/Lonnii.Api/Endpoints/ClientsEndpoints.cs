using Lonnii.Api.Security;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// The Ventes module's client space: saved customers (the <c>clients</c> table) with what each
/// has bought, owes, and when they last came.
///
/// Neither application links a sale to a client row - a sale stores the customer's name and
/// phone as typed at the till - so purchases are attributed by phone first, then by name,
/// ignoring case and spacing. Names found only on sales are listed as unsaved clients, so
/// the ranking covers every customer, not just the ones someone took the time to register.
/// </summary>
public static class ClientsEndpoints
{
    public static void MapClientsEndpoints(this IEndpointRouteBuilder app)
    {
        var clients = app.MapGroup("/api/ventes/clients").WithTags("Clients");

        // The till reads this to suggest names and warn about what a customer owes, so a
        // cashier who can only ring up sales needs it as much as a manager does.
        clients.MapGet("/", ListAsync)
            .RequireGroupScope().RequireAnyPrivilege(
                Priv.Gestion.CreateVente, Priv.Gestion.ViewVentes, Priv.Gestion.ManageClients);
        clients.MapPost("/", CreateAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageClients);
        clients.MapPut("/{id}", UpdateAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageClients);
    }

    private sealed class Totals
    {
        public string Nom = string.Empty;
        public string? Telephone;
        public int Achats;
        public decimal Total;
        public decimal Reste;
        public int Impayees;
        public DateTime? Dernier;
    }

    private static async Task<IResult> ListAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var groupId = scope.GroupId;

        var saved = await db.Clients.AsNoTracking()
            .Where(c => c.GroupId == groupId)
            .ToListAsync(ct);

        var ventes = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && (v.ClientNom != null || v.ClientTelephone != null))
            .Select(v => new { v.Id, v.ClientNom, v.ClientTelephone, v.MontantTotal, v.StatutPaiement, v.DateVente })
            .ToListAsync(ct);

        // Summed in C#, the way BilanEndpoints does it: under SQLite the amount goes through
        // the money converter, which an SQL SUM would not undo.
        var paiements = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && (v.ClientNom != null || v.ClientTelephone != null))
            .Join(db.PaiementsVentes, v => v.Id, p => p.VenteId, (v, p) => new { p.VenteId, p.Montant })
            .ToListAsync(ct);
        var paye = paiements.GroupBy(p => p.VenteId).ToDictionary(g => g.Key, g => g.Sum(p => p.Montant));

        var byPhone = new Dictionary<string, Client>();
        var byName = new Dictionary<string, Client>();
        foreach (var client in saved)
        {
            if (PhoneKey(client.Telephone) is { } phone) byPhone.TryAdd(phone, client);
            byName.TryAdd(NameKey(client.Nom), client);
        }

        var savedTotals = saved.ToDictionary(c => c.Id, c => new Totals { Nom = c.Nom, Telephone = c.Telephone });
        var unsaved = new Dictionary<string, Totals>();

        foreach (var v in ventes.Where(v => StatutPaiement.Normalise(v.StatutPaiement) != StatutPaiement.Annule))
        {
            var client = (PhoneKey(v.ClientTelephone) is { } phone ? byPhone.GetValueOrDefault(phone) : null)
                         ?? (string.IsNullOrWhiteSpace(v.ClientNom) ? null : byName.GetValueOrDefault(NameKey(v.ClientNom)));

            Totals totals;
            if (client is not null)
            {
                totals = savedTotals[client.Id];
            }
            else
            {
                // A sale with a phone but no name is not enough to say who bought.
                if (string.IsNullOrWhiteSpace(v.ClientNom)) continue;
                var key = NameKey(v.ClientNom);
                if (!unsaved.TryGetValue(key, out totals!))
                    unsaved[key] = totals = new Totals { Nom = v.ClientNom.Trim() };
            }

            var reste = Math.Max(v.MontantTotal - paye.GetValueOrDefault(v.Id), 0m);
            totals.Achats++;
            totals.Total += v.MontantTotal;
            totals.Reste += reste;
            if (reste > 0) totals.Impayees++;
            if (totals.Dernier is null || v.DateVente > totals.Dernier)
            {
                totals.Dernier = v.DateVente;
                if (!string.IsNullOrWhiteSpace(v.ClientTelephone) && client is null) totals.Telephone = v.ClientTelephone.Trim();
            }
        }

        var list = saved
            .Select(c => ToDto(c, savedTotals[c.Id]))
            .Concat(unsaved.Values.Select(t => new ClientDto(
                null, t.Nom, t.Telephone, null, null, null, null, true,
                t.Achats, t.Total, t.Reste, t.Impayees, t.Dernier)))
            .OrderByDescending(c => c.TotalAchats)
            .ThenBy(c => c.Nom, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Results.Ok(list);
    }

    private static async Task<IResult> CreateAsync(
        SaveClientRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Nom))
            return Results.BadRequest(new ApiError("Le nom du client est requis"));

        var key = NameKey(request.Nom);
        var existing = await db.Clients.AsNoTracking().Where(c => c.GroupId == scope.GroupId).Select(c => c.Nom).ToListAsync(ct);
        if (existing.Any(n => NameKey(n) == key))
            return Results.Conflict(new ApiError("Un client porte déjà ce nom"));

        var client = new Client { GroupId = scope.GroupId };
        Apply(client, request);
        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/ventes/clients/{client.Id}", ToDto(client, new Totals()));
    }

    private static async Task<IResult> UpdateAsync(
        string id, SaveClientRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Nom))
            return Results.BadRequest(new ApiError("Le nom du client est requis"));

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.GroupId == scope.GroupId, ct);
        if (client is null) return Results.NotFound(new ApiError("Client introuvable"));

        var key = NameKey(request.Nom);
        var others = await db.Clients.AsNoTracking()
            .Where(c => c.GroupId == scope.GroupId && c.Id != id).Select(c => c.Nom).ToListAsync(ct);
        if (others.Any(n => NameKey(n) == key))
            return Results.Conflict(new ApiError("Un client porte déjà ce nom"));

        Apply(client, request);
        client.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(client, new Totals()));
    }

    private static void Apply(Client client, SaveClientRequest request)
    {
        client.Nom = request.Nom.Trim();
        client.Telephone = Blank(request.Telephone);
        client.Email = Blank(request.Email);
        client.Adresse = Blank(request.Adresse);
        client.Ville = Blank(request.Ville);
        client.Notes = Blank(request.Notes);
        client.IsActive = request.IsActive;
    }

    private static ClientDto ToDto(Client c, Totals t) => new(
        c.Id, c.Nom, c.Telephone, c.Email, c.Adresse, c.Ville, c.Notes, c.IsActive,
        t.Achats, t.Total, t.Reste, t.Impayees, t.Dernier);

    /// <summary>"  Jean   DUPONT " and "jean dupont" are the same customer typed twice.</summary>
    public static string NameKey(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();

    /// <summary>Digits only, so "690 00 00 00" and "690000000" match. Too short to be a real
    /// number means no key, rather than every "0" matching every other.</summary>
    public static string? PhoneKey(string? phone)
    {
        if (phone is null) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 6 ? digits : null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
