using Microsoft.EntityFrameworkCore;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// docs/slice-branches.md, piece 1: agreeing a slice is the moment its branch is cut, on a project
/// with slice branches, and on no other.
/// </summary>
public partial class StewardServiceTests
{
    /// <summary>The project as <see cref="Configure"/> writes it, with slice branches switched on.</summary>
    private void SwitchToSliceBranches()
    {
        var paths = new DashboardPaths(_dataRoot);
        var file = paths.ProjectConfigDefaults("alpha");
        var text = File.ReadAllText(file);
        File.WriteAllText(file, text.Replace("\"mergeInsteadOfPr\": true,", "\"mergeInsteadOfPr\": true, \"sliceBranches\": true,"));
        Assert.True(new ProjectRegistry(paths).GetProjectConfig("alpha").SliceBranchesOn);
    }

    [Fact]
    public async Task Agreeing_a_slice_cuts_its_branch_and_records_it()
    {
        SwitchToSliceBranches();
        Assert.Equal("agreed", (await In(s => s.OwnerAgreesUnitAsync("alpha", 2))).Answer);

        Assert.Equal(new[] { "slice/02-second" }, _git.Cut);
        await using var ctx = NewCtx();
        Assert.Equal("slice/02-second", ctx.StewardUnits.Single(u => u.Unit == 2).Branch);
    }

    [Fact]
    public async Task With_slice_branches_a_slice_is_agreed_whether_or_not_an_earlier_one_is()
    {
        _git.More[3] = ("docs/units/03-third.md", "> **Status: draft, awaiting the owner.**\n\n- **A1.** One.\n");
        // Without the switch, units are agreed in order: unit 2 is still a draft.
        Assert.Equal("not-next", (await In(s => s.OwnerAgreesUnitAsync("alpha", 3))).Answer);
        SwitchToSliceBranches();
        Assert.Equal("agreed", (await In(s => s.OwnerAgreesUnitAsync("alpha", 3))).Answer);
        Assert.Equal(new[] { "slice/03-third" }, _git.Cut);
    }

    [Fact]
    public async Task Without_the_switch_agreeing_cuts_nothing()
    {
        Assert.Equal("agreed", (await In(s => s.OwnerAgreesUnitAsync("alpha", 2))).Answer);
        Assert.Empty(_git.Cut);
        await using var ctx = NewCtx();
        Assert.Null(ctx.StewardUnits.Single(u => u.Unit == 2).Branch);
    }

    [Fact]
    public async Task With_no_local_base_to_cut_from_a_slice_is_not_agreed()
    {
        SwitchToSliceBranches();
        _git.Head = null!;
        Assert.Equal("no-base", (await In(s => s.OwnerAgreesUnitAsync("alpha", 2))).Answer);
        Assert.Empty(_git.Committed);
        Assert.Empty(_git.Cut);
    }

    [Fact]
    public async Task A_branch_that_will_not_cut_leaves_the_agreement_standing_and_says_so()
    {
        SwitchToSliceBranches();
        _git.CutRefusal = "could not cut it";
        var reply = await In(s => s.OwnerAgreesUnitAsync("alpha", 2));
        Assert.Equal("agreed", reply.Answer);
        Assert.Contains("could not cut it", System.Text.Json.JsonSerializer.Serialize(reply.Data));
        await using var ctx = NewCtx();
        Assert.Null(ctx.StewardUnits.Single(u => u.Unit == 2).Branch);
    }
}

