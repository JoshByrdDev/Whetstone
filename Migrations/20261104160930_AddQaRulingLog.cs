using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddQaRulingLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LeaseRunId",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QaRulingEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    QaItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Track = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PriorVerdict = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    VerifiedOn = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true),
                    OverturnsEventId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QaRulingEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QaRulingEvents_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QaRulingEvents_QaItemId_At",
                table: "QaRulingEvents",
                columns: new[] { "QaItemId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_QaRulingEvents_RunId",
                table: "QaRulingEvents",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_QaRulingEvents_TicketId",
                table: "QaRulingEvents",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QaRulingEvents");

            migrationBuilder.DropColumn(
                name: "LeaseRunId",
                table: "CycleRuns");
        }
    }
}
