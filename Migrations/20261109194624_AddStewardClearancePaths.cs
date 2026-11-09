using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddStewardClearancePaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Paths",
                table: "StewardClearances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathsChangeId",
                table: "StewardClearances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Paths",
                table: "StewardClearances");

            migrationBuilder.DropColumn(
                name: "PathsChangeId",
                table: "StewardClearances");
        }
    }
}
