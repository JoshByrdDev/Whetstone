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
