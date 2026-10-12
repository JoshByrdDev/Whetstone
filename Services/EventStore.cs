using System.Collections.Concurrent;
using System.Text.Json;
using Whetstone.Models;

namespace Whetstone.Services;

public class EventStore
{
    private readonly ConcurrentQueue<HeartbeatEvent> _events = new();
    private const int MaxCapacity = 500;
    private const int CompactThreshold = 1500;

    private readonly string _filePath;
    private readonly object _fileLock = new();
    private int _fileLineCount;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// What one agent slot is doing. The fleet is a handful of these, and everything the UI shows —
    /// which bot is out, on which ticket, for how long — is read from here rather than inferred by
    /// walking the event list, which cannot tell two interleaved agents apart.
    /// </summary>
    private sealed class AgentState
    {
        public string Status = "idle";
        public string? TicketId;
        public string? Phase;
        public string? Project;
        public DateTime? Since;
    }

    // The orchestrator's own state — sleeping, hibernating, idle. Distinct from any agent's:
    // "the fleet is asleep" and "agent 2 is between tickets" are different facts.
    private string _currentState = "idle";
    private string? _currentProject;
    private DateTime? _lastEventTime;

    private readonly Dictionary<int, AgentState> _agents = new();
    private readonly object _agentLock = new();

    private readonly HeartbeatManager _heartbeat;
    private readonly ProjectRegistry _registry;

