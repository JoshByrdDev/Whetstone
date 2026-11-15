using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryMeasures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RunId",
                table: "DiscoveryVisits",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunId",
                table: "DiscoveryRoutes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftBody",
                table: "DiscoveryProposals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DraftKind",
                table: "DiscoveryProposals",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DraftTitle",
                table: "DiscoveryProposals",
                type: "TEXT",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DraftWhy",
                table: "DiscoveryProposals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftWorkItems",
                table: "DiscoveryProposals",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "NewToMe",
                table: "DiscoveryProposals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunId",
                table: "DiscoveryProposals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunId",
                table: "DiscoveryObservations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "VideoAt",
                table: "DiscoveryObservations",
                type: "REAL",
                nullable: true);

            // A proposal written before drafts were kept: its draft is what it says now, the best
            // there is. Nothing has been edited yet on any board this ships to.
            migrationBuilder.Sql(
                "UPDATE DiscoveryProposals SET DraftKind = Kind, DraftTitle = Title, DraftWhy = Why, " +
                "DraftBody = Body, DraftWorkItems = WorkItems WHERE DraftTitle = ''");

            migrationBuilder.CreateTable(
                name: "DiscoveryVerdictEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProposalId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    NewToMe = table.Column<bool>(type: "INTEGER", nullable: true),
                    Edited = table.Column<bool>(type: "INTEGER", nullable: false),
                    KindChanged = table.Column<bool>(type: "INTEGER", nullable: false),
                    EditDistance = table.Column<int>(type: "INTEGER", nullable: false),
                    DraftLength = table.Column<int>(type: "INTEGER", nullable: false),
                    FiledTicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryVerdictEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryVerdictEvents_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryVerdictEvents_ProposalId",
                table: "DiscoveryVerdictEvents",
                column: "ProposalId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryVerdictEvents_TicketId",
                table: "DiscoveryVerdictEvents",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveryVerdictEvents");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "DiscoveryVisits");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "DiscoveryRoutes");

            migrationBuilder.DropColumn(
                name: "DraftBody",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "DraftKind",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "DraftTitle",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "DraftWhy",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "DraftWorkItems",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "NewToMe",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "DiscoveryProposals");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "DiscoveryObservations");

            migrationBuilder.DropColumn(
                name: "VideoAt",
                table: "DiscoveryObservations");
        }
    }
}
