using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddClassroomGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GateAnalysisId",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SkipClassroomAnalysis",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TreeSha",
                table: "TicketAnalyses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GateFindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    AnalysisId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Ruling = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    RulingNote = table.Column<string>(type: "TEXT", nullable: true),
                    RuledAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GateFindings_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GateFindings_TicketId_AnalysisId_Order",
                table: "GateFindings",
                columns: new[] { "TicketId", "AnalysisId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GateFindings");

            migrationBuilder.DropColumn(
                name: "GateAnalysisId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SkipClassroomAnalysis",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "TreeSha",
                table: "TicketAnalyses");
        }
    }
}
