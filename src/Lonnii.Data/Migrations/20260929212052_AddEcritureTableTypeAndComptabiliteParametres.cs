using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEcritureTableTypeAndComptabiliteParametres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_bilan_ecritures_bilan_comptes_compte_id",
                table: "bilan_ecritures");

            migrationBuilder.AddColumn<string>(
                name: "table_type",
                table: "bilan_ecritures",
                type: "TEXT",
                nullable: false,
                defaultValue: "bilan");

            migrationBuilder.CreateTable(
                name: "comptabilite_parametres",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    calcul_automatique = table.Column<bool>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comptabilite_parametres", x => x.group_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comptabilite_parametres");

            migrationBuilder.DropColumn(
                name: "table_type",
                table: "bilan_ecritures");

            migrationBuilder.AddForeignKey(
                name: "FK_bilan_ecritures_bilan_comptes_compte_id",
                table: "bilan_ecritures",
                column: "compte_id",
                principalTable: "bilan_comptes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
