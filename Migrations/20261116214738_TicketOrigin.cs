using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class TicketOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FoundOn",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntakeChannel",
                table: "Tickets",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntakeIssueId",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "Tickets",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Owner");

            migrationBuilder.AddColumn<int>(
                name: "RegressionOf",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            // Every ticket from before this reads as the owner's (the column's default), except:
            // a steward's, or one the board filed for a slice, from CreatedBy; and one a run found,
            // which is either a discovery's approved proposal (DiscoveryId) or a defect the
            // heartbeat's reflection filed, recognised by the sentence New-DefectTicket writes and
            // numbered from it. TicketOrigin.Of is the same order, most specific first.
            migrationBuilder.Sql(
                "UPDATE Tickets SET Origin = 'Steward' WHERE CreatedBy IN ('Steward', 'System');");
            migrationBuilder.Sql(
                "UPDATE Tickets SET Origin = 'FoundInRun', FoundOn = DiscoveryId WHERE DiscoveryId IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE Tickets SET Origin = 'FoundInRun', "
                + "FoundOn = CAST(substr(Description, instr(Description, 'after a run on ticket #') + 23) AS INTEGER) "
                + "WHERE DiscoveryId IS NULL AND instr(Description, 'Filed by the heartbeat''s reflection after a run on ticket #') > 0;");
            migrationBuilder.Sql(
                "UPDATE Tickets SET FoundOn = NULL WHERE Origin = 'FoundInRun' AND FoundOn = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FoundOn",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "IntakeChannel",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "IntakeIssueId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "RegressionOf",
                table: "Tickets");
        }
    }
}
