using Lonnii.Shared.Contracts;
using Lonnii.Shared.Security;

namespace Lonnii.Client.Services;

/// <summary>
/// Which task section - Caisse, Ventes, Stock, or a module name - a Gestion privilege is shown
/// under. Display-only: the stored module is left alone, since the web app reads the same rows.
/// </summary>
public static class PrivilegeSections
{
    public const string Caisse = "caisse";
    public const string Ventes = "ventes";
    public const string Stock = "stock";

    /// <summary>
    /// Privileges the catalogue files under the legacy <c>sales</c> module but that the
    /// desktop checks under a different name, moved to the task they actually gate. Most
    /// other <c>sales</c> privileges (can_view_sales, can_create_sales...) need no entry
    /// here: they are aliases of a "ventes"-module privilege (see <see cref="PrivilegeAliases"/>),
    /// so <c>PrivilegeDialog</c> already sections them under their canonical name's section
    /// once it deduplicates the pair down to one row. <c>can_process_returns</c> is the only
    /// "sales" privilege with no such alias - genuinely inert, but still placed with its
    /// siblings below rather than left alone under a one-row "sales" section.
    /// </summary>
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.Ordinal)
    {
        [Priv.Gestion.AccessCaisse] = Caisse,
        [Priv.Gestion.OpenCaisse] = Caisse,
        [Priv.Gestion.CloseCaisse] = Caisse,
        [Priv.Gestion.ViewCaisseHistory] = Caisse,
        [Priv.Gestion.ResolveCaisseEcart] = Caisse,
        [Priv.Gestion.AddPayment] = Caisse,
        [Priv.Gestion.PrintReceipt] = Ventes,
        [Priv.Gestion.CancelVente] = Ventes,

        // The only "sales"-module privilege with no alias in PrivilegeAliases - genuinely
        // inert on the desktop (and in Lonnii Business itself; see that file's doc comment).
        // Sectioned with its siblings anyway rather than left in a one-row "sales" section.
        [Priv.Gestion.ProcessReturns] = Ventes,

        // Catalogued under "finance" (Lonnii Business's Dépenses module), but what it
        // actually gates on the desktop is Stock's own Fournisseurs manager - the rest of
        // "finance" (can_view_expenses and siblings) has no desktop feature at all and is
        // filtered out of the dialog entirely (see PrivilegeDialog.LoadAsync).
        [Priv.Gestion.ManageSuppliers] = Stock,
    };

    public static string Of(PrivilegeDto privilege) =>
        Overrides.TryGetValue(privilege.Name, out var section) ? section : privilege.Module;
}

/// <summary>A ready-made bundle of privileges for a typical job.</summary>
/// <param name="Grants">The privileges to grant; null means every one in <see cref="Additive"/>'s
/// scope (Caisse/Ventes/Stock only - no non-additive profile spans wider than that).</param>
/// <param name="Additive">False (the default): the profile is a job swap - every Caisse/Ventes/
/// Stock privilege not listed is revoked, so assigning one erases whichever came before it.
/// True: the profile only grants what it lists and never revokes anything, so it can be layered
/// onto whatever a member already holds - including another additive profile, or a manually
/// granted privilege - without a second admin having to re-tick boxes an earlier one set.</param>
public sealed record PrivilegeProfile(string Name, string Description, IReadOnlySet<string>? Grants, bool Additive = false)
{
    public override string ToString() => $"{Name} — {Description}";
}

/// <summary>
/// Job profiles offered next to the role. The four roles themselves (membre, modérateur,
/// sous-admin, admin) stay exactly the web app's - a new role value would be unreadable
/// there - so a profile is instead a shortcut that ticks the right privilege boxes.
///
/// A profile only ever touches the Caisse, Ventes and Stock sections: everything in them is
/// set to match it (granted if listed, revoked if not), everything elsewhere is left as it
/// was. Admin-only privileges are skipped, since the resolver ignores a grant of those anyway.
/// </summary>
public static class PrivilegeProfiles
{
    public static readonly PrivilegeProfile None = new("Aucun", "ne pas modifier les privilèges", new HashSet<string>());

