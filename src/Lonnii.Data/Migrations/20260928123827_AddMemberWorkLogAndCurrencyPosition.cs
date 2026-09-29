using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberWorkLogAndCurrencyPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "currency_before",
                table: "groupes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "member_work_log",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    group_id = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    session_date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    login_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    logout_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    duration_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    session_token = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ip_address = table.Column<string>(type: "TEXT", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    current_module = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    device_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_work_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_member_work_log_group_id_session_date",
                table: "member_work_log",
                columns: new[] { "group_id", "session_date" });

            migrationBuilder.CreateIndex(
                name: "IX_member_work_log_user_id_group_id_session_date",
                table: "member_work_log",
                columns: new[] { "user_id", "group_id", "session_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "member_work_log");

            migrationBuilder.DropColumn(
                name: "currency_before",
                table: "groupes");
        }
    }
}