/// <summary>
/// docs/slice-branches.md, piece 2: the steward's run keeps every open slice up with the base, and
/// the walk-through runs on the slice's branch.
/// </summary>
public partial class StewardServiceTests
{
    private async Task OpenSlice(int unit, string branch, bool released = false, int? parent = null)
    {
        await using var ctx = NewCtx();
        ctx.StewardUnits.Add(new Whetstone.Models.StewardUnit
        {
            ProjectKey = "alpha", Plan = "", Unit = unit, Branch = branch, AgreedAt = DateTime.UtcNow,
            ReleasedAt = released ? DateTime.UtcNow : null, ParentUnit = parent,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task A_run_brings_each_open_slice_up_to_the_base_and_leaves_a_released_one_alone()
    {
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        await OpenSlice(3, "slice/03-older", released: true);
        _git.SliceSync["slice/01-first"] = new SyncFromResult("merged", 2, new[] { "src/fix.ts" }, null);

        var begin = Json(await Begin());
        Assert.Equal(new[] { ("slice/01-first", false) }, _git.Synced);
        Assert.Contains("\"branch\":\"slice/01-first\"", begin);
        Assert.Contains("\"answer\":\"merged\"", begin);
    }

    [Fact]
    public async Task A_slice_that_cannot_take_the_base_is_put_to_the_owner_through_the_run()
    {
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        _git.SliceSync["slice/01-first"] = new SyncFromResult("conflict", 1, new[] { "src/app.ts" }, "nothing was changed");

        var begin = Json(await Begin());
        Assert.Contains("Ask the owner", begin);
        Assert.Contains("src/app.ts", begin);
    }

    [Fact]
    public async Task A_slice_behind_the_base_makes_a_run_due()
    {
        SwitchToSliceBranches();
        await Begin();
        await In(s => s.EndAsync("alpha", "x", false));
        await OpenSlice(1, "slice/01-first");
        _git.SliceSync["slice/01-first"] = new SyncFromResult("merged", 1, Array.Empty<string>(), null);

        await using var ctx = NewCtx();
        var (due, why) = await Steward(ctx).DueAsync("alpha");
        Assert.True(due);
        Assert.Equal("slice/01-first is behind the base", why);
        Assert.Contains(("slice/01-first", true), _git.Synced);
    }

    [Fact]
    public async Task Without_the_switch_no_slice_is_synced()
    {
        await OpenSlice(1, "slice/01-first");
        await Begin();
        Assert.Empty(_git.Synced);
    }

    [Fact]
    public async Task The_walk_through_of_an_open_slice_says_it_runs_on_the_slices_branch()
    {
        SwitchToSliceBranches();
        _git.First = WithWalkthrough;
        await OpenSlice(1, "slice/01-first");
        await Done(1, "A1", "A2");
        await Begin();
        await using var ctx = NewCtx();
        var t = await ctx.Tickets.SingleAsync(x => x.Walkthrough);
        Assert.Contains("on the slice's own branch, slice/01-first", t.Description);
    }
}

/// <summary>
/// docs/slice-branches.md, piece 3: releasing a slice with a branch is the board's merge of it into
/// the local base, with its refusals, and the decisions the owner ticks go into the conventions in
/// the same commit.
/// </summary>
public partial class StewardServiceTests
{
    private const string Conventions = "docs/conventions.md";

    /// <summary>Slice 1 open on its own branch, every outcome done, no run open, and two decisions of its own.</summary>
    private async Task ReleasableSlice(string checks = "")
    {
        Configure("full", $$"""
            "limits": { "createdPerDay": 2, "openAtOnce": 3, "sendBacksPerTicket": 1 },
            "plan": { "units": "docs/units" },
            "neverMerge": ["infra/"],
            "conventions": "{{Conventions}}"{{checks}}
            """);
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        await Built(1, "A1, A2");
        await Begin();
        await In(s => s.SetOutcomeAsync("alpha", 1, "A1", "Done", false, null));
        await In(s => s.SetOutcomeAsync("alpha", 1, "A2", "Done", false, null));
        await In(s => s.EndAsync("alpha", "x", false));
        await using var ctx = NewCtx();
        ctx.StewardDecisions.AddRange(
            new Whetstone.Models.StewardDecision { ProjectKey = "alpha", Number = 40, Rule = "Prices are whole cents,\n never floats.", PlanKey = "", PlanUnit = 1 },
            new Whetstone.Models.StewardDecision { ProjectKey = "alpha", Number = 41, Rule = "The card is blue.", PlanKey = "", PlanUnit = 1 },
            new Whetstone.Models.StewardDecision { ProjectKey = "alpha", Number = 42, Rule = "Another slice's.", PlanKey = "", PlanUnit = 2 });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Releasing_a_slice_merges_its_branch_and_promotes_what_the_owner_ticked()
    {
        await ReleasableSlice();
        var reply = await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true, new[] { 40 }));
        Assert.Equal("released", reply.Answer);

        var (slice, message, conventions, lines) = _git.Released.Single();
        Assert.Equal("slice/01-first", slice);
        Assert.StartsWith("Slice 1: ", message);
        Assert.Contains("Promoted to docs/conventions.md: #40", message);
        Assert.Equal(Conventions, conventions);
        Assert.Equal(new[] { "- Prices are whole cents, never floats. (#40)" }, lines);

        await using var ctx = NewCtx();
        var row = ctx.StewardUnits.Single(u => u.Unit == 1);
        Assert.Equal("release-sha", row.ReleasedSha);
        Assert.NotNull(ctx.StewardDecisions.Single(d => d.Number == 40).PromotedAt);
        Assert.Null(ctx.StewardDecisions.Single(d => d.Number == 41).PromotedAt);
        // Released, its tickets work on the base again, and taking it back is a revert, not a record.
        Assert.Null(SliceBranches.BaseFor(new ProjectRegistry(new DashboardPaths(_dataRoot)).GetProjectConfig("alpha"), 1, row));
        Assert.Equal("merged", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, false))).Answer);
    }

    [Fact]
    public async Task A_slice_with_a_ticket_open_or_a_run_open_is_not_released()
    {
        await ReleasableSlice();
        await Built(1, "A2", phase: Whetstone.Models.TicketPhase.NeedsReview);
        Assert.Equal("tickets-open", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true))).Answer);
        Assert.Empty(_git.Released);
    }

    [Fact]
    public async Task A_release_promotes_only_the_slices_own_decisions_and_only_into_a_named_file()
    {
        await ReleasableSlice();
        Assert.Equal("bad-request", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true, new[] { 42 }))).Answer);
        Assert.Equal("bad-request", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true, new[] { 99 }))).Answer);
        Assert.Empty(_git.Released);
    }

    [Fact]
    public async Task A_slice_whose_checks_fail_on_the_release_is_not_merged()
    {
        await ReleasableSlice(", \"mergeChecks\": [ { \"name\": \"tests\", \"run\": \"run-tests\" } ]");
        _git.Check = new CheckRun(false, false, "1 failed", new[] { "CardTests.Blue" });
        var reply = await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true));
        Assert.Equal("check-failed", reply.Answer);
        Assert.Contains("check-tree-slice/01-first", _git.CheckTreesMade);
        Assert.Equal(1, _git.CheckTreesReleased);
        Assert.Contains(_git.Ran, r => r.Env![StewardService.BaseRefVariable] == "main");
        Assert.Empty(_git.Released);
        await using var ctx = NewCtx();
        Assert.Null(ctx.StewardUnits.Single(u => u.Unit == 1).ReleasedAt);
    }

    [Fact]
    public async Task A_slice_that_cannot_take_the_base_is_not_released()
    {
        await ReleasableSlice();
        _git.ReleasePrep = new SliceReleasePrep("slice/01-first cannot take main without a conflict", new[] { "src/app.ts" }, Array.Empty<string>(), null, "main");
        var reply = await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true));
        Assert.Equal("conflict", reply.Answer);
        Assert.Contains("src/app.ts", System.Text.Json.JsonSerializer.Serialize(reply.Data));
        Assert.Empty(_git.Released);
    }

    [Fact]
    public async Task Without_the_switch_a_release_is_only_recorded_and_promotes_nothing()
    {
        await OpenSlice(1, "slice/01-first");
        await Built(1, "A1, A2");
        await Begin();
        await In(s => s.SetOutcomeAsync("alpha", 1, "A1", "Done", false, null));
        await In(s => s.SetOutcomeAsync("alpha", 1, "A2", "Done", false, null));
        Assert.Equal("no-slice-branch", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true, new[] { 40 }))).Answer);
        Assert.Equal("released", (await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true))).Answer);
        Assert.Empty(_git.Released);
    }

    [Fact]
    public async Task The_steward_suggests_what_to_promote_and_the_release_shows_it()
    {
        await ReleasableSlice();
        await Begin();
        Assert.Equal("bad-request", (await In(s => s.SuggestPromoteAsync("alpha", 41, ""))).Answer);
        Assert.Equal("suggested", (await In(s => s.SuggestPromoteAsync("alpha", 40, "It is about money."))).Answer);
        await In(s => s.EndAsync("alpha", "x", false));

        var view = System.Text.Json.JsonSerializer.Serialize((await In(s => s.PlanViewAsync("alpha"))).Data);
        Assert.Contains("\"sliceBranch\":\"slice/01-first\"", view);
        Assert.Contains("\"why\":\"It is about money.\"", view);
        Assert.Contains("\"conventions\":\"docs/conventions.md\"", view);
    }

    [Fact]
    public void The_conventions_file_is_never_merged_by_the_steward()
    {
        Configure("full", """
            "plan": { "units": "docs/units" },
            "conventions": "docs/conventions.md"
            """);
        var cfg = new ProjectRegistry(new DashboardPaths(_dataRoot)).GetProjectConfig("alpha");
        Assert.True(cfg.Steward.IsNeverMerged("docs/conventions.md"));
        Assert.False(cfg.Steward.IsNeverMerged("docs/other.md"));
    }
}

