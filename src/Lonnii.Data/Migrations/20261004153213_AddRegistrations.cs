using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "approval_status",
                table: "groupes",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "approved");

            migrationBuilder.CreateTable(
                name: "registration_requests",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    shop_name = table.Column<string>(type: "TEXT", nullable: false),
                    email = table.Column<string>(type: "TEXT", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: false),
                    device_id = table.Column<string>(type: "TEXT", nullable: false),
                    device_name = table.Column<string>(type: "TEXT", nullable: true),
                    code_hash = table.Column<string>(type: "TEXT", nullable: false),
                    code_expires_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ip_address = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registration_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_registration_requests_email",
                table: "registration_requests",
                column: "email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "registration_requests");

            migrationBuilder.DropColumn(
                name: "approval_status",
                table: "groupes");
        }
    }
}
