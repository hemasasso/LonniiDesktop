using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAmortissementBilan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bilan_comptes",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    groupe_id = table.Column<string>(type: "TEXT", nullable: false),
                    numero_compte = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    libelle = table.Column<string>(type: "TEXT", nullable: false),
                    type_compte = table.Column<string>(type: "TEXT", nullable: false),
                    sous_type = table.Column<string>(type: "TEXT", nullable: true),
                    solde = table.Column<long>(type: "INTEGER", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    is_system = table.Column<bool>(type: "INTEGER", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bilan_comptes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "immobilisations",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    groupe_id = table.Column<string>(type: "TEXT", nullable: false),
                    nom = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    categorie = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    date_acquisition = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    valeur_acquisition = table.Column<long>(type: "INTEGER", nullable: false),
                    valeur_residuelle = table.Column<long>(type: "INTEGER", nullable: true),
                    duree_amortissement = table.Column<int>(type: "INTEGER", nullable: false),
                    methode_amortissement = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    taux_degressif = table.Column<long>(type: "INTEGER", nullable: true),
                    date_mise_en_service = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    statut = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    date_cession = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    valeur_cession = table.Column<long>(type: "INTEGER", nullable: true),
                    motif_sortie = table.Column<string>(type: "TEXT", nullable: true),
                    numero_inventaire = table.Column<string>(type: "TEXT", nullable: true),
                    localisation = table.Column<string>(type: "TEXT", nullable: true),
                    fournisseur = table.Column<string>(type: "TEXT", nullable: true),
                    numero_facture = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_immobilisations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "resultat_comptes",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    groupe_id = table.Column<string>(type: "TEXT", nullable: false),
                    numero_compte = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    libelle = table.Column<string>(type: "TEXT", nullable: false),
                    type_compte = table.Column<string>(type: "TEXT", nullable: false),
                    sous_type = table.Column<string>(type: "TEXT", nullable: true),
                    solde = table.Column<long>(type: "INTEGER", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    is_system = table.Column<bool>(type: "INTEGER", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resultat_comptes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_snapshots",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    annee = table.Column<int>(type: "INTEGER", nullable: false),
                    stock_value_debut = table.Column<long>(type: "INTEGER", nullable: true),
                    snapshot_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_snapshots", x => new { x.group_id, x.annee });
                });

            migrationBuilder.CreateTable(
                name: "bilan_ecritures",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    groupe_id = table.Column<string>(type: "TEXT", nullable: false),
                    compte_id = table.Column<int>(type: "INTEGER", nullable: false),
                    date_ecriture = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    libelle = table.Column<string>(type: "TEXT", nullable: false),
                    montant_debit = table.Column<long>(type: "INTEGER", nullable: true),
                    montant_credit = table.Column<long>(type: "INTEGER", nullable: true),
                    reference = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bilan_ecritures", x => x.id);
                    table.ForeignKey(
                        name: "FK_bilan_ecritures_bilan_comptes_compte_id",
                        column: x => x.compte_id,
                        principalTable: "bilan_comptes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "amortissement_echeances",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    immobilisation_id = table.Column<int>(type: "INTEGER", nullable: false),
                    groupe_id = table.Column<string>(type: "TEXT", nullable: false),
                    annee = table.Column<int>(type: "INTEGER", nullable: false),
                    numero_annee = table.Column<int>(type: "INTEGER", nullable: false),
                    date_debut = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    date_fin = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    valeur_debut_periode = table.Column<long>(type: "INTEGER", nullable: false),
                    dotation_annuelle = table.Column<long>(type: "INTEGER", nullable: false),
                    amortissement_cumule = table.Column<long>(type: "INTEGER", nullable: false),
                    valeur_nette_comptable = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_amortissement_echeances", x => x.id);
                    table.ForeignKey(
                        name: "FK_amortissement_echeances_immobilisations_immobilisation_id",
                        column: x => x.immobilisation_id,
                        principalTable: "immobilisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_amortissement_echeances_annee",
                table: "amortissement_echeances",
                column: "annee");

            migrationBuilder.CreateIndex(
                name: "IX_amortissement_echeances_groupe_id",
                table: "amortissement_echeances",
                column: "groupe_id");

            migrationBuilder.CreateIndex(
                name: "IX_amortissement_echeances_immobilisation_id",
                table: "amortissement_echeances",
                column: "immobilisation_id");

            migrationBuilder.CreateIndex(
                name: "IX_bilan_comptes_groupe_id",
                table: "bilan_comptes",
                column: "groupe_id");

            migrationBuilder.CreateIndex(
                name: "IX_bilan_comptes_type_compte",
                table: "bilan_comptes",
                column: "type_compte");

            migrationBuilder.CreateIndex(
                name: "IX_bilan_ecritures_compte_id",
                table: "bilan_ecritures",
                column: "compte_id");

            migrationBuilder.CreateIndex(
                name: "IX_bilan_ecritures_date_ecriture",
                table: "bilan_ecritures",
                column: "date_ecriture");

            migrationBuilder.CreateIndex(
                name: "IX_bilan_ecritures_groupe_id",
                table: "bilan_ecritures",
                column: "groupe_id");

            migrationBuilder.CreateIndex(
                name: "IX_immobilisations_categorie",
                table: "immobilisations",
                column: "categorie");

            migrationBuilder.CreateIndex(
                name: "IX_immobilisations_groupe_id",
                table: "immobilisations",
                column: "groupe_id");

            migrationBuilder.CreateIndex(
                name: "IX_immobilisations_statut",
                table: "immobilisations",
                column: "statut");

            migrationBuilder.CreateIndex(
                name: "IX_resultat_comptes_groupe_id",
                table: "resultat_comptes",
                column: "groupe_id");

            migrationBuilder.CreateIndex(
                name: "IX_resultat_comptes_type_compte",
                table: "resultat_comptes",
                column: "type_compte");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "amortissement_echeances");

            migrationBuilder.DropTable(
                name: "bilan_ecritures");

            migrationBuilder.DropTable(
                name: "resultat_comptes");

            migrationBuilder.DropTable(
                name: "stock_snapshots");

            migrationBuilder.DropTable(
                name: "immobilisations");

            migrationBuilder.DropTable(
                name: "bilan_comptes");
        }
    }
}
