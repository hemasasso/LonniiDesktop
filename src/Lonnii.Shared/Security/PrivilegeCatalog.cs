namespace Lonnii.Shared.Security;

/// <summary>A row of the core <c>privileges</c> table.</summary>
public sealed record CorePrivilege(string Name, string DisplayName, string Description, string Category, bool IsAdminOnly);

/// <summary>A row of <c>option_privileges</c> or <c>gestion_privileges</c>.</summary>
public sealed record ModulePrivilege(string Name, string DisplayName, string Description, string Module, bool IsAdminOnly);

/// <summary>
/// The seed catalog for all three privilege systems, ported from Lonnii Business SQL:
/// backend/database/postgres_schema.sql, backend/setup_option_privileges.sql,
/// backend/setup_gestion_privileges.sql and the later gestion migrations.
///
/// The web app inserts with ON CONFLICT (name) DO NOTHING, so where the same name is
/// seeded twice the first insert wins. <see cref="Gestion"/> preserves that order and
/// de-duplicates by name to reproduce the live catalog exactly.
/// </summary>
public static class PrivilegeCatalog
{
    private const string MemberMgmt = PrivilegeCategories.MemberManagement;
    private const string ContentMgmt = PrivilegeCategories.ContentManagement;
    private const string Moderation = PrivilegeCategories.Moderation;
    private const string AdminCat = PrivilegeCategories.Admin;

    /// <summary>Core group privileges. Source: postgres_schema.sql.</summary>
    public static readonly IReadOnlyList<CorePrivilege> Core =
    [
        new(Priv.Core.AddMembers, "Ajouter des membres", "Permet d'ajouter de nouveaux membres au groupe", MemberMgmt, false),
        new(Priv.Core.RemoveMembers, "Retirer des membres", "Permet de retirer des membres du groupe", MemberMgmt, false),
        new(Priv.Core.BanMembers, "Bannir des membres", "Permet de bannir des membres du groupe", MemberMgmt, false),
        new(Priv.Core.ViewAllMembers, "Voir tous les membres", "Permet de voir la liste complète des membres", MemberMgmt, false),
        new(Priv.Core.ManageInvitations, "Gérer les invitations", "Permet de gérer les invitations au groupe", MemberMgmt, false),

        new(Priv.Core.CreateEvents, "Créer des événements", "Permet de créer des événements dans le calendrier", ContentMgmt, false),
        new(Priv.Core.EditEvents, "Modifier des événements", "Permet de modifier les événements existants", ContentMgmt, false),
        new(Priv.Core.DeleteEvents, "Supprimer des événements", "Permet de supprimer des événements", ContentMgmt, false),
        new(Priv.Core.ManageFiles, "Gérer les fichiers", "Permet de gérer les fichiers du groupe", ContentMgmt, false),
        new(Priv.Core.CreateAnnouncements, "Créer des annonces", "Permet de créer des annonces pour le groupe", ContentMgmt, false),

        new(Priv.Core.ModerateChat, "Modérer le chat", "Permet de modérer les messages du chat", Moderation, false),
        new(Priv.Core.DeleteMessages, "Supprimer des messages", "Permet de supprimer les messages du chat", Moderation, false),
        new(Priv.Core.MuteMembers, "Couper le micro des membres", "Permet de couper le micro des membres temporairement", Moderation, false),
        new(Priv.Core.ManageChatSettings, "Gérer les paramètres du chat", "Permet de modifier les paramètres du chat", Moderation, false),

        new(Priv.Core.ManagePrivileges, "Gérer les privilèges", "Permet de gérer les privilèges des autres membres", AdminCat, true),
        new(Priv.Core.PromoteMembers, "Promouvoir des membres", "Permet de promouvoir des membres à des rôles supérieurs", AdminCat, true),
        new(Priv.Core.ManageGroupSettings, "Gérer les paramètres du groupe", "Permet de modifier les paramètres du groupe", AdminCat, true),
        new(Priv.Core.ViewAuditLogs, "Voir les logs d'audit", "Permet de consulter les logs d'activité du groupe", AdminCat, true),
        new(Priv.Core.DeleteGroup, "Supprimer le groupe", "Permet de supprimer complètement le groupe", AdminCat, true),

        new(Priv.Core.ManageExpenses, "Gérer les dépenses", "Permet de gérer les dépenses du groupe", ContentMgmt, false),
        new(Priv.Core.ViewFinancialReports, "Voir les rapports financiers", "Permet de consulter les rapports financiers", ContentMgmt, false),
        new(Priv.Core.ManagePayments, "Gérer les paiements", "Permet de gérer les paiements du groupe", ContentMgmt, false),
    ];

