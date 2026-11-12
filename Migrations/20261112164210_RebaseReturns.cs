using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class RebaseReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RebaseReturns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Track = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Verdict = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    VerdictWhy = table.Column<string>(type: "TEXT", nullable: true),
                    JudgedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    JudgedByRunId = table.Column<int>(type: "INTEGER", nullable: true),
                    HandedOffTo = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    HandedOffAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RebaseReturns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RebaseReturns_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RebaseReturns_ProjectKey_At",
                table: "RebaseReturns",
                columns: new[] { "ProjectKey", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_RebaseReturns_TicketId",
                table: "RebaseReturns",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RebaseReturns");
        }
    }
}
