using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "caisses",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_name = table.Column<string>(type: "TEXT", nullable: false),
                    date_ouverture = table.Column<DateTime>(type: "TEXT", nullable: false),
                    montant_initial = table.Column<long>(type: "INTEGER", nullable: false),
                    date_fermeture = table.Column<DateTime>(type: "TEXT", nullable: true),
                    montant_final = table.Column<long>(type: "INTEGER", nullable: true),
                    total_ventes = table.Column<int>(type: "INTEGER", nullable: false),
                    total_chiffre_affaires = table.Column<long>(type: "INTEGER", nullable: false),
                    total_encaisse = table.Column<long>(type: "INTEGER", nullable: false),
                    total_avoir = table.Column<long>(type: "INTEGER", nullable: false),
                    total_restant = table.Column<long>(type: "INTEGER", nullable: false),
                    paiement_cash = table.Column<long>(type: "INTEGER", nullable: false),
                    paiement_mobile = table.Column<long>(type: "INTEGER", nullable: false),
                    paiement_carte = table.Column<long>(type: "INTEGER", nullable: false),
                    paiement_autres = table.Column<long>(type: "INTEGER", nullable: false),
                    ecart = table.Column<long>(type: "INTEGER", nullable: false),
                    ecart_resolved = table.Column<bool>(type: "INTEGER", nullable: false),
                    ecart_resolved_by = table.Column<string>(type: "TEXT", nullable: true),
                    ecart_resolved_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ecart_resolution_note = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_caisses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    color = table.Column<string>(type: "TEXT", nullable: true),
                    icon = table.Column<string>(type: "TEXT", nullable: true),
                    parent_id = table.Column<string>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                    table.ForeignKey(
                        name: "FK_categories_categories_parent_id",
                        column: x => x.parent_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "clients",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    nom = table.Column<string>(type: "TEXT", nullable: false),
                    telephone = table.Column<string>(type: "TEXT", nullable: true),
                    email = table.Column<string>(type: "TEXT", nullable: true),
                    adresse = table.Column<string>(type: "TEXT", nullable: true),
                    ville = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gestion_privilege_audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    privilege_id = table.Column<int>(type: "INTEGER", nullable: true),
                    module = table.Column<string>(type: "TEXT", nullable: true),
                    role_from = table.Column<string>(type: "TEXT", nullable: true),
                    role_to = table.Column<string>(type: "TEXT", nullable: true),
                    performed_by = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gestion_privilege_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gestion_privileges",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    module = table.Column<string>(type: "TEXT", nullable: false),
                    is_admin_only = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gestion_privileges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gestion_user_roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    module = table.Column<string>(type: "TEXT", nullable: false),
                    assigned_by = table.Column<string>(type: "TEXT", nullable: true),
                    assigned_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gestion_user_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "groupe_sessions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    session_token = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_accessed_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupe_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "option_privilege_audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    privilege_id = table.Column<int>(type: "INTEGER", nullable: true),
                    module = table.Column<string>(type: "TEXT", nullable: true),
                    performed_by = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_option_privilege_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "option_privileges",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    module = table.Column<string>(type: "TEXT", nullable: false),
                    is_admin_only = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_option_privileges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "password_history",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    iduser = table.Column<string>(type: "TEXT", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_history", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "privilege_audit",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_user_id = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    privilege_id = table.Column<int>(type: "INTEGER", nullable: true),
                    role_from = table.Column<string>(type: "TEXT", nullable: true),
                    role_to = table.Column<string>(type: "TEXT", nullable: true),
                    performed_by = table.Column<string>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privilege_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "privileges",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    category = table.Column<string>(type: "TEXT", nullable: false),
                    is_admin_only = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privileges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    setting_name = table.Column<string>(type: "TEXT", nullable: false),
                    setting_value = table.Column<string>(type: "TEXT", nullable: false),
                    setting_type = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    category = table.Column<string>(type: "TEXT", nullable: true),
                    is_public = table.Column<bool>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_user_activity",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: true),
                    target_id = table.Column<string>(type: "TEXT", nullable: true),
                    details = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_user_activity", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    contact_person = table.Column<string>(type: "TEXT", nullable: true),
                    email = table.Column<string>(type: "TEXT", nullable: true),
                    phone = table.Column<string>(type: "TEXT", nullable: true),
                    address = table.Column<string>(type: "TEXT", nullable: true),
                    city = table.Column<string>(type: "TEXT", nullable: true),
                    country = table.Column<string>(type: "TEXT", nullable: true),
                    payment_terms = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    rating = table.Column<int>(type: "INTEGER", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppliers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    assigned_by = table.Column<string>(type: "TEXT", nullable: true),
                    assigned_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    device_name = table.Column<string>(type: "TEXT", nullable: true),
                    ip_address = table.Column<string>(type: "TEXT", nullable: true),
                    login_source = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_activity_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    iduser = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    username = table.Column<string>(type: "TEXT", nullable: true),
                    password = table.Column<string>(type: "TEXT", nullable: true),
                    first_name = table.Column<string>(type: "TEXT", nullable: true),
                    last_name = table.Column<string>(type: "TEXT", nullable: true),
                    phone = table.Column<string>(type: "TEXT", nullable: true),
                    country = table.Column<string>(type: "TEXT", nullable: true),
                    poste = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    reset_code = table.Column<string>(type: "TEXT", nullable: true),
                    reset_code_created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    is_verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_blocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_login = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_seen = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.iduser);
                });

            migrationBuilder.CreateTable(
                name: "ventes",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    numero_vente = table.Column<string>(type: "TEXT", nullable: false),
                    date_vente = table.Column<DateTime>(type: "TEXT", nullable: false),
                    client_nom = table.Column<string>(type: "TEXT", nullable: true),
                    client_telephone = table.Column<string>(type: "TEXT", nullable: true),
                    client_email = table.Column<string>(type: "TEXT", nullable: true),
                    montant_total = table.Column<long>(type: "INTEGER", nullable: false),
                    montant_paye = table.Column<long>(type: "INTEGER", nullable: false),
                    montant_restant = table.Column<long>(type: "INTEGER", nullable: false),
                    statut_paiement = table.Column<string>(type: "TEXT", nullable: false),
                    mode_paiement = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    is_avoir_solded = table.Column<bool>(type: "INTEGER", nullable: false),
                    avoir_solded_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    idempotency_key = table.Column<string>(type: "TEXT", nullable: true),
                    caisse_id = table.Column<int>(type: "INTEGER", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    updated_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ventes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ventes_parametres",
                columns: table => new
                {
                    groupe_id = table.Column<string>(type: "TEXT", nullable: false),
                    company_name = table.Column<string>(type: "TEXT", nullable: true),
                    note_under_qr = table.Column<string>(type: "TEXT", nullable: true),
                    logo_path = table.Column<string>(type: "TEXT", nullable: true),
                    qr_code_path = table.Column<string>(type: "TEXT", nullable: true),
                    facture_title = table.Column<string>(type: "TEXT", nullable: true),
                    facture_header_text = table.Column<string>(type: "TEXT", nullable: true),
                    facture_footer_text = table.Column<string>(type: "TEXT", nullable: true),
                    receipt_footer_text = table.Column<string>(type: "TEXT", nullable: true),
                    avoir_notice_title = table.Column<string>(type: "TEXT", nullable: true),
                    avoir_notice_text = table.Column<string>(type: "TEXT", nullable: true),
                    font_family = table.Column<string>(type: "TEXT", nullable: true),
                    font_size = table.Column<int>(type: "INTEGER", nullable: true),
                    show_date = table.Column<bool>(type: "INTEGER", nullable: false),
                    show_document_signatory = table.Column<bool>(type: "INTEGER", nullable: false),
                    document_signatory = table.Column<string>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ventes_parametres", x => x.groupe_id);
                });

            migrationBuilder.CreateTable(
                name: "ventes_user_activity",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    action = table.Column<string>(type: "TEXT", nullable: true),
                    target_id = table.Column<string>(type: "TEXT", nullable: true),
                    details = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ventes_user_activity", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "caisse_transactions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    caisse_id = table.Column<int>(type: "INTEGER", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    montant = table.Column<long>(type: "INTEGER", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    category = table.Column<string>(type: "TEXT", nullable: true),
                    mode_paiement = table.Column<string>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_caisse_transactions", x => x.id);
                    table.ForeignKey(
                        name: "FK_caisse_transactions_caisses_caisse_id",
                        column: x => x.caisse_id,
                        principalTable: "caisses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gestion_user_privileges",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    privilege_id = table.Column<int>(type: "INTEGER", nullable: false),
                    granted_by = table.Column<string>(type: "TEXT", nullable: true),
                    granted_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gestion_user_privileges", x => x.id);
                    table.ForeignKey(
                        name: "FK_gestion_user_privileges_gestion_privileges_privilege_id",
                        column: x => x.privilege_id,
                        principalTable: "gestion_privileges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "option_user_privileges",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    privilege_id = table.Column<int>(type: "INTEGER", nullable: false),
                    granted_by = table.Column<string>(type: "TEXT", nullable: true),
                    granted_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_option_user_privileges", x => x.id);
                    table.ForeignKey(
                        name: "FK_option_user_privileges_option_privileges_privilege_id",
                        column: x => x.privilege_id,
                        principalTable: "option_privileges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_privileges",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    privilege_id = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_privileges", x => x.id);
                    table.ForeignKey(
                        name: "FK_role_privileges_privileges_privilege_id",
                        column: x => x.privilege_id,
                        principalTable: "privileges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_privileges",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    privilege_id = table.Column<int>(type: "INTEGER", nullable: false),
                    granted_by = table.Column<string>(type: "TEXT", nullable: true),
                    granted_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_privileges", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_privileges_privileges_privilege_id",
                        column: x => x.privilege_id,
                        principalTable: "privileges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    sku = table.Column<string>(type: "TEXT", nullable: true),
                    barcode = table.Column<string>(type: "TEXT", nullable: true),
                    category_id = table.Column<string>(type: "TEXT", nullable: true),
                    supplier_id = table.Column<string>(type: "TEXT", nullable: true),
                    quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    minimum_threshold = table.Column<int>(type: "INTEGER", nullable: false),
                    maximum_threshold = table.Column<int>(type: "INTEGER", nullable: true),
                    reorder_quantity = table.Column<int>(type: "INTEGER", nullable: true),
                    cost_price = table.Column<long>(type: "INTEGER", nullable: true),
                    price = table.Column<long>(type: "INTEGER", nullable: false),
                    margin_percentage = table.Column<long>(type: "INTEGER", nullable: true),
                    prix_fixe = table.Column<bool>(type: "INTEGER", nullable: false),
                    weight = table.Column<long>(type: "INTEGER", nullable: true),
                    dimensions_length = table.Column<long>(type: "INTEGER", nullable: true),
                    dimensions_width = table.Column<long>(type: "INTEGER", nullable: true),
                    dimensions_height = table.Column<long>(type: "INTEGER", nullable: true),
                    storage_location = table.Column<string>(type: "TEXT", nullable: true),
                    shelf_life_days = table.Column<int>(type: "INTEGER", nullable: true),
                    expiry_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_featured = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_perishable = table.Column<bool>(type: "INTEGER", nullable: false),
                    requires_serial_number = table.Column<bool>(type: "INTEGER", nullable: false),
                    image_url = table.Column<string>(type: "TEXT", nullable: true),
                    tags = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    deleted_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    updated_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.id);
                    table.ForeignKey(
                        name: "FK_products_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_products_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "groupes",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    nom = table.Column<string>(type: "TEXT", nullable: false),
                    iduser_admin = table.Column<string>(type: "TEXT", nullable: false),
                    gestion_access = table.Column<bool>(type: "INTEGER", nullable: false),
                    prestations_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    prestations_location = table.Column<string>(type: "TEXT", nullable: true),
                    is_blocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    block_reason = table.Column<string>(type: "TEXT", nullable: true),
                    blocked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupes", x => x.id);
                    table.ForeignKey(
                        name: "FK_groupes_users_iduser_admin",
                        column: x => x.iduser_admin,
                        principalTable: "users",
                        principalColumn: "iduser",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "paiements_ventes",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    vente_id = table.Column<string>(type: "TEXT", nullable: false),
                    montant = table.Column<long>(type: "INTEGER", nullable: false),
                    mode_paiement = table.Column<string>(type: "TEXT", nullable: false),
                    date_paiement = table.Column<DateTime>(type: "TEXT", nullable: false),
                    reference = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paiements_ventes", x => x.id);
                    table.ForeignKey(
                        name: "FK_paiements_ventes_ventes_vente_id",
                        column: x => x.vente_id,
                        principalTable: "ventes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ventes_items",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    vente_id = table.Column<string>(type: "TEXT", nullable: false),
                    product_id = table.Column<string>(type: "TEXT", nullable: true),
                    nom_produit = table.Column<string>(type: "TEXT", nullable: false),
                    quantite = table.Column<int>(type: "INTEGER", nullable: false),
                    prix_unitaire = table.Column<long>(type: "INTEGER", nullable: false),
                    prix_total = table.Column<long>(type: "INTEGER", nullable: false),
                    discount = table.Column<long>(type: "INTEGER", nullable: false),
                    discount_type = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ventes_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_ventes_items_ventes_vente_id",
                        column: x => x.vente_id,
                        principalTable: "ventes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_history",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    product_id = table.Column<string>(type: "TEXT", nullable: false),
                    movement_type = table.Column<string>(type: "TEXT", nullable: false),
                    previous_quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    quantity_changed = table.Column<int>(type: "INTEGER", nullable: false),
                    new_quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    reference_id = table.Column<string>(type: "TEXT", nullable: true),
                    reference_type = table.Column<string>(type: "TEXT", nullable: true),
                    unit_cost = table.Column<long>(type: "INTEGER", nullable: true),
                    total_cost = table.Column<long>(type: "INTEGER", nullable: true),
                    reason = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    location = table.Column<string>(type: "TEXT", nullable: true),
                    user_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_history_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "groupe_membres",
                columns: table => new
                {
                    idgroupe = table.Column<string>(type: "TEXT", nullable: false),
                    iduser = table.Column<string>(type: "TEXT", nullable: false),
                    joined_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupe_membres", x => new { x.idgroupe, x.iduser });
                    table.ForeignKey(
                        name: "FK_groupe_membres_groupes_idgroupe",
                        column: x => x.idgroupe,
                        principalTable: "groupes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_groupe_membres_users_iduser",
                        column: x => x.iduser,
                        principalTable: "users",
                        principalColumn: "iduser",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_caisse_transactions_caisse_id",
                table: "caisse_transactions",
                column: "caisse_id");

            migrationBuilder.CreateIndex(
                name: "IX_caisse_transactions_group_id_created_at",
                table: "caisse_transactions",
                columns: new[] { "group_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_caisses_group_id",
                table: "caisses",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "IX_caisses_group_id_user_id",
                table: "caisses",
                columns: new[] { "group_id", "user_id" },
                unique: true,
                filter: "status = 'open'");

            migrationBuilder.CreateIndex(
                name: "IX_caisses_status",
                table: "caisses",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_categories_group_id_name",
                table: "categories",
                columns: new[] { "group_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_categories_parent_id",
                table: "categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "IX_clients_group_id",
                table: "clients",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "IX_gestion_privilege_audit_group_id_created_at",
                table: "gestion_privilege_audit",
                columns: new[] { "group_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_gestion_privilege_audit_module",
                table: "gestion_privilege_audit",
                column: "module");

            migrationBuilder.CreateIndex(
                name: "IX_gestion_privileges_module",
                table: "gestion_privileges",
                column: "module");

            migrationBuilder.CreateIndex(
                name: "IX_gestion_privileges_name",
                table: "gestion_privileges",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gestion_user_privileges_privilege_id",
                table: "gestion_user_privileges",
                column: "privilege_id");

            migrationBuilder.CreateIndex(
                name: "IX_gestion_user_privileges_user_id_group_id_privilege_id",
                table: "gestion_user_privileges",
                columns: new[] { "user_id", "group_id", "privilege_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gestion_user_roles_user_id_group_id_module",
                table: "gestion_user_roles",
                columns: new[] { "user_id", "group_id", "module" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_groupe_membres_iduser",
                table: "groupe_membres",
                column: "iduser");

            migrationBuilder.CreateIndex(
                name: "IX_groupe_sessions_expires_at",
                table: "groupe_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_groupe_sessions_session_token",
                table: "groupe_sessions",
                column: "session_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_groupe_sessions_user_id",
                table: "groupe_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_groupes_iduser_admin",
                table: "groupes",
                column: "iduser_admin");

            migrationBuilder.CreateIndex(
                name: "IX_option_privilege_audit_group_id_created_at",
                table: "option_privilege_audit",
                columns: new[] { "group_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_option_privileges_module",
                table: "option_privileges",
                column: "module");

            migrationBuilder.CreateIndex(
                name: "IX_option_privileges_name",
                table: "option_privileges",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_option_user_privileges_privilege_id",
                table: "option_user_privileges",
                column: "privilege_id");

            migrationBuilder.CreateIndex(
                name: "IX_option_user_privileges_user_id_group_id_privilege_id",
                table: "option_user_privileges",
                columns: new[] { "user_id", "group_id", "privilege_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_paiements_ventes_vente_id",
                table: "paiements_ventes",
                column: "vente_id");

            migrationBuilder.CreateIndex(
                name: "IX_password_history_iduser",
                table: "password_history",
                column: "iduser");

            migrationBuilder.CreateIndex(
                name: "IX_privilege_audit_group_id_created_at",
                table: "privilege_audit",
                columns: new[] { "group_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_privileges_category",
                table: "privileges",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "IX_privileges_name",
                table: "privileges",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_products_category_id",
                table: "products",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_group_id",
                table: "products",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "IX_products_group_id_barcode",
                table: "products",
                columns: new[] { "group_id", "barcode" },
                unique: true,
                filter: "barcode IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_products_group_id_sku",
                table: "products",
                columns: new[] { "group_id", "sku" },
                unique: true,
                filter: "sku IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_products_supplier_id",
                table: "products",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_privileges_privilege_id",
                table: "role_privileges",
                column: "privilege_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_privileges_role_privilege_id",
                table: "role_privileges",
                columns: new[] { "role", "privilege_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_history_group_id_created_at",
                table: "stock_history",
                columns: new[] { "group_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_history_product_id",
                table: "stock_history",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_settings_group_id_setting_name",
                table: "stock_settings",
                columns: new[] { "group_id", "setting_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_user_activity_group_id_created_at",
                table: "stock_user_activity",
                columns: new[] { "group_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_group_id",
                table: "suppliers",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_privileges_privilege_id",
                table: "user_privileges",
                column: "privilege_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_privileges_user_id_group_id_privilege_id",
                table: "user_privileges",
                columns: new[] { "user_id", "group_id", "privilege_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_user_id_group_id",
                table: "user_roles",
                columns: new[] { "user_id", "group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_sessions_user_id",
                table: "user_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ventes_group_id_date_vente",
                table: "ventes",
                columns: new[] { "group_id", "date_vente" });

            migrationBuilder.CreateIndex(
                name: "IX_ventes_group_id_idempotency_key",
                table: "ventes",
                columns: new[] { "group_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ventes_group_id_numero_vente",
                table: "ventes",
                columns: new[] { "group_id", "numero_vente" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ventes_statut_paiement",
                table: "ventes",
                column: "statut_paiement");

            migrationBuilder.CreateIndex(
                name: "IX_ventes_items_product_id",
                table: "ventes_items",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_ventes_items_vente_id",
                table: "ventes_items",
                column: "vente_id");

            migrationBuilder.CreateIndex(
                name: "IX_ventes_user_activity_group_id_created_at",
                table: "ventes_user_activity",
                columns: new[] { "group_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "caisse_transactions");

            migrationBuilder.DropTable(
                name: "clients");

            migrationBuilder.DropTable(
                name: "gestion_privilege_audit");

            migrationBuilder.DropTable(
                name: "gestion_user_privileges");

            migrationBuilder.DropTable(
                name: "gestion_user_roles");

            migrationBuilder.DropTable(
                name: "groupe_membres");

            migrationBuilder.DropTable(
                name: "groupe_sessions");

            migrationBuilder.DropTable(
                name: "option_privilege_audit");

            migrationBuilder.DropTable(
                name: "option_user_privileges");

            migrationBuilder.DropTable(
                name: "paiements_ventes");

            migrationBuilder.DropTable(
                name: "password_history");

            migrationBuilder.DropTable(
                name: "privilege_audit");

            migrationBuilder.DropTable(
                name: "role_privileges");

            migrationBuilder.DropTable(
                name: "stock_history");

            migrationBuilder.DropTable(
                name: "stock_settings");

            migrationBuilder.DropTable(
                name: "stock_user_activity");

            migrationBuilder.DropTable(
                name: "user_privileges");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "user_sessions");

            migrationBuilder.DropTable(
                name: "ventes_items");

            migrationBuilder.DropTable(
                name: "ventes_parametres");

            migrationBuilder.DropTable(
                name: "ventes_user_activity");

            migrationBuilder.DropTable(
                name: "caisses");

            migrationBuilder.DropTable(
                name: "gestion_privileges");

            migrationBuilder.DropTable(
                name: "groupes");

            migrationBuilder.DropTable(
                name: "option_privileges");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "privileges");

            migrationBuilder.DropTable(
                name: "ventes");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "suppliers");
        }
    }
}
