namespace Lonnii.Shared.Security;

/// <summary>
/// Every privilege name used by Lonnii Business, grouped by the system that owns it.
/// Names are the exact strings stored in the database - do not rename them.
/// </summary>
public static class Priv
{
    /// <summary>Core group privileges (<c>privileges</c> table).</summary>
    public static class Core
    {
        public const string AddMembers = "can_add_members";
        public const string RemoveMembers = "can_remove_members";
        public const string BanMembers = "can_ban_members";
        public const string ViewAllMembers = "can_view_all_members";
        public const string ManageInvitations = "can_manage_invitations";

        public const string CreateEvents = "can_create_events";
        public const string EditEvents = "can_edit_events";
        public const string DeleteEvents = "can_delete_events";
        public const string ManageFiles = "can_manage_files";
        public const string CreateAnnouncements = "can_create_announcements";

        public const string ModerateChat = "can_moderate_chat";
        public const string DeleteMessages = "can_delete_messages";
        public const string MuteMembers = "can_mute_members";
        public const string ManageChatSettings = "can_manage_chat_settings";

        public const string ManagePrivileges = "can_manage_privileges";
        public const string PromoteMembers = "can_promote_members";
        public const string ManageGroupSettings = "can_manage_group_settings";
        public const string ViewAuditLogs = "can_view_audit_logs";
        public const string DeleteGroup = "can_delete_group";

        public const string ManageExpenses = "can_manage_expenses";
        public const string ViewFinancialReports = "can_view_financial_reports";
        public const string ManagePayments = "can_manage_payments";
    }

    /// <summary>Option privileges - Programme, Chat, Formulaire (<c>option_privileges</c>).</summary>
    public static class Option
    {
        public const string ViewProgramme = "can_view_programme";
        public const string CreateEvents = "can_create_events";
        public const string EditEvents = "can_edit_events";
        public const string DeleteEvents = "can_delete_events";
        public const string ManageEventCategories = "can_manage_event_categories";

        public const string ViewChat = "can_view_chat";
        public const string SendMessages = "can_send_messages";
        public const string EditOwnMessages = "can_edit_own_messages";
        public const string DeleteOwnMessages = "can_delete_own_messages";
        public const string ModerateChat = "can_moderate_chat";
        public const string ManageReactions = "can_manage_reactions";

        public const string ViewFormulaires = "can_view_formulaires";
        public const string CreateFormulaires = "can_create_formulaires";
        public const string EditFormulaires = "can_edit_formulaires";
        public const string DeleteFormulaires = "can_delete_formulaires";
        public const string RespondToForms = "can_respond_to_forms";
        public const string ViewResponses = "can_view_responses";
        public const string ExportResponses = "can_export_responses";
    }

    /// <summary>Gestion privileges (<c>gestion_privileges</c>), by module.</summary>
    public static class Gestion
    {
        // stock
        public const string ViewStock = "can_view_stock";
        public const string AddProducts = "can_add_products";
        public const string EditProducts = "can_edit_products";
        public const string DeleteProducts = "can_delete_products";
        public const string ManageCategories = "can_manage_categories";
        public const string AdjustStock = "can_adjust_stock";
        public const string ViewStockHistory = "can_view_stock_history";
        public const string ExportStockData = "can_export_stock_data";
        public const string ViewAnalytics = "can_view_analytics";
        public const string ViewStockAnalytics = "can_view_stock_analytics";

        // sales (legacy module name, kept for import fidelity)
        public const string ViewSales = "can_view_sales";
        public const string CreateSales = "can_create_sales";
        public const string EditSales = "can_edit_sales";
        public const string DeleteSales = "can_delete_sales";
        public const string ManageCustomers = "can_manage_customers";
        public const string ApplyDiscounts = "can_apply_discounts";
        public const string ProcessReturns = "can_process_returns";
        public const string ViewSalesAnalytics = "can_view_sales_analytics";

        // caisse (cash register, stored under the 'sales' module)
        public const string AccessCaisse = "can_access_caisse";
        public const string OpenCaisse = "can_open_caisse";
        public const string CloseCaisse = "can_close_caisse";
        public const string ViewCaisseHistory = "can_view_caisse_history";
        public const string ResolveCaisseEcart = "can_resolve_caisse_ecart";

        // ventes (current sales module)
        public const string ViewVentes = "can_view_ventes";
        public const string CreateVente = "can_create_vente";
        public const string EditVente = "can_edit_vente";
        public const string DeleteVente = "can_delete_vente";
        public const string ViewVenteDetails = "can_view_vente_details";
        public const string AddPayment = "can_add_payment";
        public const string CancelVente = "can_cancel_vente";
        public const string SoldeAvoir = "can_solde_avoir";
        public const string ExportVentes = "can_export_ventes";
        public const string ViewVentesAnalytics = "can_view_ventes_analytics";
        public const string ManageClients = "can_manage_clients";
        public const string PrintReceipt = "can_print_receipt";
        public const string ApplyDiscount = "can_apply_discount";

