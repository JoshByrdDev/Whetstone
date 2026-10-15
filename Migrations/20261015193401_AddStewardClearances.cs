using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddStewardClearances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StewardClearances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Flag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ChangeId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Sha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StewardClearances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StewardClearances_ProjectKey_TicketId_Flag_ChangeId",
                table: "StewardClearances",
                columns: new[] { "ProjectKey", "TicketId", "Flag", "ChangeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StewardClearances");
        }
    }
}
