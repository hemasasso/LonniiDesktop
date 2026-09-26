using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "charges",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    groupe_id = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: false),
                    montant = table.Column<long>(type: "INTEGER", nullable: false),
                    type_charge = table.Column<string>(type: "TEXT", nullable: false),
                    categorie = table.Column<string>(type: "TEXT", nullable: false),
                    date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    is_recurring = table.Column<bool>(type: "INTEGER", nullable: false),
                    recurring_end_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    recurring_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    recurring_source_id = table.Column<int>(type: "INTEGER", nullable: true),
                    recurring_day = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_charges", x => x.id);
                    table.ForeignKey(
                        name: "FK_charges_charges_recurring_source_id",
                        column: x => x.recurring_source_id,
                        principalTable: "charges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "charges_categories",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    groupe_id = table.Column<string>(type: "TEXT", nullable: false),
                    nom = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    color = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_charges_categories", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_charges_groupe_id",
                table: "charges",
                column: "groupe_id");

            migrationBuilder.CreateIndex(
                name: "IX_charges_groupe_id_categorie",
                table: "charges",
                columns: new[] { "groupe_id", "categorie" });

            migrationBuilder.CreateIndex(
                name: "IX_charges_groupe_id_date",
                table: "charges",
                columns: new[] { "groupe_id", "date" });

            migrationBuilder.CreateIndex(
                name: "IX_charges_is_recurring_recurring_active",
                table: "charges",
                columns: new[] { "is_recurring", "recurring_active" });

            migrationBuilder.CreateIndex(
                name: "IX_charges_recurring_source_id",
                table: "charges",
                column: "recurring_source_id");

            migrationBuilder.CreateIndex(
                name: "IX_charges_categories_groupe_id_nom",
                table: "charges_categories",
                columns: new[] { "groupe_id", "nom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "charges");

            migrationBuilder.DropTable(
                name: "charges_categories");
        }
    }
}
