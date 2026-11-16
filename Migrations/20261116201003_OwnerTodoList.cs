using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class OwnerTodoList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwnerTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    WaitingOn = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    WaitingUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    Unit = table.Column<int>(type: "INTEGER", nullable: true),
                    HoldsRelease = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DoneAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DoneNote = table.Column<string>(type: "TEXT", nullable: true),
                    DroppedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DroppedBecause = table.Column<string>(type: "TEXT", nullable: true),
                    Suggestion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SuggestionBecause = table.Column<string>(type: "TEXT", nullable: true),
                    SuggestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notice = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OwnerTaskNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerTaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    By = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerTaskNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerTaskNotes_OwnerTasks_OwnerTaskId",
                        column: x => x.OwnerTaskId,
                        principalTable: "OwnerTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerTaskNotes_OwnerTaskId",
                table: "OwnerTaskNotes",
                column: "OwnerTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerTasks_ProjectKey_Key",
                table: "OwnerTasks",
                columns: new[] { "ProjectKey", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerTasks_Status_ProjectKey",
                table: "OwnerTasks",
                columns: new[] { "Status", "ProjectKey" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerTasks_TicketId",
                table: "OwnerTasks",
                column: "TicketId");

            // A config or action release note was a to-do with a gate on it: each becomes an item that
            // holds its release (docs/owner-todo-plan.md) - open if unticked, done if ticked - and the
            // note row goes. The key carries the note's id across long enough to write the item's log.
            migrationBuilder.Sql("""
                INSERT INTO OwnerTasks (ProjectKey, Kind, Title, Body, Key, Status, TicketId, HoldsRelease, CreatedBy,
                                        CreatedAt, UpdatedAt, DoneAt, DoneNote)
                SELECT ProjectKey,
                       CASE Release WHEN 'Config' THEN 'Config' ELSE 'Action' END,
                       substr(trim(CASE WHEN instr(trim(Text), char(10)) > 0
                                        THEN substr(trim(Text), 1, instr(trim(Text), char(10)) - 1) ELSE trim(Text) END), 1, 200),
                       trim(Text),
                       'release-note:' || Id,
                       CASE WHEN HandledAt IS NULL THEN 'Open' ELSE 'Done' END,
                       TicketId, 1, 'release note', At, At, HandledAt,
                       CASE WHEN HandledAt IS NULL THEN NULL ELSE 'Ticked off as a release note.' END
                FROM StewardEntries
                WHERE Kind = 'BeforeRelease' AND Release IN ('Config', 'Action');

                INSERT INTO OwnerTaskNotes (OwnerTaskId, Kind, By, TicketId, Text, At)
                SELECT t.Id, 'Added', 'release note', t.TicketId,
                       'Moved from a ' || lower(e.Release) || ' release note' ||
                       CASE WHEN e.TicketId IS NULL THEN '' ELSE ' on ticket ' || e.TicketId END || '.',
                       e.At
                FROM OwnerTasks t JOIN StewardEntries e ON t.Key = 'release-note:' || e.Id;

                UPDATE OwnerTasks SET Key = NULL WHERE Key LIKE 'release-note:%';

                DELETE FROM StewardEntries WHERE Kind = 'BeforeRelease' AND Release IN ('Config', 'Action');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerTaskNotes");

            migrationBuilder.DropTable(
                name: "OwnerTasks");
        }
    }
}
