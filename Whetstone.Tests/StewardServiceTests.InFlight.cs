using System.Text.Json;
using Whetstone.Services;

namespace Whetstone.Tests;

/// <summary>
/// docs/slice-branches.md, section 3: the slices folder, and open slices capped at slicesInFlight.
/// </summary>
public partial class StewardServiceTests
{
    private ProjectConfig Cfg() => new ProjectRegistry(new DashboardPaths(_dataRoot)).GetProjectConfig("alpha");

    [Fact]
    public void Slices_name_the_folder_and_how_many_may_be_open()
    {
        Configure("manage", """ "slices": "docs/slices", "slicesInFlight": 2 """);
        Assert.Equal("docs/slices", Cfg().Steward.Plan.UnitsDir);
        Assert.Equal(2, Cfg().Steward.SlicesInFlight);
    }

    [Fact]
    public async Task No_more_slices_are_agreed_than_the_project_builds_at_once()
    {
        Configure("manage", """ "slices": "docs/units", "slicesInFlight": 1 """);
        await OpenSlice(1, "slice/01-first");
        _git.Second = "> **Status: draft, awaiting the owner.**\n\n- **A1.** One.\n";
        var full = await In(s => s.OwnerAgreesUnitAsync("alpha", 2));
        Assert.Equal("in-flight", full.Answer);
        Assert.Contains("slicesInFlight 1", full.Why);
        Assert.Empty(_git.Committed);
        Assert.Contains("as many as", JsonSerializer.Serialize((await In(s => s.PlanViewAsync("alpha"))).Data));

        await using (var ctx = NewCtx())
        {
            ctx.StewardUnits.Single(u => u.Unit == 1).ReleasedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        Assert.Equal("agreed", (await In(s => s.OwnerAgreesUnitAsync("alpha", 2))).Answer);
    }
}
