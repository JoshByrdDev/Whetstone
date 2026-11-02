using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddBugTracing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBug",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CommitRangeJson",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunFilesJson",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BugLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BugTicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceTicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Lines = table.Column<double>(type: "REAL", nullable: false),
                    Share = table.Column<double>(type: "REAL", nullable: false),
                    CommitsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Neighbours = table.Column<bool>(type: "INTEGER", nullable: false),
                    Escaped = table.Column<bool>(type: "INTEGER", nullable: false),
                    FoundBy = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    MissedBy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    TracedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BugLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BugLinks_Tickets_BugTicketId",
                        column: x => x.BugTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BugLinks_Tickets_SourceTicketId",
                        column: x => x.SourceTicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BugTraces",
                columns: table => new
                {
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    TracedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LinesBlamed = table.Column<double>(type: "REAL", nullable: false),
                    RangesJson = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BugTraces", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_BugTraces_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketCommits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Repo = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Sha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PatchId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketCommits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketCommits_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BugLinks_BugTicketId_SourceTicketId",
                table: "BugLinks",
                columns: new[] { "BugTicketId", "SourceTicketId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BugLinks_SourceTicketId",
                table: "BugLinks",
                column: "SourceTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCommits_ProjectKey_PatchId",
                table: "TicketCommits",
                columns: new[] { "ProjectKey", "PatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketCommits_ProjectKey_Sha",
                table: "TicketCommits",
                columns: new[] { "ProjectKey", "Sha" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketCommits_TicketId_Repo_Sha",
                table: "TicketCommits",
                columns: new[] { "TicketId", "Repo", "Sha" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BugLinks");

            migrationBuilder.DropTable(
                name: "BugTraces");

            migrationBuilder.DropTable(
                name: "TicketCommits");

            migrationBuilder.DropColumn(
                name: "IsBug",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CommitRangeJson",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "RunFilesJson",
                table: "CycleRuns");
        }
    }
}
