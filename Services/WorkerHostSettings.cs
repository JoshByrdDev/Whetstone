using System.Text.Json;
using System.Text.Json.Nodes;

namespace Whetstone.Services;

/// <summary>
/// Edits to the worker hosts' part of the board host's global config - <c>workerHostsEnabled</c>,
/// <c>workerHosts</c>, and where work goes first (<c>dispatchOrder</c>, <c>dispatchMode</c>) - as the
/// Heartbeat page makes them (docs/worker-hosts-plan.md). Pure functions
/// over the loaded config object, so every rule here is tested without a file or a request.
/// </summary>
public static class WorkerHostSettings
{
    /// <summary>The switch for all remote work. Absent means on, which is what config written before it meant.</summary>
    public const string EnabledKey = "workerHostsEnabled";
    public const string HostsKey = "workerHosts";

    /// <summary>A host's "stop after current runs": see <see cref="WorkerHostConfig.StopPending"/>.</summary>
    public const string StopPendingKey = "stopPending";

    /// <summary>Where work goes first: host names and <see cref="Board"/>, highest priority first.</summary>
    public const string OrderKey = "dispatchOrder";

    /// <summary>How work is shared out along that order: <see cref="Fill"/> or <see cref="Spread"/>.</summary>
    public const string ModeKey = "dispatchMode";

    /// <summary>The board host's own workers in the order. A host name cannot contain '@', so no host is ever it.</summary>
    public const string Board = "@board";

    /// <summary>Fill the first place in the order before the next is given anything. The default, and the only behaviour before there was a choice.</summary>
    public const string Fill = "fill";

    /// <summary>Give each ticket to whichever place has the fewest running, the order breaking ties.</summary>
    public const string Spread = "spread";

    public static readonly string[] Modes = { Fill, Spread };

    /// <summary>How the orchestrator shares out work, as it reads it every pass.</summary>
    public record Dispatch(string Mode, IReadOnlyList<string> Order);

    /// <summary>A change to it, as the page sends one. A null field is left as it is.</summary>
    public record DispatchEdit(string? Mode, string[]? Order);

    /// <summary>The phases a worker host may be given. The steward never runs on one.</summary>
    public static readonly string[] HostableSkills = { "speculate", "implement", "refine", "qa-test", "review-pr" };

    /// <summary>One host's settings as the page sends them. A null field is left as it is.</summary>
    public record HostEdit(bool? Enabled, int? Slots, int? StackCopies, string[]? Skills, string[]? Projects);

    public static void SetEnabled(JsonObject config, bool enabled) => config[EnabledKey] = enabled;

    /// <summary>
    /// Add a host, or change one. A new one starts switched off, with one berth, the phases that take no
    /// stack, and no projects: nothing is sent to a machine until its owner has said which projects may
    /// go there. Returns why the edit was refused, or null.
    /// </summary>
    public static string? Upsert(JsonObject config, string name, HostEdit edit, IReadOnlyCollection<string> knownProjects)
    {
        name = name.Trim();
        if (!WorkerHostConfig.IsValidName(name))
            return "A name is letters, digits, dash, underscore or dot - the same name as that machine's host.json.";

        var hosts = Hosts(config);
        var entry = hosts.OfType<JsonObject>()
            .FirstOrDefault(h => string.Equals(h["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new JsonObject
            {
                ["name"] = name,
                ["enabled"] = false,
                ["slots"] = 1,
                ["skills"] = new JsonArray(WorkerHostConfig.DefaultSkills.Select(s => (JsonNode)s).ToArray()),
                ["projects"] = new JsonArray(),
            };
            hosts.Add(entry);
        }

        // Either way the owner has said what they want now, so a stop that was pending is over:
        // On cancels it, and Off is where it was going.
        if (edit.Enabled is { } on) { entry["enabled"] = on; entry.Remove(StopPendingKey); }
        if (edit.Slots is { } slots) entry["slots"] = Math.Clamp(slots, 1, WorkerHostConfig.MaxSlots);
        if (edit.StackCopies is { } qa)
        {
            entry[ResourceLeaseService.StackCopiesKey] = Math.Clamp(qa, 1, WorkerHostConfig.MaxSlots);
            entry.Remove(ResourceLeaseService.LegacyStackCopiesKey);
        }
        if (edit.Skills is { } skills)
        {
            var unknown = skills.Where(s => !HostableSkills.Contains(s)).ToList();
            if (unknown.Count > 0) return $"Not a phase a worker host can run: {string.Join(", ", unknown)}.";
            // In the pipeline's own order, not the order they were clicked.
            entry["skills"] = new JsonArray(HostableSkills.Where(skills.Contains).Select(s => (JsonNode)s).ToArray());
        }
        if (edit.Projects is { } projects)
        {
            var unknown = projects.Where(p => !knownProjects.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0) return $"No such project: {string.Join(", ", unknown)}.";
            entry["projects"] = new JsonArray(projects.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode)p).ToArray());
        }
        return null;
    }

