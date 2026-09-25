using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOfflineGracePeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "last_licence_check_at",
                table: "groupes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_offline_days",
                table: "groupes",
                type: "INTEGER",
                nullable: false,
                // 7, not the 0 EF generates: zero would mean "no offline days allowed" and
                // stop every existing workspace the moment the check goes live.
                defaultValue: 7);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_licence_check_at",
                table: "groupes");

            migrationBuilder.DropColumn(
                name: "max_offline_days",
                table: "groupes");
        }
    }
}
