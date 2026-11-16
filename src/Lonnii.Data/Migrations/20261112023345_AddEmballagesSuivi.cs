using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmballagesSuivi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "emballages_suivi",
                table: "ventes_parametres",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "emballages_pris",
                table: "ventes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "emballages_rendus",
                table: "ventes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "emballages_suivi",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "emballages_pris",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "emballages_rendus",
                table: "ventes");
        }
    }
}
