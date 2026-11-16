using Lonnii.Api.Features.Auth;
using Lonnii.Api.Features.Ventes;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Stock;

/// <summary>
/// Stock > Emballages: how much returnable packaging the shop owns, per emballage. An emballage is
/// a product's own crate, or a shared <see cref="EmballageType"/> when several products' crates are
/// interchangeable (one count for all of them - a client may bring back any of its crates).
/// <list type="bullet">
/// <item>Pleins: the products' own stock (full crates/bottles).</item>
/// <item>Vides: empties in the shop = every empty a client brought back (emballages_retours)
/// + inventaire corrections - sent back to the supplier - broken/lost (emballages_mouvements).</item>
/// <item>Chez les clients: taken on sales by an identified client and not yet brought back,
/// netted per client (one client's advance never hides another's debt).</item>
/// </list>
/// Sales and client returns record the product; they are matched to the product's type when read,
/// so grouping products later regroups everything already recorded.
/// </summary>
public static class EmballagesEndpoints
{
    /// <summary>Caisse movement category of a consigne a supplier paid back for returned empties.</summary>
    public const string CaisseCategory = "consigne_fournisseur";

    public static void MapEmballagesEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/stock/emballages").WithTags("Stock");
        g.MapGet("", ListAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewEmballages);
        g.MapGet("/clients", ClientsAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewEmballages);
        g.MapGet("/fournisseurs", FournisseursAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewEmballages);
        g.MapGet("/flux", FluxAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewEmballages);
        g.MapGet("/mouvements", MouvementsAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewEmballages);
        g.MapPost("/mouvements", AddAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewEmballages).RequirePrivilege(Priv.Gestion.AdjustStock);
        // The groups list stays on ViewStock: the product form shows it.
        g.MapGet("/types", ListTypesAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.ViewStock);
        g.MapPost("/types", CreateTypeAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.EditProducts);
        g.MapPut("/types/{id}", UpdateTypeAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.EditProducts);
        g.MapDelete("/types/{id}", DeleteTypeAsync).RequireGroupScope().RequirePrivilege(Priv.Gestion.EditProducts);
    }

    private static async Task<IResult> ListAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct) =>
        Results.Ok(await ComputeAsync(db, scope.GroupId, ct));

    internal static async Task<List<EmballageStockDto>> ComputeAsync(LonniiDbContext db, string groupId, CancellationToken ct) =>
        (await LoadAsync(db, groupId, ct)).Stock;

    private sealed record ClientBalance(string Nom, string? Telephone, string? Email, decimal Dus, decimal Consigne, DateTime? DernierAchat);

    internal sealed record Produit(string Id, string Name, string? Unite, decimal? Pleins, decimal? ConsigneMontant, int? Contenance, string Key);

    /// <summary>The shop's returnable products and which emballage each counts under.</summary>
    internal sealed class Emballages
    {
        public required List<Produit> Produits { get; init; }

        /// <summary>Shared types that have at least one returnable product: id -> name.</summary>
        public required Dictionary<string, string> Types { get; init; }

        /// <summary>Product id or type id -> the emballage key (the type's id, or the product's own).</summary>
        public required Dictionary<string, string> KeyOf { get; init; }

        public List<string> ProductIds => Produits.Select(p => p.Id).ToList();

        /// <summary>Every id an emballage movement may carry: the products' and the types'.</summary>
        public List<string> MouvementIds => KeyOf.Keys.ToList();

        /// <summary>The ids recorded under <paramref name="key"/> (the type and its products).</summary>
        public List<string> IdsOf(string key) => KeyOf.Where(kv => kv.Value == key).Select(kv => kv.Key).ToList();

        public string NameOf(string key) =>
            Types.TryGetValue(key, out var nom) ? nom : Produits.FirstOrDefault(p => p.Id == key)?.Name ?? key;

        public string? UnitOf(string key) => SharedUnit(Produits.Where(p => p.Key == key).Select(p => p.Unite));

        /// <summary>The emballage a product (or type) counts under; an unknown id (a product no longer
        /// returnable) is its own.</summary>
        public string Key(string id) => KeyOf.GetValueOrDefault(id, id);

        public bool IsType(string key) => Types.ContainsKey(key);

        /// <summary>The products a shared type groups, by name.</summary>
        public List<Produit> ProductsOf(string key) =>
            Produits.Where(p => p.Key == key).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static async Task<Emballages> EmballagesAsync(LonniiDbContext db, string groupId, CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking()
            .Where(p => p.GroupId == groupId && p.DeletedAt == null && p.EmballageRecuperable)
            .Select(p => new { p.Id, p.Name, p.UniteAffichage, p.Quantity, p.FractionOuverte, p.StockIllimite, p.VenteMixte, p.FacteurConversion, p.ConsigneMontant, p.EmballageContenance, p.EmballageTypeId })
            .ToListAsync(ct);
        var allTypes = await db.EmballageTypes.AsNoTracking()
            .Where(t => t.GroupId == groupId)
            .ToDictionaryAsync(t => t.Id, t => t.Nom, ct);

        var produits = products
            .Select(p => new Produit(p.Id, p.Name, SaleCharges.RealUnit(p.UniteAffichage),
                p.StockIllimite ? null
                    : p.VenteMixte && (p.FacteurConversion ?? 1) > 1 ? Math.Floor(p.Quantity * 4m / p.FacteurConversion!.Value) / 4m
                    : p.Quantity + p.FractionOuverte / 4m,
                p.ConsigneMontant, p.EmballageContenance,
                p.EmballageTypeId is { } t && allTypes.ContainsKey(t) ? t : p.Id))
            .ToList();
        var types = produits.Where(p => allTypes.ContainsKey(p.Key)).Select(p => p.Key).Distinct()
            .ToDictionary(t => t, t => allTypes[t]);
        var keyOf = produits.ToDictionary(p => p.Id, p => p.Key);
        foreach (var t in types.Keys) keyOf[t] = t;
        return new Emballages { Produits = produits, Types = types, KeyOf = keyOf };
    }

    /// <summary>The one unit these products share, with its own spelling ("Caisse"); null when they differ.</summary>
    private static string? SharedUnit(IEnumerable<string?> units)
    {
        var list = units.ToList();
        return SaleCharges.SharedUnit(list) is { } shared
            ? list.First(u => string.Equals(u, shared, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static T? Same<T>(IEnumerable<T?> values) where T : struct
    {
        var distinct = values.Where(v => v.HasValue).Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    /// <summary>The emballage totals and, per client and emballage, what is still with them - one
    /// pass so the tab's "Chez les clients" always equals the sum of the clients list.</summary>
    private static async Task<(List<EmballageStockDto> Stock, Dictionary<(string Client, string Key), ClientBalance> Clients, Emballages Map)> LoadAsync(
        LonniiDbContext db, string groupId, CancellationToken ct)
    {
        var map = await EmballagesAsync(db, groupId, ct);
        var ids = map.ProductIds;
        var mouvementIds = map.MouvementIds;

        var pris = await db.Ventes.AsNoTracking()
            .Where(v => v.GroupId == groupId && v.StatutPaiement != StatutPaiement.Annule
                && (v.ClientNom != null || v.ClientTelephone != null || v.ClientEmail != null))
            .Join(db.VenteItems.Where(i => i.EmballagesPris != null && i.ProductId != null && ids.Contains(i.ProductId)),
                v => v.Id, i => i.VenteId,
                (v, i) => new { v.ClientNom, v.ClientTelephone, v.ClientEmail, v.DateVente, i.ProductId, Qte = i.EmballagesPris!.Value, Consigne = i.Consigne ?? 0 })
            .ToListAsync(ct);

        var retours = await db.EmballagesRetours.AsNoTracking()
            .Where(r => r.GroupId == groupId && r.ProductId != null && ids.Contains(r.ProductId))
            .Select(r => new { r.ClientNom, r.ClientTelephone, r.ClientEmail, r.ProductId, r.Quantite, Montant = r.Montant ?? 0 })
            .ToListAsync(ct);

        var mouvements = (await db.EmballagesMouvements.AsNoTracking()
                .Where(m => m.GroupId == groupId && mouvementIds.Contains(m.ProductId) && m.Type != EmballageMouvementTypes.RecuFournisseur)
                .GroupBy(m => m.ProductId)
                .Select(x => new { ProductId = x.Key, Qte = x.Sum(m => m.Quantite) })
                .ToListAsync(ct))
            .GroupBy(x => map.KeyOf[x.ProductId])
            .ToDictionary(x => x.Key, x => x.Sum(m => m.Qte));

        // Per client and emballage: taken - brought back, and consigne held - refunded. Sales are
        // walked oldest first, so the name/phone kept is the client's latest spelling.
        var parClient = new Dictionary<(string Client, string Key), ClientBalance>();
        foreach (var l in pris.OrderBy(x => x.DateVente))
        {
            var key = (ClientKey(l.ClientNom, l.ClientTelephone, l.ClientEmail), map.KeyOf[l.ProductId!]);
            var s = parClient.GetValueOrDefault(key);
            var nom = string.IsNullOrWhiteSpace(l.ClientNom) ? s?.Nom ?? l.ClientTelephone ?? l.ClientEmail ?? "Client" : l.ClientNom.Trim();
            parClient[key] = new ClientBalance(nom, l.ClientTelephone ?? s?.Telephone, l.ClientEmail ?? s?.Email,
                (s?.Dus ?? 0) + l.Qte, (s?.Consigne ?? 0) + l.Consigne, l.DateVente);
        }
        foreach (var r in retours)
        {
            var key = (ClientKey(r.ClientNom, r.ClientTelephone, r.ClientEmail), map.KeyOf[r.ProductId!]);
            var s = parClient.GetValueOrDefault(key)
                ?? new ClientBalance(r.ClientNom?.Trim() ?? r.ClientTelephone ?? r.ClientEmail ?? "Client", r.ClientTelephone, r.ClientEmail, 0, 0, null);
            parClient[key] = s with { Dus = s.Dus - r.Quantite, Consigne = s.Consigne - r.Montant };
        }
        var rapportes = retours.GroupBy(r => map.KeyOf[r.ProductId!]).ToDictionary(x => x.Key, x => x.Sum(r => r.Quantite));

        var stock = map.Produits
            .GroupBy(p => p.Key)
            .Select(g =>
            {
                var clients = parClient.Where(kv => kv.Key.Key == g.Key).Select(kv => kv.Value).ToList();
                var chez = clients.Sum(c => Math.Max(0, c.Dus));
                var detenue = clients.Sum(c => Math.Max(0, c.Consigne));
                var vides = rapportes.GetValueOrDefault(g.Key) + mouvements.GetValueOrDefault(g.Key);
                var produits = g.ToList();
                decimal? pleins = produits.All(p => p.Pleins is null) ? null : produits.Sum(p => p.Pleins ?? 0);
                var isType = map.Types.ContainsKey(g.Key);
                return new EmballageStockDto(g.Key, map.NameOf(g.Key), map.UnitOf(g.Key), pleins, vides, chez,
                    (pleins ?? 0) + vides + chez, Same(produits.Select(p => p.ConsigneMontant)), detenue,
                    Same(produits.Select(p => p.Contenance)),
                    isType ? g.Key : null,
                    isType
                        ? produits.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                            .Select(p => new EmballageProduitDto(p.Id, p.Name, p.Pleins, p.ConsigneMontant, p.Contenance)).ToList()
                        : null);
            })
            .OrderBy(e => e.NomProduit, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (stock, parClient, map);
    }

    /// <summary>Every client holding empties (or an advance of them), with what, per emballage.
    /// The ones owing the most come first.</summary>
    private static async Task<IResult> ClientsAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var (stock, parClient, _) = await LoadAsync(db, scope.GroupId, ct);
        var emballages = stock.ToDictionary(s => s.ProductId);
        var rows = parClient
            .Where(kv => kv.Value.Dus != 0 && emballages.ContainsKey(kv.Key.Key))
            .GroupBy(kv => kv.Key.Client)
            .Select(g =>
            {
                var latest = g.OrderByDescending(kv => kv.Value.DernierAchat ?? DateTime.MinValue).First().Value;
                var lignes = g.Select(kv =>
                    {
                        var e = emballages[kv.Key.Key];
                        return new EmballageClientLigne(e.ProductId, e.NomProduit, e.Unite, kv.Value.Dus,
                            Math.Max(0, kv.Value.Consigne), e.Contenance);
                    })
                    .OrderByDescending(l => l.Dus).ThenBy(l => l.NomProduit, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                return new EmballageClientDto(latest.Nom, latest.Telephone, latest.Email,
                    lignes.Sum(l => Math.Max(0, l.Dus)), lignes.Sum(l => l.ConsigneVersee),
                    g.Max(kv => kv.Value.DernierAchat), lignes);
            })
            .OrderByDescending(c => c.TotalDus).ThenBy(c => c.ClientNom, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Results.Ok(rows);
    }

    private static string ClientKey(string? nom, string? tel, string? email) =>
        ClientsEndpoints.PhoneKey(tel) is { } phone ? "t:" + phone
        : ClientsEndpoints.EmailKey(email) is { } mail ? "e:" + mail
        : "n:" + (string.IsNullOrWhiteSpace(nom) ? "" : ClientsEndpoints.NameKey(nom));

    /// <summary>The history of the empties. <paramref name="productId"/> is an emballage key (a
    /// product, or a shared type and all its products). Each row's ProductId is its emballage key;
    /// NomProduit stays what was recorded (the client brought back "Brafort").</summary>
    private static async Task<IResult> MouvementsAsync(
        string? productId, DateOnly? dateDebut, DateOnly? dateFin, int? tzOffsetMinutes,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        // The period is the shop's local days (same rule as the Ventes and stock movement filters).
        var (start, end) = VentesEndpoints.LocalRangeToUtc(dateDebut, dateFin, tzOffsetMinutes);
        var from = start ?? DateTime.MinValue;
        var to = end ?? DateTime.MaxValue;
        var map = await EmballagesAsync(db, scope.GroupId, ct);
        List<string>? ids = productId is null ? null
            : map.KeyOf.TryGetValue(productId, out var key) ? map.IdsOf(key)
            : [productId];

        var mouvements = await db.EmballagesMouvements.AsNoTracking()
            .Where(m => m.GroupId == scope.GroupId && (ids == null || ids.Contains(m.ProductId))
                && m.CreatedAt >= from && m.CreatedAt < to)
            .OrderByDescending(m => m.CreatedAt).Take(300)
            .Select(m => new { m.Id, m.ProductId, m.NomProduit, m.Type, m.Quantite, m.Montant, m.ModePaiement, m.SupplierName, m.Note, m.CreatedBy, m.CreatedAt })
            .ToListAsync(ct);
        // Empties clients brought back show in the same history, so the Vides count can be followed.
        var retours = await db.EmballagesRetours.AsNoTracking()
            .Where(r => r.GroupId == scope.GroupId && r.ProductId != null && (ids == null || ids.Contains(r.ProductId))
                && r.CreatedAt >= from && r.CreatedAt < to)
            .OrderByDescending(r => r.CreatedAt).Take(300)
            .Select(r => new { r.Id, r.ProductId, r.NomProduit, r.Quantite, r.Montant, r.ModePaiement, r.ClientNom, r.ClientTelephone, r.CreatedBy, r.CreatedAt })
            .ToListAsync(ct);

        var userIds = mouvements.Select(m => m.CreatedBy).Concat(retours.Select(r => r.CreatedBy))
            .Where(u => u != null).Distinct().ToList();
        var names = (await db.Users.AsNoTracking().Where(u => userIds.Contains(u.IdUser))
                .Select(u => new { u.IdUser, u.FirstName, u.LastName, u.Username, u.Email }).ToListAsync(ct))
            .ToDictionary(u => u.IdUser, u => $"{u.FirstName} {u.LastName}".Trim() is { Length: > 0 } full ? full : u.Username ?? u.Email);
        string KeyOf(string id) => map.KeyOf.GetValueOrDefault(id, id);

        var rows = mouvements.Select(m => new EmballageMouvementDto(m.Id, KeyOf(m.ProductId), m.NomProduit, m.Type, m.Quantite,
                m.Montant, m.ModePaiement, m.SupplierName, m.Note, m.CreatedBy is null ? null : names.GetValueOrDefault(m.CreatedBy), m.CreatedAt))
            .Concat(retours.Select(r => new EmballageMouvementDto(r.Id, KeyOf(r.ProductId!), r.NomProduit, "retour_client", r.Quantite,
                r.Montant is > 0 ? -r.Montant : null, r.ModePaiement, null, r.ClientNom ?? r.ClientTelephone,
                r.CreatedBy is null ? null : names.GetValueOrDefault(r.CreatedBy), r.CreatedAt,
                ClientNom: string.IsNullOrWhiteSpace(r.ClientNom) ? null : r.ClientNom.Trim(), ClientTelephone: r.ClientTelephone)))
            .OrderByDescending(m => m.CreatedAt).Take(300)
            .ToList();
        return Results.Ok(rows);
    }

    /// <summary>
    /// Per emballage, what moved in the period: taken by identified clients on sales (the same
    /// sales "Chez les clients" counts), brought back by clients, sent back to the supplier,
    /// broken or lost, inventaire corrections, and the consigne suppliers paid back.
    /// </summary>
    private static async Task<IResult> FluxAsync(
        DateOnly? dateDebut, DateOnly? dateFin, int? tzOffsetMinutes,
        GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var (start, end) = VentesEndpoints.LocalRangeToUtc(dateDebut, dateFin, tzOffsetMinutes);
        var from = start ?? DateTime.MinValue;
        var to = end ?? DateTime.MaxValue;
        var groupId = scope.GroupId;
        var map = await EmballagesAsync(db, groupId, ct);
        var ids = map.ProductIds;
        var mouvementIds = map.MouvementIds;

        var pris = (await db.Ventes.AsNoTracking()
                .Where(v => v.GroupId == groupId && v.StatutPaiement != StatutPaiement.Annule
                    && v.DateVente >= from && v.DateVente < to
                    && (v.ClientNom != null || v.ClientTelephone != null || v.ClientEmail != null))
                .Join(db.VenteItems.Where(i => i.EmballagesPris != null && i.ProductId != null && ids.Contains(i.ProductId)),
                    v => v.Id, i => i.VenteId, (v, i) => new { i.ProductId, Qte = i.EmballagesPris!.Value })
                .ToListAsync(ct))
            .GroupBy(x => map.KeyOf[x.ProductId!]).ToDictionary(g => g.Key, g => g.Sum(x => x.Qte));

        var rapportes = (await db.EmballagesRetours.AsNoTracking()
                .Where(r => r.GroupId == groupId && r.ProductId != null && ids.Contains(r.ProductId)
                    && r.CreatedAt >= from && r.CreatedAt < to)
                .Select(r => new { r.ProductId, r.Quantite })
                .ToListAsync(ct))
            .GroupBy(x => map.KeyOf[x.ProductId!]).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantite));

        var mouvements = (await db.EmballagesMouvements.AsNoTracking()
                .Where(m => m.GroupId == groupId && mouvementIds.Contains(m.ProductId) && m.CreatedAt >= from && m.CreatedAt < to)
                .Select(m => new { m.ProductId, m.Type, m.Quantite, Montant = m.Montant ?? 0 })
                .ToListAsync(ct))
            .Select(m => m with { ProductId = map.KeyOf[m.ProductId] })
            .ToList();

        var rows = map.Produits.Select(p => p.Key).Distinct()
            .Select(key =>
            {
                var mine = mouvements.Where(m => m.ProductId == key).ToList();
                return new EmballageFluxDto(key, map.NameOf(key), map.UnitOf(key),
                    pris.GetValueOrDefault(key),
                    rapportes.GetValueOrDefault(key),
                    -mine.Where(m => m.Type == EmballageMouvementTypes.RetourFournisseur).Sum(m => m.Quantite),
                    -mine.Where(m => m.Type == EmballageMouvementTypes.Casse).Sum(m => m.Quantite),
                    mine.Where(m => m.Type == EmballageMouvementTypes.Inventaire).Sum(m => m.Quantite),
                    mine.Where(m => m.Type == EmballageMouvementTypes.RetourFournisseur).Sum(m => m.Montant),
                    mine.Where(m => m.Type == EmballageMouvementTypes.RecuFournisseur).Sum(m => m.Quantite));
            })
            .OrderBy(r => r.NomProduit, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Results.Ok(rows);
    }

    /// <summary>Every supplier emballages were received from or sent back to: per emballage (type or
    /// product), full crates received, empties returned, and what is still to give back.</summary>
    private static async Task<IResult> FournisseursAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct) =>
        Results.Ok(await LoadFournisseursAsync(db, scope.GroupId, ct));

    internal static async Task<List<EmballageFournisseurDto>> LoadFournisseursAsync(LonniiDbContext db, string groupId, CancellationToken ct)
    {
        var map = await EmballagesAsync(db, groupId, ct);
        var rows = await db.EmballagesMouvements.AsNoTracking()
            .Where(m => m.GroupId == groupId && m.SupplierId != null
                && (m.Type == EmballageMouvementTypes.RecuFournisseur || m.Type == EmballageMouvementTypes.RetourFournisseur))
            .Select(m => new { m.SupplierId, m.SupplierName, m.ProductId, m.NomProduit, m.Type, m.Quantite, m.CreatedAt })
            .ToListAsync(ct);
        var names = await db.Suppliers.AsNoTracking().Where(s => s.GroupId == groupId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return rows.GroupBy(r => r.SupplierId!)
            .Select(g =>
            {
                var lignes = g.GroupBy(r => map.Key(r.ProductId))
                    .Select(x =>
                    {
                        var recus = x.Where(r => r.Type == EmballageMouvementTypes.RecuFournisseur).Sum(r => r.Quantite);
                        var rendus = -x.Where(r => r.Type == EmballageMouvementTypes.RetourFournisseur).Sum(r => r.Quantite);
                        var nom = map.Produits.Any(p => p.Key == x.Key) ? map.NameOf(x.Key) : x.First().NomProduit;
                        return new EmballageFournisseurLigne(x.Key, nom, map.UnitOf(x.Key), recus, rendus, recus - rendus);
                    })
                    .OrderBy(l => l.NomProduit, StringComparer.CurrentCultureIgnoreCase).ToList();
                return new EmballageFournisseurDto(g.Key, names.GetValueOrDefault(g.Key) ?? g.First().SupplierName ?? "Fournisseur",
                    lignes.Sum(l => l.Recus), lignes.Sum(l => l.Rendus), lignes.Sum(l => l.ARendre), g.Max(r => r.CreatedAt), lignes);
            })
            .OrderByDescending(f => f.ARendre).ThenBy(f => f.Nom, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>An inventaire, supplier return or casse for one emballage. Request.ProductId may be a
    /// product (recorded under its shared type when it has one) or a type.</summary>
    private static async Task<IResult> AddAsync(
        EmballageMouvementRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        using var turn = await ShopWriteLock.AcquireAsync(scope.GroupId, ct);

        var type = request.Type;
        if (type is not (EmballageMouvementTypes.Inventaire or EmballageMouvementTypes.RetourFournisseur or EmballageMouvementTypes.Casse
            or EmballageMouvementTypes.RecuFournisseur))
            return Results.BadRequest(new ApiError("Type de mouvement inconnu"));
        if (request.Quantite < 0)
            return Results.BadRequest(new ApiError("La quantité ne peut pas être négative"));
        if (!SaleCharges.IsQuarterStep(request.Quantite))
            return Results.BadRequest(new ApiError("Les emballages se comptent par quart (1, 1,5, 1 + 1/4…)"));
        if (type != EmballageMouvementTypes.Inventaire && request.Quantite == 0)
            return Results.BadRequest(new ApiError("Indiquez le nombre d'emballages"));
        if (request.Montant is < 0)
            return Results.BadRequest(new ApiError("Le montant ne peut pas être négatif"));
        if (type == EmballageMouvementTypes.RecuFournisseur && string.IsNullOrWhiteSpace(request.SupplierId))
            return Results.BadRequest(new ApiError("Choisissez le fournisseur qui a livré les emballages"));

        var (stock, _, map) = await LoadAsync(db, scope.GroupId, ct);
        if (string.IsNullOrWhiteSpace(request.ProductId) || !map.KeyOf.TryGetValue(request.ProductId, out var key))
        {
            var exists = !string.IsNullOrWhiteSpace(request.ProductId) && await db.Products.AsNoTracking()
                .AnyAsync(p => p.Id == request.ProductId && p.GroupId == scope.GroupId && p.DeletedAt == null, ct);
            return exists
                ? Results.BadRequest(new ApiError("Ce produit n'a pas d'emballage récupérable"))
                : Results.NotFound(new ApiError("Produit introuvable"));
        }
        var current = stock.First(e => e.ProductId == key);
        decimal delta;
        if (type == EmballageMouvementTypes.Inventaire)
        {
            delta = request.Quantite - current.Vides;
            if (delta == 0) return Results.Ok(current);
        }
        else if (type == EmballageMouvementTypes.RecuFournisseur)
        {
            delta = request.Quantite; // full crates in: no empties leave the shop
        }
        else
        {
            if (request.Quantite > current.Vides)
                return Results.BadRequest(new ApiError(
                    $"Il n'y a que {SaleCharges.CountLabel(Math.Max(0, current.Vides))} vide(s) en magasin. Faites d'abord un inventaire si ce chiffre est faux."));
            delta = -request.Quantite;
        }

        string? supplierName = null;
        if (!string.IsNullOrWhiteSpace(request.SupplierId))
        {
            supplierName = await db.Suppliers.AsNoTracking()
                .Where(s => s.Id == request.SupplierId && s.GroupId == scope.GroupId)
                .Select(s => s.Name).FirstOrDefaultAsync(ct);
            if (supplierName is null) return Results.BadRequest(new ApiError("Fournisseur introuvable"));
        }

        var montant = type == EmballageMouvementTypes.RetourFournisseur && request.Montant is > 0 ? request.Montant : null;
        var mode = montant is null ? null : string.IsNullOrWhiteSpace(request.ModePaiement) ? ModePaiement.Cash : request.ModePaiement;
        if (montant is not null)
        {
            if (mode is not (ModePaiement.Cash or ModePaiement.MobileMoney or ModePaiement.Carte))
                return Results.BadRequest(new ApiError("Mode de paiement invalide : espèces, mobile money ou carte."));
            var caisse = await db.Caisses.FirstOrDefaultAsync(
                c => c.GroupId == scope.GroupId && c.UserId == scope.UserId && c.Status == CaisseStatus.Open, ct);
            if (caisse is null)
                return Results.BadRequest(new ApiError("Ouvrez votre caisse pour encaisser la consigne du fournisseur (ou laissez le montant vide)."));
            db.CaisseTransactions.Add(new CaisseTransaction
            {
                CaisseId = caisse.Id,
                GroupId = scope.GroupId,
                Type = "entree",
                Montant = montant.Value,
                Description = $"Consigne fournisseur - {current.NomProduit}" + (supplierName is null ? "" : $" ({supplierName})"),
                Category = CaisseCategory,
                ModePaiement = mode,
                CreatedBy = scope.UserId,
            });
        }

        db.EmballagesMouvements.Add(new EmballageMouvement
        {
            GroupId = scope.GroupId,
            ProductId = key,
            NomProduit = current.NomProduit,
            Type = type,
            Quantite = delta,
            Montant = montant,
            ModePaiement = mode,
            SupplierId = supplierName is null ? null : request.SupplierId,
            SupplierName = supplierName,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(500, request.Note.Trim().Length)],
            CreatedBy = scope.UserId,
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok((await ComputeAsync(db, scope.GroupId, ct)).First(e => e.ProductId == key));
    }

    // ---- interchangeable types ---------------------------------------------------------------

    private static async Task<IResult> ListTypesAsync(GroupScope scope, LonniiDbContext db, CancellationToken ct) =>
        Results.Ok(await TypesAsync(db, scope.GroupId, ct));

    private static async Task<List<EmballageTypeDto>> TypesAsync(LonniiDbContext db, string groupId, CancellationToken ct)
    {
        var types = await db.EmballageTypes.AsNoTracking().Where(t => t.GroupId == groupId).ToListAsync(ct);
        var members = await db.Products.AsNoTracking()
            .Where(p => p.GroupId == groupId && p.DeletedAt == null && p.EmballageRecuperable && p.EmballageTypeId != null)
            .Select(p => new { p.Id, p.EmballageTypeId })
            .ToListAsync(ct);
        return types
            .Select(t => new EmballageTypeDto(t.Id, t.Nom, members.Where(m => m.EmballageTypeId == t.Id).Select(m => m.Id).ToList()))
            .OrderBy(t => t.Nom, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static Task<IResult> CreateTypeAsync(SaveEmballageTypeRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct) =>
        SaveTypeAsync(null, request, scope, db, ct);

    private static Task<IResult> UpdateTypeAsync(string id, SaveEmballageTypeRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct) =>
        SaveTypeAsync(id, request, scope, db, ct);

    /// <summary>Creates or renames a type and sets its products: the ones listed join it (leaving any
    /// other type), the ones no longer listed count on their own again.</summary>
    private static async Task<IResult> SaveTypeAsync(string? id, SaveEmballageTypeRequest request, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        using var turn = await ShopWriteLock.AcquireAsync(scope.GroupId, ct);
        var nom = request.Nom?.Trim() ?? "";
        if (nom.Length == 0) return Results.BadRequest(new ApiError("Donnez un nom au type d'emballage (ex. Caisse SODIBO)"));
        if (nom.Length > 255) nom = nom[..255];

        EmballageType? type = null;
        if (id is not null)
        {
            type = await db.EmballageTypes.FirstOrDefaultAsync(t => t.Id == id && t.GroupId == scope.GroupId, ct);
            if (type is null) return Results.NotFound(new ApiError("Type d'emballage introuvable"));
        }
        var lower = nom.ToLower();
        if (await db.EmballageTypes.AnyAsync(t => t.GroupId == scope.GroupId && t.Id != id && t.Nom.ToLower() == lower, ct))
            return Results.BadRequest(new ApiError($"Un type « {nom} » existe déjà"));

        var wanted = (request.ProductIds ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        var products = await db.Products
            .Where(p => p.GroupId == scope.GroupId && p.DeletedAt == null && wanted.Contains(p.Id))
            .ToListAsync(ct);
        if (products.Count != wanted.Count) return Results.BadRequest(new ApiError("Produit introuvable"));
        if (products.FirstOrDefault(p => !p.EmballageRecuperable) is { } notReturnable)
            return Results.BadRequest(new ApiError($"{notReturnable.Name} n'a pas d'emballage récupérable : cochez-le d'abord dans sa fiche produit."));
        if (ContenanceError(products) is { } contenanceError)
            return Results.BadRequest(new ApiError(contenanceError));

        if (type is null)
        {
            type = new EmballageType { GroupId = scope.GroupId, Nom = nom };
            db.EmballageTypes.Add(type);
        }
        else
        {
            type.Nom = nom;
            type.UpdatedAt = DateTime.UtcNow;
            var typeId = type.Id;
            foreach (var left in await db.Products.Where(p => p.GroupId == scope.GroupId && p.EmballageTypeId == typeId && !wanted.Contains(p.Id)).ToListAsync(ct))
                left.EmballageTypeId = null;
        }
        foreach (var p in products) p.EmballageTypeId = type.Id;
        await db.SaveChangesAsync(ct);
        return Results.Ok((await TypesAsync(db, scope.GroupId, ct)).First(t => t.Id == type.Id));
    }

    /// <summary>Only crates holding the same number are interchangeable (a SODIBO crate of 12 is not
    /// one of 16): refused when the products' contenance differ, or some say it and others don't.
    /// Products that never state one (the shop doesn't track it) may share a type.</summary>
    internal static string? ContenanceError(IReadOnlyCollection<Product> products)
    {
        var sizes = products.Select(p => p.EmballageContenance).Distinct().ToList();
        if (sizes.Count <= 1) return null;
        var detail = string.Join(", ", products
            .OrderBy(p => p.EmballageContenance ?? 0).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => $"{p.Name} ({(p.EmballageContenance is { } c ? $"de {c}" : "contenance non indiquée")})"));
        return "Seules des caisses de même contenance sont interchangeables (une caisse de 12 n'est pas une caisse de 16). "
            + $"Ici : {detail}. Corrigez la contenance dans la fiche produit, ou faites un type par contenance.";
    }

    /// <summary>Removes a type: its products count on their own again. Counts recorded on the type
    /// itself (inventaire, supplier returns) no longer count anywhere.</summary>
    private static async Task<IResult> DeleteTypeAsync(string id, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        using var turn = await ShopWriteLock.AcquireAsync(scope.GroupId, ct);
        var type = await db.EmballageTypes.FirstOrDefaultAsync(t => t.Id == id && t.GroupId == scope.GroupId, ct);
        if (type is null) return Results.NotFound(new ApiError("Type d'emballage introuvable"));
        foreach (var p in await db.Products.Where(p => p.GroupId == scope.GroupId && p.EmballageTypeId == id).ToListAsync(ct))
            p.EmballageTypeId = null;
        db.EmballageTypes.Remove(type);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
