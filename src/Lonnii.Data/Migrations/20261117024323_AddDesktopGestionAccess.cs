using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDesktopGestionAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "desktop_gestion_access",
                table: "groupes",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // What the desktop app showed until now: the licence-synced value already in gestion_access.
            // On a shop's computer both columns then move together at each check-in (LicenceGuard).
            migrationBuilder.Sql("UPDATE groupes SET desktop_gestion_access = gestion_access;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "desktop_gestion_access",
                table: "groupes");
        }
    }
}
