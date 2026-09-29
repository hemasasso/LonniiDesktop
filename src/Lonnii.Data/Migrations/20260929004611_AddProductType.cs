using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "type_produit",
                table: "products",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "produit_fini");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "type_produit",
                table: "products");
        }
    }
}
