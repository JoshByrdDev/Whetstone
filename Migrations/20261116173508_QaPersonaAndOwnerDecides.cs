using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class QaPersonaAndOwnerDecides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DrivenAs",
                table: "QaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Persona",
                table: "QaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OwnerAnsweredAt",
                table: "GateFindings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DrivenAs",
                table: "QaItems");

            migrationBuilder.DropColumn(
                name: "Persona",
                table: "QaItems");

            migrationBuilder.DropColumn(
                name: "OwnerAnsweredAt",
                table: "GateFindings");
        }
    }
}
