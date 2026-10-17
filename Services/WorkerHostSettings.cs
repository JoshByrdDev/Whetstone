using System.Text.Json.Nodes;

namespace Whetstone.Services;

/// <summary>
/// Edits to the worker hosts' part of the board host's global config - <c>workerHostsEnabled</c> and
/// <c>workerHosts</c> - as the Heartbeat page makes them (docs/worker-hosts-plan.md). Pure functions
/// over the loaded config object, so every rule here is tested without a file or a request.
/// </summary>
public static class WorkerHostSettings
{
    /// <summary>The switch for all remote work. Absent means on, which is what config written before it meant.</summary>
    public const string EnabledKey = "workerHostsEnabled";
    public const string HostsKey = "workerHosts";

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
        return true;
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
