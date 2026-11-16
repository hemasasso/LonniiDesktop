using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmballagesCredit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "emballages_credit",
                table: "ventes_items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "emballages_credit",
                table: "ventes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "emballages_credit_restant",
                table: "ventes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "emballages_credit",
                table: "ventes_items");

            migrationBuilder.DropColumn(
                name: "emballages_credit",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "emballages_credit_restant",
                table: "ventes");
        }
    }
}
