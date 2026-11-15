using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoveryTrack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DiscoveryId",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DiscoveryObservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Saw = table.Column<string>(type: "TEXT", nullable: false),
                    Tried = table.Column<string>(type: "TEXT", nullable: true),
                    Job = table.Column<int>(type: "INTEGER", nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Width = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SignedInAs = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Area = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ShotPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    FocusX = table.Column<int>(type: "INTEGER", nullable: true),
                    FocusY = table.Column<int>(type: "INTEGER", nullable: true),
                    FocusW = table.Column<int>(type: "INTEGER", nullable: true),
                    FocusH = table.Column<int>(type: "INTEGER", nullable: true),
                    Commit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryObservations_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryProposals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Why = table.Column<string>(type: "TEXT", nullable: true),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    WorkItems = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Confidence = table.Column<string>(type: "TEXT", nullable: true),
                    Area = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ObservationIds = table.Column<string>(type: "TEXT", nullable: false),
                    DropReason = table.Column<string>(type: "TEXT", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    RejectNote = table.Column<string>(type: "TEXT", nullable: true),
                    SnoozeUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Answer = table.Column<string>(type: "TEXT", nullable: true),
                    FiledTicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    FiledNote = table.Column<string>(type: "TEXT", nullable: true),
                    Edited = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryProposals_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryRoutes",
                columns: table => new
                {
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Persona = table.Column<string>(type: "TEXT", nullable: false),
                    SignedInAs = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Jobs = table.Column<string>(type: "TEXT", nullable: false),
                    StartCases = table.Column<string>(type: "TEXT", nullable: false),
                    Widths = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Areas = table.Column<string>(type: "TEXT", nullable: false),
                    WrittenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryRoutes", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_DiscoveryRoutes_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryVisits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    RoutePattern = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Width = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SignedInAs = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Seconds = table.Column<int>(type: "INTEGER", nullable: true),
                    Area = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Case = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Commit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryVisits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryVisits_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryObservations_TicketId",
                table: "DiscoveryObservations",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryProposals_TicketId",
                table: "DiscoveryProposals",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryVisits_TicketId",
                table: "DiscoveryVisits",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveryObservations");

            migrationBuilder.DropTable(
                name: "DiscoveryProposals");

            migrationBuilder.DropTable(
                name: "DiscoveryRoutes");

            migrationBuilder.DropTable(
                name: "DiscoveryVisits");

            migrationBuilder.DropColumn(
                name: "DiscoveryId",
                table: "Tickets");
        }
    }
}
