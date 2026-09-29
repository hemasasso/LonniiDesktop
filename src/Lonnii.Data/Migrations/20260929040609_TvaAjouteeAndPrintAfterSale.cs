using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class TvaAjouteeAndPrintAfterSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "facture_print_after_sale",
                table: "ventes_parametres",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "receipt_print_after_sale",
                table: "ventes_parametres",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tva_mode",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "tva_amount",
                table: "ventes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "tva_rate",
                table: "ventes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "facture_print_after_sale",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "receipt_print_after_sale",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "tva_mode",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "tva_amount",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "tva_rate",
                table: "ventes");
        }
    }
}
