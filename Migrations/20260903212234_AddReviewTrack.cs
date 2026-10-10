using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewTrack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Track",
                table: "Tickets",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Delivery");

            migrationBuilder.AddColumn<string>(
                name: "Track",
                table: "PhaseTransitions",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Delivery");

            migrationBuilder.AddColumn<string>(
                name: "Track",
                table: "CycleRuns",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Delivery");

            migrationBuilder.CreateTable(
                name: "ReviewComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Line = table.Column<int>(type: "INTEGER", nullable: true),
                    Side = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AnchorSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Author = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    QaItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    QaShotOrder = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalThreadId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReviewComments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReviewTargets",
                columns: table => new
                {
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    PrId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PrUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    SourceBranch = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TargetBranch = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PrAuthor = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ReviewedAtSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    HeadSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Verdict = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    VerdictAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewTargets", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_ReviewTargets_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewComments_TicketId_Order",
                table: "ReviewComments",
                columns: new[] { "TicketId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewComments");

            migrationBuilder.DropTable(
                name: "ReviewTargets");

            migrationBuilder.DropColumn(
                name: "Track",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Track",
                table: "PhaseTransitions");

            migrationBuilder.DropColumn(
                name: "Track",
                table: "CycleRuns");
        }
    }
}
