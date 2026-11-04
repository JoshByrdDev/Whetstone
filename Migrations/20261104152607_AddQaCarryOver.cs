using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddQaCarryOver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FullQaRequired",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CarriedOverAt",
                table: "QaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CarryOverNote",
                table: "QaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiredAt",
                table: "QaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpiryNote",
                table: "QaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedOn",
                table: "QaItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FullQaRequired",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CarriedOverAt",
                table: "QaItems");

            migrationBuilder.DropColumn(
                name: "CarryOverNote",
                table: "QaItems");

            migrationBuilder.DropColumn(
                name: "ExpiredAt",
                table: "QaItems");

            migrationBuilder.DropColumn(
                name: "ExpiryNote",
                table: "QaItems");

            migrationBuilder.DropColumn(
                name: "VerifiedOn",
                table: "QaItems");
        }
    }
}
