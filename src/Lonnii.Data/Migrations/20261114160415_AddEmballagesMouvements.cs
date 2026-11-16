using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmballagesMouvements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "emballages_mouvements",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    product_id = table.Column<string>(type: "TEXT", nullable: false),
                    nom_produit = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    type = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    quantite = table.Column<long>(type: "INTEGER", nullable: false),
                    montant = table.Column<long>(type: "INTEGER", nullable: true),
                    mode_paiement = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    supplier_id = table.Column<string>(type: "TEXT", nullable: true),
                    supplier_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_emballages_mouvements", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_emballages_mouvements_group_id_product_id",
                table: "emballages_mouvements",
                columns: new[] { "group_id", "product_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "emballages_mouvements");
        }
    }
}
