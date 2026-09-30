using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVenteMixteAndMarge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "unite",
                table: "ventes_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "facteur_conversion",
                table: "products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "prix_vente_detail",
                table: "products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "unite_vente",
                table: "products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "vente_mixte",
                table: "products",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "unite",
                table: "ventes_items");

            migrationBuilder.DropColumn(
                name: "facteur_conversion",
                table: "products");

            migrationBuilder.DropColumn(
                name: "prix_vente_detail",
                table: "products");

            migrationBuilder.DropColumn(
                name: "unite_vente",
                table: "products");

            migrationBuilder.DropColumn(
                name: "vente_mixte",
                table: "products");
        }
    }
}
