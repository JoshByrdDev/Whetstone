using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class StewardIdeasAndStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgreedFile",
                table: "StewardUnits",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WalkthroughTicketId",
                table: "StewardUnits",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedFile",
                table: "StewardQuestions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProposedText",
                table: "StewardQuestions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoryRewordingsJson",
                table: "StewardQuestions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorySentence",
                table: "StewardQuestions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgreedFile",
                table: "StewardUnits");

            migrationBuilder.DropColumn(
                name: "WalkthroughTicketId",
                table: "StewardUnits");

            migrationBuilder.DropColumn(
                name: "ProposedFile",
                table: "StewardQuestions");

            migrationBuilder.DropColumn(
                name: "ProposedText",
                table: "StewardQuestions");

            migrationBuilder.DropColumn(
                name: "StoryRewordingsJson",
                table: "StewardQuestions");

            migrationBuilder.DropColumn(
                name: "StorySentence",
                table: "StewardQuestions");
        }
    }
}
