using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class WalkthroughOfAUnitReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "AnalysisId",
                table: "AnalysisWalkthroughs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "UnitReportId",
                table: "AnalysisWalkthroughs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisWalkthroughs_UnitReportId",
                table: "AnalysisWalkthroughs",
                column: "UnitReportId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisWalkthroughs_UnitReports_UnitReportId",
                table: "AnalysisWalkthroughs",
                column: "UnitReportId",
                principalTable: "UnitReports",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AnalysisWalkthroughs_UnitReports_UnitReportId",
                table: "AnalysisWalkthroughs");

            migrationBuilder.DropIndex(
                name: "IX_AnalysisWalkthroughs_UnitReportId",
                table: "AnalysisWalkthroughs");

            migrationBuilder.DropColumn(
                name: "UnitReportId",
                table: "AnalysisWalkthroughs");

            migrationBuilder.AlterColumn<int>(
                name: "AnalysisId",
                table: "AnalysisWalkthroughs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
