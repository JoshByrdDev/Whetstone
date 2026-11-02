using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddExperiments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExperimentApplied",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExperimentArm",
                table: "CycleRuns",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExperimentId",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Experiments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Skill = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Hypothesis = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MinTicketsPerArm = table.Column<int>(type: "INTEGER", nullable: false),
                    WaitDays = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StoppedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StopKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    StopReason = table.Column<string>(type: "TEXT", nullable: true),
                    ConcludedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Winner = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ConclusionNote = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentArms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SkillVariant = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Effort = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentArms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentArms_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExperimentId = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ArmKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentAssignments_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExperimentAssignments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CycleRuns_ExperimentId",
                table: "CycleRuns",
                column: "ExperimentId");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentArms_ExperimentId_Key",
                table: "ExperimentArms",
                columns: new[] { "ExperimentId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentAssignments_ExperimentId_TicketId",
                table: "ExperimentAssignments",
                columns: new[] { "ExperimentId", "TicketId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentAssignments_TicketId",
                table: "ExperimentAssignments",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_ProjectKey_Skill_Status",
                table: "Experiments",
                columns: new[] { "ProjectKey", "Skill", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExperimentArms");

            migrationBuilder.DropTable(
                name: "ExperimentAssignments");

            migrationBuilder.DropTable(
                name: "Experiments");

            migrationBuilder.DropIndex(
                name: "IX_CycleRuns_ExperimentId",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "ExperimentApplied",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "ExperimentArm",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "ExperimentId",
                table: "CycleRuns");
        }
    }
}