        // charges
        public const string ViewCharges = "can_view_charges";
        public const string AddCharges = "can_add_charges";
        public const string EditCharges = "can_edit_charges";
        public const string DeleteCharges = "can_delete_charges";
        public const string ExportCharges = "can_export_charges";
        public const string ViewChargesAnalytics = "can_view_charges_analytics";
        public const string ManageChargesCategories = "can_manage_charges_categories";
        public const string ApproveCharges = "can_approve_charges";

        // marges
        public const string ViewMarges = "can_view_marges";
        public const string ExportMarges = "can_export_marges";

        // amortissement
        public const string ViewAmortissement = "can_view_amortissement";
        public const string AddImmobilisation = "can_add_immobilisation";
        public const string EditImmobilisation = "can_edit_immobilisation";
        public const string DeleteImmobilisation = "can_delete_immobilisation";
        public const string CederImmobilisation = "can_ceder_immobilisation";
        public const string ExportAmortissement = "can_export_amortissement";

        // bilan
        public const string ViewBilan = "can_view_bilan";
        public const string AddBilanEcriture = "can_add_bilan_ecriture";
        public const string EditBilanEcriture = "can_edit_bilan_ecriture";
        public const string DeleteBilanEcriture = "can_delete_bilan_ecriture";
        public const string ManageBilanComptes = "can_manage_bilan_comptes";
        public const string ExportBilan = "can_export_bilan";
        public const string ViewResultat = "can_view_resultat";
        public const string EditResultatDonnees = "can_edit_resultat_donnees";

        // prestations
        public const string ViewPrestations = "can_view_prestations";
        public const string ViewPrestationsClients = "can_view_prestations_clients";
        public const string ManagePrestationsClients = "can_manage_prestations_clients";
        public const string ViewPrestationsCatalogue = "can_view_prestations_catalogue";
        public const string ManagePrestationsCatalogue = "can_manage_prestations_catalogue";
        public const string ViewDevis = "can_view_devis";
        public const string CreateDevis = "can_create_devis";
        public const string EditDevis = "can_edit_devis";
        public const string DeleteDevis = "can_delete_devis";
        public const string SendDevis = "can_send_devis";
        public const string ViewFactures = "can_view_factures";
        public const string CreateFactures = "can_create_factures";
        public const string EditFactures = "can_edit_factures";
        public const string DeleteFactures = "can_delete_factures";
        public const string RecordPayment = "can_record_payment";
        public const string ViewProjets = "can_view_projets";
        public const string ManageProjets = "can_manage_projets";
        public const string ViewRentabilite = "can_view_rentabilite";
        public const string ManageProjetDepenses = "can_manage_projet_depenses";
        public const string ExportPrestations = "can_export_prestations";
        public const string ViewPrestationsAnalytics = "can_view_prestations_analytics";
        public const string ManagePrestationsParametres = "can_manage_prestations_parametres";

        // finance
        public const string ViewExpenses = "can_view_expenses";
        public const string AddExpenses = "can_add_expenses";
        public const string EditExpenses = "can_edit_expenses";
        public const string DeleteExpenses = "can_delete_expenses";
        public const string ManageSuppliers = "can_manage_suppliers";
        public const string ViewProfitLoss = "can_view_profit_loss";
        public const string ExportFinancialData = "can_export_financial_data";

        // analytics
        public const string ViewBasicAnalytics = "can_view_basic_analytics";
        public const string ViewAdvancedAnalytics = "can_view_advanced_analytics";
        public const string CreateCustomReports = "can_create_custom_reports";
        public const string ScheduleReports = "can_schedule_reports";
        public const string ViewUserActivity = "can_view_user_activity";

        // admin
        public const string ManageGestionSettings = "can_manage_gestion_settings";
        public const string BackupData = "can_backup_data";
        public const string ManageGestionUsers = "can_manage_gestion_users";
        public const string ViewSystemLogs = "can_view_system_logs";

        /// <summary>
        /// Virtual privilege. Not a row in <c>gestion_privileges</c>; the web app's
        /// my-privileges endpoint synthesises it for any admin role
        /// (backend/routes/gestion.js). Gates the Audit menu entry.
        /// </summary>
        public const string ViewAudit = "can_view_audit";

        /// <summary>Virtual privilege, as <see cref="ViewAudit"/>. Gates the Paramètres menu entry.</summary>
        public const string ViewParametres = "can_view_parametres";
    }
}
