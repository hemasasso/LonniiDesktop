using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmballagesParQuart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Emballage counts become decimal (1.5 = 1 caisse and a half). On SQLite a decimal is
            // stored in hundredths, so the whole counts already saved are scaled once.
            migrationBuilder.Sql("UPDATE ventes SET emballages_pris = emballages_pris * 100 WHERE emballages_pris IS NOT NULL;");
            migrationBuilder.Sql("UPDATE ventes SET emballages_rendus = emballages_rendus * 100 WHERE emballages_rendus IS NOT NULL;");
            migrationBuilder.Sql("UPDATE ventes_items SET emballages_pris = emballages_pris * 100 WHERE emballages_pris IS NOT NULL;");
            migrationBuilder.Sql("UPDATE emballages_retours SET quantite = quantite * 100;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE ventes SET emballages_pris = emballages_pris / 100 WHERE emballages_pris IS NOT NULL;");
            migrationBuilder.Sql("UPDATE ventes SET emballages_rendus = emballages_rendus / 100 WHERE emballages_rendus IS NOT NULL;");
            migrationBuilder.Sql("UPDATE ventes_items SET emballages_pris = emballages_pris / 100 WHERE emballages_pris IS NOT NULL;");
            migrationBuilder.Sql("UPDATE emballages_retours SET quantite = quantite / 100;");
        }
    }
}
