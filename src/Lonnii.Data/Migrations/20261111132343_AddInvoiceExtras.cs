using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceExtras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "fractions_enabled",
                table: "ventes_parametres",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "livraison_enabled",
                table: "ventes_parametres",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "prelevement_rate",
                table: "ventes_parametres",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "prix_fraction",
                table: "ventes_items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "quarts",
                table: "ventes_items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "consigne_amount",
                table: "ventes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "effort_guerre_amount",
                table: "ventes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "livraison_amount",
                table: "ventes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "prelevement_amount",
                table: "ventes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "prelevement_rate",
                table: "ventes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "consigne_montant",
                table: "products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "effort_guerre",
                table: "products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "emballage_recuperable",
                table: "products",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "fraction_ouverte",
                table: "products",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "prix_demi",
                table: "products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "prix_quart",
                table: "products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "prix_trois_quarts",
                table: "products",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "fractions_enabled",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "livraison_enabled",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "prelevement_rate",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "prix_fraction",
                table: "ventes_items");

            migrationBuilder.DropColumn(
                name: "quarts",
                table: "ventes_items");

            migrationBuilder.DropColumn(
                name: "consigne_amount",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "effort_guerre_amount",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "livraison_amount",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "prelevement_amount",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "prelevement_rate",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "consigne_montant",
                table: "products");

            migrationBuilder.DropColumn(
                name: "effort_guerre",
                table: "products");

            migrationBuilder.DropColumn(
                name: "emballage_recuperable",
                table: "products");

            migrationBuilder.DropColumn(
                name: "fraction_ouverte",
                table: "products");

            migrationBuilder.DropColumn(
                name: "prix_demi",
                table: "products");

            migrationBuilder.DropColumn(
                name: "prix_quart",
                table: "products");

            migrationBuilder.DropColumn(
                name: "prix_trois_quarts",
                table: "products");
        }
    }
}
