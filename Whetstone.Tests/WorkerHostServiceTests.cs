using System.Text.Json;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The worker hosts' registry (docs/worker-hosts-plan.md, piece 2): what <c>workerHosts</c> says,
/// how slots are numbered, and what a runner's check-in does. The orchestrator decides whether to
/// give a host work from <see cref="WorkerHostView.Dispatchable"/>, so every way that can be wrong
/// - offline, on another commit, past its account's limit - has a test here.
/// </summary>
public class WorkerHostServiceTests
{
    private const string Board = "1111111111111111111111111111111111111111";
    private DateTime _now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyDictionary<string, JsonElement> Global(string workerHostsJson) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($$"""{ "workerHosts": {{workerHostsJson}} }""")!;

    private WorkerHostService Service(string workerHostsJson = """[{ "name": "bravo-host", "slots": 3, "projects": ["alpha"] }]""")
    {
        var cfg = WorkerHostConfig.Read(Global(workerHostsJson));
        return new WorkerHostService(() => cfg, () => Board, () => _now);
    }

    // ---- Config -----------------------------------------------------------------

    [Fact]
    public void Hosts_are_numbered_after_the_board_hosts_workers_and_the_steward()
    {
        var hosts = WorkerHostConfig.Read(Global("""[{ "name": "a", "slots": 3 }, { "name": "b", "slots": 2 }]"""));
        Assert.Equal(5, WorkerHostConfig.FirstRemoteSlot);
        Assert.Equal(new[] { 5, 8 }, hosts.Select(h => h.FirstSlot));
        Assert.True(hosts[0].OwnsSlot(7));
        Assert.False(hosts[0].OwnsSlot(8));
        Assert.True(hosts[1].OwnsSlot(9));
    }

    [Fact]
    public void An_entry_that_names_no_skills_gets_only_the_phases_that_take_no_stack()
    {
        var h = WorkerHostConfig.Read(Global("""[{ "name": "a" }]""")).Single();
        Assert.Equal(new[] { "speculate", "implement", "refine" }, h.Skills);
        Assert.Equal(1, h.Slots);
        Assert.True(h.Enabled);
    }

    [Fact]
    public void A_hand_edited_config_cannot_stop_the_board()
    {
        var hosts = WorkerHostConfig.Read(Global("""
            [ { "slots": 2 }, { "name": "bad name/" }, { "name": "a", "slots": 99 }, { "name": "A" }, "junk",
              { "name": "b", "enabled": false } ]
            """));
        Assert.Equal(new[] { "a", "b" }, hosts.Select(h => h.Name));
        Assert.Equal(WorkerHostConfig.MaxSlots, hosts[0].Slots);
        Assert.False(hosts[1].Enabled);
        Assert.Empty(WorkerHostConfig.Read(new Dictionary<string, JsonElement>()));
    }

    // ---- Check-in and orders ------------------------------------------------------

    [Fact]
    public async Task A_host_the_config_does_not_name_is_told_so()
    {
        var svc = Service();
        Assert.Null(await svc.CheckInAsync("charlie", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero));
        Assert.False(svc.Enqueue("charlie", new HostOrder("kill", 5, 1, null)));
    }

    [Fact]
    public async Task An_order_is_delivered_once()
    {
        var svc = Service();
        Assert.True(svc.Enqueue("bravo-host", new HostOrder("kill", 5, 42, null)));
        var first = await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero);
        Assert.Equal(42, Assert.Single(first!).RunId);
        Assert.Empty((await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero))!);
    }

    [Fact]
    public async Task A_waiting_check_in_gets_an_order_as_soon_as_it_is_placed()
    {
        var svc = Service();
        // The check-in may wait the full 25s; it must come back well inside that. The margin is wide
        // on purpose - the whole suite runs in parallel, and a busy thread pool is not a late order.
        var waiting = svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, WorkerHostService.MaxWait);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        svc.Enqueue("bravo-host", new HostOrder("kill", 6, 7, null));
        var done = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(waiting, done);
        Assert.Equal(7, Assert.Single((await waiting)!).RunId);
    }

    [Fact]
    public async Task A_waiting_check_in_with_nothing_to_hand_out_returns_empty_when_its_time_is_up()
    {
        var svc = Service();
        var orders = await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.FromMilliseconds(150));
        Assert.Empty(orders!);
    }

    // ---- Whether a host may be given work -------------------------------------------

    [Fact]
    public async Task A_host_that_has_never_checked_in_is_offline()
    {
        var svc = Service();
        var view = Assert.Single(svc.Snapshot());
        Assert.False(view.Online);
        Assert.False(view.Dispatchable);

        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero);
        Assert.True(Assert.Single(svc.Snapshot()).Dispatchable);
    }

    [Fact]
    public async Task A_host_goes_offline_when_it_stops_checking_in()
    {
        var svc = Service();
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero);
        _now += TimeSpan.FromSeconds(119);
        Assert.True(Assert.Single(svc.Snapshot()).Online);
        _now += TimeSpan.FromSeconds(2);
        Assert.False(Assert.Single(svc.Snapshot()).Online);
    }

    [Fact]
    public async Task A_host_on_another_commit_is_shown_but_not_given_work()
    {
        var svc = Service();
        await svc.CheckInAsync("bravo-host", "2222222222222222222222222222222222222222", Array.Empty<HostRun>(), null, TimeSpan.Zero);
        var view = Assert.Single(svc.Snapshot());
        Assert.True(view.Online);
        Assert.False(view.CommitMatches);
        Assert.False(view.Dispatchable);
    }

    [Fact]
    public async Task A_host_past_its_accounts_limit_is_paused_until_the_reset()
    {
        var svc = Service();
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), _now.AddHours(2), TimeSpan.Zero);
        Assert.False(Assert.Single(svc.Snapshot()).Dispatchable);

        // A limit already in the past is no pause at all.
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), _now.AddMinutes(-1), TimeSpan.Zero);
        Assert.True(Assert.Single(svc.Snapshot()).Dispatchable);
    }

    [Fact]
    public async Task A_disabled_host_is_never_given_work()
    {
        var svc = Service("""[{ "name": "bravo-host", "enabled": false }]""");
        await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero);
        Assert.False(Assert.Single(svc.Snapshot()).Dispatchable);
    }

    [Fact]
    public async Task Hard_stop_orders_every_live_run_killed_and_none_that_has_exited()
    {
        var svc = Service();
        await svc.CheckInAsync("bravo-host", Board, new[]
        {
            new HostRun(5, 10, 100, "alpha", 1234, false, null, _now),
            new HostRun(6, 11, 101, "alpha", 1235, true, 0, _now),
        }, null, TimeSpan.Zero);

        Assert.Equal(1, svc.KillAll());
        var orders = await svc.CheckInAsync("bravo-host", Board, Array.Empty<HostRun>(), null, TimeSpan.Zero);
        var kill = Assert.Single(orders!);
        Assert.Equal(("kill", 100), (kill.Kind, kill.RunId));
    }
}
