using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddSteward : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "Tickets",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanOutcomes",
                table: "Tickets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanUnit",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StewardEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StewardQuestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Question = table.Column<string>(type: "TEXT", nullable: false),
                    Why = table.Column<string>(type: "TEXT", nullable: false),
                    OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    HeldUp = table.Column<string>(type: "TEXT", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    AskedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Answer = table.Column<string>(type: "TEXT", nullable: true),
                    AnsweredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AnswerDeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RemindedOn = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardQuestions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StewardStates",
                columns: table => new
                {
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Memo = table.Column<string>(type: "TEXT", nullable: false),
                    MemoUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DueReason = table.Column<string>(type: "TEXT", nullable: true),
                    DueSince = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RestingAtSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastRunStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunEndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunSummary = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedDay = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    CreatedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardStates", x => x.ProjectKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StewardEntries_ProjectKey_Kind_At",
                table: "StewardEntries",
                columns: new[] { "ProjectKey", "Kind", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_StewardQuestions_ProjectKey_Key",
                table: "StewardQuestions",
                columns: new[] { "ProjectKey", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StewardEntries");

            migrationBuilder.DropTable(
                name: "StewardQuestions");

            migrationBuilder.DropTable(
                name: "StewardStates");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "PlanOutcomes",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "PlanUnit",
                table: "Tickets");
        }
    }
}
