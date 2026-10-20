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
    public record HostEdit(bool? Enabled, int? Slots, int? QaLeases, string[]? Skills, string[]? Projects);

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

        if (edit.Enabled is { } on) entry["enabled"] = on;
        if (edit.Slots is { } slots) entry["slots"] = Math.Clamp(slots, 1, WorkerHostConfig.MaxSlots);
        if (edit.QaLeases is { } qa) entry["qaLeases"] = Math.Clamp(qa, 1, WorkerHostConfig.MaxSlots);
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
