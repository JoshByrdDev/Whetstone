using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddStewardDecisionRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RegisterBuiltAt",
                table: "StewardStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StewardDecisionConflicts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Older = table.Column<int>(type: "INTEGER", nullable: false),
                    Newer = table.Column<int>(type: "INTEGER", nullable: false),
                    Why = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    FoundAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Settled = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SettledAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardDecisionConflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StewardDecisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Rule = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    PlanKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PlanUnit = table.Column<int>(type: "INTEGER", nullable: true),
                    Outcomes = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Provisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProvisionalUnit = table.Column<int>(type: "INTEGER", nullable: true),
                    WithdrawnAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WithdrawnBecause = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    QuestionKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Question = table.Column<string>(type: "TEXT", nullable: true),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SourceRef = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ImportKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MadeAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StewardDecisionReplaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DecisionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Why = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardDecisionReplaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StewardDecisionReplaces_StewardDecisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "StewardDecisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StewardDecisionConflicts_ProjectKey_Older_Newer",
                table: "StewardDecisionConflicts",
                columns: new[] { "ProjectKey", "Older", "Newer" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StewardDecisionReplaces_DecisionId_Number",
                table: "StewardDecisionReplaces",
                columns: new[] { "DecisionId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StewardDecisions_ProjectKey_ImportKey",
                table: "StewardDecisions",
                columns: new[] { "ProjectKey", "ImportKey" });

            migrationBuilder.CreateIndex(
                name: "IX_StewardDecisions_ProjectKey_Number",
                table: "StewardDecisions",
                columns: new[] { "ProjectKey", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StewardDecisionConflicts");

            migrationBuilder.DropTable(
                name: "StewardDecisionReplaces");

            migrationBuilder.DropTable(
                name: "StewardDecisions");

            migrationBuilder.DropColumn(
                name: "RegisterBuiltAt",
                table: "StewardStates");
        }
    }
}