/// <summary>
/// docs/slice-branches.md, piece 4: a slice built on one other open slice - the parent its spec
/// names, cut from it and kept up with it, released after it, re-parented onto the base when it is
/// released, and put to the owner when it is abandoned.
/// </summary>
public partial class StewardServiceTests
{
    private const string BuiltOnFirst = "> **Built on:** slice 1\n> **Status: draft, awaiting the owner.**\n\n- **A1.** One.\n";
    private const string AgreedSecond = "> **Status: agreed 2026-01-02.**\n\n- **A1.** One.\n";

    private async Task<Whetstone.Models.StewardUnit> Row(int unit)
    {
        await using var ctx = NewCtx();
        return ctx.StewardUnits.Single(u => u.Unit == unit);
    }

    [Fact]
    public async Task A_slice_built_on_an_open_one_is_cut_from_it_and_remembers_it()
    {
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        _git.Second = BuiltOnFirst;
        var reply = await In(s => s.OwnerAgreesUnitAsync("alpha", 2));
        Assert.Equal("agreed", reply.Answer);
        Assert.Equal(new[] { ("slice/02-second", (string?)"slice/01-first") }, _git.CutFrom);
        Assert.Equal(1, (await Row(2)).ParentUnit);
        Assert.Contains("\"builtOn\":1", System.Text.Json.JsonSerializer.Serialize(reply.Data));
    }

