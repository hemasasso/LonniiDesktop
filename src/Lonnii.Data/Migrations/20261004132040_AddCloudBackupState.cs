using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudBackupState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cloud_backup_state",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    epoch = table.Column<string>(type: "TEXT", nullable: true),
                    device_token = table.Column<string>(type: "TEXT", nullable: true),
                    device_id = table.Column<string>(type: "TEXT", nullable: true),
                    last_attempt_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_success_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", nullable: true),
                    last_record_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_image_count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_backup_state", x => x.group_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cloud_backup_state");
        }
    }
}
