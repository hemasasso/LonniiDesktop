namespace Lonnii.Shared.Security;

/// <summary>
/// Groups of gestion privilege names that Lonnii Business treats as interchangeable - granting
/// any one of them grants the capability. Ported byte-for-byte from the <c>privilegeMapping</c>
/// object in backend/routes/ventes.js (the server's own authorization check), plus the Stock
/// pair the source documents as an alias in its own migration comment
/// (<c>add_stock_analytics_privilege.sql</c>: "Also add can_view_stock_analytics as an alias
/// for backward compatibility").
///
/// Before this existed, the desktop resolved each catalogue name independently, so a group
/// that had only ever granted the legacy <c>sales</c>-module privilege (e.g. <c>can_apply_discounts</c>,
/// ticked from the "Ventes — ancien module" section) got nothing: the newer name the actual
/// checks look at (<c>can_apply_discount</c>) stayed false. That is not how the source app
/// behaves - its own middleware checks every alternate name and grants if any one is active -
/// so the desktop was silently stricter than the app it ports, not just cosmetically confusing.
///
/// <c>can_process_returns</c> ("Traiter Retours") has no entry here on purpose: it appears in
/// neither the source's <c>privilegeMapping</c> nor any route's privilege check - it really is
/// inert in Lonnii Business itself, not merely renamed.
/// </summary>
public static class PrivilegeAliases
{
    /// <summary>Canonical name -> every name (including itself) that grants the same capability.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Groups = Build();

    /// <summary>Every name that grants the same capability as <paramref name="name"/>,
    /// including <paramref name="name"/> itself. Never empty.</summary>
    public static IReadOnlyList<string> GroupOf(string name) =>
        Groups.TryGetValue(name, out var group) ? group : [name];

    /// <summary>The name a group's own list happens to be declared with first - the one the
    /// catalogue actually shows and the newer of the pair everywhere this matters. Used to
    /// pick which single checkbox represents an aliased pair, so a privilege dialog shows one
    /// row per capability instead of two that must always agree.</summary>
    public static string Canonical(string name) => GroupOf(name)[0];

    private static Dictionary<string, string[]> Build()
    {
        string[][] groups =
        [
            [Priv.Gestion.ViewVentes, Priv.Gestion.ViewSales],
            [Priv.Gestion.CreateVente, Priv.Gestion.CreateSales],
            [Priv.Gestion.EditVente, Priv.Gestion.EditSales],
            [Priv.Gestion.DeleteVente, Priv.Gestion.DeleteSales],
            [Priv.Gestion.ViewVentesAnalytics, Priv.Gestion.ViewSalesAnalytics],
            [Priv.Gestion.ApplyDiscount, Priv.Gestion.ApplyDiscounts],
            [Priv.Gestion.ManageClients, Priv.Gestion.ManageCustomers],
            [Priv.Gestion.ViewAnalytics, Priv.Gestion.ViewStockAnalytics],
        ];

        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var group in groups)
            foreach (var name in group)
                map[name] = group;

        return map;
    }
}
