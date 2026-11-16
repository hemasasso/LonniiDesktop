using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "effort_guerre_label",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prelevement_label",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "effort_guerre_label",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "prelevement_label",
                table: "ventes_parametres");
        }
    }
}
