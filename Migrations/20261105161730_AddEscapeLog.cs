using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddEscapeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EscapeEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Track = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FromPhase = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ToPhase = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    NoteCommentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Build = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Backfilled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 24, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ClassifierNote = table.Column<string>(type: "TEXT", nullable: true),
                    ClassifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EscapeEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EscapeEvents_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EscapeEvents_ProjectKey_At",
                table: "EscapeEvents",
                columns: new[] { "ProjectKey", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_EscapeEvents_TicketId",
                table: "EscapeEvents",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EscapeEvents");
        }
    }
}
