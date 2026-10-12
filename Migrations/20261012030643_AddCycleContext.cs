using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddCycleContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LargestToolResultsJson",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PeakContextTokens",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ToolResultChars",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LargestToolResultsJson",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "PeakContextTokens",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "ToolResultChars",
                table: "CycleRuns");
        }
    }
}