    [Fact]
    public async Task A_parent_that_is_not_open_is_refused_before_anything_is_committed()
    {
        SwitchToSliceBranches();
        _git.Second = BuiltOnFirst;
        // Unit 1's spec is agreed, but it was never cut: there is no branch to build on.
        Assert.Equal("parent-not-open", (await In(s => s.OwnerAgreesUnitAsync("alpha", 2))).Answer);
        Assert.Empty(_git.Committed);
        Assert.Empty(_git.Cut);
        // The page says so before the owner presses anything.
        Assert.Contains("which is not open yet", System.Text.Json.JsonSerializer.Serialize((await In(s => s.PlanViewAsync("alpha"))).Data));
    }

    [Fact]
    public async Task Two_unreleased_parents_are_refused_and_a_released_one_is_no_parent_at_all()
    {
        SwitchToSliceBranches();
        _git.More[3] = ("docs/units/03-third.md", "> Built on: slices 1 and 2\n> **Status: draft, awaiting the owner.**\n\n- **A1.** One.\n");
        await OpenSlice(1, "slice/01-first");
        await OpenSlice(2, "slice/02-second");
        var two = await In(s => s.OwnerAgreesUnitAsync("alpha", 3));
        Assert.Equal("two-parents", two.Answer);
        Assert.Contains("1 and 2", two.Why);

        await using (var ctx = NewCtx())
        {
            ctx.StewardUnits.Single(u => u.Unit == 1).ReleasedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        Assert.Equal("agreed", (await In(s => s.OwnerAgreesUnitAsync("alpha", 3))).Answer);
        Assert.Equal(new[] { ("slice/03-third", (string?)"slice/02-second") }, _git.CutFrom);
        Assert.Equal(2, (await Row(3)).ParentUnit);
    }

    [Fact]
    public async Task A_slice_is_not_built_on_one_that_is_itself_built_on_another()
    {
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        await OpenSlice(2, "slice/02-second", parent: 1);
        _git.More[3] = ("docs/units/03-third.md", "Built on: slice 2\n\n> **Status: draft, awaiting the owner.**\n\n- **A1.** One.\n");
        var chain = await In(s => s.OwnerAgreesUnitAsync("alpha", 3));
        Assert.Equal("chain", chain.Answer);
        Assert.Contains("wait until slice 1 is released", chain.Why);
        Assert.Empty(_git.Cut);
    }

    [Fact]
    public async Task A_run_brings_a_parent_up_to_the_base_before_its_child_takes_the_parent()
    {
        SwitchToSliceBranches();
        await OpenSlice(2, "slice/02-second", parent: 1);
        await OpenSlice(1, "slice/01-first");
        await Begin();
        Assert.Equal(new[] { ("slice/01-first", (string?)null), ("slice/02-second", "slice/01-first") }, _git.SyncedFrom);
    }

    [Fact]
    public async Task A_child_is_released_after_its_parent_and_the_parents_release_hands_it_the_base()
    {
        await ReleasableSlice();
        _git.Second = AgreedSecond;
        await OpenSlice(2, "slice/02-second", parent: 1);
        await Built(2, "A1");
        await Begin();
        await In(s => s.SetOutcomeAsync("alpha", 2, "A1", "Done", false, null));
        await In(s => s.EndAsync("alpha", "x", false));

        Assert.Equal("parent-unreleased", (await In(s => s.OwnerReleasesUnitAsync("alpha", 2, true))).Answer);
        Assert.Empty(_git.Released);

        _git.SyncedFrom.Clear();
        var reply = await In(s => s.OwnerReleasesUnitAsync("alpha", 1, true));
        Assert.Equal("released", reply.Answer);
        Assert.Contains("\"reparented\":[{\"unit\":2", System.Text.Json.JsonSerializer.Serialize(reply.Data));
        Assert.Null((await Row(2)).ParentUnit);
        Assert.Contains(("slice/02-second", (string?)null), _git.SyncedFrom);

        Assert.Equal("released", (await In(s => s.OwnerReleasesUnitAsync("alpha", 2, true))).Answer);
    }

    [Fact]
    public async Task Abandoning_a_parent_asks_the_owner_about_its_child_before_anything_moves()
    {
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        await OpenSlice(2, "slice/02-second", parent: 1);

        var asked = await In(s => s.OwnerAbandonsUnitAsync("alpha", 1, null));
        Assert.Equal("children", asked.Answer);
        Assert.Contains("Slice 2 is built on slice 1", asked.Why);
        Assert.Empty(_git.Committed);
        Assert.Null((await Row(1)).AbandonedAt);

        await In(s => s.PlanViewAsync("alpha"));   // records where each agreed slice's spec is
        Assert.Equal("abandoned", (await In(s => s.OwnerAbandonsUnitAsync("alpha", 1, "recut"))).Answer);
        Assert.Equal(new[] { "slice/02-second" }, _git.Recut);
        var (file, text) = _git.Committed.Single();
        // Its spec says abandoned now, which is not a slice gone missing.
        _git.First = text;
        var view = System.Text.Json.JsonSerializer.Serialize((await In(s => s.PlanViewAsync("alpha"))).Data);
        Assert.DoesNotContain("no longer says so", view);
        Assert.Contains("\"stage\":\"abandoned\"", view);
        Assert.Equal("docs/units/01-first.md", file);
        Assert.Contains($"**Status: abandoned {DateTime.Now:yyyy-MM-dd}.**", text);
        Assert.NotNull((await Row(1)).AbandonedAt);
        Assert.Null((await Row(2)).ParentUnit);
        Assert.Null((await Row(2)).AbandonedAt);

        // Abandoned: no longer synced, and its tickets are of no open slice.
        await Begin();
        Assert.DoesNotContain(_git.Synced, x => x.Slice == "slice/01-first");
        Assert.Null(SliceBranches.BaseFor(new ProjectRegistry(new DashboardPaths(_dataRoot)).GetProjectConfig("alpha"), 1, await Row(1)));
    }

    [Fact]
    public async Task A_child_with_work_of_its_own_is_not_recut_and_nothing_is_abandoned()
    {
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        await OpenSlice(2, "slice/02-second", parent: 1);
        _git.RecutRefusals["slice/02-second"] = "slice/02-second has 2 commits of its own";
        var reply = await In(s => s.OwnerAbandonsUnitAsync("alpha", 1, "recut"));
        Assert.Equal("cannot-recut", reply.Answer);
        Assert.Contains("Nothing was abandoned", reply.Why);
        Assert.Empty(_git.Committed);
        Assert.Equal(1, (await Row(2)).ParentUnit);
    }

    [Fact]
    public async Task Abandoning_a_parent_and_its_child_marks_both_and_keeps_their_branches()
    {
        SwitchToSliceBranches();
        _git.Second = AgreedSecond;
        await OpenSlice(1, "slice/01-first");
        await OpenSlice(2, "slice/02-second", parent: 1);
        var reply = await In(s => s.OwnerAbandonsUnitAsync("alpha", 1, "abandon"));
        Assert.Equal("abandoned", reply.Answer);
        Assert.Equal(new[] { "docs/units/01-first.md", "docs/units/02-second.md" }, _git.Committed.Select(c => c.Path));
        Assert.Contains("slice/02-second", System.Text.Json.JsonSerializer.Serialize(reply.Data));
        Assert.NotNull((await Row(2)).AbandonedAt);
        Assert.Equal("slice/02-second", (await Row(2)).Branch);
        Assert.Empty(_git.Recut);
    }

    [Fact]
    public async Task A_slice_with_a_ticket_open_is_not_abandoned_and_nor_is_one_without_the_switch()
    {
        await OpenSlice(1, "slice/01-first");
        Assert.Equal("no-slice-branches", (await In(s => s.OwnerAbandonsUnitAsync("alpha", 1, null))).Answer);
        SwitchToSliceBranches();
        await Built(1, "A1", phase: Whetstone.Models.TicketPhase.NeedsImplementation);
        Assert.Equal("tickets-open", (await In(s => s.OwnerAbandonsUnitAsync("alpha", 1, null))).Answer);
        Assert.Empty(_git.Committed);
    }

    [Theory]
    [InlineData("# Cards\n\n> **Built on:** slice 11\n", new[] { 11 })]
    [InlineData("Built on: slices 3 and 7\n", new[] { 3, 7 })]
    [InlineData("> _Built on_: slice 4, slice 5", new[] { 4, 5 })]
    [InlineData("# Cards\n\nNothing here is built on anything.\n", new int[0])]
    public void A_spec_names_its_parents_on_its_built_on_line(string spec, int[] want) =>
        Assert.Equal(want, SliceBranches.ParentsNamed(spec));

    [Fact]
    public void An_abandoned_status_line_reads_as_neither_agreed_nor_a_draft()
    {
        var p = new StewardPlan("docs/units", StewardPlan.DefaultUnitFile, StewardPlan.DefaultAgreed,
            StewardPlan.DefaultDraft, StewardPlan.DefaultOutcome);
        var marked = TicketGitService.MarkAbandoned(p, "# One\n\n> **Status: agreed 2026-01-01.** Kept.\n\n- **A1.** One.\n", new DateTime(2026, 3, 4));
        Assert.Equal("# One\n\n> **Status: abandoned 2026-03-04.** Kept.\n\n- **A1.** One.\n", marked);
        Assert.Null(TicketGitService.MarkAbandoned(p, "> **Status: draft.**\n", DateTime.Now));
    }
}
