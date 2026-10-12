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
        return new StewardService(ctx, new TicketService(ctx, dependencies: deps, leases: leases), registry, _git, leases, deps, flags);
    }

    private async Task<StewardReply> In(Func<StewardService, Task<StewardReply>> act, IStewardProjectFlags? flags = null)
    {
        await using var ctx = NewCtx();
        return await act(Steward(ctx, flags));
    }

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

        Assert.Equal(TicketPhase.Done, (await Reload(id)).Phase);
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
    public async Task The_steward_cannot_write_what_the_owner_said()
    {
        var id = await AtReview();
        await Begin();
        Assert.Equal("refused", (await In(s => s.RecordAsync("alpha", StewardEntryKind.OwnerSaid, id, "Ship it."))).Answer);
        Assert.Equal("noted", (await In(s => s.RecordAsync("alpha", StewardEntryKind.Call, id, "Kept the wording: unit 1, A2."))).Answer);
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

    // ---- fakes ------------------------------------------------------------------------

    private sealed class FakeFlags(params StewardFlag[] flags) : IStewardProjectFlags
    {
        public Task<IReadOnlyList<StewardFlag>> RunAsync(string projectKey, StewardFlagInput input, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StewardFlag>>(flags);
    }

    /// <summary>A repository with one agreed unit (A1, A2), one draft, and a branch that is ready to merge.</summary>
    private sealed class FakeGit : IStewardGit
    {
        public string Head = "abc";
        public IReadOnlyList<string> Files = new[] { "src/app.ts" };
        public int Behind;
        public CheckRun Check = new(true, false, "");
        public List<string> Merged = new();

        private static readonly StewardPlan P = new("docs/units", StewardPlan.DefaultUnitFile, StewardPlan.DefaultAgreed,
            StewardPlan.DefaultDraft, StewardPlan.DefaultOutcome);

        public Task<string?> BaseHeadAsync(string projectKey, CancellationToken ct = default) => Task.FromResult<string?>(Head);

        public Task<IReadOnlyList<PlanUnit>> PlanAsync(string projectKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlanUnit>>(new[]
            {
                TicketGitService.ReadUnit(P, 1, "docs/units/01-first.md", "> **Status: agreed 2026-01-01.**\n\n- **A1.** One.\n- **A2.** Two.\n"),
                TicketGitService.ReadUnit(P, 2, "docs/units/02-second.md", "> **Status: draft, awaiting the owner.**\n\n- **A1.** One.\n"),
            });

        public Task<BranchChange?> BranchChangeAsync(string projectKey, string branch, CancellationToken ct = default) =>
            Task.FromResult<BranchChange?>(new BranchChange(Files, Behind, 1, "1 file changed", "main"));

        public Task<(string Text, bool Cut, int Length)?> BranchDiffAsync(string projectKey, string branch, string? path, int maxChars, CancellationToken ct = default) =>
            Task.FromResult<(string, bool, int)?>(("diff", false, 4));

        public Task<int> AheadOfBaseAsync(string projectKey, string from, CancellationToken ct = default) => Task.FromResult(0);

        public Task<SyncFromResult> SyncFromAsync(string projectKey, string from, bool dryRun, CancellationToken ct = default) =>
            Task.FromResult(new SyncFromResult("up-to-date", 0, Array.Empty<string>(), null));

        public Task<(string? Sha, bool Unchanged, string? Refusal)> CommitFileAsync(string projectKey, string relativePath, string content, string message, CancellationToken ct = default) =>
            Task.FromResult<(string?, bool, string?)>(("def", false, null));

        public Task<bool> WorktreeAtTipAsync(string projectKey, string worktree, string branch, CancellationToken ct = default) => Task.FromResult(true);

        public Task<CheckRun> RunCheckAsync(string dir, string command, TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(Check);

        public Task<MergeResult> MergeAsync(string projectKey, string branch, string? title, string? description, CancellationToken ct = default)
        {
            Merged.Add(branch);
            return Task.FromResult(new MergeResult(true, null, false, Array.Empty<string>(), branch, "main", "", "0123456789", false, 1, Array.Empty<string>()));
        }

        public Task<RebaseResult> RebaseAsync(string projectKey, string branch, CancellationToken ct = default) =>
            Task.FromResult(new RebaseResult(true, null, false, Array.Empty<string>(), branch, "main", "a", "b", "", Array.Empty<string>()));
    }
}
