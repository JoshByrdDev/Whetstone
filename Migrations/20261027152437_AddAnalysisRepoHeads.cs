using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisRepoHeads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RepoHeadsJson",
                table: "TicketAnalyses",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepoHeadsJson",
                table: "TicketAnalyses");
        }
    }
}
