using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServeurs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "serveur_enabled",
                table: "ventes_parametres",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "serveur_label",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "serveur_label_plural",
                table: "ventes_parametres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "serveur_id",
                table: "ventes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "serveur_nom",
                table: "ventes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "serveurs",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    nom = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    telephone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_serveurs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_serveurs_group_id",
                table: "serveurs",
                column: "group_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "serveurs");

            migrationBuilder.DropColumn(
                name: "serveur_enabled",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "serveur_label",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "serveur_label_plural",
                table: "ventes_parametres");

            migrationBuilder.DropColumn(
                name: "serveur_id",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "serveur_nom",
                table: "ventes");
        }
    }
}
