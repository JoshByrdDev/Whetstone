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
    public async Task A_run_asked_for_within_the_gap_says_when_it_can_start()
    {
        _minGap = TimeSpan.FromMinutes(5);
        Assert.Equal("started", (await Begin()).Answer);
        Assert.Equal("ended", (await In(s => s.EndAsync("alpha", "did nothing", false))).Answer);

        Assert.Equal("recorded", (await In(s => s.OwnerAsksForRunAsync("alpha"))).Answer);

        // Not due to the heartbeat yet, but owed, and the page and Run now can say when.
        Assert.False((await In0(s => s.DueAsync("alpha"))).Due);
        var until = await In0(s => s.RunHeldUntilAsync("alpha"));
        Assert.NotNull(until);
        Assert.Equal(DateTimeKind.Utc, until!.Value.Kind);
        Assert.InRange(until.Value, DateTime.UtcNow.AddMinutes(4), DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task Nothing_holds_a_run_once_the_gap_is_over()
    {
        Assert.Equal("started", (await Begin()).Answer);
        Assert.Equal("ended", (await In(s => s.EndAsync("alpha", "did nothing", false))).Answer);
        Assert.Equal("recorded", (await In(s => s.OwnerAsksForRunAsync("alpha"))).Answer);

        // This fixture has no gap: the run is simply due, and nothing is held.
        Assert.True((await In0(s => s.DueAsync("alpha"))).Due);
        Assert.Null(await In0(s => s.RunHeldUntilAsync("alpha")));
    }

    [Fact]
    public async Task A_run_that_is_open_is_not_held()
    {
        _minGap = TimeSpan.FromMinutes(5);
        Assert.Equal("started", (await Begin()).Answer);
        Assert.Null(await In0(s => s.RunHeldUntilAsync("alpha")));
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
