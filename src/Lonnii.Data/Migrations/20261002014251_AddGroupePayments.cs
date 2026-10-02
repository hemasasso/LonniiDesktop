using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "groupe_payments",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    client_name = table.Column<string>(type: "TEXT", nullable: true),
                    caissier_name = table.Column<string>(type: "TEXT", nullable: true),
                    facture_ids = table.Column<string>(type: "TEXT", nullable: false),
                    factures_data = table.Column<string>(type: "TEXT", nullable: false),
                    total_amount = table.Column<long>(type: "INTEGER", nullable: false),
                    montant_paye = table.Column<long>(type: "INTEGER", nullable: false),
                    avoir_amount = table.Column<long>(type: "INTEGER", nullable: false),
                    partial_change_given = table.Column<long>(type: "INTEGER", nullable: false),
                    mode_paiement = table.Column<string>(type: "TEXT", nullable: false),
                    is_avoir_solded = table.Column<bool>(type: "INTEGER", nullable: false),
                    avoir_solded_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    avoir_solded_by = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_groupe_payments", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_groupe_payments_created_at",
                table: "groupe_payments",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_groupe_payments_group_id",
                table: "groupe_payments",
                column: "group_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "groupe_payments");
        }
    }
}
