using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class VenteLibreIsIllimite : Migration
    {
        /// <inheritdoc />
        /// <summary>Products saved Vente Libre without "stock illimité" (see
        /// LonniiDbContext.ApplyProductRules, which keeps new saves right).</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? "UPDATE products SET stock_illimite = TRUE WHERE vente_libre AND NOT stock_illimite;"
                : "UPDATE products SET stock_illimite = 1 WHERE vente_libre = 1 AND stock_illimite = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: which rows were wrong before is not kept, and none should be again.
        }
    }
}
