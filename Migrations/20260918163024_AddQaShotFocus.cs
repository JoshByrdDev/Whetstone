using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddQaShotFocus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FocusH",
                table: "QaShots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FocusW",
                table: "QaShots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FocusX",
                table: "QaShots",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FocusY",
                table: "QaShots",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FocusH",
                table: "QaShots");

            migrationBuilder.DropColumn(
                name: "FocusW",
                table: "QaShots");

            migrationBuilder.DropColumn(
                name: "FocusX",
                table: "QaShots");

            migrationBuilder.DropColumn(
                name: "FocusY",
                table: "QaShots");
        }
    }
}
