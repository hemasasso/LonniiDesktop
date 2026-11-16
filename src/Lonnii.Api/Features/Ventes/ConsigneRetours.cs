using Lonnii.Api.Features.Stock;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Ventes;

/// <summary>
/// Returnable packaging per client and emballage: crates taken with consigne on sales, minus
/// empties brought back. An emballage is a product's own crate, or a shared type when products'
/// crates are interchangeable (Brafaso and Brafort): a client who took a Brafaso crate may bring
/// back a Brafort one. A refund follows what that emballage's consigne actually was for this
/// client - the deposit still held divided by the crates still owed - and never exceeds it.
/// A client is matched the way the Clients list matches sales: phone, else email, else name.
/// </summary>
public static class ConsigneRetours
{
    /// <summary>Caisse movement category of a consigne paid back out of the drawer.</summary>
    public const string CaisseCategory = "consigne_rembourse";

    public static async Task<List<ConsigneSoldeDto>> SoldesAsync(
        LonniiDbContext db, string groupId, string? nom, string? tel, string? email, CancellationToken ct) =>
        await SoldesAsync(db, groupId, nom, tel, email, await EmballagesEndpoints.EmballagesAsync(db, groupId, ct), ct);

    private static async Task<List<ConsigneSoldeDto>> SoldesAsync(
        LonniiDbContext db, string groupId, string? nom, string? tel, string? email,
        EmballagesEndpoints.Emballages map, CancellationToken ct)
    {
        var phone = ClientsEndpoints.PhoneKey(tel);
        var mail = ClientsEndpoints.EmailKey(email);
        var name = string.IsNullOrWhiteSpace(nom) ? null : ClientsEndpoints.NameKey(nom);
        if (phone is null && mail is null && name is null) return [];

        bool Same(string? n, string? t, string? e) =>
            phone is not null ? ClientsEndpoints.PhoneKey(t) == phone
            : mail is not null ? ClientsEndpoints.EmailKey(e) == mail
            : !string.IsNullOrWhiteSpace(n) && ClientsEndpoints.NameKey(n) == name;

        var lignes = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && v.StatutPaiement != StatutPaiement.Annule
                && (v.ClientNom != null || v.ClientTelephone != null || v.ClientEmail != null))
            .Join(db.VenteItems.Where(i => i.EmballagesPris != null && i.ProductId != null),
                v => v.Id, i => i.VenteId,
                (v, i) => new { v.ClientNom, v.ClientTelephone, v.ClientEmail, i.ProductId, i.NomProduit, i.Unite, i.EmballagesPris, i.Consigne, i.EmballageContenance })
            .ToListAsync(ct);

        var retours = await db.EmballagesRetours.AsNoTracking()
            .Where(r => r.GroupId == groupId && r.ProductId != null)
            .Select(r => new { r.ClientNom, r.ClientTelephone, r.ClientEmail, r.ProductId, r.Quantite, r.Montant })
            .ToListAsync(ct);

        // Per emballage key (the shared type, or the product itself).
        var soldes = new Dictionary<string, (string Nom, string? Unite, decimal Pris, decimal Versee, decimal Rendus, decimal Remboursee, int? Contenance, string ProductId)>();
        foreach (var l in lignes.Where(l => Same(l.ClientNom, l.ClientTelephone, l.ClientEmail)))
        {
            var key = map.Key(l.ProductId!);
            var s = soldes.TryGetValue(key, out var found) ? found
                : (Nom: l.NomProduit, Unite: l.Unite, Pris: 0m, Versee: 0m, Rendus: 0m, Remboursee: 0m, Contenance: (int?)null, ProductId: l.ProductId!);
            soldes[key] = (l.NomProduit, s.Unite ?? l.Unite, s.Pris + (l.EmballagesPris ?? 0), s.Versee + (l.Consigne ?? 0), s.Rendus, s.Remboursee,
                l.EmballageContenance ?? s.Contenance, s.ProductId);
        }
        foreach (var r in retours.Where(r => Same(r.ClientNom, r.ClientTelephone, r.ClientEmail)))
        {
            if (!soldes.TryGetValue(map.Key(r.ProductId!), out var s)) continue;
            soldes[map.Key(r.ProductId!)] = s with { Rendus = s.Rendus + r.Quantite, Remboursee = s.Remboursee + (r.Montant ?? 0) };
        }

