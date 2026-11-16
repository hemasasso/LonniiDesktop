using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmballagesRetours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "emballages_retours",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    client_nom = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    client_telephone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    client_email = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    quantite = table.Column<int>(type: "INTEGER", nullable: false),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_emballages_retours", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_emballages_retours_group_id",
                table: "emballages_retours",
                column: "group_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "emballages_retours");
        }
    }
}
