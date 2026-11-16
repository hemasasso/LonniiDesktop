using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPageOrientation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "emballages_unite",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "page_orientation",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "emballages_unite",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "page_orientation",
                table: "ventes_parametres");
        }
    }
}
