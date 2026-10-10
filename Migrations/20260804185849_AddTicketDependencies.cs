using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketDependencies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockerJiraKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BlockerTicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Override = table.Column<bool>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    BlockerStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BlockerStatusCategory = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RefreshedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketDependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketDependencies_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketDependencies_TicketId",
                table: "TicketDependencies",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketDependencies");
        }
    }
}
