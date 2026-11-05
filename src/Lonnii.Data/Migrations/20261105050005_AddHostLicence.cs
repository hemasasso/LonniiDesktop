using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostLicence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "host_licence",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "TEXT", nullable: false),
                    token = table.Column<string>(type: "TEXT", nullable: true),
                    unsigned_since = table.Column<DateTime>(type: "TEXT", nullable: true),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_licence", x => x.group_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "host_licence");
        }
    }
}
