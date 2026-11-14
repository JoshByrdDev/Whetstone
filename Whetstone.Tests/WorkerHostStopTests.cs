using System.Text.Json;
using System.Text.Json.Nodes;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A worker host's "stop after current runs": the gentle way to take one machine out of rotation.
/// It takes nothing new at once, its runs finish and hand back, and the board switches it Off once
/// nothing is out there. Switching Off itself kills nothing either - these tests also pin that, since
/// the On/Off control's are-you-sure says so.
/// </summary>
public class WorkerHostStopTests
{
    private const string Board = "1111111111111111111111111111111111111111";
    private static readonly string[] Projects = { "alpha" };
    private DateTime _now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyDictionary<string, JsonElement> AsGlobal(JsonObject config) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.ToJsonString())!;

    private static JsonObject OnHost()
    {
        var config = new JsonObject();
        WorkerHostSettings.Upsert(config, "bravo-host", new(true, 2, null, null, new[] { "alpha" }), Projects);
        return config;
    }

    private WorkerHostService Service(JsonObject config) =>
        new(() => WorkerHostConfig.Read(AsGlobal(config)), () => Board, () => _now);

    private static HostRun Run(int runId, bool exited = false) =>
        new(11, 7, runId, "alpha", 100, exited, exited ? 0 : null, null);

    [Fact]
    public void A_pending_stop_takes_the_host_out_of_dispatch_but_leaves_it_on()
    {
        var config = OnHost();
        Assert.Null(WorkerHostSettings.SetStopPending(config, "BRAVO-HOST", true));
        var h = WorkerHostConfig.Read(AsGlobal(config)).Single();
        Assert.True(h.Enabled);
        Assert.True(h.StopPending);

        var svc = Service(config);
        svc.CheckInAsync("bravo-host", Board, new[] { Run(5) }, null, TimeSpan.Zero).Wait();
        var view = svc.Snapshot().Single();
        Assert.True(view.StopPending);
        Assert.False(view.Dispatchable);
        Assert.Equal(1, view.Out);
    }

    [Fact]
    public void It_is_drained_only_once_the_hosts_runs_have_finished()
    {
        var config = OnHost();
        WorkerHostSettings.SetStopPending(config, "bravo-host", true);
        var svc = Service(config);

        svc.CheckInAsync("bravo-host", Board, new[] { Run(5), Run(6) }, null, TimeSpan.Zero).Wait();
        Assert.Empty(svc.DrainedStops());

        svc.CheckInAsync("bravo-host", Board, new[] { Run(5, exited: true), Run(6) }, null, TimeSpan.Zero).Wait();
        Assert.Empty(svc.DrainedStops());
        Assert.Equal(1, svc.Snapshot().Single().Out);

        svc.CheckInAsync("bravo-host", Board, new[] { Run(5, exited: true), Run(6, exited: true) }, null, TimeSpan.Zero).Wait();
        Assert.Equal(new[] { "bravo-host" }, svc.DrainedStops());

        Assert.True(WorkerHostSettings.FinishStop(config, "bravo-host"));
        var h = WorkerHostConfig.Read(AsGlobal(config)).Single();
        Assert.False(h.Enabled);
        Assert.False(h.StopPending);
        Assert.Empty(svc.DrainedStops());
    }

    [Fact]
    public void A_start_ordered_before_the_press_is_work_out_until_its_run_is_reported()
    {
        var config = OnHost();
        WorkerHostSettings.SetStopPending(config, "bravo-host", true);
        var svc = Service(config);
        svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero).Wait();

        // Queued and not yet picked up.
        svc.Enqueue("bravo-host", new HostOrder("start", 11, 9, JsonDocument.Parse("{}").RootElement));
        Assert.Empty(svc.DrainedStops());

        // Picked up by this check-in, which cannot report it yet.
        svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero).Wait();
        Assert.Empty(svc.DrainedStops());

        // Reported, then finished.
        svc.CheckInAsync("bravo-host", Board, new[] { Run(9) }, null, TimeSpan.Zero).Wait();
        Assert.Empty(svc.DrainedStops());
        svc.CheckInAsync("bravo-host", Board, new[] { Run(9, exited: true) }, null, TimeSpan.Zero).Wait();
        Assert.Equal(new[] { "bravo-host" }, svc.DrainedStops());
    }

    [Fact]
    public void A_start_the_runner_never_reports_stops_counting_after_the_grace()
    {
        var config = OnHost();
        WorkerHostSettings.SetStopPending(config, "bravo-host", true);
        var svc = Service(config);
        svc.Enqueue("bravo-host", new HostOrder("start", 11, 9, JsonDocument.Parse("{}").RootElement));
        svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero).Wait();

        _now += WorkerHostService.HandedGrace + TimeSpan.FromSeconds(1);
        svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero).Wait();
        Assert.Equal(new[] { "bravo-host" }, svc.DrainedStops());
    }

    [Fact]
    public void A_host_not_heard_from_is_never_taken_as_drained()
    {
        // A board restarted mid-drain knows nothing of the host's runs until it checks in again,
        // and an offline host's last report may be stale. Neither is "nothing out".
        var config = OnHost();
        WorkerHostSettings.SetStopPending(config, "bravo-host", true);
        var svc = Service(config);
        Assert.Empty(svc.DrainedStops());

        svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero).Wait();
        _now += WorkerHostService.OfflineAfter + TimeSpan.FromSeconds(1);
        Assert.Empty(svc.DrainedStops());
    }

    [Fact]
    public void Switching_on_or_off_ends_a_pending_stop_and_cancel_clears_it()
    {
        var config = OnHost();
        WorkerHostSettings.SetStopPending(config, "bravo-host", true);
        WorkerHostSettings.Upsert(config, "bravo-host", new(true, null, null, null, null), Projects);
        Assert.False(WorkerHostConfig.Read(AsGlobal(config)).Single().StopPending);

        WorkerHostSettings.SetStopPending(config, "bravo-host", true);
        WorkerHostSettings.Upsert(config, "bravo-host", new(false, null, null, null, null), Projects);
        var off = WorkerHostConfig.Read(AsGlobal(config)).Single();
        Assert.False(off.Enabled);
        Assert.False(off.StopPending);

        WorkerHostSettings.Upsert(config, "bravo-host", new(true, null, null, null, null), Projects);
        WorkerHostSettings.SetStopPending(config, "bravo-host", true);
        Assert.Null(WorkerHostSettings.SetStopPending(config, "bravo-host", false));
        var on = WorkerHostConfig.Read(AsGlobal(config)).Single();
        Assert.True(on.Enabled);
        Assert.False(on.StopPending);
        // Nothing pending any more, so the board's finish is a no-op rather than a switch-off.
        Assert.False(WorkerHostSettings.FinishStop(config, "bravo-host"));
        Assert.True(WorkerHostConfig.Read(AsGlobal(config)).Single().Enabled);
    }

    [Fact]
    public void A_stop_is_refused_for_a_host_that_is_off_or_unknown()
    {
        var config = OnHost();
        WorkerHostSettings.Upsert(config, "bravo-host", new(false, null, null, null, null), Projects);
        Assert.NotNull(WorkerHostSettings.SetStopPending(config, "bravo-host", true));
        Assert.NotNull(WorkerHostSettings.SetStopPending(config, "charlie", true));
    }

    [Fact]
    public async Task Switching_off_orders_nothing_killed()
    {
        // Off only stops new claims: the runs out keep running and nothing is queued to stop them.
        var config = OnHost();
        var svc = Service(config);
        await svc.CheckInAsync("bravo-host", Board, new[] { Run(5) }, null, TimeSpan.Zero);
        WorkerHostSettings.Upsert(config, "bravo-host", new(false, null, null, null, null), Projects);

        var view = svc.Snapshot().Single();
        Assert.False(view.Dispatchable);
        Assert.Equal(0, view.PendingOrders);
        Assert.Empty((await svc.CheckInAsync("bravo-host", Board, new[] { Run(5) }, null, TimeSpan.Zero))!);
        Assert.Equal(1, svc.Snapshot().Single().Out);
    }
}
