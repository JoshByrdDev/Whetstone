using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class TicketBranchMadeAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BranchMadeAt",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            // Before this column, a recorded branch was the branch a run made almost always - the
            // drawer's chip was the only other writer. Every one is stamped, so none of them can be
            // renamed by a record edit alone: the safe reading of the ones that were only a name.
            migrationBuilder.Sql(
                "UPDATE Tickets SET BranchMadeAt = UpdatedAt WHERE Branch IS NOT NULL AND trim(Branch) <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BranchMadeAt",
                table: "Tickets");
        }
    }
}
