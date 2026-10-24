using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The limits on a project's steward, which are the whole reason it is code and not a prompt.
/// <para>
/// Everything pinned here is a thing the steward is refused, or a thing it is only allowed inside
/// a boundary: the mode its owner set, a run it has opened, a unit of the plan its owner agreed,
/// the counts it may not pass, and what a merge has to prove first. The judgment about WHAT to
/// file or merge is the agent's and is not tested here; that it cannot do either outside these
/// lines is.
/// </para>
/// </summary>
public class StewardServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly string _dataRoot;
    private readonly FakeGit _git = new();
    private TimeSpan _minGap = TimeSpan.Zero;

    public StewardServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();

        _dataRoot = Path.Combine(Path.GetTempPath(), "whetstone-stw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dataRoot, "projects", "alpha"));
        Configure("full");
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* temp dir */ }
    }

    private void Configure(string mode, string steward = """
        "limits": { "createdPerDay": 2, "openAtOnce": 3, "sendBacksPerTicket": 1 },
        "memoMaxChars": 1000,
        "plan": { "units": "docs/units" },
        "neverMerge": ["infra/"]
        """)
    {
        var paths = new DashboardPaths(_dataRoot);
        File.WriteAllText(paths.ProjectConfigDefaults("alpha"),
            $$"""{ "displayName": "Alpha", "projectPath": "C:/Repos/Alpha", "mergeInsteadOfPr": true, "steward": { {{steward}} } }""");
        File.WriteAllText(paths.ProjectConfig("alpha"), $$"""{ "stewardMode": "{{mode}}" }""");
    }

    private DashboardDbContext NewCtx() => new(_options);

    private StewardService Steward(DashboardDbContext ctx, IStewardProjectFlags? flags = null)
    {
        var registry = new ProjectRegistry(new DashboardPaths(_dataRoot));
        var leases = new ResourceLeaseService(ctx, registry);
        var deps = new TicketDependencyService(ctx);
        // No gap between runs here: these tests are about what a run may do, and one that had to
        // wait five minutes for its second run would be testing the clock.
        return new StewardService(ctx, new TicketService(ctx, dependencies: deps, leases: leases), registry, _git, leases, deps, flags)
        {
            MinGapBetweenRuns = _minGap,
        };
    }

    private async Task<StewardReply> In(Func<StewardService, Task<StewardReply>> act, IStewardProjectFlags? flags = null)
    {
        await using var ctx = NewCtx();
        return await act(Steward(ctx, flags));
    }

    private List<StewardEntry> Entries() { using var ctx = NewCtx(); return ctx.StewardEntries.AsNoTracking().ToList(); }

    private Task<StewardReply> Begin() => In(s => s.BeginAsync("alpha"));

    private static readonly string Whole =
        "What is wanted and why: a customer can save an address so a visit knows where to go. " +
        "Acceptance criteria: an address can be added, edited and removed; a second address can be made the default. " +
        "Out of scope: looking the address up on a map, and anything a business sees.";

    private static StewardTicketDraft Draft(string title = "A customer can save an address", int unit = 1, params string[] outcomes) =>
        new(unit, outcomes.Length > 0 ? outcomes : new[] { "A1" }, title, Whole);

    private async Task<int> AtReview(string branch = "ticket-1")
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "Finished work", Phase = TicketPhase.NeedsReview, Branch = branch, WorktreePath = _dataRoot };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private async Task<Ticket> Reload(int id)
    {
        await using var ctx = NewCtx();
        return (await new TicketService(ctx).GetAsync(id))!;
    }

    // ---- the mode and the run --------------------------------------------------

    [Fact]
    public async Task With_the_mode_off_there_is_no_steward_at_all()
    {
        Configure("off");
        Assert.Equal("off", (await Begin()).Answer);
        Assert.Equal("off", (await In(s => s.StatusAsync("alpha"))).Answer);
        Assert.False((await In0(s => s.DueAsync("alpha"))).Due);
    }

    private async Task<T> In0<T>(Func<StewardService, Task<T>> act)
    {
        await using var ctx = NewCtx();
        return await act(Steward(ctx));
    }

    [Fact]
    public async Task Nothing_is_changed_outside_a_run()
    {
        Assert.Equal("no-run", (await In(s => s.CreateAsync("alpha", Draft()))).Answer);
        Assert.Equal("no-run", (await In(s => s.SaveMemoAsync("alpha", "x"))).Answer);
    }

    [Fact]
    public async Task A_second_run_is_locked_out_until_the_first_ends()
    {
        Assert.Equal("started", (await Begin()).Answer);
        Assert.Equal("locked", (await Begin()).Answer);
        Assert.Equal("ended", (await In(s => s.EndAsync("alpha", "did nothing", false))).Answer);
        Assert.Equal("started", (await Begin()).Answer);
    }

    [Fact]
    public async Task Propose_mode_checks_everything_and_changes_nothing()
    {
        Configure("propose");
        await Begin();
        var r = await In(s => s.CreateAsync("alpha", Draft()));
        Assert.Equal("proposed", r.Answer);
        await using var ctx = NewCtx();
        Assert.Empty(ctx.Tickets);
    }

    // ---- filing tickets ------------------------------------------------------------

    [Fact]
    public async Task A_ticket_is_filed_against_an_agreed_unit_and_carries_its_outcomes()
    {
        await Begin();
        var r = await In(s => s.CreateAsync("alpha", Draft(outcomes: new[] { "A1", "A2" })));
        Assert.Equal("created", r.Answer);

        await using var ctx = NewCtx();
        var t = await ctx.Tickets.SingleAsync();
        Assert.Equal(TicketPhase.NeedsSpeculation, t.Phase);
        Assert.Equal(TransitionActor.Steward, t.CreatedBy);
        Assert.Equal(1, t.PlanUnit);
        Assert.Equal("A1, A2", t.PlanOutcomes);
        Assert.Equal(TransitionActor.Steward, (await ctx.PhaseTransitions.SingleAsync()).Actor);
    }

    [Fact]
    public async Task A_unit_nobody_agreed_is_never_ticketed()
    {
        await Begin();
        Assert.Equal("unit-not-agreed", (await In(s => s.CreateAsync("alpha", Draft(unit: 2, outcomes: "A1")))).Answer);
    }

    [Fact]
    public async Task An_outcome_the_unit_does_not_have_is_refused()
    {
        await Begin();
        Assert.Equal("bad-request", (await In(s => s.CreateAsync("alpha", Draft(outcomes: "Q9")))).Answer);
    }

    [Fact]
    public async Task A_thin_ticket_is_refused()
    {
        await Begin();
        var thin = Draft() with { Description = "Add addresses." };
        Assert.Equal("bad-request", (await In(s => s.CreateAsync("alpha", thin))).Answer);
    }

    [Fact]
    public async Task The_daily_limit_stops_the_filing()
    {
        await Begin();
        Assert.Equal("created", (await In(s => s.CreateAsync("alpha", Draft("The first ticket of the day")))).Answer);
        Assert.Equal("created", (await In(s => s.CreateAsync("alpha", Draft("The second ticket of the day")))).Answer);
        Assert.Equal("limit", (await In(s => s.CreateAsync("alpha", Draft("The third ticket of the day")))).Answer);
    }

    [Fact]
    public async Task The_same_title_is_not_filed_twice()
    {
        await Begin();
        await In(s => s.CreateAsync("alpha", Draft()));
        Assert.Equal("duplicate", (await In(s => s.CreateAsync("alpha", Draft()))).Answer);
    }

    // ---- merging -------------------------------------------------------------------

    [Fact]
    public async Task A_clean_ticket_at_review_is_merged_and_closed_in_the_stewards_name()
    {
        var id = await AtReview();
        await Begin();
        Assert.Equal("merged", (await In(s => s.MergeAsync("alpha", id))).Answer);

        var t = await Reload(id);
        Assert.Equal(TicketPhase.Done, t.Phase);
        // The commit it landed at is kept on the ticket: a unit report's range is counted off it.
        Assert.Equal("0123456789", t.MergeSha);
        Assert.NotNull(t.MergedAt);
        await using var ctx = NewCtx();
        Assert.Equal(TransitionActor.Steward, (await ctx.PhaseTransitions.SingleAsync(p => p.To == TicketPhase.Done)).Actor);
    }

    [Fact]
    public async Task Manage_mode_never_merges()
    {
        Configure("manage");
        var id = await AtReview();
        await Begin();
        Assert.Equal("not-in-this-mode", (await In(s => s.MergeAsync("alpha", id))).Answer);
        Assert.Empty(_git.Merged);
    }

    [Fact]
    public async Task A_change_to_a_never_merged_path_is_the_owners()
    {
        _git.Files = new[] { "src/app.ts", "infra/stack.ts" };
        var id = await AtReview();
        await Begin();
        Assert.Equal("owner", (await In(s => s.MergeAsync("alpha", id))).Answer);
        Assert.Empty(_git.Merged);
    }

    [Fact]
    public async Task A_projects_own_flag_can_stop_a_merge_the_shared_rules_would_allow()
    {
        var id = await AtReview();
        await Begin();
        var flags = new FakeFlags(new StewardFlag("no-phone-screenshot", Owner: false, Blocks: true));
        Assert.Equal("not-proved", (await In(s => s.MergeAsync("alpha", id), flags)).Answer);
        Assert.Empty(_git.Merged);
    }

    [Fact]
    public async Task A_flag_the_owner_cleared_no_longer_stops_the_merge()
    {
        var id = await AtReview();
        await Begin();
        var flags = new FakeFlags(new StewardFlag("migration-changes-existing-data", Owner: true, Blocks: false));
        Assert.Equal("owner", (await In(s => s.MergeAsync("alpha", id), flags)).Answer);

        Assert.Equal("cleared", (await In(s => s.OwnerClearsFlagAsync("alpha", id, "migration-changes-existing-data"), flags)).Answer);
        Assert.Contains(Entries(), e => e.Kind == StewardEntryKind.OwnerSaid && e.TicketId == id);
        Assert.Equal("merged", (await In(s => s.MergeAsync("alpha", id), flags)).Answer);
    }

    [Fact]
    public async Task A_clearance_lapses_when_the_ticket_is_reworked()
    {
        var id = await AtReview();
        await Begin();
        var flags = new FakeFlags(new StewardFlag("migration-changes-existing-data", Owner: true, Blocks: false));
        await In(s => s.OwnerClearsFlagAsync("alpha", id, "migration-changes-existing-data"), flags);

        _git.ChangeId = "change-2";
        Assert.Equal("owner", (await In(s => s.MergeAsync("alpha", id), flags)).Answer);
        Assert.Empty(_git.Merged);
    }

    [Fact]
    public async Task A_clearance_is_for_one_flag_and_only_one_that_is_raised()
    {
        var id = await AtReview();
        await Begin();
        var flags = new FakeFlags(
            new StewardFlag("migration-changes-existing-data", Owner: true, Blocks: false),
            new StewardFlag("new-package-no-spec-names", Owner: true, Blocks: false),
            new StewardFlag("no-phone-screenshot", Owner: false, Blocks: true));

        Assert.Equal("not-raised", (await In(s => s.OwnerClearsFlagAsync("alpha", id, "env-file"), flags)).Answer);
        // A flag that sends work back is not the owner's to wave through either.
        Assert.Equal("not-raised", (await In(s => s.OwnerClearsFlagAsync("alpha", id, "no-phone-screenshot"), flags)).Answer);

        await In(s => s.OwnerClearsFlagAsync("alpha", id, "migration-changes-existing-data"), flags);
        var refused = await In(s => s.MergeAsync("alpha", id), flags);
        Assert.Equal("owner", refused.Answer);
        Assert.DoesNotContain("migration-changes-existing-data", System.Text.Json.JsonSerializer.Serialize(refused.Data));
    }

    [Fact]
    public async Task A_flag_that_says_nothing_was_checked_cannot_be_cleared()
    {
        var id = await AtReview();
        await Begin();
        var flags = new FakeFlags(new StewardFlag("project-flags-did-not-run", Owner: true, Blocks: false));
        Assert.Equal("refused", (await In(s => s.OwnerClearsFlagAsync("alpha", id, "project-flags-did-not-run"), flags)).Answer);
        Assert.Equal("owner", (await In(s => s.MergeAsync("alpha", id), flags)).Answer);
    }

    [Fact]
    public void A_change_keeps_its_id_through_a_rebase_and_not_through_rework()
    {
        const string before = "diff --git a/x.sql b/x.sql\nindex 111..222 100644\n@@ -1,2 +1,3 @@ table\n a\n+b\n";
        const string rebased = "diff --git a/x.sql b/x.sql\nindex 333..444 100644\n@@ -9,2 +9,3 @@ table\n a\n+b\n";
        const string reworked = "diff --git a/x.sql b/x.sql\nindex 333..555 100644\n@@ -9,2 +9,3 @@ table\n a\n+c\n";
        Assert.Equal(TicketGitService.ChangeIdOf(before), TicketGitService.ChangeIdOf(rebased));
        Assert.NotEqual(TicketGitService.ChangeIdOf(before), TicketGitService.ChangeIdOf(reworked));
    }

    [Fact]
    public async Task A_branch_behind_its_base_is_rebased_first()
    {
        _git.Behind = 2;
        var id = await AtReview();
        await Begin();
        Assert.Equal("behind", (await In(s => s.MergeAsync("alpha", id))).Answer);
    }

    [Fact]
    public async Task A_failing_check_stops_the_merge_and_a_machine_fault_is_not_the_tickets()
    {
        Configure("full", """
            "plan": { "units": "docs/units" },
            "mergeChecks": [ { "name": "tests", "run": "run-tests", "machineFault": "database is not running" } ]
            """);
        var id = await AtReview();
        await Begin();

        _git.Check = new CheckRun(false, false, "2 tests failed");
        Assert.Equal("check-failed", (await In(s => s.MergeAsync("alpha", id))).Answer);

        _git.Check = new CheckRun(false, false, "the database is not running");
        Assert.Equal("check-could-not-run", (await In(s => s.MergeAsync("alpha", id))).Answer);

        _git.Check = new CheckRun(true, false, "");
        Assert.Equal("merged", (await In(s => s.MergeAsync("alpha", id))).Answer);
    }

    [Fact]
    public async Task Only_a_ticket_at_review_is_merged()
    {
        await using (var ctx = NewCtx())
        {
            ctx.Tickets.Add(new Ticket { ProjectKey = "alpha", Title = "Still being built", Phase = TicketPhase.Implementing, Branch = "b" });
            await ctx.SaveChangesAsync();
        }
        await Begin();
        Assert.Equal("not-ready", (await In(s => s.MergeAsync("alpha", 1))).Answer);
    }

    // ---- sending back, answering ------------------------------------------------------

    private const string WhatIsWrong =
        "The empty state still says 'No results' where the ticket asked for a way to clear the filter; done is a Clear button.";

    [Fact]
    public async Task A_send_back_leaves_a_note_the_worker_must_answer_and_is_counted()
    {
        var id = await AtReview();
        await Begin();
        Assert.Equal("sent-back", (await In(s => s.SendBackAsync("alpha", id, "NeedsImplementation", WhatIsWrong))).Answer);

        var t = await Reload(id);
        Assert.Equal(TicketPhase.NeedsImplementation, t.Phase);
        Assert.Single(HandoffRequirements.UnansweredOwnerNotes(t));
        Assert.Equal(AuthorKind.Steward, HandoffRequirements.UnansweredOwnerNotes(t)[0].Author);

        // Back at review a second time, the limit (one, here) makes the next send-back the owner's.
        await using (var ctx = NewCtx())
        {
            (await ctx.Tickets.FindAsync(id))!.Phase = TicketPhase.NeedsReview;
            await ctx.SaveChangesAsync();
        }
        Assert.Equal("limit", (await In(s => s.SendBackAsync("alpha", id, "NeedsImplementation", WhatIsWrong))).Answer);
    }

    [Fact]
    public async Task A_blocked_ticket_is_answered_back_to_the_queue_it_stopped_in()
    {
        int id;
        await using (var ctx = NewCtx())
        {
            var t = new Ticket { ProjectKey = "alpha", Title = "Asked a question", Phase = TicketPhase.Implementing };
            ctx.Tickets.Add(t);
            await ctx.SaveChangesAsync();
            id = t.Id;
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.Blocked, "Which of the two date formats does the spec mean?");
        }
        await Begin();
        Assert.Equal("answered", (await In(s => s.AnswerBlockAsync("alpha", id, "ISO dates: unit 1, section 3."))).Answer);
        Assert.Equal(TicketPhase.NeedsImplementation, (await Reload(id)).Phase);
    }

    // ---- the owner ------------------------------------------------------------------

    private static StewardAsk Question(string key = "sms-provider") => new(key,
        "Which provider sends our texts?", "The first notice ticket cannot be written without one.",
        new[] { new BlockOption("The one we already pay for", "No new bill", true), new BlockOption("A new one", "Costs money") },
        "Every ticket that sends a text.");

    [Fact]
    public async Task A_question_is_asked_once_for_good()
    {
        await Begin();
        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", Question()))).Answer);
        Assert.Equal("already-asked", (await In(s => s.AskAsync("alpha", Question()))).Answer);

        await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "The one we already pay for."));
        Assert.Equal("already-asked", (await In(s => s.AskAsync("alpha", Question()))).Answer);
    }

    [Fact]
    public async Task A_question_must_carry_a_pick()
    {
        await Begin();
        var noPick = Question() with { Options = new[] { new BlockOption("One"), new BlockOption("Two") } };
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", noPick))).Answer);
    }

    [Fact]
    public async Task The_owners_answer_is_a_reason_to_run_and_reaches_the_next_run_once()
    {
        await Begin();
        await In(s => s.AskAsync("alpha", Question()));
        await In(s => s.EndAsync("alpha", "asked one question", true));
        Assert.False((await In0(s => s.DueAsync("alpha"))).Due);   // resting: nothing has changed

        Assert.Equal("recorded", (await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "The one we already pay for."))).Answer);
        var due = await In0(s => s.DueAsync("alpha"));
        Assert.True(due.Due);
        Assert.Contains("sms-provider", due.Reason);

        await Begin();
        await In(s => s.EndAsync("alpha", "applied the answer", true));
        Assert.False((await In0(s => s.DueAsync("alpha"))).Due);
        Assert.Equal("decisions", (await In(s => s.DecisionsAsync("alpha", "provider"))).Answer);
    }

    [Fact]
    public async Task An_instruction_from_the_owner_is_put_in_front_of_the_next_run_once()
    {
        await In(s => s.OwnerSaysAsync("alpha", "From now on a ticket carries one whole outcome."));
        Assert.True((await In0(s => s.DueAsync("alpha"))).Due);

        var first = System.Text.Json.JsonSerializer.Serialize((await Begin()).Data);
        Assert.Contains("one whole outcome", first);
        await In(s => s.EndAsync("alpha", "applied it", true));

        var second = System.Text.Json.JsonSerializer.Serialize((await Begin()).Data);
        Assert.DoesNotContain("one whole outcome", second.Split("\"status\"")[0]);   // not news twice
        Assert.Contains("one whole outcome",
            System.Text.Json.JsonSerializer.Serialize((await In(s => s.DecisionsAsync("alpha", null))).Data));
    }

    [Fact]
    public async Task A_record_kept_elsewhere_is_taken_in_once_and_is_not_news()
    {
        var id = await AtReview();
        var import = new StewardImport(
            Questions: new[]
            {
                new StewardImportQuestion("sms-provider", "Which provider sends our texts?", Answer: "The one we already pay for."),
                new StewardImportQuestion("map-provider", "Which map provider looks addresses up?", "It decides the address box.",
                    new[] { new BlockOption("None", null, true), new BlockOption("A paid one") }, "The address screen."),
            },
            Entries: new[]
            {
                new StewardImportEntry("OwnerSaid", "A ticket carries one whole outcome."),
                new StewardImportEntry("Call", "Kept the wording: unit 1, A2.", id),
            },
            Outcomes: new[] { new StewardImportOutcome(1, "A1", "InProgress", true, "The screen is left.") },
            Tickets: new[] { new StewardImportTicket(id, 1, new[] { "A1" }) },
            Memo: "Swept group A.");

        Assert.Equal("imported", (await In(s => s.ImportAsync("alpha", import))).Answer);
        Assert.Equal("imported", (await In(s => s.ImportAsync("alpha", import))).Answer);   // again: nothing doubles

        await using (var ctx = NewCtx())
        {
            Assert.Equal(2, await ctx.StewardQuestions.CountAsync());
            Assert.Equal(2, await ctx.StewardEntries.CountAsync());
            Assert.Equal("A1", (await ctx.Tickets.FindAsync(id))!.PlanOutcomes);
            Assert.Equal("Swept group A.", (await ctx.StewardStates.FindAsync("alpha"))!.Memo);
        }

        // History, not news: the first run is not handed any of it as new, and can still find it.
        var begin = System.Text.Json.JsonSerializer.Serialize((await Begin()).Data);
        Assert.Contains("\"newAnswers\":[]", begin);
        Assert.Contains("\"newInstructions\":[]", begin);
        Assert.Contains("map-provider", begin);   // still waiting on the owner
        Assert.Contains("one whole outcome",
            System.Text.Json.JsonSerializer.Serialize((await In(s => s.DecisionsAsync("alpha", null))).Data));
        Assert.Equal("already-asked", (await In(s => s.AskAsync("alpha", Question()))).Answer);
    }

    [Fact]
    public async Task The_owners_decisions_are_written_out_to_the_repo_when_it_names_a_file_for_them()
    {
        Configure("full", """ "plan": { "units": "docs/units" }, "decisionsFile": "docs/decisions.md" """);
        await Begin();
        await In(s => s.AskAsync("alpha", Question()));
        await In(s => s.EndAsync("alpha", "asked", true));
        Assert.Empty(_git.Committed);   // nothing decided yet, so nothing to write

        await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "The one we already pay for."));
        await In(s => s.OwnerSaysAsync("alpha", "A ticket carries one whole outcome."));

        var last = _git.Committed[^1];
        Assert.Equal("docs/decisions.md", last.Path);
        Assert.Contains("**sms-provider**: Which provider sends our texts? The one we already pay for.", last.Content);
        Assert.Contains("A ticket carries one whole outcome.", last.Content);
    }

    [Fact]
    public async Task The_steward_cannot_write_what_the_owner_said()
    {
        var id = await AtReview();
        await Begin();
        Assert.Equal("refused", (await In(s => s.RecordAsync("alpha", StewardEntryKind.OwnerSaid, id, "Ship it."))).Answer);
        Assert.Equal("noted", (await In(s => s.RecordAsync("alpha", StewardEntryKind.Call, id, "Kept the wording: unit 1, A2."))).Answer);
    }

    [Fact]
    public async Task A_note_for_a_release_says_what_kind_it_is()
    {
        var id = await AtReview();
        await Begin();
        Assert.Equal("bad-request", (await In(s => s.RecordAsync("alpha", StewardEntryKind.BeforeRelease, id, "Adds a table."))).Answer);
        Assert.Equal("bad-request", (await In(s => s.RecordAsync("alpha", StewardEntryKind.BeforeRelease, id, "Adds a table.", "0"))).Answer);
        Assert.Equal("noted", (await In(s => s.RecordAsync("alpha", StewardEntryKind.BeforeRelease, id, "Adds a table.", "Migration"))).Answer);

        var note = Entries().Single(e => e.Kind == StewardEntryKind.BeforeRelease);
        Assert.Equal(StewardReleaseKind.Migration, note.Release);
        Assert.Null(note.HandledAt);
    }

    [Fact]
    public async Task Ticking_a_note_off_and_sorting_it_are_the_owners()
    {
        var id = await AtReview();
        await Begin();
        await In(s => s.RecordAsync("alpha", StewardEntryKind.BeforeRelease, id, "Words to check.", "note"));
        await In(s => s.RecordAsync("alpha", StewardEntryKind.Call, id, "Kept the wording."));
        var note = Entries().Single(e => e.Kind == StewardEntryKind.BeforeRelease).Id;
        var call = Entries().Single(e => e.Kind == StewardEntryKind.Call).Id;

        Assert.Equal("recorded", (await In(s => s.OwnerMarksEntryAsync("alpha", note, true, "action"))).Answer);
        var row = Entries().Single(e => e.Id == note);
        Assert.Equal(StewardReleaseKind.Action, row.Release);
        Assert.NotNull(row.HandledAt);

        Assert.Equal("recorded", (await In(s => s.OwnerMarksEntryAsync("alpha", note, false, null))).Answer);
        Assert.Null(Entries().Single(e => e.Id == note).HandledAt);

        // A call has no kind, and another project's line is not this one's to mark.
        Assert.Equal("bad-request", (await In(s => s.OwnerMarksEntryAsync("alpha", call, null, "migration"))).Answer);
        Assert.Equal("no-entry", (await In(s => s.OwnerMarksEntryAsync("bravo", note, true, null))).Answer);

        await In(s => s.OwnerSortsReleaseNotesAsync("alpha", new Dictionary<int, string> { [note] = "config", [call] = "config", [9999] = "note" }));
        Assert.Equal(StewardReleaseKind.Config, Entries().Single(e => e.Id == note).Release);
        Assert.Null(Entries().Single(e => e.Id == call).Release);
    }

    [Fact]
    public async Task The_owner_reads_the_plan_with_its_words()
    {
        var id = await AtReview();
        await using (var ctx = NewCtx())
        {
            var t = await ctx.Tickets.SingleAsync(x => x.Id == id);
            (t.PlanUnit, t.PlanOutcomes) = (1, "A1");
            await ctx.SaveChangesAsync();
        }
        var r = await In(s => s.PlanViewAsync("alpha"));
        Assert.Equal("plan", r.Answer);
        var json = System.Text.Json.JsonSerializer.Serialize(r.Data);
        // Each outcome carries its sentence, and the ticket that delivers it is named on it.
        Assert.Contains("\"Text\":\"One.\"", json);
        Assert.Contains($"\"Id\":{id}", json);
    }

    [Fact]
    public void A_spec_gives_each_outcome_its_sentence_and_its_heading()
    {
        var p = new StewardPlan("docs/units", StewardPlan.DefaultUnitFile, StewardPlan.DefaultAgreed,
            StewardPlan.DefaultDraft, StewardPlan.DefaultOutcome);
        var unit = TicketGitService.ReadUnit(p, 1, "docs/units/01-first.md", string.Join("\n",
            "# Unit 1: the first thing", "", "> **Status: agreed 2026-01-01.**", "",
            "## Outcomes", "", "### A. Signing in", "",
            "- **A1.** A person can sign in,", "  and lands on a home of their own.", "- **A2.** They can sign out.", "",
            "A paragraph that belongs to no outcome.", "",
            "### B. Paying", "", "- **B1.** They can save a card.", "- not an outcome", "",
            "## Decided", "", "- **A1.** mentioned again, and not a second outcome."));

        Assert.Equal("Unit 1: the first thing", TicketGitService.UnitTitle(unit));
        var groups = TicketGitService.UnitGroups(p, unit);
        Assert.Equal(new[] { "A. Signing in", "B. Paying" }, groups.Select(g => g.Heading));
        Assert.Equal("A person can sign in, and lands on a home of their own.", groups[0].Outcomes[0].Text);
        Assert.Equal(new[] { "A1", "A2" }, groups[0].Outcomes.Select(o => o.Outcome));
        Assert.Equal("They can save a card.", groups[1].Outcomes.Single().Text);
    }

    // ---- a unit, from a draft to released ---------------------------------------------

    private async Task<int> Built(int unit, string outcomes, string? planKey = null, TicketPhase phase = TicketPhase.Done)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = $"Work for {outcomes}", Phase = phase, PlanUnit = unit, PlanOutcomes = outcomes, PlanKey = planKey };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    [Fact]
    public async Task The_owner_agrees_a_draft_from_the_board_and_it_is_the_same_commit()
    {
        Assert.Equal("already-agreed", (await In(s => s.OwnerAgreesUnitAsync("alpha", 1))).Answer);
        Assert.Equal("bad-request", (await In(s => s.OwnerAgreesUnitAsync("alpha", 9))).Answer);
        Assert.Empty(_git.Committed);

        Assert.Equal("agreed", (await In(s => s.OwnerAgreesUnitAsync("alpha", 2))).Answer);
        var (path, content) = _git.Committed.Single();
        Assert.Equal("docs/units/02-second.md", path);
        Assert.Contains($"**Status: agreed {DateTime.Now:yyyy-MM-dd}.**", content);
        Assert.DoesNotContain("draft", content);
        // Agreeing is news for the steward: there is now something to file against.
        await using var ctx = NewCtx();
        Assert.Contains("agreed unit 2", ctx.StewardStates.Single().DueReason);
        Assert.NotNull(ctx.StewardUnits.Single(u => u.Unit == 2).AgreedAt);
    }

    // ---- a unit's open decisions -------------------------------------------------------

    private const string WithDecisions = """
        # Unit 2: the second thing

        > **Status: draft, awaiting the owner.**

        - **A1.** One.

        ## To decide

        Each is open until the owner answers.

        - **Decide (#4) How long an invite lasts.** Options: (a) 14 days; (b) 30 days, or (c) never. **Pick: (b),** long enough for a slow reader.
        - **Decide (#5) The reminder.** A reminder a few days before it lapses, or none. **Pick: one reminder 3 days before.**
        - **(#6) Who sends it.** Options: (a) we do; (b) the business copies a link (the business is the sender).
          It is mail we start ourselves.

        ## Decided

        Nothing yet.

        ## Done means

        It works.
        """;

    [Fact]
    public void A_spec_lists_its_open_decisions_with_their_options_and_pick()
    {
        var open = UnitDecisions.Open(WithDecisions);
        Assert.Equal(new[] { 4, 5, 6 }, open.Select(d => d.Number));

        Assert.Equal("How long an invite lasts", open[0].Title);
        Assert.Equal(new[] { "a", "b", "c" }, open[0].Options.Select(o => o.Letter));
        Assert.Equal(new[] { "14 days", "30 days", "never" }, open[0].Options.Select(o => o.Text));
        Assert.Equal(new[] { "b" }, open[0].PickLetters);
        Assert.Equal("(b)", open[0].Pick);
        Assert.Equal("long enough for a slow reader.", open[0].PickWhy);

        // A pick that is not one of the letters is a pick all the same.
        Assert.Empty(open[1].Options);
        Assert.Empty(open[1].PickLetters);
        Assert.Equal("one reminder 3 days before", open[1].Pick);

        // A decision that asks two things picks two letters.
        var two = UnitDecisions.Open("## To decide\n\n- **(#8) Two things.** Options: (a) one; (b) two. And later: (c) three; (d) four. **Pick: (a) and (c):** both cheap.\n").Single();
        Assert.Equal(new[] { "a", "c" }, two.PickLetters);
        Assert.Equal("two", two.Options[1].Text);

        // "(the business)" is not an option, and a bullet runs on over its indented line.
        Assert.Equal(new[] { "we do", "the business copies a link (the business is the sender)" }, open[2].Options.Select(o => o.Text));
        Assert.Null(open[2].Pick);
        Assert.Contains("mail we start ourselves", open[2].Text);

        // The template's heading and the older one are read alike; a decided one is not open.
        Assert.Single(UnitDecisions.Open("## Decide\n\n- **Decide (#9) X.** Options: (a) one; (b) two.\n"));
        Assert.Empty(UnitDecisions.Open("## Decided\n\n- **(#9) X.** Decided by the owner 2026-01-01: (a) one.\n"));
    }

    [Fact]
    public void Settling_a_decision_moves_it_under_decided_in_the_owners_words()
    {
        var day = new DateTime(2026, 3, 4);
        var (text, said, no) = UnitDecisions.Settle(WithDecisions,
            new[] { new UnitDecisionChoice(4), new UnitDecisionChoice(6, "a", "Copying the link stays possible.") }, day);
        Assert.Null(no);
        Assert.Equal(new[] { "#4 (b), as picked", "#6 (a): Copying the link stays possible." }, said);
        Assert.Equal(new[] { 5 }, UnitDecisions.Open(text!).Select(d => d.Number));
        Assert.Contains("- **(#4) How long an invite lasts.** Decided by the owner 2026-03-04: (b) 30 days.", text);
        Assert.Contains("- **(#6) Who sends it.** Decided by the owner 2026-03-04: (a) we do. Copying the link stays possible.", text);
        // Under "Decided", after what it already said, and before the next section.
        Assert.True(text!.IndexOf("Nothing yet.") < text.IndexOf("(#4)") && text.IndexOf("(#6)") < text.IndexOf("## Done means"));

        // The last one settled takes the open section with it.
        var (all, _, _) = UnitDecisions.Settle(text, new[] { new UnitDecisionChoice(5) }, day);
        Assert.DoesNotContain("## To decide", all);
        Assert.Contains("Decided by the owner 2026-03-04: one reminder 3 days before.", all);

        // Nothing is written for a sheet that is half wrong.
        Assert.NotNull(UnitDecisions.Settle(WithDecisions, new[] { new UnitDecisionChoice(4), new UnitDecisionChoice(7) }, day).Refusal);
        Assert.NotNull(UnitDecisions.Settle(WithDecisions, new[] { new UnitDecisionChoice(4, "d") }, day).Refusal);
        // No pick to take and nothing said is no answer; something said is.
        Assert.NotNull(UnitDecisions.Settle(WithDecisions, new[] { new UnitDecisionChoice(6) }, day).Refusal);
        Assert.Null(UnitDecisions.Settle(WithDecisions, new[] { new UnitDecisionChoice(6, null, "Neither: we send it by hand.") }, day).Refusal);
    }

    [Fact]
    public async Task The_owner_agrees_a_unit_and_settles_its_decisions_in_one_commit()
    {
        _git.Second = WithDecisions;
        var r = await In(s => s.OwnerDecidesAsync("alpha", 2, new[] { new UnitDecisionChoice(4), new UnitDecisionChoice(5) }, true, null));
        Assert.Equal("agreed", r.Answer);
        var (path, content) = _git.Committed.Single();
        Assert.Equal("docs/units/02-second.md", path);
        Assert.Contains("**Status: agreed", content);
        Assert.Contains("(#4) How long an invite lasts.** Decided by the owner", content);
        // What was left open stays open for the steward to ask about.
        Assert.Equal(new[] { 6 }, UnitDecisions.Open(content).Select(d => d.Number));

        await using var ctx = NewCtx();
        Assert.Contains("agreed unit 2", ctx.StewardStates.Single().DueReason);
        Assert.Contains("#4 (b), as picked", ctx.StewardEntries.Single(e => e.Kind == StewardEntryKind.OwnerSaid).Text);
    }

    [Fact]
    public async Task A_question_can_ask_for_a_units_decisions_and_settling_them_answers_it()
    {
        _git.Second = WithDecisions;
        await Begin();
        var ask = Question("invite-picks") with { Options = null, Unit = 2, Decisions = new[] { 4, 5 } };
        // It names decisions the spec really leaves open, and a unit with them.
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", ask with { Decisions = new[] { 4, 9 } }))).Answer);
        Assert.Equal("bad-request", (await In(s => s.AskAsync("alpha", ask with { Unit = null }))).Answer);
        // Without options of its own: the decisions are its options.
        Assert.Equal("asked", (await In(s => s.AskAsync("alpha", ask))).Answer);

        Assert.Equal("decided", (await In(s => s.OwnerDecidesAsync("alpha", 2,
            new[] { new UnitDecisionChoice(4, "a"), new UnitDecisionChoice(5) }, false, "invite-picks"))).Answer);
        await using var ctx = NewCtx();
        var q = ctx.StewardQuestions.Single();
        Assert.Equal(2, q.PlanUnit);
        Assert.Equal("4,5", q.Decisions);
        Assert.Contains("#4 (a)", q.Answer);
        Assert.Contains("invite-picks", ctx.StewardStates.Single().DueReason);
        // Said once: as the answer, not again as an instruction beside it.
        Assert.DoesNotContain(ctx.StewardEntries, e => e.Kind == StewardEntryKind.OwnerSaid);
        Assert.Equal("not-waiting", (await In(s => s.OwnerDecidesAsync("alpha", 2, new[] { new UnitDecisionChoice(6, "a") }, false, "invite-picks"))).Answer);
    }

    [Fact]
    public void A_status_line_is_rewritten_only_when_the_result_reads_as_agreed()
    {
        var p = new StewardPlan("docs/units", StewardPlan.DefaultUnitFile, StewardPlan.DefaultAgreed,
            StewardPlan.DefaultDraft, StewardPlan.DefaultOutcome);
        var day = new DateTime(2026, 3, 4);

        var marked = TicketGitService.MarkAgreed(p, "# One\n\n> **Status: draft, awaiting the owner.** Written last week.\n\n- **A1.** One.\n", day);
        Assert.Equal("# One\n\n> **Status: agreed 2026-03-04.** Written last week.\n\n- **A1.** One.\n", marked);

        // No status, an agreed one, and a bold run that never closes on its line are all left alone.
        Assert.Null(TicketGitService.MarkAgreed(p, "# One\n\n- **A1.** One.\n", day));
        Assert.Null(TicketGitService.MarkAgreed(p, "> **Status: agreed 2026-01-01.**\n", day));
        Assert.Null(TicketGitService.MarkAgreed(p, "> **Status: draft\n\n**later**\n", day));
        // A project whose agreed line does not satisfy its own pattern gets nothing written.
        Assert.Null(TicketGitService.MarkAgreed(p with { AgreedLine = "**Status: ready.**" }, "> **Status: draft.**\n", day));
    }

    [Fact]
    public async Task A_unit_is_released_only_when_it_is_built_and_its_notes_are_dealt_with()
    {
        var id = await Built(1, "A1, A2");
        Assert.Equal("unit-not-agreed", (await In(s => s.OwnerReleasesUnitAsync("alpha", 2, true))).Answer);
        Assert.Equal("not-built", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true))).Answer);

        await Begin();
        await In(s => s.SetOutcomeAsync("alpha", 1, "A1", "Done", false, null));
        await In(s => s.SetOutcomeAsync("alpha", 1, "A2", "Done", false, null));
        await In(s => s.RecordAsync("alpha", StewardEntryKind.BeforeRelease, id, "Adds a table.", "migration"));
        await In(s => s.RecordAsync("alpha", StewardEntryKind.BeforeRelease, id, "It rounds down.", "note"));

        Assert.Equal("notes-outstanding", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true))).Answer);
        var migration = Entries().Single(e => e.Release == StewardReleaseKind.Migration).Id;
        await In(s => s.OwnerMarksEntryAsync("alpha", migration, true, null));

        Assert.Equal("released", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true))).Answer);
        // What was only to know went out with it.
        Assert.All(Entries().Where(e => e.Kind == StewardEntryKind.BeforeRelease), e => Assert.NotNull(e.HandledAt));
        await using (var ctx = NewCtx())
        {
            var row = ctx.StewardUnits.Single(u => u.Unit == 1);
            Assert.NotNull(row.ReleasedAt);
            Assert.Equal("abc", row.ReleasedSha);
        }
        Assert.Contains("\"stage\":\"released\"", System.Text.Json.JsonSerializer.Serialize((await In(s => s.PlanViewAsync("alpha"))).Data));

        Assert.Equal("already-released", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true))).Answer);
        Assert.Equal("recorded", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, false))).Answer);
        await using var after = NewCtx();
        Assert.Null(after.StewardUnits.Single(u => u.Unit == 1).ReleasedAt);
    }

    [Fact]
    public async Task A_note_with_no_unit_goes_out_with_the_next_release()
    {
        var loose = await AtReview();   // a ticket filed outside the plan
        await Built(1, "A1, A2");
        await Begin();
        await In(s => s.SetOutcomeAsync("alpha", 1, "A1", "Done", false, null));
        await In(s => s.SetOutcomeAsync("alpha", 1, "A2", "Done", false, null));
        await In(s => s.RecordAsync("alpha", StewardEntryKind.BeforeRelease, loose, "Set the variable.", "config"));

        Assert.Equal("notes-outstanding", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true))).Answer);
    }

    // ---- one plan after another -------------------------------------------------------

    [Fact]
    public async Task The_last_plans_unit_one_is_not_this_plans()
    {
        // The plan being worked has a key; a ticket and an outcome left by the plan before it do not.
        var old = await Built(1, "A1");
        await using (var ctx = NewCtx())
        {
            ctx.StewardOutcomes.Add(new StewardOutcome { ProjectKey = "alpha", Plan = "", Unit = 1, Outcome = "A1", State = StewardOutcomeState.Done });
            await ctx.SaveChangesAsync();
        }
        Configure("full", """
            "plan": { "key": "second", "name": "The second plan", "units": "docs/units" }
            """);

        var json = System.Text.Json.JsonSerializer.Serialize((await In(s => s.PlanViewAsync("alpha"))).Data);
        Assert.DoesNotContain($"\"Id\":{old}", json);
        Assert.DoesNotContain("\"state\":\"Done\"", json);
        Assert.Contains("\"name\":\"The second plan\"", json);

        // What a run records now is filed under the plan's own key, beside the old row and not over it.
        await Begin();
        Assert.Equal("recorded", (await In(s => s.SetOutcomeAsync("alpha", 1, "A1", "InProgress", false, null))).Answer);
        await using var after = NewCtx();
        Assert.Equal(new[] { "", "second" }, after.StewardOutcomes.Where(o => o.Outcome == "A1").Select(o => o.Plan).OrderBy(x => x));
        Assert.Equal(StewardOutcomeState.Done, after.StewardOutcomes.Single(o => o.Plan == "").State);
    }

    private const string PlanTemplate = "# {{NAME}}\n\nStarted {{DATE}}.\n";
    private const string UnitTemplate = "# Unit 1\n\n> **Status: draft, awaiting the owner.**\n\n- **A1.** A person can ...\n";

    [Fact]
    public async Task A_new_plan_is_its_two_documents_and_waits_for_the_last_one_to_finish()
    {
        Assert.Equal("bad-request", (await In(s => s.OwnerStartsPlanAsync("alpha", "Not A Key", "Next", null, PlanTemplate, UnitTemplate))).Answer);
        Assert.Equal("bad-request", (await In(s => s.OwnerStartsPlanAsync("alpha", "next", "Next thing", "../outside", PlanTemplate, UnitTemplate))).Answer);
        // The plan being worked has no key, so "" is taken; and it has a ticket still open.
        var open = await Built(1, "A1", phase: TicketPhase.Implementing);
        var held = await In(s => s.OwnerStartsPlanAsync("alpha", "next", "Next thing", null, PlanTemplate, UnitTemplate));
        Assert.Equal("plan-in-flight", held.Answer);
        Assert.Contains($"#{open}", held.Why);
        Assert.Empty(_git.Committed);

        await using (var ctx = NewCtx())
        {
            (await ctx.Tickets.SingleAsync(t => t.Id == open)).Phase = TicketPhase.Done;
            await ctx.SaveChangesAsync();
        }
        _git.InRepo["docs/plans/taken/PLAN.md"] = "already here";
        Assert.Equal("exists", (await In(s => s.OwnerStartsPlanAsync("alpha", "taken", "Taken", null, PlanTemplate, UnitTemplate))).Answer);

        var r = await In(s => s.OwnerStartsPlanAsync("alpha", "next", "Next thing", null, PlanTemplate, UnitTemplate));
        Assert.Equal("started", r.Answer);
        Assert.Equal(new[] { "docs/plans/next/PLAN.md", "docs/plans/next/units/01-first-unit.md" }, _git.Committed.Select(c => c.Path));
        Assert.StartsWith("# Next thing", _git.Committed[0].Content);
        Assert.DoesNotContain("{{", _git.Committed[0].Content);
    }

    [Fact]
    public async Task Starting_a_plan_keeps_the_one_before_it_to_be_read()
    {
        var paths = new DashboardPaths(_dataRoot);
        var file = paths.ProjectConfigDefaults("alpha");
        Assert.Null(await StewardPlanFile.StartAsync(file, "next", "Next thing", "docs/plans/next/units", "docs/plans/next/PLAN.md"));

        var cfg = new ProjectRegistry(paths).GetProjectConfig("alpha");
        Assert.Equal("next", cfg.Steward.Plan.Key);
        Assert.Equal("Next thing", cfg.Steward.Plan.DisplayName);
        Assert.Equal("docs/plans/next/PLAN.md", cfg.Steward.Plan.Master);
        var past = Assert.Single(cfg.Steward.PastPlans);
        Assert.Equal("docs/units", past.UnitsDir);
        Assert.Same(past, cfg.Steward.PlanByKey(""));
        // Everything else the project said is still said.
        Assert.Equal(2, cfg.Steward.CreatedPerDay);
        Assert.Equal("Alpha", cfg.DisplayName);
    }

    // ---- when a run is owed -----------------------------------------------------------

    [Fact]
    public async Task A_ticket_arriving_for_review_or_blocking_makes_a_run_due()
    {
        await Begin();
        await In(s => s.EndAsync("alpha", "nothing to do", true));
        Assert.False((await In0(s => s.DueAsync("alpha"))).Due);

        await using (var ctx = NewCtx())
        {
            var t = new Ticket { ProjectKey = "alpha", Title = "Nearly there", Phase = TicketPhase.Refining };
            ctx.Tickets.Add(t);
            await ctx.SaveChangesAsync();
            await new TicketService(ctx).TransitionAsync(t.Id, TicketPhase.NeedsReview, null, TransitionActor.Human);
        }
        var due = await In0(s => s.DueAsync("alpha"));
        Assert.True(due.Due);
        Assert.Contains("ready for review", due.Reason);
    }

    [Fact]
    public async Task A_resting_steward_runs_again_when_the_base_moves()
    {
        await Begin();
        await In(s => s.EndAsync("alpha", "nothing to do", true));
        _git.Head = "moved";
        Assert.True((await In0(s => s.DueAsync("alpha"))).Due);
    }

    [Fact]
    public async Task A_run_is_not_started_again_moments_after_the_last_one_began()
    {
        _minGap = TimeSpan.FromMinutes(5);
        Assert.True((await In0(s => s.DueAsync("alpha"))).Due);            // never run: due at once
        Assert.Equal("launching", (await In(s => s.LaunchingAsync("alpha"))).Answer);
        // The agent died before it could open the run. It is still an attempt, and not retried at once.
        Assert.False((await In0(s => s.DueAsync("alpha"))).Due);
    }

    [Fact]
    public async Task A_memo_over_its_limit_is_saved_and_said_to_be_too_long()
    {
        await Begin();
        Assert.Equal("saved", (await In(s => s.SaveMemoAsync("alpha", "Next: file A2 once #1 merges."))).Answer);
        Assert.Equal("saved-too-long", (await In(s => s.SaveMemoAsync("alpha", new string('x', 1001)))).Answer);
    }

    [Fact]
    public async Task Only_the_next_unit_can_be_drafted_and_never_as_agreed()
    {
        await Begin();
        const string draft = "> **Status: draft, awaiting the owner.**\n\n## Outcomes\n\n- **A1.** Something new.\n";
        Assert.Equal("not-next", (await In(s => s.DraftUnitAsync("alpha", "04-later.md", draft))).Answer);
        Assert.Equal("bad-request", (await In(s => s.DraftUnitAsync("alpha", "02-second.md", draft.Replace("draft, awaiting the owner", "agreed")))).Answer);
        Assert.Equal("exists", (await In(s => s.DraftUnitAsync("alpha", "02-other-name.md", draft))).Answer);
        Assert.Equal("revised", (await In(s => s.DraftUnitAsync("alpha", "02-second.md", draft))).Answer);
    }

    // ---- a merge that outlasts the call --------------------------------------------------

    [Fact]
    public async Task A_merge_slower_than_a_call_answers_checking_and_the_next_call_joins_it()
    {
        var started = 0;
        var checks = new TaskCompletionSource<StewardReply>();
        var merges = new StewardMerges((_, _) => { started++; return checks.Task; }) { Wait = TimeSpan.FromMilliseconds(50) };

        Assert.Equal("checking", (await merges.MergeAsync("alpha", 1)).Answer);
        Assert.Equal("checking", (await merges.MergeAsync("alpha", 1)).Answer);

        checks.SetResult(new StewardReply("merged"));
        Assert.Equal("merged", (await merges.MergeAsync("alpha", 1)).Answer);
        Assert.Equal(1, started);

        // The answer was collected, so the next call is a new merge and not that one again.
        checks = new TaskCompletionSource<StewardReply>();
        checks.SetResult(new StewardReply("not-ready"));
        Assert.Equal("not-ready", (await merges.MergeAsync("alpha", 1)).Answer);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task A_call_that_drops_does_not_take_the_merge_with_it()
    {
        var started = 0;
        var checks = new TaskCompletionSource<StewardReply>();
        var merges = new StewardMerges((_, _) => { started++; return checks.Task; }) { Wait = TimeSpan.FromMinutes(5) };

        using var dropped = new CancellationTokenSource();
        var call = merges.MergeAsync("alpha", 1, dropped.Token);
        dropped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        checks.SetResult(new StewardReply("merged"));
        Assert.Equal("merged", (await merges.MergeAsync("alpha", 1)).Answer);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task A_run_that_ended_during_the_checks_gets_no_merge()
    {
        Configure("full", """
            "plan": { "units": "docs/units" },
            "mergeChecks": [ { "name": "tests", "run": "run-tests" } ]
            """);
        var id = await AtReview();
        await Begin();
        _git.DuringCheck = () => In(s => s.EndAsync("alpha", "ended early", false));

        Assert.Equal("no-run", (await In(s => s.MergeAsync("alpha", id))).Answer);
        Assert.Empty(_git.Merged);
        Assert.Equal(TicketPhase.NeedsReview, (await Reload(id)).Phase);
    }

    // ---- fakes ------------------------------------------------------------------------

    private sealed class FakeFlags(params StewardFlag[] flags) : IStewardProjectFlags
    {
        public Task<IReadOnlyList<StewardFlag>> RunAsync(string projectKey, StewardFlagInput input, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StewardFlag>>(flags);
    }

    // ---- a run that dies part-way ----------------------------------------------------

    private async Task<string> BeginJson() => System.Text.Json.JsonSerializer.Serialize((await Begin()).Data);

    [Fact]
    public async Task A_run_that_dies_part_way_leaves_its_answers_and_reasons_for_the_next()
    {
        await Begin();
        await In(s => s.AskAsync("alpha", Question()));
        await In(s => s.EndAsync("alpha", "asked one question", false));
        await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "The one we already pay for."));

        // Handed the answer, then killed before steward_end.
        Assert.Contains("The one we already pay for.", await BeginJson());
        await using (var c = NewCtx()) Assert.True(await Steward(c).AbandonRunAsync("alpha", "the process ended"));
        await using (var ctx = NewCtx())
        {
            var st = await ctx.StewardStates.FindAsync("alpha");
            Assert.Contains("the owner answered sms-provider", st!.DueReason);
            Assert.Contains("the last run did not finish", st.DueReason);
            Assert.Null((await ctx.StewardQuestions.SingleAsync()).AnswerDeliveredAt);
        }

        // The next run is handed the same answer, and why it is due.
        var again = await BeginJson();
        Assert.Contains("The one we already pay for.", again);
        Assert.Contains("the last run did not finish", again);
    }

    [Fact]
    public async Task A_run_that_ends_settles_what_it_was_handed_and_keeps_what_came_in_meanwhile()
    {
        await Begin();
        await In(s => s.AskAsync("alpha", Question()));
        await In(s => s.AskAsync("alpha", Question("map-provider")));
        await In(s => s.EndAsync("alpha", "asked two questions", false));
        await In(s => s.OwnerAnswersAsync("alpha", "sms-provider", "The one we already pay for."));

        await Begin();
        // While it runs: the owner answers the other, and a ticket comes for review.
        await Task.Delay(20);
        await In(s => s.OwnerAnswersAsync("alpha", "map-provider", "None."));
        await using (var c = NewCtx()) await Steward(c).MarkDueAsync("alpha", "#9 is ready for review");
        Assert.Equal("ended", (await In(s => s.EndAsync("alpha", "applied an answer", false))).Answer);

        await using var ctx = NewCtx();
        var st = (await ctx.StewardStates.FindAsync("alpha"))!;
        Assert.DoesNotContain("sms-provider", st.DueReason);
        Assert.Contains("map-provider", st.DueReason);
        Assert.Contains("#9 is ready for review", st.DueReason);
        Assert.Null(st.RunDueReason);
        var qs = await ctx.StewardQuestions.ToListAsync();
        Assert.NotNull(qs.Single(q => q.Key == "sms-provider").AnswerDeliveredAt);
        Assert.Null(qs.Single(q => q.Key == "map-provider").AnswerDeliveredAt);
    }

    // ---- ticket notes ----------------------------------------------------------------

    private static string Json(StewardReply r) => System.Text.Json.JsonSerializer.Serialize(r.Data);

    [Fact]
    public async Task A_ticket_note_is_shown_by_review_and_status_until_it_is_closed()
    {
        var id = await AtReview();
        Assert.Equal("no-run", (await In(s => s.NoteTicketAsync("alpha", id, "Merge it once rebased."))).Answer);

        await Begin();
        var noted = await In(s => s.NoteTicketAsync("alpha", id, "When it merges, set A1 Done."));
        Assert.Equal("noted", noted.Answer);
        var noteId = (int)noted.Data!.GetType().GetProperty("id")!.GetValue(noted.Data)!;

        Assert.Contains("When it merges, set A1 Done.", Json(await In(s => s.ReviewAsync("alpha", id))));
        var status = Json(await In(s => s.StatusAsync("alpha")));
        Assert.Contains("ticketNotes", status);
        Assert.Contains("When it merges, set A1 Done.", status);

        // A run that dies keeps it: the next run is still shown it.
        await using (var c = NewCtx()) await Steward(c).AbandonRunAsync("alpha", "killed");
        Assert.Contains("When it merges, set A1 Done.", await BeginJson());

        Assert.Equal("done", (await In(s => s.DoneWithAsync("alpha", noteId))).Answer);
        Assert.Equal("already-done", (await In(s => s.DoneWithAsync("alpha", noteId))).Answer);
        Assert.DoesNotContain("set A1 Done", Json(await In(s => s.ReviewAsync("alpha", id))));
        Assert.DoesNotContain("set A1 Done", Json(await In(s => s.StatusAsync("alpha"))));
    }

    [Fact]
    public async Task A_ticket_note_is_one_thing_on_a_ticket_of_this_project()
    {
        var id = await AtReview();
        await Begin();
        Assert.Equal("bad-request", (await In(s => s.NoteTicketAsync("alpha", id, "  "))).Answer);
        Assert.Equal("bad-request", (await In(s => s.NoteTicketAsync("alpha", id, new string('x', StewardService.TicketNoteMaxChars + 1)))).Answer);
        Assert.NotEqual("noted", (await In(s => s.NoteTicketAsync("alpha", id + 50, "No such ticket."))).Answer);
        Assert.Equal("not-found", (await In(s => s.DoneWithAsync("alpha", 9999))).Answer);
        // Only a ticket note can be closed this way, not a call or a release note.
        await In(s => s.RecordAsync("alpha", StewardEntryKind.Call, id, "Kept the wording."));
        var call = Entries().Single(e => e.Kind == StewardEntryKind.Call).Id;
        Assert.Equal("not-found", (await In(s => s.DoneWithAsync("alpha", call))).Answer);
    }

    [Fact]
    public async Task Propose_mode_only_says_it_would_note_a_ticket()
    {
        Configure("propose");
        var id = await AtReview();
        await Begin();
        Assert.Equal("proposed", (await In(s => s.NoteTicketAsync("alpha", id, "When it merges, set A1 Done."))).Answer);
        Assert.DoesNotContain(Entries(), e => e.Kind == StewardEntryKind.TicketNote);
    }

    [Fact]
    public async Task Taking_a_ticket_out_of_the_plan_closes_its_notes()
    {
        var id = await Loose();
        Assert.Equal("linked", (await In(s => s.LinkTicketAsync(id, null, 1, new[] { "A1" }))).Answer);
        await Begin();
        await In(s => s.NoteTicketAsync("alpha", id, "When it merges, set A1 Done."));
        await In(s => s.LinkTicketAsync(id, null, null, null));
        Assert.NotNull(Entries().Single(e => e.Kind == StewardEntryKind.TicketNote).HandledAt);
    }

    // ---- the owner's link from a ticket to the plan --------------------------------

    private async Task<int> Loose(string? description = null)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "Made by hand", Description = description, Phase = TicketPhase.Backlog };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private async Task<StewardState?> State() { await using var ctx = NewCtx(); return await ctx.StewardStates.FindAsync("alpha"); }

    [Fact]
    public async Task The_owner_links_a_hand_made_ticket_to_a_unit_and_the_steward_is_owed_a_run()
    {
        var id = await Loose();
        var r = await In(s => s.LinkTicketAsync(id, null, 1, new[] { "A2", "A1", "A1" }));
        Assert.Equal("linked", r.Answer);

        var t = await Reload(id);
        Assert.Equal(1, t.PlanUnit);
        Assert.Equal("", t.PlanKey);
        Assert.Equal("A1, A2", t.PlanOutcomes); // the spec's order, once each
        Assert.Contains($"linked #{id} to unit 1", (await State())!.DueReason);
    }

    [Fact]
    public async Task A_unit_can_be_linked_without_naming_an_outcome_and_a_draft_unit_counts()
    {
        var id = await Loose();
        Assert.Equal("linked", (await In(s => s.LinkTicketAsync(id, null, 2, null))).Answer);
        var t = await Reload(id);
        Assert.Equal(2, t.PlanUnit);
        Assert.Equal("", t.PlanOutcomes);
    }

    [Fact]
    public async Task A_unit_or_outcome_the_plan_does_not_have_is_refused_and_nothing_changes()
    {
        var id = await Loose();
        Assert.Equal("bad-request", (await In(s => s.LinkTicketAsync(id, null, 9, null))).Answer);
        Assert.Equal("bad-request", (await In(s => s.LinkTicketAsync(id, null, 1, new[] { "Q9" }))).Answer);
        Assert.Equal("no-plan", (await In(s => s.LinkTicketAsync(id, "nope", 1, null))).Answer);
        Assert.Equal("not-found", (await In(s => s.LinkTicketAsync(id + 100, null, 1, null))).Answer);
        Assert.Null((await Reload(id)).PlanUnit);
    }

    [Fact]
    public async Task Taking_a_filed_ticket_out_of_the_plan_clears_the_link_and_its_line_in_the_description()
    {
        await Begin();
        Assert.Equal("created", (await In(s => s.CreateAsync("alpha", Draft(outcomes: new[] { "A1" })))).Answer);
        int id;
        await using (var ctx = NewCtx()) id = (await ctx.Tickets.SingleAsync()).Id;
        Assert.Contains("Unit 1: A1.", (await Reload(id)).Description);

        Assert.Equal("linked", (await In(s => s.LinkTicketAsync(id, null, 1, new[] { "A2" }))).Answer);
        var moved = (await Reload(id)).Description!;
        Assert.Contains("Unit 1: A2.", moved);
        Assert.DoesNotContain("Unit 1: A1.", moved);

        Assert.Equal("linked", (await In(s => s.LinkTicketAsync(id, null, null, null))).Answer);
        var t = await Reload(id);
        Assert.Null(t.PlanUnit);
        Assert.Null(t.PlanKey);
        Assert.Null(t.PlanOutcomes);
        Assert.DoesNotContain("**Plan**", t.Description);
        Assert.StartsWith(Whole[..20], t.Description);
    }

    [Fact]
    public async Task Linking_works_with_the_steward_off_and_owes_it_nothing()
    {
        Configure("off");
        var id = await Loose("Something to fix.");
        Assert.Equal("linked", (await In(s => s.LinkTicketAsync(id, null, 1, new[] { "A1" }))).Answer);
        Assert.Equal("Something to fix.", (await Reload(id)).Description); // no line added to one that had none
        Assert.Null((await State())?.DueReason);
        Assert.Equal("unchanged", (await In(s => s.LinkTicketAsync(id, null, 1, new[] { "A1" }))).Answer);
    }

    /// <summary>A repository with one agreed unit (A1, A2), one draft, and a branch that is ready to merge.</summary>
    private sealed class FakeGit : IStewardGit
    {
        public string Head = "abc";
        public IReadOnlyList<string> Files = new[] { "src/app.ts" };
        public int Behind;
        /// <summary>The change as the owner would see it; a new value is the ticket reworked.</summary>
        public string ChangeId = "change-1";
        public CheckRun Check = new(true, false, "");
        public List<string> Merged = new();

        private static readonly StewardPlan P = new("docs/units", StewardPlan.DefaultUnitFile, StewardPlan.DefaultAgreed,
            StewardPlan.DefaultDraft, StewardPlan.DefaultOutcome);

        public Task<string?> BaseHeadAsync(string projectKey, CancellationToken ct = default) => Task.FromResult<string?>(Head);

        /// <summary>The second unit's spec, which a test may give decisions to leave open.</summary>
        public string Second = "> **Status: draft, awaiting the owner.**\n\n- **A1.** One.\n";

        public Task<IReadOnlyList<PlanUnit>> PlanAsync(string projectKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlanUnit>>(new[]
            {
                TicketGitService.ReadUnit(P, 1, "docs/units/01-first.md", "> **Status: agreed 2026-01-01.**\n\n- **A1.** One.\n- **A2.** Two.\n"),
                TicketGitService.ReadUnit(P, 2, "docs/units/02-second.md", Second),
            });

        public Task<IReadOnlyList<PlanUnit>> PlanOfAsync(string projectKey, StewardPlan plan, CancellationToken ct = default) =>
            PlanAsync(projectKey, ct);

        /// <summary>What is already in the repo, by path, for the reads that ask whether a file is there.</summary>
        public Dictionary<string, string> InRepo = new();

        public Task<string?> FileAtBaseAsync(string projectKey, string path, CancellationToken ct = default) =>
            Task.FromResult(InRepo.TryGetValue(path, out var text) ? text : null);

        public Task<BranchChange?> BranchChangeAsync(string projectKey, string branch, CancellationToken ct = default) =>
            Task.FromResult<BranchChange?>(new BranchChange(Files, Behind, 1, "1 file changed", "main", "tip0123456", ChangeId));

        public Task<(string Text, bool Cut, int Length)?> BranchDiffAsync(string projectKey, string branch, string? path, int maxChars, CancellationToken ct = default) =>
            Task.FromResult<(string, bool, int)?>(("diff", false, 4));

        public Task<int> AheadOfBaseAsync(string projectKey, string from, CancellationToken ct = default) => Task.FromResult(0);

        public Task<SyncFromResult> SyncFromAsync(string projectKey, string from, bool dryRun, CancellationToken ct = default) =>
            Task.FromResult(new SyncFromResult("up-to-date", 0, Array.Empty<string>(), null));

        public List<(string Path, string Content)> Committed = new();

        public Task<(string? Sha, bool Unchanged, string? Refusal)> CommitFileAsync(string projectKey, string relativePath, string content, string message, CancellationToken ct = default)
        {
            Committed.Add((relativePath, content));
            return Task.FromResult<(string?, bool, string?)>(("def", false, null));
        }

        public Task<bool> WorktreeAtTipAsync(string projectKey, string worktree, string branch, CancellationToken ct = default) => Task.FromResult(true);

        /// <summary>Something that happens while a check runs, for what a slow check lets change under it.</summary>
        public Func<Task>? DuringCheck;

        public async Task<CheckRun> RunCheckAsync(string dir, string command, TimeSpan timeout, CancellationToken ct = default)
        {
            if (DuringCheck is not null) await DuringCheck();
            return Check;
        }

        public Task<MergeResult> MergeAsync(string projectKey, string branch, string? title, string? description, CancellationToken ct = default)
        {
            Merged.Add(branch);
            return Task.FromResult(new MergeResult(true, null, false, Array.Empty<string>(), branch, "main", "", "0123456789", false, 1, Array.Empty<string>()));
        }

        public Task<RebaseResult> RebaseAsync(string projectKey, string branch, CancellationToken ct = default) =>
            Task.FromResult(new RebaseResult(true, null, false, Array.Empty<string>(), branch, "main", "a", "b", "", Array.Empty<string>()));
    }
}
