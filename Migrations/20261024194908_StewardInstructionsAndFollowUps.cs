using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class StewardInstructionsAndFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Reads",
                table: "StewardEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StewardFollowUps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Decision = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CitedBy = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CitedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DroppedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardFollowUps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StewardInstructions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Until = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedBy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ClosedNote = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardInstructions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StewardFollowUps_ProjectKey_Decision",
                table: "StewardFollowUps",
                columns: new[] { "ProjectKey", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_StewardInstructions_ProjectKey_ClosedAt",
                table: "StewardInstructions",
                columns: new[] { "ProjectKey", "ClosedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StewardFollowUps");

            migrationBuilder.DropTable(
                name: "StewardInstructions");

            migrationBuilder.DropColumn(
                name: "Reads",
                table: "StewardEntries");
        }
    }
}
