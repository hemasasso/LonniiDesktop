using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDevicesAndGroupIsDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_deleted",
                table: "groupes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    device_id = table.Column<string>(type: "TEXT", nullable: false),
                    device_name = table.Column<string>(type: "TEXT", nullable: true),
                    platform = table.Column<string>(type: "TEXT", nullable: false),
                    app_version = table.Column<string>(type: "TEXT", nullable: true),
                    last_ip_address = table.Column<string>(type: "TEXT", nullable: true),
                    registered_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    revoked_reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.id);
                    table.ForeignKey(
                        name: "FK_devices_groupes_group_id",
                        column: x => x.group_id,
                        principalTable: "groupes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_devices_group_id",
                table: "devices",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "IX_devices_group_id_device_id",
                table: "devices",
                columns: new[] { "group_id", "device_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropColumn(
                name: "is_deleted",
                table: "groupes");
        }
    }
}
