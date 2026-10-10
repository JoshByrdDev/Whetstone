using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceLeases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ResourceKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Skill = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AcquiredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleaseReason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceLeases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLeases_ResourceKey_Slot",
                table: "ResourceLeases",
                columns: new[] { "ResourceKey", "Slot" },
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLeases_TicketId",
                table: "ResourceLeases",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceLeases");
        }
    }
}
