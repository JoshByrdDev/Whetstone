using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketAnalyses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PhaseAtGeneration = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Lens = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    HeadSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Branch = table.Column<string>(type: "TEXT", nullable: true),
                    Headline = table.Column<string>(type: "TEXT", nullable: false),
                    BlocksJson = table.Column<string>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheCreationTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketAnalyses_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAnalyses_TicketId_GeneratedAt",
                table: "TicketAnalyses",
                columns: new[] { "TicketId", "GeneratedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketAnalyses");
        }
    }
}
