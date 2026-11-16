using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryNarration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscoveryNarrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Job = table.Column<int>(type: "INTEGER", nullable: true),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VideoAt = table.Column<double>(type: "REAL", nullable: true),
                    RecordingId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryNarrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryNarrations_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryNarrations_TicketId",
                table: "DiscoveryNarrations",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveryNarrations");
        }
    }
}
