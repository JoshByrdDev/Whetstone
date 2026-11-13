using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class SlicesOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StewardDecisionConflicts");

            migrationBuilder.DropIndex(
                name: "IX_UnitReports_ProjectKey_Plan_Unit_GeneratedAt",
                table: "UnitReports");

            migrationBuilder.DropIndex(
                name: "IX_StewardUnits_ProjectKey_Plan_Unit",
                table: "StewardUnits");

            migrationBuilder.DropIndex(
                name: "IX_StewardOutcomes_ProjectKey_Plan_Unit_Outcome",
                table: "StewardOutcomes");

            migrationBuilder.DropIndex(
                name: "IX_StewardDecisions_ProjectKey_ImportKey",
                table: "StewardDecisions");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "UnitReports");

            migrationBuilder.DropColumn(
                name: "PlanKey",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "StewardUnits");

            migrationBuilder.DropColumn(
                name: "RegisterBuiltAt",
                table: "StewardStates");

            migrationBuilder.DropColumn(
                name: "PlanKey",
                table: "StewardQuestions");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "StewardOutcomes");

            migrationBuilder.DropColumn(
                name: "ImportKey",
                table: "StewardDecisions");

            migrationBuilder.DropColumn(
                name: "PlanKey",
                table: "StewardDecisions");

            migrationBuilder.CreateIndex(
                name: "IX_UnitReports_ProjectKey_Unit_GeneratedAt",
                table: "UnitReports",
                columns: new[] { "ProjectKey", "Unit", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StewardUnits_ProjectKey_Unit",
                table: "StewardUnits",
                columns: new[] { "ProjectKey", "Unit" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StewardOutcomes_ProjectKey_Unit_Outcome",
                table: "StewardOutcomes",
                columns: new[] { "ProjectKey", "Unit", "Outcome" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UnitReports_ProjectKey_Unit_GeneratedAt",
                table: "UnitReports");

            migrationBuilder.DropIndex(
                name: "IX_StewardUnits_ProjectKey_Unit",
                table: "StewardUnits");

            migrationBuilder.DropIndex(
                name: "IX_StewardOutcomes_ProjectKey_Unit_Outcome",
                table: "StewardOutcomes");

            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "UnitReports",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PlanKey",
                table: "Tickets",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "StewardUnits",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "RegisterBuiltAt",
                table: "StewardStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanKey",
                table: "StewardQuestions",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "StewardOutcomes",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ImportKey",
                table: "StewardDecisions",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanKey",
                table: "StewardDecisions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StewardDecisionConflicts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FoundAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Newer = table.Column<int>(type: "INTEGER", nullable: false),
                    Older = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Settled = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SettledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Why = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardDecisionConflicts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnitReports_ProjectKey_Plan_Unit_GeneratedAt",
                table: "UnitReports",
                columns: new[] { "ProjectKey", "Plan", "Unit", "GeneratedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StewardUnits_ProjectKey_Plan_Unit",
                table: "StewardUnits",
                columns: new[] { "ProjectKey", "Plan", "Unit" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StewardOutcomes_ProjectKey_Plan_Unit_Outcome",
                table: "StewardOutcomes",
                columns: new[] { "ProjectKey", "Plan", "Unit", "Outcome" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StewardDecisions_ProjectKey_ImportKey",
                table: "StewardDecisions",
                columns: new[] { "ProjectKey", "ImportKey" });

            migrationBuilder.CreateIndex(
                name: "IX_StewardDecisionConflicts_ProjectKey_Older_Newer",
                table: "StewardDecisionConflicts",
                columns: new[] { "ProjectKey", "Older", "Newer" },
                unique: true);
        }
    }
}
