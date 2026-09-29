using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductTypeIsStockType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "type_produit",
                table: "products",
                newName: "stock_type");

            migrationBuilder.AlterColumn<string>(
                name: "stock_type",
                table: "products",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "marchandise",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32,
                oldDefaultValue: "produit_fini");

            // Until now "produit fini" was the only sellable type, so every sellable product
            // carries it without anyone having chosen it - and the Bilan valued them all as
            // marchandises. Reclassifying keeps the Bilan unchanged; a shop that really
            // manufactures can set produit fini back on those products.
            migrationBuilder.Sql("UPDATE products SET stock_type = 'marchandise' WHERE stock_type = 'produit_fini';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE products SET stock_type = 'produit_fini' WHERE stock_type = 'marchandise';");

            migrationBuilder.RenameColumn(
                name: "stock_type",
                table: "products",
                newName: "type_produit");

            migrationBuilder.AlterColumn<string>(
                name: "type_produit",
                table: "products",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "produit_fini",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 32,
                oldDefaultValue: "marchandise");
        }
    }
}
