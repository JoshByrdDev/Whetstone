using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class DecisionPromotion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PromoteSuggestedAt",
                table: "StewardDecisions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromoteWhy",
                table: "StewardDecisions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PromotedAt",
                table: "StewardDecisions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PromoteSuggestedAt",
                table: "StewardDecisions");

            migrationBuilder.DropColumn(
                name: "PromoteWhy",
                table: "StewardDecisions");

            migrationBuilder.DropColumn(
                name: "PromotedAt",
                table: "StewardDecisions");
        }
    }
}