    /// <summary>
    /// Role to core-privilege mapping, ported from the <c>role_privileges</c> seed in
    /// postgres_schema.sql. Admin receives every privilege.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RolePrivileges =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [GroupRoles.Admin] = Core.Select(p => p.Name).ToArray(),
            [GroupRoles.SubAdmin] =
            [
                Priv.Core.AddMembers, Priv.Core.RemoveMembers, Priv.Core.ViewAllMembers, Priv.Core.ManageInvitations,
                Priv.Core.CreateEvents, Priv.Core.EditEvents, Priv.Core.DeleteEvents, Priv.Core.ManageFiles,
                Priv.Core.CreateAnnouncements, Priv.Core.ModerateChat, Priv.Core.DeleteMessages,
                Priv.Core.MuteMembers, Priv.Core.ManageExpenses,
            ],
            [GroupRoles.Moderator] =
            [
                Priv.Core.ViewAllMembers, Priv.Core.CreateEvents, Priv.Core.EditEvents,
                Priv.Core.ModerateChat, Priv.Core.DeleteMessages, Priv.Core.MuteMembers,
            ],
            [GroupRoles.Member] = [Priv.Core.CreateEvents],
        };

    /// <summary>Option privileges. Source: setup_option_privileges.sql.</summary>
    public static readonly IReadOnlyList<ModulePrivilege> Option =
    [
        new(Priv.Option.ViewProgramme, "Consulter Programme", "Permet de consulter le calendrier et les événements du groupe", OptionModules.Programme, false),
        new(Priv.Option.CreateEvents, "Créer Événements", "Permet de créer de nouveaux événements dans le calendrier", OptionModules.Programme, false),
        new(Priv.Option.EditEvents, "Modifier Événements", "Permet de modifier les événements existants du calendrier", OptionModules.Programme, false),
        new(Priv.Option.DeleteEvents, "Supprimer Événements", "Permet de supprimer des événements du calendrier", OptionModules.Programme, true),
        new(Priv.Option.ManageEventCategories, "Gérer Catégories", "Permet de créer et gérer les catégories d'événements", OptionModules.Programme, false),

        new(Priv.Option.ViewChat, "Consulter Chat", "Permet de consulter les messages du chat de groupe", OptionModules.Chat, false),
        new(Priv.Option.SendMessages, "Envoyer Messages", "Permet d'envoyer des messages dans le chat du groupe", OptionModules.Chat, false),
        new(Priv.Option.EditOwnMessages, "Modifier Ses Messages", "Permet de modifier ses propres messages envoyés", OptionModules.Chat, false),
        new(Priv.Option.DeleteOwnMessages, "Supprimer Ses Messages", "Permet de supprimer ses propres messages envoyés", OptionModules.Chat, false),
        new(Priv.Option.ModerateChat, "Modérer Chat", "Permet de modérer et supprimer les messages des autres membres", OptionModules.Chat, true),
        new(Priv.Option.ManageReactions, "Gérer Réactions", "Permet d'ajouter et gérer les réactions aux messages", OptionModules.Chat, false),

        new(Priv.Option.ViewFormulaires, "Consulter Formulaires", "Permet de consulter les formulaires disponibles du groupe", OptionModules.Formulaire, false),
        new(Priv.Option.CreateFormulaires, "Créer Formulaires", "Permet de créer de nouveaux formulaires pour le groupe", OptionModules.Formulaire, false),
        new(Priv.Option.EditFormulaires, "Modifier Formulaires", "Permet de modifier les formulaires existants", OptionModules.Formulaire, false),
        new(Priv.Option.DeleteFormulaires, "Supprimer Formulaires", "Permet de supprimer des formulaires du groupe", OptionModules.Formulaire, true),
        new(Priv.Option.RespondToForms, "Répondre Formulaires", "Permet de soumettre des réponses aux formulaires", OptionModules.Formulaire, false),
        new(Priv.Option.ViewResponses, "Consulter Réponses", "Permet de consulter les réponses soumises aux formulaires", OptionModules.Formulaire, false),
        new(Priv.Option.ExportResponses, "Exporter Réponses", "Permet d'exporter les réponses aux formulaires en fichier", OptionModules.Formulaire, true),
    ];

    /// <summary>
    /// Privileges granted to every non-admin member when a group is created.
    /// Source: the bootstrap INSERT at the foot of setup_option_privileges.sql.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultOptionGrants =
    [
        Priv.Option.ViewProgramme, Priv.Option.ViewChat, Priv.Option.SendMessages,
        Priv.Option.ViewFormulaires, Priv.Option.RespondToForms,
    ];

    /// <summary>
    /// Gestion privileges, in the order the web app seeds them. De-duplicated by name,
    /// first occurrence winning, to match ON CONFLICT DO NOTHING.
    /// </summary>
    public static readonly IReadOnlyList<ModulePrivilege> Gestion = Deduplicate(GestionSeedOrder());

    /// <summary>All Gestion privileges for one module.</summary>
    public static IEnumerable<ModulePrivilege> GestionByModule(string module) =>
        Gestion.Where(p => p.Module == module);

    private static IReadOnlyList<ModulePrivilege> Deduplicate(IEnumerable<ModulePrivilege> source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ModulePrivilege>();
        foreach (var p in source)
        {
            if (seen.Add(p.Name)) result.Add(p);
        }
        return result;
    }

    private static IEnumerable<ModulePrivilege> GestionSeedOrder()
    {
        const string stock = GestionModules.Stock;
        const string sales = GestionModules.Sales;
        const string finance = GestionModules.Finance;
        const string analytics = GestionModules.Analytics;
        const string admin = GestionModules.Admin;
        const string ventes = GestionModules.Ventes;
        const string charges = GestionModules.Charges;
        const string marges = GestionModules.Marges;
        const string amort = GestionModules.Amortissement;
        const string bilan = GestionModules.Bilan;
        const string presta = GestionModules.Prestations;

        // --- setup_gestion_privileges.sql ---
        yield return new(Priv.Gestion.ViewStock, "Consulter Stock", "Permet de consulter les niveaux de stock et l'inventaire", stock, false);
        yield return new(Priv.Gestion.AddProducts, "Ajouter Produits", "Permet d'ajouter de nouveaux produits à l'inventaire", stock, false);
        yield return new(Priv.Gestion.EditProducts, "Modifier Produits", "Permet de modifier les informations des produits existants", stock, false);
        yield return new(Priv.Gestion.DeleteProducts, "Supprimer Produits", "Permet de supprimer des produits de l'inventaire", stock, true);
        yield return new(Priv.Gestion.ManageCategories, "Gérer Catégories", "Permet de créer et gérer les catégories de produits", stock, false);
        yield return new(Priv.Gestion.AdjustStock, "Ajuster Stock", "Permet d'ajuster manuellement les quantités en stock", stock, false);
        yield return new(Priv.Gestion.ViewStockHistory, "Consulter Historique", "Permet de consulter l'historique des mouvements de stock", stock, false);
        yield return new(Priv.Gestion.ExportStockData, "Exporter Données", "Permet d'exporter les données de stock vers des fichiers", stock, false);

        yield return new(Priv.Gestion.ViewSales, "Consulter Ventes", "Permet de consulter les données et rapports de vente", sales, false);
        yield return new(Priv.Gestion.CreateSales, "Créer Ventes", "Permet d'enregistrer de nouvelles transactions de vente", sales, false);
        yield return new(Priv.Gestion.EditSales, "Modifier Ventes", "Permet de modifier les enregistrements de vente existants", sales, false);
        yield return new(Priv.Gestion.DeleteSales, "Supprimer Ventes", "Permet de supprimer des transactions de vente", sales, true);
        yield return new(Priv.Gestion.CancelVente, "Annuler Ventes", "Permet d'annuler des transactions de vente", sales, false);
        yield return new(Priv.Gestion.ManageCustomers, "Gérer Clients", "Permet d'ajouter et gérer les informations des clients", sales, false);
        yield return new(Priv.Gestion.ApplyDiscounts, "Appliquer Remises", "Permet d'appliquer des remises et promotions aux ventes", sales, false);
        yield return new(Priv.Gestion.ProcessReturns, "Traiter Retours", "Permet de gérer les retours de produits et remboursements", sales, false);
        yield return new(Priv.Gestion.ViewSalesAnalytics, "Consulter Analyses", "Permet d'accéder aux analyses détaillées des ventes", sales, false);
        yield return new(Priv.Gestion.PrintReceipt, "Imprimer Reçu", "Permet d'imprimer les reçus et factures", sales, false);
        yield return new(Priv.Gestion.AddPayment, "Ajouter Paiement", "Permet d'ajouter des paiements aux ventes", sales, false);

        yield return new(Priv.Gestion.AccessCaisse, "Accéder Caisse", "Permet d'accéder au module caisse", sales, false);
        yield return new(Priv.Gestion.OpenCaisse, "Ouvrir Caisse", "Permet d'ouvrir une nouvelle session de caisse", sales, false);
        yield return new(Priv.Gestion.CloseCaisse, "Fermer Caisse", "Permet de fermer une session de caisse", sales, false);
        yield return new(Priv.Gestion.ViewCaisseHistory, "Historique Caisse", "Permet de consulter l'historique des sessions de caisse", sales, false);
        yield return new(Priv.Gestion.ResolveCaisseEcart, "Résoudre Écarts", "Permet de résoudre les écarts de caisse", sales, true);

        yield return new(Priv.Gestion.ViewExpenses, "Consulter Dépenses", "Permet de consulter les enregistrements de dépenses", finance, false);
        yield return new(Priv.Gestion.AddExpenses, "Ajouter Dépenses", "Permet d'ajouter de nouvelles entrées de dépenses", finance, false);
        yield return new(Priv.Gestion.EditExpenses, "Modifier Dépenses", "Permet de modifier les enregistrements de dépenses existants", finance, false);
        yield return new(Priv.Gestion.DeleteExpenses, "Supprimer Dépenses", "Permet de supprimer des enregistrements de dépenses", finance, true);
        yield return new(Priv.Gestion.ManageSuppliers, "Gérer Fournisseurs", "Permet d'ajouter et gérer les informations des fournisseurs", finance, false);
        yield return new(Priv.Gestion.ViewProfitLoss, "Consulter Bilan", "Permet d'accéder aux rapports de profits et pertes", finance, false);
        yield return new(Priv.Gestion.ExportFinancialData, "Exporter Finances", "Permet d'exporter les rapports et données financières", finance, false);

        yield return new(Priv.Gestion.ViewBasicAnalytics, "Analyses Basiques", "Permet d'accéder aux tableaux de bord et analyses de base", analytics, false);
        yield return new(Priv.Gestion.ViewAdvancedAnalytics, "Analyses Avancées", "Permet d'accéder aux analyses détaillées et insights avancés", analytics, false);
        yield return new(Priv.Gestion.CreateCustomReports, "Rapports Personnalisés", "Permet de créer et générer des rapports personnalisés", analytics, false);
        yield return new(Priv.Gestion.ScheduleReports, "Programmer Rapports", "Permet de configurer la génération automatique de rapports", analytics, true);
        yield return new(Priv.Gestion.ViewUserActivity, "Activité Utilisateurs", "Permet de surveiller les activités et actions des utilisateurs", analytics, true);

        yield return new(Priv.Gestion.ManageGestionSettings, "Gérer Paramètres", "Permet de configurer les paramètres système de Gestion", admin, true);
        yield return new(Priv.Gestion.BackupData, "Sauvegarder Données", "Permet de créer et restaurer des sauvegardes complètes", admin, true);
        yield return new(Priv.Gestion.ManageGestionUsers, "Gérer Utilisateurs", "Permet d'ajouter et supprimer l'accès aux modules Gestion", admin, true);
        yield return new(Priv.Gestion.ViewSystemLogs, "Consulter Logs", "Permet d'accéder aux journaux système et d'audit", admin, true);

        // --- add_stock_analytics_privilege.sql ---
        yield return new(Priv.Gestion.ViewAnalytics, "Voir Analyses Stock", "Permet de consulter les analyses et statistiques du stock", stock, false);
        yield return new(Priv.Gestion.ViewStockAnalytics, "Statistiques Stock", "Permet d'accéder aux statistiques détaillées du stock", stock, false);

        // --- setup_ventes_privileges.sql ---
        yield return new(Priv.Gestion.ViewVentes, "Voir les ventes", "Consulter la liste des ventes", ventes, false);
        yield return new(Priv.Gestion.CreateVente, "Créer une vente", "Enregistrer une nouvelle vente", ventes, false);
        yield return new(Priv.Gestion.EditVente, "Modifier une vente", "Modifier les informations d'une vente", ventes, false);
        yield return new(Priv.Gestion.DeleteVente, "Supprimer une vente", "Supprimer une vente (sans paiements)", ventes, true);
        yield return new(Priv.Gestion.ViewVenteDetails, "Détails de vente", "Voir les détails complets d'une vente", ventes, false);
        yield return new(Priv.Gestion.SoldeAvoir, "Solder un Avoir", "Solder un avoir client", ventes, false);
        yield return new(Priv.Gestion.ExportVentes, "Exporter les ventes", "Exporter les données de ventes", ventes, false);
        yield return new(Priv.Gestion.ViewVentesAnalytics, "Statistiques ventes", "Voir les statistiques et analyses des ventes", ventes, false);
        yield return new(Priv.Gestion.ManageClients, "Gérer les clients", "Gérer les informations clients", ventes, false);
        yield return new(Priv.Gestion.ApplyDiscount, "Appliquer remise", "Appliquer des remises sur les ventes", ventes, false);

        // --- create_charges_system.sql ---
        yield return new(Priv.Gestion.ViewCharges, "Voir les charges", "Permet de consulter la liste des charges", charges, false);
        yield return new(Priv.Gestion.AddCharges, "Ajouter des charges", "Permet d'ajouter de nouvelles charges", charges, false);
        yield return new(Priv.Gestion.EditCharges, "Modifier les charges", "Permet de modifier les charges existantes", charges, false);
        yield return new(Priv.Gestion.DeleteCharges, "Supprimer les charges", "Permet de supprimer des charges", charges, false);
        yield return new(Priv.Gestion.ExportCharges, "Exporter les charges", "Permet d'exporter les données de charges", charges, false);
        yield return new(Priv.Gestion.ViewChargesAnalytics, "Voir les analyses", "Permet de consulter les analyses et statistiques des charges", charges, false);
        yield return new(Priv.Gestion.ManageChargesCategories, "Gérer les catégories", "Permet de créer et gérer les catégories de charges", charges, false);
        yield return new(Priv.Gestion.ApproveCharges, "Approuver les charges", "Permet d'approuver ou rejeter des charges", charges, true);

        // --- add_marges_privileges.sql ---
        yield return new(Priv.Gestion.ViewMarges, "Voir Marges", "Peut voir l'analyse des marges et bénéfices", marges, false);
        yield return new(Priv.Gestion.ExportMarges, "Exporter Marges", "Peut exporter les données des marges", marges, false);

        // --- setup_amortissement_bilan.sql ---
        yield return new(Priv.Gestion.ViewAmortissement, "Consulter Amortissements", "Permet de consulter les immobilisations et leurs amortissements", amort, false);
        yield return new(Priv.Gestion.AddImmobilisation, "Ajouter Immobilisation", "Permet d'ajouter de nouvelles immobilisations (machines, véhicules, etc.)", amort, false);
        yield return new(Priv.Gestion.EditImmobilisation, "Modifier Immobilisation", "Permet de modifier les informations des immobilisations", amort, false);
        yield return new(Priv.Gestion.DeleteImmobilisation, "Supprimer Immobilisation", "Permet de supprimer des immobilisations", amort, true);
        yield return new(Priv.Gestion.CederImmobilisation, "Céder/Réformer", "Permet d'enregistrer la cession ou la réforme d'une immobilisation", amort, false);
        yield return new(Priv.Gestion.ExportAmortissement, "Exporter Amortissements", "Permet d'exporter les tableaux d'amortissement", amort, false);

        yield return new(Priv.Gestion.ViewBilan, "Consulter Bilan", "Permet de consulter le bilan et le compte de résultat de l'entreprise", bilan, false);
        yield return new(Priv.Gestion.AddBilanEcriture, "Ajouter Écriture Bilan", "Permet d'ajouter des écritures au bilan", bilan, false);
        yield return new(Priv.Gestion.EditBilanEcriture, "Modifier Écriture Bilan", "Permet de modifier les écritures du bilan", bilan, false);
        yield return new(Priv.Gestion.DeleteBilanEcriture, "Supprimer Écriture Bilan", "Permet de supprimer des écritures du bilan", bilan, true);
        yield return new(Priv.Gestion.ManageBilanComptes, "Gérer Comptes Bilan", "Permet de créer et gérer les comptes du bilan", bilan, true);
        yield return new(Priv.Gestion.ExportBilan, "Exporter Bilan", "Permet d'exporter le bilan et le compte de résultat", bilan, false);
        yield return new(Priv.Gestion.ViewResultat, "Consulter Résultat", "Permet de consulter le compte de résultat", bilan, false);

        // --- create_prestations_services_system.sql ---
        yield return new(Priv.Gestion.ViewPrestationsClients, "Voir Clients Prestations", "Permet de consulter la liste des clients", presta, false);
        yield return new(Priv.Gestion.ManagePrestationsClients, "Gérer Clients Prestations", "Permet d'ajouter, modifier et supprimer des clients", presta, false);
        yield return new(Priv.Gestion.ViewPrestationsCatalogue, "Voir Catalogue Services", "Permet de consulter le catalogue de services", presta, false);
        yield return new(Priv.Gestion.ManagePrestationsCatalogue, "Gérer Catalogue Services", "Permet de gérer le catalogue de services et tarifs", presta, false);
        yield return new(Priv.Gestion.ViewDevis, "Voir Devis", "Permet de consulter les devis", presta, false);
        yield return new(Priv.Gestion.CreateDevis, "Créer Devis", "Permet de créer de nouveaux devis", presta, false);
        yield return new(Priv.Gestion.EditDevis, "Modifier Devis", "Permet de modifier les devis existants", presta, false);
        yield return new(Priv.Gestion.DeleteDevis, "Supprimer Devis", "Permet de supprimer des devis", presta, true);
        yield return new(Priv.Gestion.SendDevis, "Envoyer Devis", "Permet d'envoyer les devis aux clients", presta, false);
        yield return new(Priv.Gestion.ViewFactures, "Voir Factures", "Permet de consulter les factures", presta, false);
        yield return new(Priv.Gestion.CreateFactures, "Créer Factures", "Permet de créer de nouvelles factures", presta, false);
        yield return new(Priv.Gestion.EditFactures, "Modifier Factures", "Permet de modifier les factures existantes", presta, false);
        yield return new(Priv.Gestion.DeleteFactures, "Supprimer Factures", "Permet de supprimer des factures", presta, true);
        yield return new(Priv.Gestion.RecordPayment, "Enregistrer Paiement", "Permet d'enregistrer les paiements sur les factures", presta, false);
        yield return new(Priv.Gestion.ViewProjets, "Voir Projets", "Permet de consulter les projets", presta, false);
        yield return new(Priv.Gestion.ManageProjets, "Gérer Projets", "Permet de créer et gérer les projets", presta, false);
        yield return new(Priv.Gestion.ViewRentabilite, "Voir Rentabilité", "Permet de consulter l'analyse de rentabilité", presta, false);
        yield return new(Priv.Gestion.ManageProjetDepenses, "Gérer Dépenses Projet", "Permet d'ajouter des dépenses aux projets", presta, false);
        yield return new(Priv.Gestion.ExportPrestations, "Exporter Prestations", "Permet d'exporter les données de prestations", presta, false);
        yield return new(Priv.Gestion.ViewPrestationsAnalytics, "Analyses Prestations", "Permet de consulter les analyses et statistiques", presta, false);

        // --- add_prestations_option_privileges.sql (only the name it adds first) ---
        yield return new(Priv.Gestion.ViewPrestations, "Consulter Prestations", "Permet de consulter le module Prestations et Services", presta, false);
    }
}
