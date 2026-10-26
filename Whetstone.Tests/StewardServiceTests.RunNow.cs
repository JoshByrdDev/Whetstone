using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>Run now, from the Steward page: what it refuses, and what it leaves owed.</summary>
public partial class StewardServiceTests
{
    [Fact]
    public async Task Run_now_is_refused_while_the_steward_is_off()
    {
        Configure("off");
        Assert.Equal("off", (await In(s => s.OwnerAsksForRunAsync("alpha"))).Answer);
        Assert.False((await In0(s => s.DueAsync("alpha"))).Due);
    }

    [Fact]
    public async Task Run_now_is_refused_while_a_run_is_open()
    {
        Assert.Equal("started", (await Begin()).Answer);
        Assert.Equal("running", (await In(s => s.OwnerAsksForRunAsync("alpha"))).Answer);
    }

    [Fact]
    public async Task Run_now_leaves_a_run_owed_with_the_owner_as_its_reason()
    {
        Assert.Equal("recorded", (await In(s => s.OwnerAsksForRunAsync("alpha"))).Answer);
        var (due, why) = await In0(s => s.DueAsync("alpha"));
        Assert.True(due);
        Assert.Contains(StewardService.OwnerAskedReason, why);
    }

    [Fact]
    public async Task A_QA_copy_never_starts_a_steward_run()
    {
        var paths = new DashboardPaths(_dataRoot);
        var isolated = new QaIsolation(true);
        var runner = new StewardRunner(paths, new HeartbeatManager(paths, isolated), isolated);
        Assert.Equal("isolated", (await In(s => runner.StartAsync("alpha", s))).Answer);
        // Refused at the door: nothing was even marked owed.
        Assert.Null(runner.RunningProject);
    }
}
