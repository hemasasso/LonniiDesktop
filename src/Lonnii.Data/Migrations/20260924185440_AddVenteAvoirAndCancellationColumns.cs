using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVenteAvoirAndCancellationColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "avoir_amount",
                table: "ventes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "avoir_solded_by",
                table: "ventes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                table: "ventes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "cancelled_at",
                table: "ventes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_avoir",
                table: "ventes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "avoir_amount",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "avoir_solded_by",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "ventes");

            migrationBuilder.DropColumn(
                name: "is_avoir",
                table: "ventes");
        }
    }
}
