using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionsAndGroupMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "local", not the "" EF generates: an existing workspace has no subscription
            // row and must keep working after the upgrade, which only local mode guarantees.
            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "groupes",
                type: "TEXT",
                nullable: false,
                defaultValue: "local");

            migrationBuilder.CreateTable(
                name: "dashboard_subscriptions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    group_name = table.Column<string>(type: "TEXT", nullable: true),
                    admin_id = table.Column<string>(type: "TEXT", nullable: true),
                    admin_name = table.Column<string>(type: "TEXT", nullable: true),
                    montant = table.Column<long>(type: "INTEGER", nullable: false),
                    montant_preabonnement = table.Column<long>(type: "INTEGER", nullable: true),
                    statut = table.Column<string>(type: "TEXT", nullable: false),
                    contract_start_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    contract_end_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    cancellation_reason = table.Column<string>(type: "TEXT", nullable: true),
                    cancellation_date = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_by = table.Column<int>(type: "INTEGER", nullable: true),
                    date_vente = table.Column<DateTime>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dashboard_subscriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dashboard_subscriptions_group_id_contract_end_date",
                table: "dashboard_subscriptions",
                columns: new[] { "group_id", "contract_end_date" });

            migrationBuilder.CreateIndex(
                name: "IX_dashboard_subscriptions_statut",
                table: "dashboard_subscriptions",
                column: "statut");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dashboard_subscriptions");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "groupes");
        }
    }
}
