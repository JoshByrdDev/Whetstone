using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class StewardClearanceAbout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Paths",
                table: "StewardClearances");

            migrationBuilder.RenameColumn(
                name: "PathsChangeId",
                table: "StewardClearances",
                newName: "About");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "About",
                table: "StewardClearances",
                newName: "PathsChangeId");

            migrationBuilder.AddColumn<string>(
                name: "Paths",
                table: "StewardClearances",
                type: "TEXT",
                nullable: true);
        }
    }
}
