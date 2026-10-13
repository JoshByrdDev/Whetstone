using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddStewardPlanKeyAndUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StewardOutcomes_ProjectKey_Unit_Outcome",
                table: "StewardOutcomes");

            migrationBuilder.AddColumn<string>(
                name: "PlanKey",
                table: "Tickets",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "StewardOutcomes",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "StewardUnits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Plan = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Unit = table.Column<int>(type: "INTEGER", nullable: false),
                    AgreedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleasedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleasedSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardUnits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StewardOutcomes_ProjectKey_Plan_Unit_Outcome",
                table: "StewardOutcomes",
                columns: new[] { "ProjectKey", "Plan", "Unit", "Outcome" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StewardUnits_ProjectKey_Plan_Unit",
                table: "StewardUnits",
                columns: new[] { "ProjectKey", "Plan", "Unit" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StewardUnits");

            migrationBuilder.DropIndex(
                name: "IX_StewardOutcomes_ProjectKey_Plan_Unit_Outcome",
                table: "StewardOutcomes");

            migrationBuilder.DropColumn(
                name: "PlanKey",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "StewardOutcomes");

            migrationBuilder.CreateIndex(
                name: "IX_StewardOutcomes_ProjectKey_Unit_Outcome",
                table: "StewardOutcomes",
                columns: new[] { "ProjectKey", "Unit", "Outcome" },
                unique: true);
        }
    }
}
