using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Doc = Whetstone.Services.AnalysisDocument;

namespace Whetstone.Tests;

/// <summary>
/// The independent analysis between QA and refinement: the claim waits for it, hands its findings
/// to the refinement pass, and the clean hand-off is refused until every finding is ruled on and
/// none is a defect. See <see cref="ClassroomGate"/>.
/// </summary>
public class ClassroomGateTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public ClassroomGateTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private static bool AllSkillsExist(string project, string skill) => true;

    /// <summary>A gate that answers whatever the test tells it to, and counts the questions.</summary>
    private sealed class FakeGate(GateReadiness answer) : IClassroomGate
    {
        public int Asked;
        public Task<GateReadiness> CheckAsync(Ticket t) { Asked++; return Task.FromResult(answer); }
    }

    private static string DocWith(params Doc.Block[] blocks) =>
        Doc.Serialize(new Doc.Doc("headline", [new Doc.Section("What would catch you out", blocks)]));

    private static Doc.CoverageSegment Seg(string id, string text, string verdict, string? evidence = null) =>
        new(id, "line", text, 0, null, null, verdict, evidence, null, null, null, null, null);

    private static readonly string ThreeFindings = DocWith(
        new Doc.Block("callout") { Tone = "risk", Title = "The picker and the group disagree", Text = "Office only vs office and chart type." },
        new Doc.Block("callout") { Tone = "gotcha", Title = "Overwrite re-reads the screen", Text = "Teaching, not a defect." },
        new Doc.Block("callout") { Tone = "insight", Title = "itemCount is an invariant", Text = "Also teaching." },
        new Doc.Block("coverage")
        {
            Text = "summary",
            Segments =
            [
                Seg("S1", "Save only the account numbers", "met", "done"),
                Seg("S2", "Each month for each office", "gap", "nothing scopes by month"),
                Seg("S3", "Load then post", "partial", "load yes, post untested"),
                Seg("S4", "Background", "context"),
                Seg("S5", "Unclear line", "unknown"),
            ],
        });

    private async Task<(int ticketId, int analysisId)> SeedRefinable(string blocksJson = "", bool skip = false)
    {
        await using var ctx = NewCtx();
        var t = new Ticket
        {
            ProjectKey = "bravo", Title = "t", Phase = TicketPhase.NeedsRefinement,
            Branch = "jb/1", ImplementationSummary = "done", SkipClassroomAnalysis = skip,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        var a = new TicketAnalysis
        {
            TicketId = t.Id, ProjectKey = "bravo", PhaseAtGeneration = TicketPhase.NeedsRefinement,
            BlocksJson = blocksJson.Length > 0 ? blocksJson : ThreeFindings,
        };
        ctx.TicketAnalyses.Add(a);
        await ctx.SaveChangesAsync();
        return (t.Id, a.Id);
    }

    // ---- which findings a document yields -----------------------------------

    [Fact]
    public void Findings_are_the_risk_callouts_and_the_gaps_and_partials_only()
    {
        var findings = ClassroomGate.ExtractFindings(7, 9, ThreeFindings);

        Assert.Equal(
            new[] { GateFindingKind.Risk, GateFindingKind.Gap, GateFindingKind.Partial },
            findings.Select(f => f.Kind));
        Assert.Equal("The picker and the group disagree", findings[0].Title);
        // A coverage finding is titled with the ticket's own words, and carries the map's evidence.
        Assert.Equal("Each month for each office", findings[1].Title);
        Assert.Equal("nothing scopes by month", findings[1].Text);
        Assert.All(findings, f => { Assert.Equal(7, f.TicketId); Assert.Equal(9, f.AnalysisId); });
        Assert.Equal(new[] { 0, 1, 2 }, findings.Select(f => f.Order));
    }

    [Fact]
    public void An_unreadable_document_yields_no_findings_rather_than_throwing()
    {
        Assert.Empty(ClassroomGate.ExtractFindings(1, 1, "not json"));
    }

    [Fact]
    public void The_gate_applies_to_delivery_tickets_in_refinement_unless_skipped()
    {
        var t = new Ticket { Phase = TicketPhase.NeedsRefinement, Track = TicketTrack.Delivery };
        Assert.True(ClassroomGate.Applies(t));

        t.SkipClassroomAnalysis = true;
        Assert.False(ClassroomGate.Applies(t));

        t.SkipClassroomAnalysis = false;
        t.Track = TicketTrack.Review;
        Assert.False(ClassroomGate.Applies(t));

        t.Track = TicketTrack.Delivery;
        t.Phase = TicketPhase.NeedsQa;
        Assert.False(ClassroomGate.Applies(t));
    }

    // ---- the claim ----------------------------------------------------------

    [Fact]
    public async Task A_ticket_whose_analysis_is_not_ready_is_passed_over_not_claimed()
    {
        var (id, _) = await SeedRefinable();
        var gate = new FakeGate(GateReadiness.NotYet);

        await using var ctx = NewCtx();
        Assert.Null(await new TicketService(ctx, classroomGate: gate).ClaimNextAsync(null, AllSkillsExist));

        Assert.Equal(1, gate.Asked);
        await using var check = NewCtx();
        Assert.Equal(TicketPhase.NeedsRefinement, (await check.Tickets.FindAsync(id))!.Phase);
    }

    [Fact]
    public async Task The_claim_hands_the_analysis_and_its_findings_to_refinement()
    {
        var (id, analysisId) = await SeedRefinable();

        await using var ctx = NewCtx();
        var result = await new TicketService(ctx, classroomGate: new FakeGate(new GateReadiness(true, analysisId)))
            .ClaimNextAsync(null, AllSkillsExist);

        Assert.NotNull(result);
        Assert.Equal(TicketPhase.Refining, result!.Ticket.Phase);
        Assert.Equal(analysisId, result.Ticket.GateAnalysisId);
        Assert.Equal(3, HandoffRequirements.CurrentGateFindings(result.Ticket).Count);
        Assert.Contains(result.Ticket.Comments, c => c.Body.Contains("raised 3 finding(s)"));
    }

    [Fact]
    public async Task Returning_to_refinement_on_the_same_analysis_keeps_the_rulings_already_made()
    {
        var (id, analysisId) = await SeedRefinable();
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx, classroomGate: new FakeGate(new GateReadiness(true, analysisId)));
            await svc.ClaimNextAsync(null, AllSkillsExist);
            var first = (await svc.GetAsync(id))!.GateFindings.First();
            await svc.RuleOnFindingAsync(id, first.Id, GateRuling.NotADefect, "GLAccountListQuery.cs:42 is the same filter.");
        }

        // Back in the queue on the same code - the reviewer's NeedsReview -> NeedsRefinement, say.
        await using (var ctx = NewCtx())
        {
            (await ctx.Tickets.FindAsync(id))!.Phase = TicketPhase.NeedsRefinement;
            await ctx.SaveChangesAsync();
        }

        await using var again = NewCtx();
        var result = await new TicketService(again, classroomGate: new FakeGate(new GateReadiness(true, analysisId)))
            .ClaimNextAsync(null, AllSkillsExist);

        var findings = HandoffRequirements.CurrentGateFindings(result!.Ticket);
        Assert.Equal(3, findings.Count);
        Assert.Equal(GateRuling.NotADefect, findings[0].Ruling);
    }

    [Fact]
    public async Task Refinement_without_an_analysis_says_why_on_the_ticket()
    {
        var (id, _) = await SeedRefinable();

        await using var ctx = NewCtx();
        var result = await new TicketService(ctx,
                classroomGate: new FakeGate(new GateReadiness(true, Note: "there is no checkout of this branch on disk for it to read")))
            .ClaimNextAsync(null, AllSkillsExist);

        Assert.Null(result!.Ticket.GateAnalysisId);
        Assert.Contains(result.Ticket.Comments, c => c.Body.Contains("without an independent Classroom analysis: there is no checkout"));
    }

    [Fact]
    public async Task A_failed_analysis_blocks_the_ticket_instead_of_refining_without_it()
    {
        var (id, _) = await SeedRefinable();
        var reason = "The independent Classroom analysis failed: the analysis timed out after 30 minutes. Unblock to run it again.";

        await using (var ctx = NewCtx())
            Assert.Null(await new TicketService(ctx, classroomGate: new FakeGate(GateReadiness.Blocked(reason)))
                .ClaimNextAsync(null, AllSkillsExist));

        await using var check = NewCtx();
        var t = (await check.Tickets.FindAsync(id))!;
        Assert.Equal(TicketPhase.Blocked, t.Phase);
        Assert.Equal(TicketPhase.NeedsRefinement, t.PhaseBeforeBlock);
        Assert.Equal(reason, t.BlockedReason);
    }

    [Fact]
    public async Task Other_phases_never_ask_the_gate()
    {
        await using (var ctx = NewCtx())
        {
            ctx.Tickets.Add(new Ticket { ProjectKey = "bravo", Title = "t", Phase = TicketPhase.NeedsQa });
            await ctx.SaveChangesAsync();
        }
        var gate = new FakeGate(GateReadiness.NotYet);

        await using var svcCtx = NewCtx();
        Assert.NotNull(await new TicketService(svcCtx, classroomGate: gate).ClaimNextAsync(null, AllSkillsExist));
        Assert.Equal(0, gate.Asked);
    }

    // ---- the hand-off -------------------------------------------------------

    private async Task<(TicketService svc, DashboardDbContext ctx, int id, List<GateFinding> findings)> Refining()
    {
        var (id, analysisId) = await SeedRefinable();
        var ctx = NewCtx();
        var svc = new TicketService(ctx, classroomGate: new FakeGate(new GateReadiness(true, analysisId)));
        await svc.ClaimNextAsync(null, AllSkillsExist);
        await svc.SetLearningBriefAsync(id, "## The area\nManual GL entry.");
        var findings = HandoffRequirements.CurrentGateFindings((await svc.GetAsync(id))!).ToList();
        return (svc, ctx, id, findings);
    }

    [Fact]
    public async Task Review_is_refused_while_a_finding_is_unruled()
    {
        var (svc, ctx, id, findings) = await Refining();
        await using var _ = ctx;
        await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.NotADefect, "checked, it is the same filter");

        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            svc.TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker));
        Assert.Contains("Not yet ruled", ex.Message);
        Assert.Contains($"#{findings[1].Id}", ex.Message);
        Assert.DoesNotContain($"#{findings[0].Id}", ex.Message);
    }

    [Fact]
    public async Task Review_is_refused_when_a_finding_is_ruled_a_defect_but_implementation_is_open()
    {
        var (svc, ctx, id, findings) = await Refining();
        await using var _ = ctx;
        await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.Defect, "Save filters by office only.");
        foreach (var f in findings.Skip(1))
            await svc.RuleOnFindingAsync(id, f.Id, GateRuling.NotADefect, "not asked for");

        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            svc.TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker));
        Assert.Contains("a Defect", ex.Message);

        var back = await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "the picker filter", TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsImplementation, back!.Phase);
    }

    [Fact]
    public async Task Review_is_allowed_once_every_finding_is_ruled_not_a_defect()
    {
        var (svc, ctx, id, findings) = await Refining();
        await using var _ = ctx;
        foreach (var f in findings)
            await svc.RuleOnFindingAsync(id, f.Id, GateRuling.NotADefect, "checked against the code");

        var t = await svc.TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsReview, t!.Phase);
    }

    // ---- owner decides -------------------------------------------------------

    private async Task RuleRestNotADefect(TicketService svc, int id, IEnumerable<GateFinding> rest)
    {
        foreach (var f in rest)
            await svc.RuleOnFindingAsync(id, f.Id, GateRuling.NotADefect, "checked against the code");
    }

    private static readonly BlockPayload ScopeQuestion = new(null,
    [
        new BlockQuestion("Should this ticket give outside signers read access, or leave it to a follow-up?",
            "The status page reads a route that refuses them.",
            [new BlockOption("Leave it to a follow-up", "hide the preview when it 404s", Recommended: true),
             new BlockOption("Handle it here", "a signer-aware read path")]),
    ]);

    [Fact]
    public async Task A_finding_the_owner_decides_leaves_the_block_as_the_only_move()
    {
        var (svc, ctx, id, findings) = await Refining();
        await using var _ = ctx;
        await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.OwnerDecides,
            "Real: signers outside the creating org get no documents. Fix here, or follow-up?");
        await RuleRestNotADefect(svc, id, findings.Skip(1));

        var review = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            svc.TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker));
        Assert.Contains("OwnerDecides", review.Message);
        Assert.Contains("Blocked", review.Message);

        // Sideways is the run answering the question by acting on it.
        await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "do it here", TransitionActor.Worker));

        var blocked = await svc.TransitionAsync(id, TicketPhase.Blocked, null, TransitionActor.Worker, ScopeQuestion);
        Assert.Equal(TicketPhase.Blocked, blocked!.Phase);
    }

    [Fact]
    public async Task The_guide_says_so_on_every_move_out_of_refinement()
    {
        var (svc, ctx, id, findings) = await Refining();
        await using var _ = ctx;
        await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.OwnerDecides, "real; scope is the owner's");
        var t = (await svc.GetAsync(id))!;

        Assert.Contains("owner's answer", HandoffRequirements.GuideSummary(t, TicketPhase.Refining, TicketPhase.NeedsImplementation));
        Assert.DoesNotContain("OwnerDecides", HandoffRequirements.GuideSummary(t, TicketPhase.Refining, TicketPhase.Blocked) ?? "");
    }

    [Fact]
    public async Task The_run_cannot_rule_its_own_question_a_defect_before_the_owner_answers()
    {
        var (svc, ctx, id, findings) = await Refining();
        await using var _ = ctx;
        await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.OwnerDecides, "real; scope is the owner's");

        var (f, error) = await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.Defect, "I'll just do it");
        Assert.Null(f);
        Assert.Contains("waiting on the owner's answer", error);

        // Withdrawing the question because the claim was false is a finding about the code, and stays open.
        var (withdrawn, _) = await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.NotADefect, "Foo.cs:12 already checks it");
        Assert.Equal(GateRuling.NotADefect, withdrawn!.Ruling);
    }

    [Fact]
    public async Task The_owner_unblocking_answers_it_and_refinement_re_rules_on_the_answer()
    {
        var (svc, ctx, id, findings) = await Refining();
        await using var _ = ctx;
        await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.OwnerDecides, "real; scope is the owner's");
        await RuleRestNotADefect(svc, id, findings.Skip(1));
        await svc.TransitionAsync(id, TicketPhase.Blocked, null, TransitionActor.Worker, ScopeQuestion);

        await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, "Leave it to a follow-up", TransitionActor.Human);
        await svc.ClaimNextAsync(null, AllSkillsExist);
        var answered = HandoffRequirements.CurrentGateFindings((await svc.GetAsync(id))!).First();
        Assert.NotNull(answered.OwnerAnsweredAt);

        // Answered, but not yet re-ruled on the answer: still not ready for review.
        var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            svc.TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker));
        Assert.Contains("has answered", ex.Message);

        await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.NotADefect,
            "Owner: leave it to a follow-up; the preview already hides on a 404.");
        var t = await svc.TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsReview, t!.Phase);
    }

    [Fact]
    public async Task A_worker_unblocking_does_not_answer_for_the_owner()
    {
        var (svc, ctx, id, findings) = await Refining();
        await using var _ = ctx;
        await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.OwnerDecides, "real; scope is the owner's");
        await svc.TransitionAsync(id, TicketPhase.Blocked, null, TransitionActor.Worker, ScopeQuestion);

        // The System actor (the claim lock, a breaker) moving it is not the owner either.
        await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.System);
        Assert.Null(HandoffRequirements.CurrentGateFindings((await svc.GetAsync(id))!).First().OwnerAnsweredAt);
    }

    [Fact]
    public async Task The_guide_publishes_the_requirement_before_the_move()
    {
        var (svc, ctx, id, _) = await Refining();
        await using var __ = ctx;
        var t = (await svc.GetAsync(id))!;

        var guide = HandoffRequirements.GuideSummary(t, TicketPhase.Refining, TicketPhase.NeedsReview);
        Assert.Contains("rule_on_finding", guide);
        Assert.Null(HandoffRequirements.GuideSummary(t, TicketPhase.Refining, TicketPhase.NeedsImplementation));
    }

    [Fact]
    public async Task A_person_moving_the_ticket_is_not_held_to_the_rulings()
    {
        var (svc, ctx, id, _) = await Refining();
        await using var _ = ctx;

        var t = await svc.TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Human);
        Assert.Equal(TicketPhase.NeedsReview, t!.Phase);
    }

    // ---- ruling -------------------------------------------------------------

    [Fact]
    public async Task A_ruling_needs_a_reason()
    {
        var (svc, ctx, id, findings) = await Refining();
        await using var _ = ctx;

        var (f, error) = await svc.RuleOnFindingAsync(id, findings[0].Id, GateRuling.NotADefect, "  ");
        Assert.Null(f);
        Assert.Contains("Say why", error);
    }

    [Fact]
    public async Task A_finding_from_an_earlier_analysis_cannot_be_ruled_on()
    {
        var (svc, ctx, id, _) = await Refining();
        await using var _ = ctx;
        ctx.GateFindings.Add(new GateFinding { TicketId = id, AnalysisId = 999, Kind = GateFindingKind.Risk, Title = "old" });
        await ctx.SaveChangesAsync();
        var old = await ctx.GateFindings.SingleAsync(f => f.AnalysisId == 999);

        var (f, error) = await svc.RuleOnFindingAsync(id, old.Id, GateRuling.NotADefect, "reason");
        Assert.Null(f);
        Assert.Contains("earlier analysis", error);
    }

    // ---- the flag -----------------------------------------------------------

    [Fact]
    public async Task The_skip_flag_is_set_on_create_and_editable_afterwards()
    {
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        var t = await svc.CreateAsync("bravo", "t", null, null, null, TicketPhase.Backlog, skipClassroomAnalysis: true);
        Assert.True(t.SkipClassroomAnalysis);

        var off = await svc.UpdateAsync(t.Id, null, null, null, null, null, null, skipClassroomAnalysis: false);
        Assert.False(off!.SkipClassroomAnalysis);

        // Absent on an update means unchanged, like every other flag there.
        var untouched = await svc.UpdateAsync(t.Id, "renamed", null, null, null, null, null);
        Assert.False(untouched!.SkipClassroomAnalysis);
    }

    // ---- staleness ----------------------------------------------------------

    [Fact]
    public void An_analysis_written_before_refinement_is_still_fresh_at_review_after_the_squash()
    {
        var a = new TicketAnalysis
        {
            PhaseAtGeneration = TicketPhase.NeedsRefinement, HeadSha = "before-squash", TreeSha = "tree-1",
        };
        var t = new Ticket { Phase = TicketPhase.NeedsReview };

        Assert.False(TicketAnalysisService.IsStale(a, t, "after-squash", "tree-1"));
        Assert.True(TicketAnalysisService.IsStale(a, t, "after-squash", "tree-2"));
    }

    [Fact]
    public void Without_trees_the_commit_still_decides()
    {
        var a = new TicketAnalysis { PhaseAtGeneration = TicketPhase.NeedsReview, HeadSha = "one" };
        var t = new Ticket { Phase = TicketPhase.NeedsReview };

        Assert.True(TicketAnalysisService.IsStale(a, t, "two", "tree"));
        Assert.False(TicketAnalysisService.IsStale(a, t, "one", null));
    }
}
