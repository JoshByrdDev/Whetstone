using System.Text.Json;
using System.Text.Json.Nodes;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// This computer's own place on the Heartbeat page: the board host's workers switched On or Off,
/// stopped after their current runs, and their berths and stacks at once - the same controls a worker
/// host has. Off and a stop take nothing from anyone: they only stop new work being given here.
/// </summary>
public class WorkerHostBoardPlaceTests
{
    private readonly DateTime _now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyDictionary<string, JsonElement> AsGlobal(JsonObject config) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.ToJsonString())!;

    [Fact]
    public void Unwritten_it_is_on_with_the_configured_berths()
    {
        var place = WorkerHostSettings.ReadBoard(AsGlobal(new JsonObject { ["heartbeatWorkers"] = 4, ["stackCopies"] = 2 }));
        Assert.True(place.Enabled);
        Assert.False(place.StopPending);
        Assert.True(place.Dispatchable);
        Assert.Equal(4, place.Slots);
        Assert.Equal(2, place.StackCopies);
    }

    [Fact]
    public void Berths_and_stacks_are_the_settings_that_already_exist_and_are_clamped_where_stored()
    {
        var config = new JsonObject();
        WorkerHostSettings.SetBoard(config, new(null, 40, 0));
        Assert.Equal(HeartbeatManager.MaxWorkers, config["heartbeatWorkers"]!.GetValue<int>());
        Assert.Equal(1, config["stackCopies"]!.GetValue<int>());
        Assert.Null(config[WorkerHostSettings.BoardKey]);   // nothing about On/Off was said
    }

    [Fact]
    public void Stacks_at_once_written_under_its_old_name_is_still_read_and_the_next_save_renames_it()
    {
        // A config from before the rename, when QA was the only phase that took the stack.
        var config = new JsonObject { ["qaLeases"] = 3 };
        Assert.Equal(3, WorkerHostSettings.ReadBoard(AsGlobal(config)).StackCopies);

        // The new name wins over the old where both are present.
        Assert.Equal(2, WorkerHostSettings.ReadBoard(AsGlobal(new JsonObject { ["qaLeases"] = 3, ["stackCopies"] = 2 })).StackCopies);

        WorkerHostSettings.SetBoard(config, new(null, null, 4));
        Assert.Equal(4, config["stackCopies"]!.GetValue<int>());
        Assert.Null(config["qaLeases"]);
    }

    [Fact]
    public void A_pending_stop_takes_it_out_of_dispatch_but_leaves_it_on()
    {
        var config = new JsonObject();
        Assert.Null(WorkerHostSettings.SetBoardStop(config, true, _now));
        var place = WorkerHostSettings.ReadBoard(AsGlobal(config));
        Assert.True(place.Enabled);
        Assert.True(place.StopPending);
        Assert.False(place.Dispatchable);
    }

    [Fact]
    public void A_stop_on_a_computer_already_off_is_refused()
    {
        var config = new JsonObject();
        WorkerHostSettings.SetBoard(config, new(false, null, null));
        Assert.NotNull(WorkerHostSettings.SetBoardStop(config, true, _now));
    }

    [Fact]
    public void It_switches_off_only_once_nothing_is_running_and_the_grace_is_over()
    {
        var config = new JsonObject();
        WorkerHostSettings.SetBoardStop(config, true, _now);

        Assert.False(WorkerHostSettings.FinishBoardStop(config, 2, _now.AddMinutes(10)));   // runs out
        Assert.False(WorkerHostSettings.FinishBoardStop(config, 0, _now.AddSeconds(5)));    // a claim may be starting
        Assert.True(WorkerHostSettings.ReadBoard(AsGlobal(config)).StopPending);

        Assert.True(WorkerHostSettings.FinishBoardStop(config, 0, _now + WorkerHostSettings.BoardStopGrace));
        var place = WorkerHostSettings.ReadBoard(AsGlobal(config));
        Assert.False(place.Enabled);
        Assert.False(place.StopPending);
    }

    [Fact]
    public void A_cancelled_stop_is_not_finished()
    {
        var config = new JsonObject();
        WorkerHostSettings.SetBoardStop(config, true, _now);
        WorkerHostSettings.SetBoardStop(config, false, _now);
        Assert.False(WorkerHostSettings.FinishBoardStop(config, 0, _now.AddHours(1)));
        Assert.True(WorkerHostSettings.ReadBoard(AsGlobal(config)).Dispatchable);
    }

    [Fact]
    public void On_or_Off_ends_a_pending_stop()
    {
        var config = new JsonObject();
        WorkerHostSettings.SetBoardStop(config, true, _now);
        WorkerHostSettings.SetBoard(config, new(true, null, null));
        var place = WorkerHostSettings.ReadBoard(AsGlobal(config));
        Assert.True(place.Dispatchable);
        Assert.False(WorkerHostSettings.FinishBoardStop(config, 0, _now.AddHours(1)));
    }
}
