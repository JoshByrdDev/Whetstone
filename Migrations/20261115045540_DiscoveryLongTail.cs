using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryLongTail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresRouteReview",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "DiscoveryRoutes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OwnerEdited",
                table: "DiscoveryRoutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "GoalId",
                table: "DiscoveryProposals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecheckKind",
                table: "DiscoveryProposals",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Round",
                table: "DiscoveryProposals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Round",
                table: "DiscoveryObservations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DiscoveryGoalChecks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProposalId = table.Column<int>(type: "INTEGER", nullable: false),
                    FiledTicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    ObservationIds = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<int>(type: "INTEGER", nullable: true),
                    Commit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryGoalChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryGoalChecks_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryGoalChecks_ProposalId",
                table: "DiscoveryGoalChecks",
                column: "ProposalId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryGoalChecks_TicketId",
                table: "DiscoveryGoalChecks",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveryGoalChecks");

            migrationBuilder.DropColumn(
                name: "RequiresRouteReview",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "DiscoveryRoutes");

            migrationBuilder.DropColumn(
                name: "OwnerEdited",
                table: "DiscoveryRoutes");

            migrationBuilder.DropColumn(
                name: "GoalId",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "RecheckKind",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "Round",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "Round",
                table: "DiscoveryObservations");
        }
    }
}
