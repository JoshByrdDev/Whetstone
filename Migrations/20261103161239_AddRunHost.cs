using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddRunHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Host",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Slot",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            // Where the runs so far ran, as far as the record can prove it. Three rules, in order, each
            // only for a run still unplaced:
            //  1. Before worker hosts existed (the runner landed on 2026-10-17), every run was the board
            //     host's.
            //  2. A run that took a resource on a worker host for its ticket - the QA stack, an emulator -
            //     has a lease named `<resource>@<host>` taken while it ran. That host.
            //  3. A run that took the same kind of resource with no host on it ran here.
            // Everything else stays null: a worker host's speculate or implement run takes no resource,
            // so nothing says where it was, and calling it the board's would credit this machine with it.
            migrationBuilder.Sql(
                "UPDATE CycleRuns SET Host = '@board' WHERE Host IS NULL AND StartedAt < '2026-10-17'");
            const string lease =
                "FROM ResourceLeases l WHERE l.TicketId = CycleRuns.TicketId " +
                "AND l.ResourceKey NOT LIKE 'ticket:%' AND l.ResourceKey NOT LIKE 'steward:%' " +
                "AND l.AcquiredAt >= datetime(CycleRuns.StartedAt, '-2 minutes') " +
                "AND l.AcquiredAt <= datetime(CycleRuns.StartedAt, '+' || CAST(CycleRuns.DurationSeconds AS INTEGER) || ' seconds')";
            migrationBuilder.Sql(
                "UPDATE CycleRuns SET Host = (SELECT substr(l.ResourceKey, instr(l.ResourceKey, '@') + 1) " + lease +
                " AND instr(l.ResourceKey, '@') > 0 ORDER BY l.AcquiredAt LIMIT 1) WHERE Host IS NULL");
            migrationBuilder.Sql(
                "UPDATE CycleRuns SET Host = '@board' WHERE Host IS NULL AND EXISTS (SELECT 1 " + lease +
                " AND instr(l.ResourceKey, '@') = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Host",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "Slot",
                table: "CycleRuns");
        }
    }
}
