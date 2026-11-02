using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class AddRunTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaseBehind",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangedFilesJson",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CliVersion",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Effort",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryReason",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FilesChanged",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HandoffSeconds",
                table: "CycleRuns",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinesAdded",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinesRemoved",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Pass",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PrepSeconds",
                table: "CycleRuns",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromptHash",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunCommits",
                table: "CycleRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SkillHash",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartRebase",
                table: "CycleRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StartupSeconds",
                table: "CycleRuns",
                type: "REAL",
                nullable: true);

            // The heartbeat's own moves used to go through the route for people, so the log has them
            // as a person's. Put right the ones whose reason says unmistakably where they came from -
            // a branch sent back for conflicting, a worktree that could not be prepared, a worker
            // host's hand-back that failed - and leave anything that could have been typed by hand.
            // Not undone by Down: nothing could tell these rows from a person's again, and nothing
            // needs to.
            migrationBuilder.Sql(
                "UPDATE PhaseTransitions SET Actor = 'System' WHERE Actor = 'Human' AND Backfilled = 0 AND (" +
                "Reason LIKE 'Branch conflicts with % and cannot be merged as it stands.' OR " +
                "Reason LIKE 'Workspace setup failed (%' OR " +
                "Reason LIKE 'Hand-back from worker host % failed:%')");

            // Why each run so far happened, and which time round it was, so the history reads the same
            // as every run recorded from now on. The CASE is RunEntryReasons.Of, written once more in
            // SQL for the rows that predate it; new rows are classified by that method alone. After
            // the actor fix above, so it reads the log as corrected.
            migrationBuilder.Sql(
                "UPDATE CycleRuns SET Pass = 1 + (SELECT COUNT(*) FROM CycleRuns c2 " +
                "WHERE c2.TicketId = CycleRuns.TicketId AND c2.Skill = CycleRuns.Skill " +
                "AND (c2.StartedAt < CycleRuns.StartedAt OR (c2.StartedAt = CycleRuns.StartedAt AND c2.Id < CycleRuns.Id)))");
            migrationBuilder.Sql(
                "UPDATE CycleRuns SET EntryReason = CASE WHEN Resumed = 1 THEN 'resumed' ELSE (" +
                "SELECT CASE WHEN t.\"From\" = 'Blocked' THEN 'unblocked' " +
                "WHEN t.Reason LIKE '%conflicts with%' THEN 'conflict' " +
                "WHEN t.Backward = 0 THEN 'forward' " +
                "WHEN t.\"From\" = 'QaTesting' THEN 'qa-failed' " +
                "WHEN t.\"From\" = 'Refining' THEN 'refine-sent-back' " +
                "ELSE 'review-changes' END " +
                "FROM PhaseTransitions t WHERE t.TicketId = CycleRuns.TicketId AND t.At <= CycleRuns.StartedAt " +
                "AND t.\"To\" <> CycleRuns.Phase ORDER BY t.At DESC, t.Id DESC LIMIT 1) END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseBehind",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "ChangedFilesJson",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "CliVersion",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "Effort",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "EntryReason",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "FilesChanged",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "HandoffSeconds",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "LinesAdded",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "LinesRemoved",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "Pass",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "PrepSeconds",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "PromptHash",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "RunCommits",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "SkillHash",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "StartRebase",
                table: "CycleRuns");

            migrationBuilder.DropColumn(
                name: "StartupSeconds",
                table: "CycleRuns");
        }
    }
}
