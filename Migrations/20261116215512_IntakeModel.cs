using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class IntakeModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntakeIssues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    FirstSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Occurrences = table.Column<int>(type: "INTEGER", nullable: false),
                    LastState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReportedCount = table.Column<long>(type: "INTEGER", nullable: true),
                    UnnotedOccurrences = table.Column<int>(type: "INTEGER", nullable: false),
                    LastNotedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Muted = table.Column<bool>(type: "INTEGER", nullable: false),
                    MutedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HeldIn = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TicketHistory = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntakeMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    HandledAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Adapter = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IssueId = table.Column<int>(type: "INTEGER", nullable: true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    RawBody = table.Column<string>(type: "TEXT", nullable: true),
                    RawAttributes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntakeStorms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Window = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeStorms", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntakeIssues_ProjectKey_Channel_Fingerprint",
                table: "IntakeIssues",
                columns: new[] { "ProjectKey", "Channel", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntakeIssues_TicketId",
                table: "IntakeIssues",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeMessages_HandledAt",
                table: "IntakeMessages",
                column: "HandledAt");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeMessages_ProjectKey_Source_MessageId",
                table: "IntakeMessages",
                columns: new[] { "ProjectKey", "Source", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntakeStorms_ProjectKey_Window",
                table: "IntakeStorms",
                columns: new[] { "ProjectKey", "Window" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntakeIssues");

            migrationBuilder.DropTable(
                name: "IntakeMessages");

            migrationBuilder.DropTable(
                name: "IntakeStorms");
        }
    }
}