    /// <summary>
    /// Start or cancel a host's "stop after current runs". Starting one on a host that is already Off is
    /// refused - there is nothing to wait for. Returns why, or null.
    /// </summary>
    public static string? SetStopPending(JsonObject config, string name, bool pending)
    {
        var entry = Entry(config, name);
        if (entry is null) return $"No worker host '{name.Trim()}'.";
        if (pending)
        {
            if (entry["enabled"] is JsonValue v && v.TryGetValue<bool>(out var on) && !on)
                return $"{entry["name"]} is already off.";
            entry[StopPendingKey] = true;
        }
        else entry.Remove(StopPendingKey);
        return null;
    }

    /// <summary>
    /// The end of a pending stop: the host switched Off, as the On/Off control would. False, and no
    /// change, when the stop is no longer pending - cancelled between the board seeing the host drained
    /// and this write.
    /// </summary>
    public static bool FinishStop(JsonObject config, string name)
    {
        var entry = Entry(config, name);
        if (entry?[StopPendingKey] is not JsonValue v || !v.TryGetValue<bool>(out var pending) || !pending) return false;
        entry["enabled"] = false;
        entry.Remove(StopPendingKey);
        return true;
    }

    // ---- This computer: the board host's own place ----
    //
    // The same three things a worker host has - On/Off, a stop after current runs, berths and stacks
    // at once - for the workers in slots 1..MaxWorkers here. Off takes this machine out of dispatch and
    // kills nothing; the orchestrator keeps running, worker hosts keep working, and the steward's berth
    // is not one of these and is untouched. Berths and stacks at once are the existing heartbeatWorkers and
    // stackCopies, so nothing that already reads them changes; the orchestrator reads the berths every
    // pass through /api/hosts/dispatch rather than once at its start.

    /// <summary>The board host's own place: <c>{ enabled, stopPending, stopSince }</c>. Absent means on.</summary>
    public const string BoardKey = "boardHost";

    /// <summary>
    /// How long after a stop is pressed the board waits before it may call this machine drained. A
    /// claim made just before the press reaches the board's slot count only when its worker reports in,
    /// and switching Off in that gap would say "off" over a run that is starting.
    /// </summary>
    public static readonly TimeSpan BoardStopGrace = TimeSpan.FromSeconds(60);

    /// <summary>This computer's place as the orchestrator and the page read it.</summary>
    public record BoardPlace(bool Enabled, bool StopPending, int Slots, int StackCopies)
    {
        /// <summary>Whether the orchestrator may give this machine new work.</summary>
        public bool Dispatchable => Enabled && !StopPending;
    }

    /// <summary>A change to it, as the page sends one. A null field is left as it is.</summary>
    public record BoardEdit(bool? Enabled, int? Slots, int? StackCopies);

