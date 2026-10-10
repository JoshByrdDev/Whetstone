using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class RenameJiraColumnsToIssue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "JiraUrl",
                table: "Tickets",
                newName: "IssueUrl");

            migrationBuilder.RenameColumn(
                name: "JiraSnapshotAt",
                table: "Tickets",
                newName: "IssueSnapshotAt");

            migrationBuilder.RenameColumn(
                name: "JiraSnapshot",
                table: "Tickets",
                newName: "IssueSnapshot");

            migrationBuilder.RenameColumn(
                name: "JiraKey",
                table: "Tickets",
                newName: "IssueKey");

            migrationBuilder.RenameColumn(
                name: "BlockerJiraKey",
                table: "TicketDependencies",
                newName: "BlockerIssueKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IssueUrl",
                table: "Tickets",
                newName: "JiraUrl");

            migrationBuilder.RenameColumn(
                name: "IssueSnapshotAt",
                table: "Tickets",
                newName: "JiraSnapshotAt");

            migrationBuilder.RenameColumn(
                name: "IssueSnapshot",
                table: "Tickets",
                newName: "JiraSnapshot");

            migrationBuilder.RenameColumn(
                name: "IssueKey",
                table: "Tickets",
                newName: "JiraKey");

            migrationBuilder.RenameColumn(
                name: "BlockerIssueKey",
                table: "TicketDependencies",
                newName: "BlockerJiraKey");
        }
    }
}
