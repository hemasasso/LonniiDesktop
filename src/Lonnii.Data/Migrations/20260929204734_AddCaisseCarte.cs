using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCaisseCarte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "montant_final_carte",
                table: "caisses",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "montant_initial_carte",
                table: "caisses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "montant_final_carte",
                table: "caisses");

            migrationBuilder.DropColumn(
                name: "montant_initial_carte",
                table: "caisses");
        }
    }
}
