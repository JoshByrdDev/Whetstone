using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CycleRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Skill = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Resumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    ApiDurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    NumTurns = table.Column<int>(type: "INTEGER", nullable: true),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CacheReadTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CacheCreationTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    Mutations = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PhaseAfter = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CycleRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CycleRuns_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PhaseTransitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    From = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    To = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    Backward = table.Column<bool>(type: "INTEGER", nullable: false),
                    Backfilled = table.Column<bool>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhaseTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhaseTransitions_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CycleRuns_ProjectKey_StartedAt",
                table: "CycleRuns",
                columns: new[] { "ProjectKey", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CycleRuns_StartedAt",
                table: "CycleRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CycleRuns_TicketId",
                table: "CycleRuns",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_PhaseTransitions_At",
                table: "PhaseTransitions",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_PhaseTransitions_ProjectKey_At",
                table: "PhaseTransitions",
                columns: new[] { "ProjectKey", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PhaseTransitions_TicketId",
                table: "PhaseTransitions",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CycleRuns");

            migrationBuilder.DropTable(
                name: "PhaseTransitions");
        }
    }
}
