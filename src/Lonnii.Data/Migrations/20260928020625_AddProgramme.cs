using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProgramme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "programme_announcements",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: true),
                    titre = table.Column<string>(type: "TEXT", nullable: false),
                    message = table.Column<string>(type: "TEXT", nullable: false),
                    date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programme_announcements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "programme_entries",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    user_id = table.Column<string>(type: "TEXT", nullable: false),
                    date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    heure_debut = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    heure_fin = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    note = table.Column<string>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_programme_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_programme_announcements_group_id_date",
                table: "programme_announcements",
                columns: new[] { "group_id", "date" });

            migrationBuilder.CreateIndex(
                name: "IX_programme_announcements_group_id_user_id_date",
                table: "programme_announcements",
                columns: new[] { "group_id", "user_id", "date" });

            migrationBuilder.CreateIndex(
                name: "IX_programme_entries_group_id_date",
                table: "programme_entries",
                columns: new[] { "group_id", "date" });

            migrationBuilder.CreateIndex(
                name: "IX_programme_entries_group_id_user_id_date",
                table: "programme_entries",
                columns: new[] { "group_id", "user_id", "date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "programme_announcements");

            migrationBuilder.DropTable(
                name: "programme_entries");
        }
    }
}
