using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddStewardQuestionDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Decisions",
                table: "StewardQuestions",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanKey",
                table: "StewardQuestions",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanUnit",
                table: "StewardQuestions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Decisions",
                table: "StewardQuestions");

            migrationBuilder.DropColumn(
                name: "PlanKey",
                table: "StewardQuestions");

            migrationBuilder.DropColumn(
                name: "PlanUnit",
                table: "StewardQuestions");
        }
    }
}