    public static BoardPlace ReadBoard(IReadOnlyDictionary<string, JsonElement> global)
    {
        bool enabled = true, stopping = false;
        if (global.TryGetValue(BoardKey, out var b) && b.ValueKind == JsonValueKind.Object)
        {
            if (b.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False) enabled = false;
            stopping = enabled && b.TryGetProperty(StopPendingKey, out var s) && s.ValueKind == JsonValueKind.True;
        }
        var slots = global.TryGetValue("heartbeatWorkers", out var w) && w.ValueKind == JsonValueKind.Number
            ? Math.Clamp((int)w.GetDouble(), 1, HeartbeatManager.MaxWorkers) : HeartbeatManager.DefaultWorkers;
        return new BoardPlace(enabled, stopping, slots, ResourceLeaseService.StackCopies(global));
    }

    /// <summary>Change this computer's place. On or Off ends a pending stop, as it does for a worker host.</summary>
    public static void SetBoard(JsonObject config, BoardEdit edit)
    {
        if (edit.Enabled is { } on)
        {
            var b = BoardEntry(config);
            b["enabled"] = on;
            b.Remove(StopPendingKey);
            b.Remove("stopSince");
        }
        if (edit.Slots is { } slots) config["heartbeatWorkers"] = Math.Clamp(slots, 1, HeartbeatManager.MaxWorkers);
        if (edit.StackCopies is { } qa)
        {
            config[ResourceLeaseService.StackCopiesKey] = Math.Clamp(qa, 1, ResourceLeaseService.MaxStackCopies);
            config.Remove(ResourceLeaseService.LegacyStackCopiesKey);
        }
    }

    /// <summary>Start or cancel this computer's stop after current runs. Returns why it was refused, or null.</summary>
    public static string? SetBoardStop(JsonObject config, bool pending, DateTime now)
    {
        var b = BoardEntry(config);
        if (pending)
        {
            if (b["enabled"] is JsonValue v && v.TryGetValue<bool>(out var on) && !on)
                return "This computer is already off.";
            b[StopPendingKey] = true;
            b["stopSince"] = now.ToUniversalTime().ToString("o");
        }
        else { b.Remove(StopPendingKey); b.Remove("stopSince"); }
        return null;
    }

    /// <summary>
    /// The end of this computer's pending stop: Off, once none of its berths is running and the grace
    /// since the press is over. False, and no change, when there is nothing to finish yet.
    /// </summary>
    public static bool FinishBoardStop(JsonObject config, int busy, DateTime now)
    {
        if (busy > 0 || config[BoardKey] is not JsonObject b) return false;
        if (b[StopPendingKey] is not JsonValue v || !v.TryGetValue<bool>(out var pending) || !pending) return false;
        if (b["stopSince"] is JsonValue s && s.TryGetValue<string>(out var since)
            && DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            && now.ToUniversalTime() - t.ToUniversalTime() < BoardStopGrace)
            return false;
        b["enabled"] = false;
        b.Remove(StopPendingKey);
        b.Remove("stopSince");
        return true;
    }

    private static JsonObject BoardEntry(JsonObject config)
    {
        if (config[BoardKey] is JsonObject o) return o;
        var made = new JsonObject();
        config[BoardKey] = made;
        return made;
    }