    public EventStore(DashboardPaths paths, HeartbeatManager heartbeat, ProjectRegistry registry)
    {
        // One global activity log across all projects — the heartbeat is project-agnostic.
        _heartbeat = heartbeat;
        _registry = registry;
        _filePath = paths.GlobalEventsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            var lines = File.ReadAllLines(_filePath);
            _fileLineCount = lines.Length;

            // Only replay the last MaxCapacity lines
            var start = Math.Max(0, lines.Length - MaxCapacity);
            for (int i = start; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<HeartbeatEvent>(lines[i], _jsonOptions);
                    if (evt != null)
                    {
                        _events.Enqueue(evt);
                        UpdateState(evt);
                    }
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch { /* file read failure — start empty */ }
    }

    public void Add(HeartbeatEvent evt)
    {
        // The heartbeat stamps each worker event with its project; global events
        // (sleep, idle, off-hours) legitimately have no project.
        _events.Enqueue(evt);
        UpdateState(evt);

        // Trim memory buffer
        while (_events.Count > MaxCapacity)
            _events.TryDequeue(out _);

        // Persist to disk
        AppendToDisk(evt);
    }

    private void AppendToDisk(HeartbeatEvent evt)
    {
        try
        {
            lock (_fileLock)
            {
                var line = JsonSerializer.Serialize(evt, _jsonOptions);
                File.AppendAllText(_filePath, line + "\n");
                _fileLineCount++;

                if (_fileLineCount > CompactThreshold)
                    Compact();
            }
        }
        catch { /* disk write failure — events still live in memory */ }
    }

    private void Compact()
    {
        try
        {
            var lines = File.ReadAllLines(_filePath);
            var keep = lines.TakeLast(MaxCapacity).ToArray();
            File.WriteAllLines(_filePath, keep);
            _fileLineCount = keep.Length;
        }
        catch { /* compaction failure is non-fatal */ }
    }

    private void UpdateState(HeartbeatEvent evt)
    {
        _lastEventTime = evt.Timestamp;

        if (evt.Project != null) _currentProject = evt.Project;

        // An event carrying an agent slot is that agent's business and nobody else's. Events without
        // one come from the orchestrator itself and describe the fleet.
        if (evt.Agent is int slot && slot > 0) { UpdateAgent(slot, evt); return; }

        switch (evt.Type)
        {
            case EventTypes.Sleep:
                _currentState = "sleeping";
                break;
            case EventTypes.Error:
                _currentState = "error";
                break;
            case EventTypes.StateChange:
                if (evt.Text != null)
                    _currentState = evt.Text;
                break;
        }
    }

    /// <summary>
    /// Fold one agent's event into that agent's state. Deliberately narrow: an agent going idle says
    /// nothing about whether the other two are still working, and the fleet-level status is derived
    /// from all of them in <see cref="GetState"/> rather than overwritten by whoever spoke last.
    /// </summary>
    private void UpdateAgent(int slot, HeartbeatEvent evt)
    {
        lock (_agentLock)
        {
            if (!_agents.TryGetValue(slot, out var a)) _agents[slot] = a = new AgentState();

            switch (evt.Type)
            {
                case EventTypes.CycleStart:
                    a.Status = "running";
                    a.TicketId = evt.TicketId ?? a.TicketId;
                    a.Phase = evt.Phase ?? a.Phase;
                    a.Project = evt.Project ?? a.Project;
                    a.Since = evt.Timestamp;
                    break;
                case EventTypes.CycleEnd:
                    a.Status = "idle";
                    a.TicketId = null;
                    a.Phase = null;
                    a.Since = evt.Timestamp;
                    break;
                case EventTypes.StateChange:
                    // How the orchestrator reports a slot from outside the worker: "idle" for one
                    // it has reaped (including a worker killed before its own cycle_end), "working"
                    // for a survivor of a dead orchestrator it has just adopted mid-cycle. The
                    // adopted one is running by any other name, and has to be stored under the name
                    // the fleet view filters on or an adopted agent would never be shown at all.
                    if (evt.Text != null) a.Status = evt.Text == "working" ? "running" : evt.Text;
                    if (a.Status != "running") { a.TicketId = null; a.Phase = null; }
                    else
                    {
                        a.TicketId = evt.TicketId ?? a.TicketId;
                        a.Phase = evt.Phase ?? a.Phase;
                        a.Project = evt.Project ?? a.Project;
                    }
                    a.Since = evt.Timestamp;
                    break;
                case EventTypes.Error:
                    // Not a status change: an agent that hits an error is still the agent working
                    // this ticket, and it goes on to finish the cycle and say so.
                    a.TicketId = evt.TicketId ?? a.TicketId;
                    break;
                default:
                    // Any other chatter (tools, narration, thinking) just keeps the agent's identity
                    // current — a resumed ticket announces itself through these too.
                    a.TicketId = evt.TicketId ?? a.TicketId;
                    a.Phase = evt.Phase ?? a.Phase;
                    a.Project = evt.Project ?? a.Project;
                    break;
            }
        }
    }

    /// <summary>
    /// Put every slot that still claims to be busy back to idle. For the two moments the fleet is
    /// known to be gone and cannot say so itself: right after a hard Stop (the kill takes every
    /// worker with it, so nobody sends its own goodbye) and right after a successful Start (a
    /// heartbeat killed outside the dashboard leaves the same ghost slots behind, and Start only
    /// succeeds when nothing was alive). Without this, the ghosts sit invisible while stopped —
    /// GetState hides agents when the process is dead — and resurface as "working" their long-gone
    /// tickets the moment a new heartbeat brings liveness back.
    ///
    /// The reset goes through the same event path the orchestrator uses so it also lands in the
    /// on-disk log: a bare in-memory wipe would be undone by the next restart's replay. Anything
    /// genuinely still alive re-announces itself within seconds — fresh agents with cycle_start,
    /// adopted survivors through the orchestrator's adoption event.
    /// </summary>
    public int ResetAgents()
    {
        List<int> stale;
        lock (_agentLock)
        {
            stale = _agents.Where(kv => kv.Value.Status != "idle")
                           .Select(kv => kv.Key).OrderBy(s => s).ToList();
        }
        foreach (var slot in stale)
            Add(new HeartbeatEvent { Type = EventTypes.StateChange, Agent = slot, Text = "idle" });
        return stale.Count;
    }

    /// <summary>
    /// How many agent slots are out right now — which, during a graceful stop, is exactly what the
    /// shutdown is waiting on. Read straight from the slot map rather than from
    /// <see cref="GetState"/>, because that hides the fleet whenever the orchestrator is not
    /// running and a drain is precisely when both facts have to be true at once.
    /// </summary>
    public int BusyAgentCount
    {
        get { lock (_agentLock) return _agents.Count(kv => kv.Value.Status == "running"); }
    }

    public List<HeartbeatEvent> GetRecent(int count = 50)
    {
        return _events.ToArray()
            .TakeLast(count)
            .ToList();
    }

    /// <summary>
    /// Read all events from disk (for history queries that may exceed the in-memory buffer).
    /// </summary>
    public List<HeartbeatEvent> GetAllFromDisk()
    {
        var results = new List<HeartbeatEvent>();
        if (!File.Exists(_filePath)) return results;

        try
        {
            foreach (var line in File.ReadLines(_filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<HeartbeatEvent>(line, _jsonOptions);
                    if (evt != null) results.Add(evt);
                }
                catch { /* skip malformed */ }
            }
        }
        catch { /* file read failure */ }

        return results;
    }

    /// <summary>How many agent slots the fleet is configured to run — the number of berths the UI draws.</summary>
    private int ConfiguredWorkers()
    {
        try
        {
            var g = _registry.GetGlobalConfig();
            if (g.TryGetValue("heartbeatWorkers", out var w) && w.ValueKind == JsonValueKind.Number)
                return Math.Clamp((int)w.GetDouble(), 1, HeartbeatManager.MaxWorkers);
        }
        catch { /* a malformed config must not blank the whole status */ }
        return HeartbeatManager.MaxWorkers;
    }

    public object GetState()
    {
        // The event stream only ever moves the status forward (running -> sleeping -> ...); it has no
        // way to represent "the heartbeat isn't running", so a stopped/crashed process would otherwise
        // leave the status frozen at its last event (e.g. "sleeping"). Reconcile against actual process
        // liveness: if the heartbeat isn't running, it's "stopped" and there is no active ticket/phase.
        var running = _heartbeat.IsRunning;

        List<object> agents;
        object? steward;
        AgentState? lead = null;
        lock (_agentLock)
        {
            // Only the agents that are actually out. An empty berth is the UI's business to draw
            // from the slot count, not a fact worth streaming on every event.
            var busy = running
                ? _agents.Where(kv => kv.Value.Status == "running").OrderBy(kv => kv.Key).ToList()
                : new List<KeyValuePair<int, AgentState>>();

            // A steward's run is out too, but it is not a ticket being worked: it holds no ticket,
            // takes no worker's berth, and "4 of 3 working" is what counting it as one looks like.
            // It is reported beside the agents, never among them.
            steward = busy.Where(kv => kv.Value.Phase == EventPhases.Steward)
                .Select(kv => (object)new { slot = kv.Key, project = kv.Value.Project, since = kv.Value.Since })
                .FirstOrDefault();
            var workers = busy.Where(kv => kv.Value.Phase != EventPhases.Steward).ToList();

            lead = workers.Select(kv => kv.Value).FirstOrDefault();
            agents = workers.Select(kv => (object)new
            {
                slot = kv.Key,
                status = kv.Value.Status,
                ticketId = kv.Value.TicketId,
                phase = kv.Value.Phase,
                project = kv.Value.Project,
                since = kv.Value.Since,
            }).ToList();
        }

        // One agent working means the fleet is running, whatever the orchestrator was last heard
        // saying: it reports "idle" between claims, and with three agents out that would read as a
        // stopped pipeline. A steward's run counts for the same reason.
        var status = !running ? "stopped"
                   : agents.Count > 0 || steward is not null ? "running"
                   : _currentState;

        return new
        {
            status,
            // The lead agent's ticket, kept so every surface that only ever knew about one agent
            // still says something true while the multi-agent views read `agents` instead.
            ticketId = lead?.TicketId,
            phase = lead?.Phase,
            project = lead?.Project ?? _currentProject,
            lastEventTime = _lastEventTime,
            totalEvents = _events.Count,
            workers = ConfiguredWorkers(),
            agents,
            steward,
        };
    }
}
