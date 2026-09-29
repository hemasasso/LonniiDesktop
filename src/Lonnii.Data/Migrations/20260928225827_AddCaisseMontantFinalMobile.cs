using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCaisseMontantFinalMobile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "montant_final_mobile",
                table: "caisses",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "montant_final_mobile",
                table: "caisses");
        }
    }
}
