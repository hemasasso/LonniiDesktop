using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupMaxDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 3, not the 0 EF generates: 0 would read as "no machines allowed" and lock
            // every existing workspace out on upgrade. The real value arrives from the
            // dashboard at the first licence sync.
            migrationBuilder.AddColumn<int>(
                name: "max_devices",
                table: "groupes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "max_devices",
                table: "groupes");
        }
    }
}
