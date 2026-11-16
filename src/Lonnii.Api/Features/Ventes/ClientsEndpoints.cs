using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Ventes;

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
        clients.MapGet("/consignes", ConsignesAsync)
            .RequireGroupScope().RequireAnyPrivilege(Priv.Gestion.CreateVente, Priv.Gestion.ManageClients);
        clients.MapPost("/emballages-retours", EmballageRetourAsync)
            .RequireGroupScope().RequireAnyPrivilege(Priv.Gestion.CreateVente, Priv.Gestion.ManageClients);
        clients.MapPut("/{id}", UpdateAsync)
            .RequireGroupScope().RequirePrivilege(Priv.Gestion.ManageClients);
    }

    private sealed class Totals
    {
        public string Nom = string.Empty;
        public string? Telephone;
        public string? Email;
        public int Achats;
        public decimal Total;
        public decimal Reste;
        public int Impayees;
        public DateTime? Dernier;
        public decimal Emballages;
        public decimal Consigne;
    }

    private static async Task<IResult> ListAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var groupId = scope.GroupId;

        var saved = await db.Clients.AsNoTracking()
            .Where(c => c.GroupId == groupId)
            .ToListAsync(ct);

        var ventes = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && (v.ClientNom != null || v.ClientTelephone != null || v.ClientEmail != null))
            .Select(v => new { v.Id, v.ClientNom, v.ClientTelephone, v.ClientEmail, v.MontantTotal, v.StatutPaiement, v.DateVente, v.EmballagesPris, v.EmballagesRendus, v.ConsigneAmount, v.ConsigneRendue })
            .ToListAsync(ct);

        // Summed in C#, the way BilanEndpoints does it: under SQLite the amount goes through
        // the money converter, which an SQL SUM would not undo.
        var paiements = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && (v.ClientNom != null || v.ClientTelephone != null || v.ClientEmail != null))
            .Join(db.PaiementsVentes, v => v.Id, p => p.VenteId, (v, p) => new { p.VenteId, p.Montant })
            .ToListAsync(ct);
        var paye = paiements.GroupBy(p => p.VenteId).ToDictionary(g => g.Key, g => g.Sum(p => p.Montant));

        // Two saved clients may share a name - phone or email is what tells them apart, so a
        // name match is only trusted here when it is not ambiguous.
        var byPhone = new Dictionary<string, Client>();
        var byEmail = new Dictionary<string, Client>();
        var byName = new Dictionary<string, List<Client>>();
        foreach (var client in saved)
        {
            if (PhoneKey(client.Telephone) is { } phone) byPhone.TryAdd(phone, client);
            if (EmailKey(client.Email) is { } email) byEmail.TryAdd(email, client);

            var nameKey = NameKey(client.Nom);
            if (!byName.TryGetValue(nameKey, out var group)) byName[nameKey] = group = [];
            group.Add(client);
        }

        var savedTotals = saved.ToDictionary(c => c.Id, c => new Totals { Nom = c.Nom, Telephone = c.Telephone, Email = c.Email });
        var unsaved = new Dictionary<string, Totals>();

        var retours = await db.EmballagesRetours.AsNoTracking()
            .Where(r => r.GroupId == groupId && r.VenteId == null)
            .Select(r => new { r.ClientNom, r.ClientTelephone, r.ClientEmail, r.Quantite, r.Montant })
            .ToListAsync(ct);
        foreach (var r in retours)
        {
            var client = (PhoneKey(r.ClientTelephone) is { } phone ? byPhone.GetValueOrDefault(phone) : null)
                         ?? (EmailKey(r.ClientEmail) is { } email ? byEmail.GetValueOrDefault(email) : null)
                         ?? (string.IsNullOrWhiteSpace(r.ClientNom) ? null
                             : byName.GetValueOrDefault(NameKey(r.ClientNom)) is { Count: 1 } group ? group[0] : null);
            if (client is not null)
            {
                savedTotals[client.Id].Emballages -= r.Quantite;
                savedTotals[client.Id].Consigne -= r.Montant ?? 0;
                continue;
            }
            if (string.IsNullOrWhiteSpace(r.ClientNom)) continue;
            var key = PhoneKey(r.ClientTelephone) is { } p ? $"p:{p}"
                : EmailKey(r.ClientEmail) is { } e ? $"e:{e}"
                : $"n:{NameKey(r.ClientNom)}";
            if (!unsaved.TryGetValue(key, out var t)) unsaved[key] = t = new Totals { Nom = r.ClientNom.Trim() };
            t.Emballages -= r.Quantite;
            t.Consigne -= r.Montant ?? 0;
        }

        foreach (var v in ventes.Where(v => StatutPaiement.Normalise(v.StatutPaiement) != StatutPaiement.Annule))
        {
            var client = (PhoneKey(v.ClientTelephone) is { } phone ? byPhone.GetValueOrDefault(phone) : null)
                         ?? (EmailKey(v.ClientEmail) is { } email ? byEmail.GetValueOrDefault(email) : null)
                         ?? (string.IsNullOrWhiteSpace(v.ClientNom) ? null
                             : byName.GetValueOrDefault(NameKey(v.ClientNom)) is { Count: 1 } group ? group[0] : null);

            Totals totals;
            if (client is not null)
            {
                totals = savedTotals[client.Id];
            }
            else
            {
                // A sale with only a phone or only an email, and no name, is not enough to say who bought.
                if (string.IsNullOrWhiteSpace(v.ClientNom)) continue;

                // Same name, different phone/email = a different person - each gets their own bucket.
                var key = PhoneKey(v.ClientTelephone) is { } p ? $"p:{p}"
                    : EmailKey(v.ClientEmail) is { } e ? $"e:{e}"
                    : $"n:{NameKey(v.ClientNom)}";
                if (!unsaved.TryGetValue(key, out totals!))
                    unsaved[key] = totals = new Totals { Nom = v.ClientNom.Trim() };
            }

            var reste = Math.Max(v.MontantTotal - paye.GetValueOrDefault(v.Id), 0m);
            totals.Achats++;
            totals.Emballages += (v.EmballagesPris ?? 0) - (v.EmballagesRendus ?? 0);
            totals.Consigne += (v.ConsigneAmount ?? 0) - (v.ConsigneRendue ?? 0);
            totals.Total += v.MontantTotal;
            totals.Reste += reste;
            if (reste > 0) totals.Impayees++;
            if (totals.Dernier is null || v.DateVente > totals.Dernier)
            {
                totals.Dernier = v.DateVente;
                if (client is null)
                {
                    if (!string.IsNullOrWhiteSpace(v.ClientTelephone)) totals.Telephone = v.ClientTelephone.Trim();
                    if (!string.IsNullOrWhiteSpace(v.ClientEmail)) totals.Email = v.ClientEmail.Trim();
                }
            }
        }

        var list = saved
            .Select(c => ToDto(c, savedTotals[c.Id]))
            .Concat(unsaved.Values.Where(t => t.Achats > 0).Select(t => new ClientDto(
                null, t.Nom, t.Telephone, t.Email, null, null, null, true,
                t.Achats, t.Total, t.Reste, t.Impayees, t.Dernier, t.Emballages, Math.Max(t.Consigne, 0))))
            .OrderByDescending(c => c.TotalAchats)
            .ThenBy(c => c.Nom, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Results.Ok(list);
    }

    private static async Task<IResult> EmballageRetourAsync(
        EmballageRetourRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (request.Quantite <= 0)
            return Results.BadRequest(new ApiError("Indiquez le nombre d'emballages rapportés"));
        if (!SaleCharges.IsQuarterStep(request.Quantite))
            return Results.BadRequest(new ApiError("Le nombre d'emballages se compte par quart (1, 1,5, 1 + 1/4…)"));
        if (string.IsNullOrWhiteSpace(request.ClientNom) && string.IsNullOrWhiteSpace(request.ClientTelephone)
            && string.IsNullOrWhiteSpace(request.ClientEmail))
            return Results.BadRequest(new ApiError("Le client est requis"));

        var nom = request.ClientNom?.Trim();
        var tel = string.IsNullOrWhiteSpace(request.ClientTelephone) ? null : request.ClientTelephone.Trim();
        var email = string.IsNullOrWhiteSpace(request.ClientEmail) ? null : request.ClientEmail.Trim();

        if (request.Lignes is not { Count: > 0 } lignes)
        {
            db.EmballagesRetours.Add(new EmballageRetour
            {
                GroupId = scope.GroupId, ClientNom = nom, ClientTelephone = tel, ClientEmail = email,
                Quantite = request.Quantite, CreatedBy = scope.UserId,
            });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }

        var creditAccepte = await db.VentesParametres.AsNoTracking()
            .Where(p => p.GroupeId == scope.GroupId).Select(p => p.EmballagesCreditAccepte).FirstOrDefaultAsync(ct) == true;
        var (retours, error) = await ConsigneRetours.BuildAsync(db, scope.GroupId, nom, tel, email, lignes, null, scope.UserId, ct,
            creditAccepte: creditAccepte);
        if (error is not null) return Results.BadRequest(new ApiError(error));

        var montant = retours.Sum(r => r.Montant ?? 0);
        if (montant > 0)
        {
            var mode = string.IsNullOrWhiteSpace(request.ModePaiement) ? ModePaiement.Cash : request.ModePaiement;
            foreach (var r in retours) r.ModePaiement = mode;
            // Mixte: part in mobile money, the rest in espèces - each out of its own pool.
            var mobile = Math.Clamp(request.MontantMobile ?? 0, 0, montant);
            var parts = mode == "mixte"
                ? new[] { (Mode: ModePaiement.Cash, Montant: montant - mobile), (Mode: ModePaiement.MobileMoney, Montant: mobile) }
                : new[] { (Mode: mode, Montant: montant) };
            foreach (var part in parts.Where(p => p.Montant > 0))
            {
                var refused = await Lonnii.Api.Features.Caisse.CaisseEndpoints.AddAvoirRefundAsync(
                    db, scope.GroupId, scope.UserId, part.Montant, part.Mode,
                    $"Remboursement consigne - {nom ?? tel ?? email}", ct, ConsigneRetours.CaisseCategory);
                if (refused is not null) return Results.BadRequest(new ApiError(refused));
            }
        }

        db.EmballagesRetours.AddRange(retours);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ConsignesAsync(
        string? nom, string? telephone, string? email, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nom) && string.IsNullOrWhiteSpace(telephone) && string.IsNullOrWhiteSpace(email))
            return Results.Ok(Array.Empty<ConsigneSoldeDto>());
        return Results.Ok(await ConsigneRetours.SoldesAsync(db, scope.GroupId, nom, telephone, email, ct));
    }

    private static async Task<IResult> CreateAsync(
        SaveClientRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Nom))
            return Results.BadRequest(new ApiError("Le nom du client est requis"));

        var others = await db.Clients.AsNoTracking().Where(c => c.GroupId == scope.GroupId)
            .Select(c => new { c.Nom, c.Telephone, c.Email }).ToListAsync(ct);
        if (others.Any(o => IsIndistinguishable(o.Nom, o.Telephone, o.Email, request.Nom, request.Telephone, request.Email)))
            return Results.Conflict(new ApiError(
                "Un client porte déjà ce nom - ajoutez un numéro ou un email pour le différencier"));

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

        var others = await db.Clients.AsNoTracking()
            .Where(c => c.GroupId == scope.GroupId && c.Id != id)
            .Select(c => new { c.Nom, c.Telephone, c.Email }).ToListAsync(ct);
        if (others.Any(o => IsIndistinguishable(o.Nom, o.Telephone, o.Email, request.Nom, request.Telephone, request.Email)))
            return Results.Conflict(new ApiError(
                "Un client porte déjà ce nom - ajoutez un numéro ou un email pour le différencier"));

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
        t.Achats, t.Total, t.Reste, t.Impayees, t.Dernier, t.Emballages, Math.Max(t.Consigne, 0));

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

    public static string? EmailKey(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    /// <summary>Two clients with the same name are the same customer only if nothing on either
    /// record tells them apart - a phone or an email that differs (or that only one of them has)
    /// is enough to say they are two different people sharing a name.</summary>
    private static bool IsIndistinguishable(
        string nomA, string? telA, string? emailA, string nomB, string? telB, string? emailB)
    {
        if (NameKey(nomA) != NameKey(nomB)) return false;

        var phoneA = PhoneKey(telA);
        var phoneB = PhoneKey(telB);
        if (phoneA is not null || phoneB is not null) return phoneA == phoneB;

        var mailA = EmailKey(emailA);
        var mailB = EmailKey(emailB);
        return mailA == mailB;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
