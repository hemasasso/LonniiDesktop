using Lonnii.Api.Security;
using Lonnii.Data;
using Lonnii.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lonnii.Api.Endpoints;

/// <summary>
/// Paramètres → Consommation Données: how many records this workspace holds per section, and a
/// rough size for them. Ported from <c>GET /:sessionToken/data-consumption</c> in
/// backend/routes/gestion.js.
///
/// <para>
/// The sizes are the web app's estimates (a fixed byte count per row, 500 KB per product
/// image), kept identical so both apps quote the same figure for the same shop. They are not a
/// measurement of the database file, and the screen says so.
/// </para>
///
/// <para>
/// Sections the desktop has no table for (messages, prestations, formulaires, print jobs...)
/// are left out rather than shown at zero.
/// </para>
/// </summary>
public static class ConsommationEndpoints
{
    private const long ProductImageBytes = 500_000;

    public static void MapConsommationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/parametres/consommation", GetAsync)
            .WithTags("Paramètres")
            .RequireGroupScope().RequireGroupAdmin();
    }

    /// <summary><paramref name="year"/> narrows the dated sections (ventes, charges, caisses)
    /// to one year, as the web app's year picker does; omitted, every record counts.</summary>
    private static async Task<IResult> GetAsync(
        int? year, GroupScope scope, LonniiDbContext db, CancellationToken ct)
    {
        var g = scope.GroupId;
        var rows = new List<DataConsumptionRowDto>();

        void Add(string key, string label, string section, int count, int bytesPerRow, long extraBytes = 0) =>
            rows.Add(new DataConsumptionRowDto(key, label, section, count, count * (long)bytesPerRow + extraBytes));

        // --- Stock ---
        var products = await db.Products.Where(p => p.GroupId == g)
            .GroupBy(_ => 1)
            .Select(x => new { Total = x.Count(), Images = x.Count(p => p.ImageUrl != null && p.ImageUrl != "") })
            .FirstOrDefaultAsync(ct);
        var productImages = products?.Images ?? 0;
        Add("products", "Produits", "Stock", products?.Total ?? 0, 1024, productImages * ProductImageBytes);
        Add("categories", "Catégories", "Stock", await db.Categories.CountAsync(x => x.GroupId == g, ct), 256);
        Add("suppliers", "Fournisseurs", "Stock", await db.Suppliers.CountAsync(x => x.GroupId == g, ct), 512);
        Add("stock_history", "Historique stock", "Stock", await db.StockHistories.CountAsync(x => x.GroupId == g, ct), 256);
        Add("stock_snapshots", "Snapshots stock", "Stock", await db.StockSnapshots.CountAsync(x => x.GroupId == g, ct), 512);

        // --- Ventes ---
        var ventes = db.Ventes.Where(v => v.GroupId == g);
        var datedVentes = year is { } y1 ? ventes.Where(v => v.DateVente.Year == y1) : ventes;
        Add("ventes", "Ventes", "Ventes", await datedVentes.CountAsync(ct), 2048);
        Add("ventes_items", "Articles vendus", "Ventes",
            await db.VenteItems.CountAsync(i => ventes.Any(v => v.Id == i.VenteId), ct), 512);
        Add("paiements", "Paiements", "Ventes",
            await db.PaiementsVentes.CountAsync(p => ventes.Any(v => v.Id == p.VenteId), ct), 256);
        Add("clients", "Clients", "Ventes", await db.Clients.CountAsync(x => x.GroupId == g, ct), 512);

        var caisses = db.Caisses.Where(c => c.GroupId == g);
        if (year is { } y2) caisses = caisses.Where(c => c.DateOuverture.Year == y2);
        Add("caisses", "Sessions caisse", "Ventes", await caisses.CountAsync(ct), 1024);
        Add("caisse_transactions", "Transactions caisse", "Ventes",
            await db.CaisseTransactions.CountAsync(x => x.GroupId == g, ct), 256);
        Add("ventes_parametres", "Paramètres reçu", "Ventes",
            await db.VentesParametres.CountAsync(x => x.GroupeId == g, ct), 256);

        // --- Charges ---
        var charges = db.Charges.Where(c => c.GroupId == g);
        if (year is { } y3) charges = charges.Where(c => c.Date.Year == y3);
        Add("charges", "Charges", "Charges", await charges.CountAsync(ct), 512);
        Add("charges_categories", "Catégories charges", "Charges",
            await db.ChargeCategories.CountAsync(x => x.GroupId == g, ct), 256);

        // --- Comptabilité ---
        Add("immobilisations", "Immobilisations", "Comptabilité",
            await db.Immobilisations.CountAsync(x => x.GroupId == g, ct), 1024);
        Add("amortissement_echeances", "Échéances d'amortissement", "Comptabilité",
            await db.AmortissementEcheances.CountAsync(x => x.GroupId == g, ct), 256);
        Add("bilan_ecritures", "Écritures comptables", "Comptabilité",
            await db.BilanEcritures.CountAsync(x => x.GroupId == g, ct), 512);
        Add("bilan_comptes", "Plan comptable", "Comptabilité",
            await db.BilanComptes.CountAsync(x => x.GroupId == g, ct), 256);

        // --- Équipe ---
        Add("programme", "Programme (journées)", "Équipe",
            await db.ProgrammeEntries.CountAsync(x => x.GroupId == g, ct), 256);
        Add("programme_annonces", "Annonces", "Équipe",
            await db.ProgrammeAnnouncements.CountAsync(x => x.GroupId == g, ct), 512);
        Add("work_log", "Journal de présence", "Équipe",
            await db.MemberWorkLogs.CountAsync(x => x.GroupId == g, ct), 256);

        var members = await db.GroupMembers.CountAsync(m => m.IdGroupe == g, ct);

        return Results.Ok(new DataConsumptionResponse(
            Year: year,
            TotalRecords: rows.Sum(r => r.Count),
            TotalBytes: rows.Sum(r => r.Bytes),
            Sections: rows.Count,
            SectionsWithData: rows.Count(r => r.Count > 0),
            TotalFiles: productImages,
            TotalMembers: members,
            Rows: rows));
    }
}