    private static JsonObject? Entry(JsonObject config, string name) =>
        Hosts(config).OfType<JsonObject>()
            .FirstOrDefault(h => string.Equals(h["name"]?.GetValue<string>(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Take a host out of the config. False when there was none by that name.</summary>
    public static bool Remove(JsonObject config, string name)
    {
        var hosts = Hosts(config);
        var entry = hosts.OfType<JsonObject>()
            .FirstOrDefault(h => string.Equals(h["name"]?.GetValue<string>(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        hosts.Remove(entry);
        // Out of the order too, so a host added again later by the same name starts at the end
        // rather than wherever the one before it stood.
        if (config[OrderKey] is JsonArray order)
            foreach (var n in order.Where(n => n is JsonValue v && v.TryGetValue<string>(out var s)
                         && string.Equals(s, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList())
                order.Remove(n);
        return true;
    }

    /// <summary>
    /// The mode and the order as they stand. The order always names every configured host and the
    /// board exactly once: a name it does not know is dropped, and a host it does not list is added
    /// after those it does - before the board when the board is not listed either, which is what an
    /// order nobody has written means. Unwritten, that is every host in config order, then the board,
    /// in fill mode: worker hosts first, as it was before there was a setting.
    /// </summary>
    public static Dispatch ReadDispatch(IReadOnlyDictionary<string, JsonElement> global, IReadOnlyList<WorkerHostConfig> hosts)
    {
        var mode = global.TryGetValue(ModeKey, out var m) && m.ValueKind == JsonValueKind.String
            && Modes.Contains(m.GetString()) ? m.GetString()! : Fill;
        var listed = global.TryGetValue(OrderKey, out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
            : Enumerable.Empty<string>();
        return new Dispatch(mode, NormalizeOrder(listed, hosts.Select(h => h.Name).ToList()));
    }

    public static IReadOnlyList<string> NormalizeOrder(IEnumerable<string> listed, IReadOnlyList<string> hostNames)
    {
        var result = new List<string>();
        foreach (var raw in listed)
        {
            var n = raw.Trim();
            var name = n.Equals(Board, StringComparison.OrdinalIgnoreCase) ? Board
                : hostNames.FirstOrDefault(h => h.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (name is not null && !result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
        }
        var hadBoard = result.Contains(Board);
        result.AddRange(hostNames.Where(h => !result.Contains(h, StringComparer.OrdinalIgnoreCase)));
        if (!hadBoard) result.Add(Board);
        return result;
    }

    /// <summary>Change the mode, the order, or both. Returns why the edit was refused, or null.</summary>
    public static string? SetDispatch(JsonObject config, DispatchEdit edit, IReadOnlyList<WorkerHostConfig> hosts)
    {
        if (edit.Mode is { } mode && !Modes.Contains(mode))
            return $"Not a way of sharing out work: {mode}. It is one of {string.Join(", ", Modes)}.";
        if (edit.Order is { } order)
        {
            var unknown = order.Where(n => !n.Trim().Equals(Board, StringComparison.OrdinalIgnoreCase)
                && !hosts.Any(h => h.Name.Equals(n.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0) return $"Not a worker host: {string.Join(", ", unknown)}.";
        }

        if (edit.Mode is { } m) config[ModeKey] = m;
        if (edit.Order is { } o)
            config[OrderKey] = new JsonArray(NormalizeOrder(o, hosts.Select(h => h.Name).ToList()).Select(n => (JsonNode)n).ToArray());
        return null;
    }

    /// <summary>
    /// Why changing <paramref name="name"/>'s berth count, or removing it, would be unsafe now - or null.
    /// Hosts are numbered in blocks, in config order, so either one renumbers every host after it, and
    /// a host with runs out is tracked by the slot numbers it was given. The host itself may be
    /// changed while busy only by removing it, which is refused too: its runs would be orphaned.
    /// </summary>
    public static string? RenumberingProblem(IReadOnlyList<WorkerHostView> hosts, string name, bool removing)
    {
        var index = hosts.ToList().FindIndex(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (removing && hosts[index].Runs.Any(r => !r.Exited))
            return $"{hosts[index].Name} has work out. Switch it off, let its runs finish, then remove it.";
        var busy = hosts.Skip(index + 1).FirstOrDefault(h => h.Runs.Any(r => !r.Exited));
        return busy is null ? null
            : $"That would renumber {busy.Name}'s berths while it has work out. Wait for its runs to finish.";
    }

    private static JsonArray Hosts(JsonObject config)
    {
        if (config[HostsKey] is JsonArray a) return a;
        var made = new JsonArray();
        config[HostsKey] = made;
        return made;
    }
}