        // The product's display unit as set now in Stock ("caisse"), not the one copied onto old sale
        // lines - those predate it and often say "unité", which printed "emballage" everywhere.
        var ids = soldes.Values.Select(s => s.ProductId).Distinct().ToList();
        var unitesActuelles = await db.Products.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.UniteAffichage })
            .ToDictionaryAsync(p => p.Id, p => p.UniteAffichage, ct);

        return soldes
            .Select(kv =>
            {
                var (nomProduit, unite, pris, versee, rendusCount, remboursee, contenance, productId) = kv.Value;
                if (unitesActuelles.TryGetValue(productId, out var actuelle) && SaleCharges.RealUnit(actuelle) is not null)
                    unite = actuelle;
                // Can go below 0: emballages brought back in excess are a credit for next time.
                var dus = pris - rendusCount;
                var restante = Math.Max(0, versee - remboursee);
                return Solde(map, kv.Key, nomProduit, unite, dus, restante, contenance);
            })
            .Where(s => s.Dus != 0) // below 0 = a credit; callers list only what is owed
            .OrderBy(s => s.NomProduit, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>One balance line; for a shared type, named after it and listing its products.</summary>
    private static ConsigneSoldeDto Solde(EmballagesEndpoints.Emballages map, string key, string nomProduit, string? unite,
        decimal dus, decimal restante, int? contenance)
    {
        if (!map.IsType(key))
            return new ConsigneSoldeDto(key, nomProduit, unite, dus, restante, dus > 0 ? restante / dus : 0, contenance);
        var produits = map.ProductsOf(key);
        return new ConsigneSoldeDto(key, map.NameOf(key), map.UnitOf(key) ?? unite, dus, restante, dus > 0 ? restante / dus : 0,
            contenance ?? produits.Select(p => p.Contenance).FirstOrDefault(c => c is not null),
            produits.Select(p => p.Id).ToList(), string.Join(", ", produits.Select(p => p.Name)));
    }

    /// <summary>The return rows for <paramref name="lignes"/>, each with the consigne it refunds. A
    /// line may name a product or a shared type; any product of a type returns against the type.
    /// Refused when the client is unknown or an emballage is not owed by them.</summary>
    public static async Task<(List<EmballageRetour> Retours, string? Error)> BuildAsync(
        LonniiDbContext db, string groupId, string? nom, string? tel, string? email,
        IReadOnlyList<EmballageRetourLigne> lignes, string? venteId, string userId, CancellationToken ct,
        IReadOnlyDictionary<string, (string Nom, decimal Pris, int? Contenance, decimal Versee)>? enCours = null,
        bool creditAccepte = false)
    {
        if (string.IsNullOrWhiteSpace(nom) && string.IsNullOrWhiteSpace(tel) && string.IsNullOrWhiteSpace(email))
            return ([], "Indiquez le client qui rapporte les emballages");
        if (lignes.Any(l => l.Quantite < 0))
            return ([], "Le nombre d'emballages rapportés ne peut pas être négatif");
        if (lignes.Any(l => !SaleCharges.IsQuarterStep(l.Quantite)))
            return ([], "Le nombre d'emballages se compte par quart (1, 1,5, 1 + 1/4…)");

        var map = await EmballagesEndpoints.EmballagesAsync(db, groupId, ct);
        var soldes = (await SoldesAsync(db, groupId, nom, tel, email, map, ct)).ToDictionary(s => s.ProductId);
        // During a sale the client may hand back empties for what they take now (an exchange,
        // even on a first purchase): those count, but refund no consigne they never paid.
        // The consigne charged on this very sale is refundable too, for the empties handed back now.
        var pris = (enCours ?? new Dictionary<string, (string, decimal, int?, decimal)>())
            .GroupBy(kv => map.Key(kv.Key))
            .ToDictionary(g => g.Key, g => (Nom: g.First().Value.Nom, Pris: g.Sum(x => x.Value.Pris),
                Contenance: g.First().Value.Contenance, Versee: g.Sum(x => x.Value.Versee), ProductId: g.First().Key));
        foreach (var (key, taken) in pris)
        {
            var (dus, restante) = soldes.TryGetValue(key, out var s) ? (s.Dus + taken.Pris, s.ConsigneRestante + taken.Versee)
                : (taken.Pris, taken.Versee);
            soldes[key] = s is not null
                ? s with { Dus = dus, ConsigneRestante = restante, ParUnite = dus > 0 ? restante / dus : 0 }
                : Solde(map, key, taken.Nom, null, dus, restante, taken.Contenance);
        }
        var retours = new List<EmballageRetour>();
        foreach (var ligne in lignes.Where(l => l.Quantite > 0).GroupBy(l => map.Key(l.ProductId)))
        {
            var quantite = ligne.Sum(l => l.Quantite);
            if (!soldes.TryGetValue(ligne.Key, out var solde))
                return ([], "Ce produit n'a pas d'emballage chez ce client (ni dû, ni dans la vente)");
            // More than owed is kept as a credit only when the shop accepts emballages in advance
            // (Paramètres de stock); the next purchase uses it up. Only what was owed refunds a consigne.
            if (!creditAccepte && quantite > Math.Max(0, solde.Dus))
                return ([], $"{solde.NomProduit} : le client ne doit que {SaleCharges.CountLabel(Math.Max(0, solde.Dus))} emballage(s)");

            // A return row always names a real product (the Emballages tab finds its type from it):
            // the one typed, else one taken in this sale, else the type's first product.
            var productId = ligne.Select(l => l.ProductId).FirstOrDefault(id => !map.IsType(id))
                ?? (pris.TryGetValue(ligne.Key, out var p) ? p.ProductId : null)
                ?? map.ProductsOf(ligne.Key).FirstOrDefault()?.Id
                ?? ligne.Key;
            var montant = solde.Remboursement(quantite);
            retours.Add(new EmballageRetour
            {
                GroupId = groupId,
                ClientNom = nom?.Trim(),
                ClientTelephone = string.IsNullOrWhiteSpace(tel) ? null : tel.Trim(),
                ClientEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                ProductId = productId,
                NomProduit = solde.NomProduit,
                Quantite = quantite,
                Montant = montant > 0 ? montant : null,
                VenteId = venteId,
                CreatedBy = userId,
            });
        }
        return (retours, null);
    }
}
