using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddStewardRulings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Basis",
                table: "StewardEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "StewardEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversedBecause",
                table: "StewardEntries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Basis",
                table: "StewardEntries");

            migrationBuilder.DropColumn(
                name: "ReversedAt",
                table: "StewardEntries");

            migrationBuilder.DropColumn(
                name: "ReversedBecause",
                table: "StewardEntries");
        }
    }
}
