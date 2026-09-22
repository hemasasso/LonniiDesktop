using Lonnii.Shared.Security;

namespace Lonnii.Shared.Navigation;

/// <summary>
/// One entry in the application menu. <see cref="RequiredPrivilege"/> mirrors the
/// requiredPrivilege field the React app uses to filter its navigation cards.
/// </summary>
/// <param name="Key">Stable identifier, matching the route segment used by the web app.</param>
/// <param name="Label">Menu text, as shown in Lonnii Business.</param>
/// <param name="Description">Subtitle shown on the web app's navigation cards.</param>
/// <param name="Accent">Accent colour the web app assigns to this entry.</param>
/// <param name="RequiredPrivilege">Privilege that must be granted, or null when always visible.</param>
/// <param name="RequiresGestionAccess">True when the entry is hidden unless the group has gestion_access.</param>
/// <param name="RequiresPrestationsEnabled">True when the entry depends on the group's prestations toggle.</param>
/// <param name="RequiresAdmin">True when only an admin or sub_admin may see the entry.</param>
public sealed record MenuEntry(
    string Key,
    string Label,
    string Description,
    string Accent,
    string? RequiredPrivilege = null,
    bool RequiresGestionAccess = false,
    bool RequiresPrestationsEnabled = false,
    bool RequiresAdmin = false);

/// <summary>A titled group of menu entries, matching the admin sections of the web Gestion page.</summary>
public sealed record MenuSection(string Id, string Label, string Description, string Accent, IReadOnlyList<string> Keys);

/// <summary>
/// The Lonnii Business menu structure, ported from
/// client/src/components/baro/GroupePage.jsx and .../gestion/Gestion.jsx.
/// Labels, order, colours and privilege gates are kept identical.
/// </summary>
public static class AppMenu
{
    /// <summary>
    /// Top-level group navigation. Source: GroupePage.jsx.
    /// Réunion and Suggestion are commented out in the web app and are listed here
    /// as disabled so the desktop shell keeps the same order if they are re-enabled.
    /// </summary>
    public static readonly IReadOnlyList<MenuEntry> Espace =
    [
        new("program", "Programme", "Gérez le calendrier et les événements", "#3b82f6", Priv.Option.ViewProgramme),
        new("chat", "Chat", "Discutez avec les membres du groupe", "#10b981", Priv.Option.ViewChat),
        // Admin-only, unlike the web app. Options is where members and privileges are
        // managed; an ordinary member could only look at it, so showing it to them just
        // advertises a door they cannot open.
        new("options", "Options", "Gérez les membres et les privilèges", "#8b5cf6", RequiresAdmin: true),
        new("espace/gestion", "Gestion", "Accédez aux outils de gestion", "#f59e0b", RequiresGestionAccess: true),
        new("formulaire", "Formulaire", "Créez et remplissez des formulaires", "#ec4899", Priv.Option.ViewFormulaires),
        new("prestations", "Prestations", "Devis, factures et rentabilité", "#e11d48",
            Priv.Gestion.ViewPrestations, RequiresPrestationsEnabled: true),
    ];

    /// <summary>Gestion sub-navigation. Source: the buttons array in Gestion.jsx.</summary>
    public static readonly IReadOnlyList<MenuEntry> Gestion =
    [
        new("gestion-de-stock", "Gestion de Stock", "Gérez votre inventaire et stock", "#4361ee", Priv.Gestion.ViewStock),
        new("ventes", "Ventes", "Gérez vos ventes et factures", "#10b981", Priv.Gestion.ViewVentes),
        new("charges", "Charges", "Gérez les charges de l'entreprise", "#f59e0b", Priv.Gestion.ViewCharges),
        new("marges", "Marges", "Analysez vos marges et bénéfices", "#8b5cf6", Priv.Gestion.ViewMarges),
        new("prestations", "Prestation et Services", "Devis, factures et rentabilité", "#e11d48",
            Priv.Gestion.ViewPrestations, RequiresPrestationsEnabled: true),
        new("amortissement", "Amortissement", "Suivez les amortissements et immobilisations", "#4895ef", Priv.Gestion.ViewAmortissement),
        new("bilan", "Bilan", "Bilan comptable et compte de résultat", "#6366f1", Priv.Gestion.ViewBilan),
        new("audit", "Audit", "Surveillez les activités en temps réel", "#6366f1", Priv.Gestion.ViewAudit),
        new("parametres", "Paramètres", "Configurez les paramètres et privilèges", "#64748b", Priv.Gestion.ViewParametres),
    ];

    /// <summary>
    /// How the web app groups Gestion entries for admins. Source: adminSections in Gestion.jsx.
    /// </summary>
    public static readonly IReadOnlyList<MenuSection> GestionSections =
    [
        new("operations", "Opérations", "Activités commerciales quotidiennes", "#3b82f6",
            ["gestion-de-stock", "ventes", "charges", "prestations"]),
        new("finance", "Finance & Analyse", "Comptabilité et performance financière", "#06b6d4",
            ["marges", "amortissement", "bilan"]),
        new("administration", "Administration", "Contrôle, surveillance et configuration", "#7c3aed",
            ["audit", "parametres"]),
    ];

    /// <summary>
    /// Filters a menu the way the React components do: an entry with a required privilege is
    /// shown only when that privilege resolves to true, and feature toggles are honoured.
    /// </summary>
    /// <param name="entries">The menu to filter.</param>
    /// <param name="privileges">Resolved privilege map for the signed-in user in the current group.</param>
    /// <param name="gestionAccess">The group's gestion_access flag.</param>
    /// <param name="prestationsEnabled">The group's prestations toggle.</param>
    /// <param name="isAdmin">True when the user holds an admin or sub_admin role.</param>
    public static IReadOnlyList<MenuEntry> Visible(
        IEnumerable<MenuEntry> entries,
        IReadOnlyDictionary<string, bool> privileges,
        bool gestionAccess,
        bool prestationsEnabled,
        bool isAdmin = false)
    {
        var visible = new List<MenuEntry>();
        foreach (var entry in entries)
        {
            if (entry.RequiresAdmin && !isAdmin) continue;
            if (entry.RequiresGestionAccess && !gestionAccess) continue;
            if (entry.RequiresPrestationsEnabled && !prestationsEnabled) continue;
            if (entry.RequiredPrivilege is { } required &&
                !(privileges.TryGetValue(required, out var granted) && granted)) continue;
            visible.Add(entry);
        }
        return visible;
    }
}
