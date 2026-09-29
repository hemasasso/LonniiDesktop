using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptTemplatesAndSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "company_address",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "company_email",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "company_legal_info",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "company_phone",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "facture_hidden_sections",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "facture_template",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legal_footer_text",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_hidden_sections",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_template",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "tva_rate",
                table: "ventes_parametres",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "company_address",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "company_email",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "company_legal_info",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "company_phone",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "facture_hidden_sections",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "facture_template",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "legal_footer_text",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "receipt_hidden_sections",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "receipt_template",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "tva_rate",
                table: "ventes_parametres");
        }
    }
}
