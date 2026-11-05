using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddEscapeClassifiedByRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClassifiedBy",
                table: "EscapeEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClassifiedByRunId",
                table: "EscapeEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Overridden",
                table: "EscapeEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClassifiedBy",
                table: "EscapeEvents");

            migrationBuilder.DropColumn(
                name: "ClassifiedByRunId",
                table: "EscapeEvents");

            migrationBuilder.DropColumn(
                name: "Overridden",
                table: "EscapeEvents");
        }
    }
}
