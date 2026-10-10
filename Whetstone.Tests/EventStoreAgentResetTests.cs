using System.Text.Json;
using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Clearing ghost agents after a heartbeat is killed.
///
/// A hard Stop kills the whole process tree, so neither the workers (cycle_end) nor the
/// orchestrator (state_change idle) ever report their slots free — the slots sit in the EventStore
/// still "running" their long-dead tickets. GetState hides them while the process is down, which is
/// exactly what made this bug invisible: they resurfaced only when the NEXT heartbeat brought
/// liveness back, showing two bots working a ticket only one of them held.
/// </summary>
public class EventStoreAgentResetTests : IDisposable
{
    private readonly string _root;
    private readonly DashboardPaths _paths;

    public EventStoreAgentResetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-evt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _paths = new DashboardPaths(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private EventStore NewStore() =>
        new(_paths, new HeartbeatManager(_paths), new ProjectRegistry(_paths));

    /// <summary>
    /// Make IsRunning true by pointing the lock file at this very test process — the same two-line
    /// contract heartbeat.ps1 writes (pid, then start time). The busy-agent list is only reported
    /// while a heartbeat is alive, so a test about what that list shows needs one.
    /// </summary>
    private void FakeLiveHeartbeat()
    {
        var me = System.Diagnostics.Process.GetCurrentProcess();
        File.WriteAllLines(_paths.GlobalLockFile,
            new[] { me.Id.ToString(), me.StartTime.ToUniversalTime().ToString("o") });
    }

    private static HeartbeatEvent CycleStart(int slot, string ticketId) => new()
    {
        Type = EventTypes.CycleStart,
        Agent = slot,
        TicketId = ticketId,
        Phase = "Implementing",
        Project = "alpha",
    };

    private static List<JsonElement> BusyAgents(EventStore store)
    {
        var json = JsonSerializer.Serialize(store.GetState());
        return JsonDocument.Parse(json).RootElement.GetProperty("agents")
            .EnumerateArray().ToList();
    }

    [Fact]
    public void A_killed_fleet_reports_its_slots_as_ghosts_until_reset()
    {
        FakeLiveHeartbeat();
        var store = NewStore();
        store.Add(CycleStart(1, "36"));
        store.Add(CycleStart(2, "40"));

        // The kill itself produces no events at all — that silence IS the bug being tested.
        Assert.Equal(2, BusyAgents(store).Count);

        Assert.Equal(2, store.ResetAgents());
        Assert.Empty(BusyAgents(store));
    }

    [Fact]
    public void The_reset_survives_a_dashboard_restart()
    {
        FakeLiveHeartbeat();
        var store = NewStore();
        store.Add(CycleStart(1, "36"));
        store.Add(CycleStart(2, "40"));
        store.ResetAgents();

        // A fresh store over the same data root is a dashboard restart: state is rebuilt purely by
        // replaying the log. An in-memory-only reset would hand the ghosts right back here.
        Assert.Empty(BusyAgents(NewStore()));
    }

    [Fact]
    public void Only_busy_slots_are_reset_and_idle_ones_stay_quiet()
    {
        FakeLiveHeartbeat();
        var store = NewStore();
        store.Add(CycleStart(1, "36"));
        store.Add(new HeartbeatEvent { Type = EventTypes.CycleEnd, Agent = 1 });
        store.Add(CycleStart(2, "40"));

        // Slot 1 already said its own goodbye; only slot 2 needs clearing.
        Assert.Equal(1, store.ResetAgents());
        Assert.Empty(BusyAgents(store));
    }

    [Fact]
    public void An_adopted_survivor_announced_as_working_counts_as_running()
    {
        FakeLiveHeartbeat();
        var store = NewStore();

        // What Add-OrphanAgents sends when a new orchestrator takes a still-live worker onto its
        // books. "working" has to land as running or the busy filter never shows adopted agents.
        store.Add(new HeartbeatEvent
        {
            Type = EventTypes.StateChange,
            Agent = 2,
            Text = "working",
            TicketId = "36",
            Project = "alpha",
        });

        var one = Assert.Single(BusyAgents(store));
        Assert.Equal("running", one.GetProperty("status").GetString());
        Assert.Equal("36", one.GetProperty("ticketId").GetString());
        Assert.Equal("alpha", one.GetProperty("project").GetString());
    }
}
