using System.Text.Json;
using System.Text.Json.Nodes;
using Lonnii.Data;
using Lonnii.Data.Entities;
using Lonnii.Shared.Comptabilite;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Features.Parametres;

/// <summary>
/// Copies one espace's business data from one database to another, table by table, in
/// parent-before-child order. The same class runs both directions: on export the target is a
/// fresh <see cref="EspaceArchive"/>, on import the source is one.
///
/// <para>
/// Rows travel as entities rather than as raw SQL on purpose. Each context applies its own
/// provider's value conversions, so a copy between SQLite and PostgreSQL carries money
/// correctly in both directions - a raw column copy would move minor units into a
/// DECIMAL(15,2) column and multiply every amount by a hundred (see
/// <see cref="LonniiDbContext"/>).
/// </para>
/// <para>
/// Every primary key is renumbered on the way in (<paramref name="renewIds"/>), and every
/// column pointing at one is rewritten to match. This is not optional: the usual import is
/// into a <em>new espace on the same host</em>, so the rows being inserted are very often
/// the ones already sitting in that database under the espace they came from - a copy that
/// kept its keys would collide on the first product. The integer keys have to be renumbered
/// in both directions anyway, since the database assigns them.
/// </para>
/// <para>
/// What does <em>not</em> travel: users, memberships, privileges, devices, sessions,
/// subscriptions, the work programme and the per-user activity logs. An espace's members are
/// accounts on a particular server, and the receiving espace has its own; copying the rows
/// that name them would fill a new workspace with schedules and audit trails for people who
/// are not in it. Sales, caisse sessions and charges keep the user id that created them, so
/// the history is intact even though the name behind it may no longer resolve.
/// </para>
/// </summary>
internal sealed class EspaceCopier(
    LonniiDbContext source,
    LonniiDbContext target,
    string sourceGroupId,
    string targetGroupId,
    IEspaceImageSink images,
    bool renewIds)
{
    private readonly List<EspaceTransferSectionDto> _sections = [];

    // Old id -> new id, for every table something else points at.
    private readonly Dictionary<string, string> _categoryIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _supplierIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _productIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _venteIds = new(StringComparer.Ordinal);
    private Dictionary<int, int> _caisseIds = [];

    /// <summary>How many rows were copied per table, in the order they were copied.</summary>
    public IReadOnlyList<EspaceTransferSectionDto> Sections => _sections;

    public int RecordCount => _sections.Sum(s => s.Count);

    public async Task RunAsync(CancellationToken ct)
    {
        // Thousands of rows are added per stage and nothing is read back through the tracker,
        // so change detection is pure cost here.
        target.ChangeTracker.AutoDetectChangesEnabled = false;

        await CopyStockAsync(ct);
        await CopyVentesAsync(ct);

        // After the sales: a stock movement caused by one points back at it.
        await CopyStockHistoryAsync(ct);

        await CopyChargesAsync(ct);
        await CopyComptabiliteAsync(ct);
    }

    // --- Stock -------------------------------------------------------------------

    private async Task CopyStockAsync(CancellationToken ct)
    {
        var categories = await LoadAsync(source.Categories.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in categories)
        {
            row.GroupId = targetGroupId;
            row.Parent = null;
            row.Id = Renew(row.Id, _categoryIds);
        }
        // Sub-categories: resolved in a second pass, once every category has its new id.
        foreach (var row in categories)
        {
            row.ParentId = Lookup(_categoryIds, row.ParentId);
            row.ImageUrl = await images.TransferAsync(row.ImageUrl, row.Id, lossless: false, ct);
        }
        await InsertAsync("Catégories", categories, ct);

        var suppliers = await LoadAsync(source.Suppliers.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in suppliers)
        {
            row.GroupId = targetGroupId;
            row.Id = Renew(row.Id, _supplierIds);
        }
        await InsertAsync("Fournisseurs", suppliers, ct);

        var products = await LoadAsync(source.Products.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in products)
        {
            row.GroupId = targetGroupId;
            row.Category = null;
            row.Supplier = null;
            row.CategoryId = Lookup(_categoryIds, row.CategoryId);
            row.SupplierId = Lookup(_supplierIds, row.SupplierId);
            row.Id = Renew(row.Id, _productIds);

            // After the new id: the stored filename is built from it, and that is what
            // ImageEndpoints reads back to check the photo belongs to this espace.
            row.ImageUrl = await images.TransferAsync(row.ImageUrl, row.Id, lossless: false, ct);
        }
        await InsertAsync("Produits", products, ct);

        var settings = await LoadAsync(source.StockSettings.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in settings)
        {
            row.GroupId = targetGroupId;
            row.Id = Renew(row.Id);
        }
        await InsertAsync("Paramètres stock", settings, ct);

        var snapshots = await LoadAsync(source.StockSnapshots.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in snapshots) row.GroupId = targetGroupId;
        await InsertAsync("Snapshots stock", snapshots, ct);
    }

    private async Task CopyStockHistoryAsync(CancellationToken ct)
    {
        var history = await LoadAsync(source.StockHistories.Where(x => x.GroupId == sourceGroupId), ct);
        var copied = new List<StockHistory>(history.Count);

        foreach (var row in history)
        {
            // The product is a real foreign key: a movement whose product did not come
            // across cannot be inserted at all.
            if (Lookup(_productIds, row.ProductId) is not { } productId) continue;

            row.GroupId = targetGroupId;
            row.Product = null;
            row.ProductId = productId;
            row.Id = Renew(row.Id);

            // reference_id is a loose link, by reference_type, to whatever caused the
            // movement. Only sales are copied, so only those can be re-pointed.
            row.ReferenceId = row.ReferenceType == "sale" ? Lookup(_venteIds, row.ReferenceId) : row.ReferenceId;

            copied.Add(row);
        }

        await InsertAsync("Historique stock", copied, ct);
    }

    // --- Ventes ------------------------------------------------------------------

    private async Task CopyVentesAsync(CancellationToken ct)
    {
        var clients = await LoadAsync(source.Clients.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in clients)
        {
            row.GroupId = targetGroupId;
            row.Id = Renew(row.Id);
        }
        await InsertAsync("Clients", clients, ct);

        // Caisse ids are database-generated integers, so the copy gets new ones and
        // everything pointing at a session has to be renumbered with it.
        var caisses = await LoadAsync(source.Caisses.Where(x => x.GroupId == sourceGroupId), ct);
        var caisseIds = caisses.Select(c => c.Id).ToList();
        foreach (var row in caisses)
        {
            row.GroupId = targetGroupId;
            row.Id = 0;
        }
        await InsertAsync("Sessions caisse", caisses, ct);
        _caisseIds = MapIds(caisseIds, caisses.Select(c => c.Id));

        var ventes = await LoadAsync(source.Ventes.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in ventes)
        {
            row.GroupId = targetGroupId;
            row.CaisseId = row.CaisseId is { } old && _caisseIds.TryGetValue(old, out var renumbered)
                ? renumbered
                : null;
            row.Id = Renew(row.Id, _venteIds);
        }
        await InsertAsync("Ventes", ventes, ct);

        // Neither of the next two tables carries a group id: they are reached through the
        // sales themselves.
        var items = await LoadAsync(
            source.VenteItems.Where(i => source.Ventes.Any(v => v.Id == i.VenteId && v.GroupId == sourceGroupId)), ct);
        var copiedItems = new List<VenteItem>(items.Count);
        foreach (var row in items)
        {
            if (Lookup(_venteIds, row.VenteId) is not { } venteId) continue;

            row.Vente = null;
            row.VenteId = venteId;

            // Null when the product was deleted before the export; NomProduit carries the
            // name, which is what the receipt prints either way.
            row.ProductId = Lookup(_productIds, row.ProductId);
            row.Id = Renew(row.Id);

            copiedItems.Add(row);
        }
        await InsertAsync("Articles vendus", copiedItems, ct);

        var paiements = await LoadAsync(
            source.PaiementsVentes.Where(p => source.Ventes.Any(v => v.Id == p.VenteId && v.GroupId == sourceGroupId)), ct);
        var copiedPaiements = new List<PaiementVente>(paiements.Count);
        foreach (var row in paiements)
        {
            if (Lookup(_venteIds, row.VenteId) is not { } venteId) continue;

            row.Vente = null;
            row.VenteId = venteId;
            row.Id = Renew(row.Id);

            copiedPaiements.Add(row);
        }
        await InsertAsync("Paiements", copiedPaiements, ct);

        var transactions = await LoadAsync(source.CaisseTransactions.Where(x => x.GroupId == sourceGroupId), ct);
        var copiedTransactions = new List<CaisseTransaction>(transactions.Count);
        foreach (var row in transactions)
        {
            // A movement whose session did not come across would break the foreign key, so
            // it is dropped rather than quietly reattached to another session.
            if (!_caisseIds.TryGetValue(row.CaisseId, out var caisseId)) continue;

            row.GroupId = targetGroupId;
            row.Caisse = null;
            row.CaisseId = caisseId;
            row.Id = 0;

            copiedTransactions.Add(row);
        }
        await InsertAsync("Transactions caisse", copiedTransactions, ct);

        var groupePayments = await LoadAsync(source.GroupePayments.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in groupePayments)
        {
            row.GroupId = targetGroupId;
            row.FactureIds = RenumberFactureIds(row.FactureIds);
            row.FacturesData = RenumberFacturesData(row.FacturesData);
            row.Id = Renew(row.Id);
        }
        await InsertAsync("Paiements groupés", groupePayments, ct);

        // One row per espace: the receipt and facture configuration, with its logo and QR
        // code. Stored under the receiving group's id, which is how ImageEndpoints checks
        // that only this workspace can fetch them.
        var parametres = await LoadAsync(source.VentesParametres.Where(x => x.GroupeId == sourceGroupId), ct);
        foreach (var row in parametres)
        {
            row.GroupeId = targetGroupId;
            row.LogoPath = await images.TransferAsync(row.LogoPath, targetGroupId, lossless: false, ct);
            row.QrCodePath = await images.TransferAsync(row.QrCodePath, targetGroupId, lossless: true, ct);
        }
        await InsertAsync("Paramètres reçu", parametres, ct);
    }

    /// <summary>
    /// The sales a grouped payment settled, as the live <c>uuid[]</c> column holds them.
    /// A facture that did not come across is dropped from the list rather than left pointing
    /// at the espace the file came from.
    /// </summary>
    private Guid[] RenumberFactureIds(Guid[] factureIds) =>
        factureIds
            .Select(id => Lookup(_venteIds, id.ToString()))
            .Where(id => id is not null)
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();

    /// <summary>
    /// The same ids again, inside the JSON array of <c>{facture_id, numero_vente, montant}</c>
    /// the grouped receipt is reprinted from. Left untouched if it is not the expected shape:
    /// the receipt then still prints, with its original references, which is better than
    /// losing the row.
    /// </summary>
    private string RenumberFacturesData(string facturesData)
    {
        if (string.IsNullOrWhiteSpace(facturesData)) return facturesData;

        try
        {
            if (JsonNode.Parse(facturesData) is not JsonArray lines) return facturesData;

            foreach (var line in lines)
            {
                if (line is not JsonObject row) continue;
                if (row["facture_id"]?.GetValue<string>() is not { } factureId) continue;
                if (Lookup(_venteIds, factureId) is not { } renumbered) continue;

                row["facture_id"] = renumbered;
            }

            return lines.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return facturesData;
        }
    }

    // --- Charges -----------------------------------------------------------------

    private async Task CopyChargesAsync(CancellationToken ct)
    {
        // charges.categorie matches a category by name, not by id - as in the source schema -
        // so nothing here needs renumbering.
        var categories = await LoadAsync(source.ChargeCategories.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in categories)
        {
            row.GroupId = targetGroupId;
            row.Id = 0;
        }
        await InsertAsync("Catégories de charges", categories, ct);

        var charges = await LoadAsync(source.Charges.Where(x => x.GroupId == sourceGroupId), ct);
        var chargeIds = charges.Select(c => c.Id).ToList();

        // A recurring charge points at the source charge it was created from, in its own
        // table. The link is restored in a second pass, once the new ids are known.
        var recurringSources = charges.Select(c => c.RecurringSourceId).ToList();

        foreach (var row in charges)
        {
            row.GroupId = targetGroupId;
            row.Id = 0;
            row.RecurringSource = null;
            row.RecurringSourceId = null;
        }
        await InsertAsync("Charges", charges, ct);

        var chargeMap = MapIds(chargeIds, charges.Select(c => c.Id));

        var relinked = charges
            .Select((charge, index) => (charge, source: recurringSources[index]))
            .Where(x => x.source is { } old && chargeMap.ContainsKey(old))
            .ToList();

        if (relinked.Count == 0) return;

        foreach (var (charge, recurringSource) in relinked)
        {
            charge.RecurringSourceId = chargeMap[recurringSource!.Value];
            target.Charges.Attach(charge).Property(c => c.RecurringSourceId).IsModified = true;
        }

        await target.SaveChangesAsync(ct);
        target.ChangeTracker.Clear();
    }

    // --- Comptabilité ------------------------------------------------------------

    private async Task CopyComptabiliteAsync(CancellationToken ct)
    {
        var immobilisations = await LoadAsync(source.Immobilisations.Where(x => x.GroupId == sourceGroupId), ct);
        var immobilisationIds = immobilisations.Select(i => i.Id).ToList();
        foreach (var row in immobilisations)
        {
            row.GroupId = targetGroupId;
            row.Id = 0;
            row.Echeances.Clear();
        }
        await InsertAsync("Immobilisations", immobilisations, ct);
        var immobilisationMap = MapIds(immobilisationIds, immobilisations.Select(i => i.Id));

        var echeances = await LoadAsync(source.AmortissementEcheances.Where(x => x.GroupId == sourceGroupId), ct);
        var copiedEcheances = new List<AmortissementEcheance>(echeances.Count);
        foreach (var row in echeances)
        {
            if (!immobilisationMap.TryGetValue(row.ImmobilisationId, out var immobilisationId)) continue;

            row.GroupId = targetGroupId;
            row.Id = 0;
            row.Immobilisation = null;
            row.ImmobilisationId = immobilisationId;

            copiedEcheances.Add(row);
        }
        await InsertAsync("Échéances d'amortissement", copiedEcheances, ct);

        var bilanComptes = await LoadAsync(source.BilanComptes.Where(x => x.GroupId == sourceGroupId), ct);
        var bilanIds = bilanComptes.Select(c => c.Id).ToList();
        foreach (var row in bilanComptes)
        {
            row.GroupId = targetGroupId;
            row.Id = 0;
        }
        await InsertAsync("Plan comptable (bilan)", bilanComptes, ct);
        var bilanMap = MapIds(bilanIds, bilanComptes.Select(c => c.Id));

        var resultatComptes = await LoadAsync(source.ResultatComptes.Where(x => x.GroupId == sourceGroupId), ct);
        var resultatIds = resultatComptes.Select(c => c.Id).ToList();
        foreach (var row in resultatComptes)
        {
            row.GroupId = targetGroupId;
            row.Id = 0;
        }
        await InsertAsync("Plan comptable (résultat)", resultatComptes, ct);
        var resultatMap = MapIds(resultatIds, resultatComptes.Select(c => c.Id));

        // compte_id names a row of bilan_comptes or of resultat_comptes depending on
        // table_type - two tables, so which map applies depends on the row itself.
        var ecritures = await LoadAsync(source.BilanEcritures.Where(x => x.GroupId == sourceGroupId), ct);
        var copiedEcritures = new List<BilanEcriture>(ecritures.Count);
        foreach (var row in ecritures)
        {
            var map = row.TableType == TablesCompte.Resultat ? resultatMap : bilanMap;
            if (!map.TryGetValue(row.CompteId, out var compteId)) continue;

            row.GroupId = targetGroupId;
            row.Id = 0;
            row.CompteId = compteId;
            copiedEcritures.Add(row);
        }
        await InsertAsync("Écritures comptables", copiedEcritures, ct);

        var parametres = await LoadAsync(source.ComptabiliteParametres.Where(x => x.GroupId == sourceGroupId), ct);
        foreach (var row in parametres) row.GroupId = targetGroupId;
        await InsertAsync("Paramètres comptabilité", parametres, ct);
    }

    // --- Plumbing ----------------------------------------------------------------

    private static Task<List<T>> LoadAsync<T>(IQueryable<T> query, CancellationToken ct) where T : class =>
        query.AsNoTracking().ToListAsync(ct);

    /// <summary>
    /// Inserts one table's rows and records the count for the summary. The tracker is cleared
    /// afterwards so the next table starts from nothing; the rows keep whatever ids the
    /// database assigned, which is what <see cref="MapIds"/> then reads.
    /// </summary>
    private async Task InsertAsync<T>(string label, List<T> rows, CancellationToken ct) where T : class
    {
        _sections.Add(new EspaceTransferSectionDto(label, rows.Count));

        if (rows.Count == 0) return;

        target.Set<T>().AddRange(rows);
        await target.SaveChangesAsync(ct);
        target.ChangeTracker.Clear();
    }

    /// <summary>
    /// The id a copied row should carry, noted in <paramref name="map"/> against the one it
    /// had. A plain pass-through when <c>renewIds</c> is false, which keeps an exported
    /// archive's keys identical to the espace it was taken from.
    /// </summary>
    private string Renew(string oldId, Dictionary<string, string>? map = null)
    {
        var newId = renewIds ? Guid.NewGuid().ToString() : oldId;
        if (map is not null) map[oldId] = newId;
        return newId;
    }

    /// <summary>The new id for an old one: null for a null input, and null for a row that did
    /// not come across - which every caller treats as "no longer linked".</summary>
    private static string? Lookup(Dictionary<string, string> map, string? oldId) =>
        oldId is not null && map.TryGetValue(oldId, out var newId) ? newId : null;

    /// <summary>Pairs the ids a table had before the copy with the ones it was given, by
    /// position - <c>AddRange</c> keeps the list's order.</summary>
    private static Dictionary<int, int> MapIds(List<int> before, IEnumerable<int> after)
    {
        var map = new Dictionary<int, int>(before.Count);
        var index = 0;

        foreach (var id in after)
        {
            if (index >= before.Count) break;
            map[before[index++]] = id;
        }

        return map;
    }
}
