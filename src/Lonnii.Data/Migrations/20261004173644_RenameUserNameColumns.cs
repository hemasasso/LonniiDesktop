using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lonnii.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameUserNameColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "last_name",
                table: "users",
                newName: "lastname");

            migrationBuilder.RenameColumn(
                name: "first_name",
                table: "users",
                newName: "firstname");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "lastname",
                table: "users",
                newName: "last_name");

            migrationBuilder.RenameColumn(
                name: "firstname",
                table: "users",
                newName: "first_name");
        }
    }
}
