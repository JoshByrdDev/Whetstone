using System.Text.Json;
using System.Text.Json.Nodes;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The worker hosts as the Heartbeat page edits them: the switch for all remote work, adding and
/// changing a host, removing one, and the machines that checked in before being added. A new host
/// must start unable to receive anything, and nothing a click can do may orphan a run that is out.
/// </summary>
public class WorkerHostSettingsTests
{
    private static readonly string[] Projects = { "alpha", "bravo" };
    private DateTime _now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyDictionary<string, JsonElement> AsGlobal(JsonObject config) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.ToJsonString())!;

    [Fact]
    public void A_new_host_starts_switched_off_with_nothing_it_may_be_sent()
    {
        var config = new JsonObject();
        Assert.Null(WorkerHostSettings.Upsert(config, "bravo-host", new(null, null, null, null, null), Projects));
        var h = WorkerHostConfig.Read(AsGlobal(config)).Single();
        Assert.False(h.Enabled);
        Assert.Equal(1, h.Slots);
        Assert.Empty(h.Projects);
        Assert.Equal(WorkerHostConfig.DefaultSkills, h.Skills);
    }

    [Fact]
    public void An_edit_changes_only_what_it_names()
    {
        var config = new JsonObject();
        WorkerHostSettings.Upsert(config, "bravo-host", new(true, 3, 2, null, new[] { "alpha" }), Projects);
        WorkerHostSettings.Upsert(config, "BRAVO-HOST", new(null, null, null, new[] { "qa-test", "implement" }, null), Projects);
        var h = WorkerHostConfig.Read(AsGlobal(config)).Single();
        Assert.Equal(("bravo-host", true, 3, 2), (h.Name, h.Enabled, h.Slots, h.QaLeases));
        Assert.Equal(new[] { "alpha" }, h.Projects);
        // Stored in the pipeline's order, whatever order they were clicked in.
        Assert.Equal(new[] { "implement", "qa-test" }, h.Skills);
    }

    [Fact]
    public void Counts_are_clamped_where_they_are_stored()
    {
        var config = new JsonObject();
        WorkerHostSettings.Upsert(config, "bravo-host", new(null, 99, 0, null, null), Projects);
        var h = WorkerHostConfig.Read(AsGlobal(config)).Single();
        Assert.Equal((WorkerHostConfig.MaxSlots, 1), (h.Slots, h.QaLeases));
    }

    [Theory]
    [InlineData("bad name/", null, null)]
    [InlineData("bravo-host", "steward", null)]
    [InlineData("bravo-host", null, "charlie")]
    public void What_the_page_cannot_ask_for_is_refused_and_nothing_is_written(string name, string? skill, string? project)
    {
        var config = new JsonObject();
        var why = WorkerHostSettings.Upsert(config, name,
            new(null, null, null, skill is null ? null : new[] { skill }, project is null ? null : new[] { project }), Projects);
        Assert.NotNull(why);
        if (name == "bad name/") Assert.False(config.ContainsKey(WorkerHostSettings.HostsKey));
    }

    [Fact]
    public void The_switch_for_all_remote_work_is_on_unless_it_is_switched_off()
    {
        var config = new JsonObject();
        Assert.True(WorkerHostConfig.RemoteEnabled(AsGlobal(config)));
        WorkerHostSettings.SetEnabled(config, false);
        Assert.False(WorkerHostConfig.RemoteEnabled(AsGlobal(config)));
        WorkerHostSettings.SetEnabled(config, true);
        Assert.True(WorkerHostConfig.RemoteEnabled(AsGlobal(config)));
    }

    [Fact]
    public async Task With_remote_work_switched_off_no_host_is_given_work()
    {
        var on = true;
        var cfg = new[] { new WorkerHostConfig("bravo-host", true, 2, WorkerHostConfig.DefaultSkills, Projects, 5) };
        var svc = new WorkerHostService(() => cfg, () => "c", () => _now, () => on);
        await svc.CheckInAsync("bravo-host", "c", Array.Empty<HostRun>(), null, TimeSpan.Zero);
        Assert.True(Assert.Single(svc.Snapshot()).Dispatchable);
        on = false;
        var view = Assert.Single(svc.Snapshot());
        Assert.False(view.Dispatchable);
        Assert.False(view.RemoteOn);
        Assert.True(view.Online);
    }

    [Fact]
    public void Removing_takes_the_host_out()
    {
        var config = new JsonObject();
        WorkerHostSettings.Upsert(config, "a", new(null, null, null, null, null), Projects);
        WorkerHostSettings.Upsert(config, "b", new(null, null, null, null, null), Projects);
        Assert.True(WorkerHostSettings.Remove(config, "A"));
        Assert.False(WorkerHostSettings.Remove(config, "nope"));
        Assert.Equal(new[] { "b" }, WorkerHostConfig.Read(AsGlobal(config)).Select(h => h.Name));
    }

    private static IReadOnlyList<WorkerHostConfig> Hosts(params string[] names) =>
        names.Select((n, i) => new WorkerHostConfig(n, true, 1, WorkerHostConfig.DefaultSkills, Projects, 5 + i)).ToList();

    [Fact]
    public void Unwritten_dispatch_is_fill_with_worker_hosts_before_the_board()
    {
        var d = WorkerHostSettings.ReadDispatch(AsGlobal(new JsonObject()), Hosts("a", "b"));
        Assert.Equal(WorkerHostSettings.Fill, d.Mode);
        Assert.Equal(new[] { "a", "b", WorkerHostSettings.Board }, d.Order);
    }

    [Fact]
    public void No_worker_hosts_is_the_board_alone()
    {
        var d = WorkerHostSettings.ReadDispatch(AsGlobal(new JsonObject()), Hosts());
        Assert.Equal(new[] { WorkerHostSettings.Board }, d.Order);
    }

    [Fact]
    public void A_written_order_names_every_host_and_the_board_once()
    {
        var hostNames = new[] { "a", "b", "c" };
        // A stranger is dropped, a repeat is dropped, names match whatever their case, and a host it
        // leaves out goes at the end - behind the board, once the board has a place of its own.
        Assert.Equal(new[] { "b", WorkerHostSettings.Board, "a", "c" },
            WorkerHostSettings.NormalizeOrder(new[] { "B", "gone", "@BOARD", "b" }, hostNames));
        // With the board left out too, it goes last, as in an order nobody wrote.
        Assert.Equal(new[] { "c", "a", "b", WorkerHostSettings.Board },
            WorkerHostSettings.NormalizeOrder(new[] { "c" }, hostNames));
    }

    [Fact]
    public void Dispatch_is_saved_normalized_and_read_back()
    {
        var config = new JsonObject();
        var hosts = Hosts("a", "b");
        Assert.Null(WorkerHostSettings.SetDispatch(config, new("spread", new[] { "b", "@board" }), hosts));
        var d = WorkerHostSettings.ReadDispatch(AsGlobal(config), hosts);
        Assert.Equal(WorkerHostSettings.Spread, d.Mode);
        Assert.Equal(new[] { "b", WorkerHostSettings.Board, "a" }, d.Order);

        // Changing only the mode leaves the order alone.
        Assert.Null(WorkerHostSettings.SetDispatch(config, new("fill", null), hosts));
        d = WorkerHostSettings.ReadDispatch(AsGlobal(config), hosts);
        Assert.Equal((WorkerHostSettings.Fill, "b"), (d.Mode, d.Order[0]));
    }

    [Theory]
    [InlineData("pack", null)]
    [InlineData(null, "nobody")]
    public void Dispatch_the_page_cannot_ask_for_is_refused_and_nothing_is_written(string? mode, string? host)
    {
        var config = new JsonObject();
        Assert.NotNull(WorkerHostSettings.SetDispatch(config,
            new(mode, host is null ? null : new[] { host, "@board" }), Hosts("a")));
        Assert.Empty(config);
    }

    [Fact]
    public void A_mode_written_by_hand_that_is_not_one_reads_as_fill()
    {
        var config = new JsonObject { [WorkerHostSettings.ModeKey] = "evenly" };
        Assert.Equal(WorkerHostSettings.Fill, WorkerHostSettings.ReadDispatch(AsGlobal(config), Hosts("a")).Mode);
    }

    [Fact]
    public void A_removed_host_leaves_the_order_and_comes_back_at_the_end()
    {
        var config = new JsonObject();
        WorkerHostSettings.Upsert(config, "a", new(null, null, null, null, null), Projects);
        WorkerHostSettings.Upsert(config, "b", new(null, null, null, null, null), Projects);
        WorkerHostSettings.SetDispatch(config, new(null, new[] { "a", "@board", "b" }), WorkerHostConfig.Read(AsGlobal(config)));
        WorkerHostSettings.Remove(config, "a");
        WorkerHostSettings.Upsert(config, "a", new(null, null, null, null, null), Projects);
        Assert.Equal(new[] { WorkerHostSettings.Board, "b", "a" },
            WorkerHostSettings.ReadDispatch(AsGlobal(config), WorkerHostConfig.Read(AsGlobal(config))).Order);
    }

    private static WorkerHostView View(string name, int first, int slots, bool busy) =>
        new(name, true, slots, first, WorkerHostConfig.DefaultSkills, Projects, true, null, "c", true,
            busy ? new[] { new HostRun(first, 1, 10, "alpha", 1, false, null, null) } : Array.Empty<HostRun>(), 0, null);

    [Fact]
    public void Nothing_is_renumbered_under_a_run_that_is_out()
    {
        var hosts = new[] { View("a", 5, 2, busy: true), View("b", 7, 2, busy: true), View("c", 9, 1, busy: false) };
        Assert.NotNull(WorkerHostSettings.RenumberingProblem(hosts, "a", removing: true));    // its own runs
        Assert.NotNull(WorkerHostSettings.RenumberingProblem(hosts, "a", removing: false));   // b's berths move
        Assert.Null(WorkerHostSettings.RenumberingProblem(hosts, "b", removing: false));      // its own count: nothing after it is busy
        Assert.NotNull(WorkerHostSettings.RenumberingProblem(hosts, "b", removing: true));
        Assert.Null(WorkerHostSettings.RenumberingProblem(hosts, "c", removing: true));
        Assert.Null(WorkerHostSettings.RenumberingProblem(hosts, "unknown", removing: true));
    }

    [Fact]
    public async Task A_machine_that_checks_in_before_it_is_added_is_listed_and_given_nothing()
    {
        var cfg = new List<WorkerHostConfig>();
        var svc = new WorkerHostService(() => cfg, () => "c", () => _now);
        Assert.Null(await svc.CheckInAsync("worker-pc", "abc", Array.Empty<HostRun>(), null, TimeSpan.Zero));
        Assert.Null(await svc.CheckInAsync("bad name/", "abc", Array.Empty<HostRun>(), null, TimeSpan.Zero));
        var seen = Assert.Single(svc.Seen());
        Assert.Equal(("worker-pc", "abc"), (seen.Name, seen.Commit));

        // Once added, it is a host and no longer a stranger.
        cfg.Add(new WorkerHostConfig("worker-pc", false, 1, WorkerHostConfig.DefaultSkills, Array.Empty<string>(), 5));
        Assert.Empty(svc.Seen());
    }

    [Fact]
    public async Task A_stranger_heard_from_over_a_day_ago_is_not_listed()
    {
        var svc = new WorkerHostService(() => new List<WorkerHostConfig>(), () => "c", () => _now);
        await svc.CheckInAsync("worker-pc", "abc", Array.Empty<HostRun>(), null, TimeSpan.Zero);
        _now += TimeSpan.FromHours(25);
        Assert.Empty(svc.Seen());
    }
}
