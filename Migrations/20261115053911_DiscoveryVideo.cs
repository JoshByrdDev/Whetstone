using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryVideo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClipFile",
                table: "DiscoveryProposals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClipPrunedAt",
                table: "DiscoveryProposals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "DiscoveryProposals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ObservedAt",
                table: "DiscoveryObservations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecordingId",
                table: "DiscoveryObservations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ShotPrunedAt",
                table: "DiscoveryObservations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DiscoveryChapters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Job = table.Column<int>(type: "INTEGER", nullable: true),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VideoAt = table.Column<double>(type: "REAL", nullable: true),
                    RecordingId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryChapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryChapters_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryRecordings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true),
                    File = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Seconds = table.Column<double>(type: "REAL", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    VerdictAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PrunedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryRecordings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryRecordings_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryChapters_TicketId",
                table: "DiscoveryChapters",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryRecordings_TicketId",
                table: "DiscoveryRecordings",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveryChapters");

            migrationBuilder.DropTable(
                name: "DiscoveryRecordings");

            migrationBuilder.DropColumn(
                name: "ClipFile",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "ClipPrunedAt",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "Pinned",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "ObservedAt",
                table: "DiscoveryObservations");

            migrationBuilder.DropColumn(
                name: "RecordingId",
                table: "DiscoveryObservations");

            migrationBuilder.DropColumn(
                name: "ShotPrunedAt",
                table: "DiscoveryObservations");
        }
    }
}
