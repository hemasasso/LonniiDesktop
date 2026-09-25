using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReconcileWithLiveSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "prestations_enabled",
                table: "groupes",
                newName: "prestations_access");

            migrationBuilder.AlterColumn<string>(
                name: "group_name",
                table: "dashboard_subscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "created_by",
                table: "dashboard_subscriptions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "admin_name",
                table: "dashboard_subscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "id",
                table: "dashboard_subscriptions",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT")
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<string>(
                name: "contract_pdf_filename",
                table: "dashboard_subscriptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contract_pdf_url",
                table: "dashboard_subscriptions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "contract_pdf_filename",
                table: "dashboard_subscriptions");

            migrationBuilder.DropColumn(
                name: "contract_pdf_url",
                table: "dashboard_subscriptions");

            migrationBuilder.RenameColumn(
                name: "prestations_access",
                table: "groupes",
                newName: "prestations_enabled");

            migrationBuilder.AlterColumn<string>(
                name: "group_name",
                table: "dashboard_subscriptions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "created_by",
                table: "dashboard_subscriptions",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "admin_name",
                table: "dashboard_subscriptions",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "id",
                table: "dashboard_subscriptions",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .OldAnnotation("Sqlite:Autoincrement", true);
        }
    }
}
