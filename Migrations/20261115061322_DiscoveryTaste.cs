using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryTaste : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscoveryTasteReads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    VersionId = table.Column<int>(type: "INTEGER", nullable: true),
                    VersionNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryTasteReads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryTasteReads_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryTasteStates",
                columns: table => new
                {
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FoldRequestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Claim = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClaimedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClaimBasedOn = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClaimEventIds = table.Column<string>(type: "TEXT", nullable: true),
                    LastFoldNote = table.Column<string>(type: "TEXT", nullable: true),
                    LastFoldAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryTasteStates", x => x.ProjectKey);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryTasteVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Commit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CommitNote = table.Column<string>(type: "TEXT", nullable: true),
                    EventIds = table.Column<string>(type: "TEXT", nullable: false),
                    Verdicts = table.Column<int>(type: "INTEGER", nullable: false),
                    Suggestions = table.Column<string>(type: "TEXT", nullable: true),
                    RevertOf = table.Column<int>(type: "INTEGER", nullable: true),
                    RodeOnTicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryTasteVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryTasteReads_TicketId",
                table: "DiscoveryTasteReads",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryTasteVersions_ProjectKey_Number",
                table: "DiscoveryTasteVersions",
                columns: new[] { "ProjectKey", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveryTasteReads");

            migrationBuilder.DropTable(
                name: "DiscoveryTasteStates");

            migrationBuilder.DropTable(
                name: "DiscoveryTasteVersions");
        }
    }
}
