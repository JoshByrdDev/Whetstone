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
    private async Task OpenSlice(int unit, string branch, bool released = false)
    {
        await using var ctx = NewCtx();
        ctx.StewardUnits.Add(new Whetstone.Models.StewardUnit
        {
            ProjectKey = "alpha", Plan = "", Unit = unit, Branch = branch, AgreedAt = DateTime.UtcNow,
            ReleasedAt = released ? DateTime.UtcNow : null,
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
