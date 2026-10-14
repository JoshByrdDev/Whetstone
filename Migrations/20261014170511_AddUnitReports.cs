using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddUnitReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UnitReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Plan = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Unit = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseRef = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FromSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ToSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RangeCommits = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnCommits = table.Column<int>(type: "INTEGER", nullable: false),
                    ReadJson = table.Column<string>(type: "TEXT", nullable: false),
                    ShotsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GapsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Headline = table.Column<string>(type: "TEXT", nullable: false),
                    BlocksJson = table.Column<string>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheCreationTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    StepCount = table.Column<int>(type: "INTEGER", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnitReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnitReports_ProjectKey_Plan_Unit_GeneratedAt",
                table: "UnitReports",
                columns: new[] { "ProjectKey", "Plan", "Unit", "GeneratedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnitReports");
        }
    }
}
