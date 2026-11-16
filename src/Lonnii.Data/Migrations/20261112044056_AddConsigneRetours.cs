using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConsigneRetours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "consigne",
                table: "ventes_items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "emballages_pris",
                table: "ventes_items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "consigne_rendue",
                table: "ventes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mode_paiement",
                table: "emballages_retours",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "montant",
                table: "emballages_retours",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nom_produit",
                table: "emballages_retours",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "product_id",
                table: "emballages_retours",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vente_id",
                table: "emballages_retours",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consigne",
                table: "ventes_items");

            migrationBuilder.DropColumn(
                name: "emballages_pris",
                table: "ventes_items");

            migrationBuilder.DropColumn(
                name: "consigne_rendue",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "mode_paiement",
                table: "emballages_retours");

            migrationBuilder.DropColumn(
                name: "montant",
                table: "emballages_retours");

            migrationBuilder.DropColumn(
                name: "nom_produit",
                table: "emballages_retours");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "emballages_retours");

            migrationBuilder.DropColumn(
                name: "vente_id",
                table: "emballages_retours");
        }
    }
}
