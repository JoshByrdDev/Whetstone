using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class Recon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReconFrom",
                table: "Tickets",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReconDismissalEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SuggestionKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AreaHeadCommit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Until = table.Column<DateTime>(type: "TEXT", nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconDismissalEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconDismissalEvents_ProjectKey_SuggestionKey",
                table: "ReconDismissalEvents",
                columns: new[] { "ProjectKey", "SuggestionKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReconDismissalEvents");

            migrationBuilder.DropColumn(
                name: "ReconFrom",
                table: "Tickets");
        }
    }
}