    public static readonly IReadOnlyList<PrivilegeProfile> All =
    [
        None,
        new("Caissier", "ouvre/ferme la caisse, encaisse et vend", new HashSet<string>
        {
            Priv.Gestion.AccessCaisse, Priv.Gestion.OpenCaisse, Priv.Gestion.CloseCaisse,
            Priv.Gestion.ViewCaisseHistory, Priv.Gestion.AddPayment,
            Priv.Gestion.CreateVente, Priv.Gestion.ViewVentes, Priv.Gestion.ViewAllVentes,
            Priv.Gestion.ViewVenteDetails, Priv.Gestion.PrintReceipt, Priv.Gestion.SoldeAvoir,
            Priv.Gestion.ViewStock,
        }),
        // No can_add_payment: a préparateur builds the cart as an unpaid facture and the
        // cashier collects it (see the lonnii-preparer-cashier-flow memory).
        new("Vendeur / Préparateur", "crée des factures, n'encaisse pas", new HashSet<string>
        {
            Priv.Gestion.CreateVente, Priv.Gestion.ViewVentes, Priv.Gestion.ViewVenteDetails,
            Priv.Gestion.PrintReceipt, Priv.Gestion.ViewStock,
        }),
        new("Gestionnaire de stock", "produits, catégories, inventaire", new HashSet<string>
        {
            Priv.Gestion.ViewStock, Priv.Gestion.AddProducts, Priv.Gestion.EditProducts,
            Priv.Gestion.ManageCategories, Priv.Gestion.ManageSuppliers, Priv.Gestion.AdjustStock, Priv.Gestion.ViewStockHistory,
            Priv.Gestion.ExportStockData, Priv.Gestion.ViewAnalytics, Priv.Gestion.ViewStockAnalytics,
        }),
        new("Responsable", "tout Caisse, Ventes et Stock", Grants: null),

        // Additive, unlike the four above: these delegate a finance responsibility on top of
        // whatever commerce profile (or manual grants) a member already has, rather than
        // replacing it - see PrivilegeProfile.Additive. Admin-only privileges (delete an
        // écriture, manage the chart of accounts, edit résultat données) are never included,
        // same reason PrivilegeDialog locks their checkbox: the resolver ignores a grant of
        // those for anyone but the Admin Général.
        new("Comptable", "saisit les écritures : amortissements et bilan", new HashSet<string>
        {
            Priv.Gestion.ViewAmortissement, Priv.Gestion.AddImmobilisation, Priv.Gestion.EditImmobilisation,
            Priv.Gestion.CederImmobilisation, Priv.Gestion.ExportAmortissement,
            Priv.Gestion.ViewBilan, Priv.Gestion.AddBilanEcriture, Priv.Gestion.EditBilanEcriture,
            Priv.Gestion.ExportBilan, Priv.Gestion.ViewResultat,
        }, Additive: true),
        new("Financier", "consulte et exporte marges, bilan, amortissements et charges", new HashSet<string>
        {
            Priv.Gestion.ViewMarges, Priv.Gestion.ExportMarges,
            Priv.Gestion.ViewBilan, Priv.Gestion.ViewResultat, Priv.Gestion.ExportBilan,
            Priv.Gestion.ViewAmortissement, Priv.Gestion.ExportAmortissement,
            Priv.Gestion.ViewCharges, Priv.Gestion.ViewChargesAnalytics, Priv.Gestion.ExportCharges,
        }, Additive: true),
    ];

    private static readonly HashSet<string> Scope =
        [PrivilegeSections.Caisse, PrivilegeSections.Ventes, PrivilegeSections.Stock];

    /// <summary>
    /// Applies <paramref name="profile"/> to the member and returns how many privileges
    /// changed. A non-additive profile sets every Caisse/Ventes/Stock privilege to match it
    /// exactly (granted if listed, revoked otherwise) - a job swap. An additive one only grants
    /// what it lists and never revokes, so it can be layered on top of anything the member
    /// already has - see <see cref="PrivilegeProfile.Additive"/>.
    /// </summary>
    public static async Task<int> ApplyAsync(AppSession session, string userId, PrivilegeProfile profile)
    {
        if (ReferenceEquals(profile, None)) return 0;

        var current = await session.Api.GetMemberPrivilegesAsync(userId);
        var changed = 0;

        if (profile.Additive)
        {
            foreach (var name in profile.Grants ?? Enumerable.Empty<string>())
            {
                var privilege = current.Gestion.FirstOrDefault(p => p.Name == name);
                if (privilege is null || privilege.IsAdminOnly || privilege.IsGranted) continue;

                foreach (var alias in PrivilegeAliases.GroupOf(name))
                    await session.Api.SetGestionPrivilegeAsync(new SetPrivilegeRequest(userId, alias, true));
                changed++;
            }
            return changed;
        }

        // Canonical entries only: applying to every alias too (below) already covers its
        // legacy pair, and processing both separately would either write the same request
        // twice or, worse, let the alias's own stale IsGranted disagree with what was just
        // decided for its canonical name.
        foreach (var privilege in current.Gestion)
        {
            if (privilege.IsAdminOnly || PrivilegeAliases.Canonical(privilege.Name) != privilege.Name
                || !Scope.Contains(PrivilegeSections.Of(privilege))) continue;

            var wanted = profile.Grants?.Contains(privilege.Name) ?? true;
            if (wanted == privilege.IsGranted) continue;

            // Every alias, not just the canonical name - see PrivilegeDialog.SaveAsync for why
            // leaving a legacy alias ungoverned would let it silently override this decision.
            foreach (var name in PrivilegeAliases.GroupOf(privilege.Name))
                await session.Api.SetGestionPrivilegeAsync(new SetPrivilegeRequest(userId, name, wanted));
            changed++;
        }

        return changed;
    }
}
