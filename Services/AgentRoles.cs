using System.Text.Json;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>What one kind of run is set to: which CLI, which model on it, and how hard it thinks.</summary>
public sealed record AgentRole(string Provider, string Model, string Effort);

/// <summary>
/// Every kind of agent run the system makes, and which provider, model and effort each one takes.
/// <para>
/// There is no fleet-wide provider. Each role names its own, so implement can run on codex while
/// qa-test stays on Claude. Stored as <c>agentRoles</c> in global config, one object per role.
/// </para>
/// <para>
/// A role missing from <c>agentRoles</c> is read from the settings that came before it
/// (<c>agentProvider</c>, <c>workerModel</c>, <c>phaseModels</c>, <c>reflectionModel</c>,
/// <c>analysisModel</c>, <c>workerReasoningEffort</c>), so a config file written before roles
/// existed runs exactly as it did. Saving from Settings writes every role and drops those keys.
/// </para>
/// <para>
/// The heartbeat reads the same setting with the same fallbacks in <c>Get-AgentRole</c>
/// (<c>data/heartbeat-common.ps1</c>). The two must agree.
/// </para>
/// </summary>
public static class AgentRoles
{
    public const string Steward = "steward";
    public const string Reflection = "reflection";
    /// <summary>The Classroom: a ticket's analysis, its walkthrough, and a unit's report.</summary>
    public const string Analysis = "analysis";
    public const string Applier = "applier";

    /// <summary>The keys of the settings <c>agentRoles</c> replaced, removed once it is saved.</summary>
    public static readonly string[] LegacyKeys =
    {
        "agentProvider", "workerModel", "reflectionModel", "phaseModels", "analysisModel", "workerReasoningEffort",
    };

    /// <summary>The providers a role may name. An unknown name reads as Claude, the incumbent.</summary>
    public static readonly string[] Providers = { "claude", "codex", "gemini" };

    /// <summary>The effort levels a role may name.</summary>
    public static readonly string[] Efforts = { "low", "medium", "high", "xhigh" };

    /// <summary>
    /// Every role, pipeline phases first in pipeline order. The phases come from
    /// <see cref="PhaseMachine.AllSkills"/>, so a skill added to the pipeline gets a role without an
    /// edit here.
    /// </summary>
    public static readonly string[] All =
        PhaseMachine.AllSkills.Concat(new[] { Steward, Reflection, Analysis, Applier }).ToArray();

    /// <summary>
    /// The roles the heartbeat runs, which is every role but the two the dashboard runs itself.
    /// Mirrored as <c>$AgentRoleKeys</c> in <c>data/heartbeat-common.ps1</c>.
    /// </summary>
    public static readonly string[] HeartbeatRoles = All.Where(r => r is not (Analysis or Applier)).ToArray();

    public static string NormalizeProvider(string? stored)
    {
        var key = (stored ?? "").Trim().ToLowerInvariant();
        return Providers.Contains(key) ? key : "claude";
    }

    /// <summary>What a provider runs for a role when no model is named.</summary>
    public static string DefaultModel(string provider, string role)
    {
        var reflection = role == Reflection;
        return NormalizeProvider(provider) switch
        {
            "codex" => reflection ? CodexAgentProvider.DefaultReflectionModel : CodexAgentProvider.DefaultModel,
            "gemini" => reflection ? GeminiAgentProvider.DefaultReflectionModel : GeminiAgentProvider.DefaultModel,
            _ => reflection ? ClaudeAgentProvider.DefaultReflectionModel : ClaudeAgentProvider.DefaultModel,
        };
    }

    /// <summary>The role as configured, with every gap filled. Never null, never a blank field.</summary>
    public static AgentRole Resolve(IReadOnlyDictionary<string, JsonElement> cfg, string role)
    {
        JsonElement entry = default;
        var hasEntry = cfg.TryGetValue("agentRoles", out var roles)
            && roles.ValueKind == JsonValueKind.Object
            && roles.TryGetProperty(role, out entry)
            && entry.ValueKind == JsonValueKind.Object;

        var provider = NormalizeProvider(hasEntry && Text(entry, "provider") is { } p ? p : Text(cfg, "agentProvider"));

        // The first candidate this provider will take. A model that is unmistakably another
        // provider's is skipped rather than run: it would fail every run with an error that says
        // nothing about the setting that caused it.
        var candidates = hasEntry
            ? new[] { Text(entry, "model") }
            : role switch
            {
                Reflection => new[] { Text(cfg, "reflectionModel") },
                Analysis => new[] { Text(cfg, "analysisModel"), Text(cfg, "workerModel") },
                Applier => new[] { Text(cfg, "workerModel") },
                _ => new[] { PhaseModel(cfg, role), Text(cfg, "workerModel") },
            };
        var model = candidates.FirstOrDefault(m => m is not null && AgentModels.BelongsTo(provider, m))
            ?? DefaultModel(provider, role);

        var effort = (hasEntry ? Text(entry, "effort") : null) ?? Text(cfg, "workerReasoningEffort")
            ?? AgentProviderFactory.DefaultEffort;

        return new AgentRole(provider, model, effort);
    }

    /// <summary>A trimmed string property, or null when it is absent, not a string, or blank.</summary>
    private static string? Text(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString()?.Trim() is { Length: > 0 } s ? s : null;

    private static string? Text(IReadOnlyDictionary<string, JsonElement> cfg, string key) =>
        cfg.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            && v.GetString()?.Trim() is { Length: > 0 } s ? s : null;

    private static string? PhaseModel(IReadOnlyDictionary<string, JsonElement> cfg, string role) =>
        cfg.TryGetValue("phaseModels", out var map) ? Text(map, role) : null;
}
