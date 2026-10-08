using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class MatchLiveCaisseClientProductColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "vente_mixte",
                table: "products",
                newName: "allow_mixed_sales");

            migrationBuilder.RenameColumn(
                name: "unite_vente",
                table: "products",
                newName: "sell_unit");

            migrationBuilder.RenameColumn(
                name: "prix_vente_detail",
                table: "products",
                newName: "detail_unit_price");

            migrationBuilder.RenameColumn(
                name: "facteur_conversion",
                table: "products",
                newName: "conversion_factor");

            migrationBuilder.RenameColumn(
                name: "ville",
                table: "clients",
                newName: "city");

            migrationBuilder.RenameColumn(
                name: "telephone",
                table: "clients",
                newName: "phone");

            migrationBuilder.RenameColumn(
                name: "nom",
                table: "clients",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "adresse",
                table: "clients",
                newName: "address");

            migrationBuilder.RenameColumn(
                name: "ecart_resolution_note",
                table: "caisses",
                newName: "ecart_resolution_notes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "sell_unit",
                table: "products",
                newName: "unite_vente");

            migrationBuilder.RenameColumn(
                name: "detail_unit_price",
                table: "products",
                newName: "prix_vente_detail");

            migrationBuilder.RenameColumn(
                name: "conversion_factor",
                table: "products",
                newName: "facteur_conversion");

            migrationBuilder.RenameColumn(
                name: "allow_mixed_sales",
                table: "products",
                newName: "vente_mixte");

            migrationBuilder.RenameColumn(
                name: "phone",
                table: "clients",
                newName: "telephone");

            migrationBuilder.RenameColumn(
                name: "name",
                table: "clients",
                newName: "nom");

            migrationBuilder.RenameColumn(
                name: "city",
                table: "clients",
                newName: "ville");

            migrationBuilder.RenameColumn(
                name: "address",
                table: "clients",
                newName: "adresse");

            migrationBuilder.RenameColumn(
                name: "ecart_resolution_notes",
                table: "caisses",
                newName: "ecart_resolution_note");
        }
    }
}
