using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sync_id",
                table: "immobilisations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sync_id",
                table: "charges_categories",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sync_id",
                table: "charges",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sync_id",
                table: "caisses",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sync_id",
                table: "caisse_transactions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sync_id",
                table: "amortissement_echeances",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Existing rows get a GUID of their own before the unique index goes on; new rows get one
            // from the model (ISyncable). Same text form as Guid.ToString().
            foreach (var table in new[] { "immobilisations", "charges_categories", "charges", "caisses", "caisse_transactions", "amortissement_echeances" })
            {
                migrationBuilder.Sql($"""
                    UPDATE {table} SET sync_id =
                        lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' ||
                        substr(lower(hex(randomblob(2))), 2) || '-' ||
                        substr('89ab', 1 + (abs(random()) % 4), 1) || substr(lower(hex(randomblob(2))), 2) || '-' ||
                        lower(hex(randomblob(6)))
                    WHERE sync_id = '';
                    """);
            }

            migrationBuilder.CreateIndex(
                name: "IX_immobilisations_sync_id",
                table: "immobilisations",
                column: "sync_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_charges_categories_sync_id",
                table: "charges_categories",
                column: "sync_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_charges_sync_id",
                table: "charges",
                column: "sync_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_caisses_sync_id",
                table: "caisses",
                column: "sync_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_caisse_transactions_sync_id",
                table: "caisse_transactions",
                column: "sync_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_amortissement_echeances_sync_id",
                table: "amortissement_echeances",
                column: "sync_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_immobilisations_sync_id",
                table: "immobilisations");

            migrationBuilder.DropIndex(
                name: "IX_charges_categories_sync_id",
                table: "charges_categories");

            migrationBuilder.DropIndex(
                name: "IX_charges_sync_id",
                table: "charges");

            migrationBuilder.DropIndex(
                name: "IX_caisses_sync_id",
                table: "caisses");

            migrationBuilder.DropIndex(
                name: "IX_caisse_transactions_sync_id",
                table: "caisse_transactions");

            migrationBuilder.DropIndex(
                name: "IX_amortissement_echeances_sync_id",
                table: "amortissement_echeances");

            migrationBuilder.DropColumn(
                name: "sync_id",
                table: "immobilisations");

            migrationBuilder.DropColumn(
                name: "sync_id",
                table: "charges_categories");

            migrationBuilder.DropColumn(
                name: "sync_id",
                table: "charges");

            migrationBuilder.DropColumn(
                name: "sync_id",
                table: "caisses");

            migrationBuilder.DropColumn(
                name: "sync_id",
                table: "caisse_transactions");

            migrationBuilder.DropColumn(
                name: "sync_id",
                table: "amortissement_echeances");
        }
    }
}
