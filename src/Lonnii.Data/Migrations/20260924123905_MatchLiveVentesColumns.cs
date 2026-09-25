using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class MatchLiveVentesColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "montant_paye",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "montant_restant",
                table: "ventes");

            migrationBuilder.RenameColumn(
                name: "statut_paiement",
                table: "ventes",
                newName: "payment_status");

            migrationBuilder.RenameColumn(
                name: "numero_vente",
                table: "ventes",
                newName: "sale_number");

            migrationBuilder.RenameColumn(
                name: "montant_total",
                table: "ventes",
                newName: "total_amount");

            migrationBuilder.RenameColumn(
                name: "mode_paiement",
                table: "ventes",
                newName: "payment_method");

            migrationBuilder.RenameColumn(
                name: "date_vente",
                table: "ventes",
                newName: "date");

            migrationBuilder.RenameColumn(
                name: "created_by",
                table: "ventes",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "client_telephone",
                table: "ventes",
                newName: "customer_phone");

            migrationBuilder.RenameColumn(
                name: "client_nom",
                table: "ventes",
                newName: "customer_name");

            migrationBuilder.RenameColumn(
                name: "client_email",
                table: "ventes",
                newName: "customer_email");

            migrationBuilder.RenameIndex(
                name: "IX_ventes_statut_paiement",
                table: "ventes",
                newName: "IX_ventes_payment_status");

            migrationBuilder.RenameIndex(
                name: "IX_ventes_group_id_numero_vente",
                table: "ventes",
                newName: "IX_ventes_group_id_sale_number");

            migrationBuilder.RenameIndex(
                name: "IX_ventes_group_id_date_vente",
                table: "ventes",
                newName: "IX_ventes_group_id_date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "ventes",
                newName: "created_by");

            migrationBuilder.RenameColumn(
                name: "total_amount",
                table: "ventes",
                newName: "montant_total");

            migrationBuilder.RenameColumn(
                name: "sale_number",
                table: "ventes",
                newName: "numero_vente");

            migrationBuilder.RenameColumn(
                name: "payment_status",
                table: "ventes",
                newName: "statut_paiement");

            migrationBuilder.RenameColumn(
                name: "payment_method",
                table: "ventes",
                newName: "mode_paiement");

            migrationBuilder.RenameColumn(
                name: "date",
                table: "ventes",
                newName: "date_vente");

            migrationBuilder.RenameColumn(
                name: "customer_phone",
                table: "ventes",
                newName: "client_telephone");

            migrationBuilder.RenameColumn(
                name: "customer_name",
                table: "ventes",
                newName: "client_nom");

            migrationBuilder.RenameColumn(
                name: "customer_email",
                table: "ventes",
                newName: "client_email");

            migrationBuilder.RenameIndex(
                name: "IX_ventes_payment_status",
                table: "ventes",
                newName: "IX_ventes_statut_paiement");

            migrationBuilder.RenameIndex(
                name: "IX_ventes_group_id_sale_number",
                table: "ventes",
                newName: "IX_ventes_group_id_numero_vente");

            migrationBuilder.RenameIndex(
                name: "IX_ventes_group_id_date",
                table: "ventes",
                newName: "IX_ventes_group_id_date_vente");

            migrationBuilder.AddColumn<long>(
                name: "montant_paye",
                table: "ventes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "montant_restant",
                table: "ventes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
