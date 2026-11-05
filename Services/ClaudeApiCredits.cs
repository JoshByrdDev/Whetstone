using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>A Console API key from an organization a Max or Ultra plan is linked to, and its name.</summary>
public sealed record ApiCreditKey(string Name, string Key);

/// <summary>The API-credits setting as configured. Off, with nothing in it, unless its owner turned it on.</summary>
public sealed record ApiCreditsSettings(bool Enabled, IReadOnlyList<string> Roles, IReadOnlyList<ApiCreditKey> Keys)
{
    public static readonly ApiCreditsSettings Off = new(false, Array.Empty<string>(), Array.Empty<ApiCreditKey>());
}

/// <summary>
/// The dashboard's own runs - the Classroom, the improvement applier - on a plan's monthly API
/// credits instead of its usage limits.
///
/// <para>
/// Max and Ultra come with $100 / $200 a month of credit in a linked Console organization. It pays
/// for the Agent SDK on an API key and not for Claude Code, so a run that should spend it goes
/// through <c>data/agent-sdk/run.mjs</c> rather than the CLI. It is off by default: a machine whose
/// plans have no linked organization keeps running every role on plan usage, exactly as before.
/// </para>
///
/// <para>
/// The credits are the whole budget, never a floor. A linked organization with no purchased credit
/// and no auto-reload refuses requests once the credit is spent rather than charging anyone, and a
/// run refused that way - or by a key that does not work - is run again on plan usage, so a spent
/// key costs a moment, not the run. Keeping that organization free of purchased credit is its
/// owner's part; nothing here can buy any.
/// </para>
/// </summary>
public static class ClaudeApiCredits
{
    public const string ConfigKey = "claudeApiCredits";

    /// <summary>The roles that may spend credits: the ones the dashboard runs itself.</summary>
    public static readonly string[] EligibleRoles = { AgentRoles.Analysis, AgentRoles.Applier };

    /// <summary>What a newly switched-on setting covers when it names no roles: the Classroom.</summary>
    public static readonly string[] DefaultRoles = { AgentRoles.Analysis };

    public static ApiCreditsSettings Read(IReadOnlyDictionary<string, JsonElement> config)
    {
        if (!config.TryGetValue(ConfigKey, out var o) || o.ValueKind != JsonValueKind.Object) return ApiCreditsSettings.Off;
        var enabled = o.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;

        var roles = new List<string>();
        if (o.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array)
            foreach (var v in r.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && EligibleRoles.Contains(v.GetString()) && !roles.Contains(v.GetString()!))
                    roles.Add(v.GetString()!);

        var keys = new List<ApiCreditKey>();
        if (o.TryGetProperty("keys", out var k) && k.ValueKind == JsonValueKind.Array)
            foreach (var entry in k.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
                var key = entry.TryGetProperty("key", out var kv) && kv.ValueKind == JsonValueKind.String ? kv.GetString()!.Trim() : "";
                if (name.Length > 0 && key.Length > 0 && !keys.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    keys.Add(new ApiCreditKey(name, key));
            }
        return new ApiCreditsSettings(enabled, roles, keys);
    }

    /// <summary>
    /// The setting as it is stored, from what Settings sent and what is stored already.
    /// <para>
    /// The keys never travel back to the page - it is told only that one is saved - so an entry
    /// sent without a key keeps the key already saved under its name. Sending a key replaces it;
    /// leaving the entry out removes it.
    /// </para>
    /// </summary>
    public static JsonObject Normalize(JsonNode? incoming, JsonNode? stored)
    {
        var enabled = incoming?["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var on) && on;

        var roles = new JsonArray();
        if (incoming?["roles"] is JsonArray rs)
            foreach (var v in rs)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var role) && EligibleRoles.Contains(role)
                    && !roles.Any(x => x!.GetValue<string>() == role))
                    roles.Add(role);
        if (incoming?["roles"] is null)
            foreach (var role in DefaultRoles) roles.Add(role);

        var saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stored?["keys"] is JsonArray sk)
            foreach (var entry in sk.OfType<JsonObject>())
                if (Text(entry["name"]) is { Length: > 0 } n && Text(entry["key"]) is { Length: > 0 } k)
                    saved.TryAdd(n, k);

        var keys = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (incoming?["keys"] is JsonArray ks)
            foreach (var entry in ks.OfType<JsonObject>())
            {
                var name = Text(entry["name"]) ?? "";
                if (name.Length == 0 || !seen.Add(name)) continue;
                var key = Text(entry["key"]) is { Length: > 0 } sent ? sent : saved.GetValueOrDefault(name);
                if (string.IsNullOrEmpty(key)) continue;
                keys.Add(new JsonObject { ["name"] = name, ["key"] = key });
            }

        return new JsonObject { ["enabled"] = enabled, ["roles"] = roles, ["keys"] = keys };
    }

    private static string? Text(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    /// <summary>Why the runner cannot be used on this machine, or null when it can.</summary>
    public static string? RunnerUnavailable(DashboardPaths paths)
    {
        var script = RunnerScript(paths);
        if (!File.Exists(script)) return $"the Agent SDK runner is missing ({script})";
        if (!Directory.Exists(Path.Combine(paths.AgentSdkDir, "node_modules", "@anthropic-ai", "claude-agent-sdk")))
            return $"the Agent SDK is not installed - run `npm install` in {paths.AgentSdkDir}";
        return null;
    }

    public static string RunnerScript(DashboardPaths paths) => Path.Combine(paths.AgentSdkDir, "run.mjs");

    // "Your credit balance is too low to access the Anthropic API" from the API, "Credit balance
    // too low" from Claude Code, and the two ways a key that does not work is reported.
    private static readonly Regex RefusedByKey = new(
        @"credit balance|invalid api key|invalid x-api-key|authentication_error|\b401\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Whether a run on credits ended because its key could not pay for it - spent, or not a working
    /// key - rather than because of the work itself. Only that kind of failure is worth retrying on
    /// plan usage: anything else would fail there the same way.
    /// </summary>
    public static bool IsRefusedByKey(string? error) => error is not null && RefusedByKey.IsMatch(error);

    /// <summary>Why a key refused a run, in a few words for Settings.</summary>
    public static string Describe(string error) =>
        Regex.IsMatch(error, "credit balance", RegexOptions.IgnoreCase)
            ? "credits used up for this cycle"
            : "the key was not accepted";
}

/// <summary>
/// Which keys have lately refused a run, so the next run does not ask a spent key first.
/// <para>
/// In memory on purpose. Credits arrive on each plan's own billing date, which nothing here knows,
/// so a refusal is only believed for a few hours and the key is then tried again: a spent key
/// answers that one request in under a second and costs nothing. A restart forgets every refusal,
/// which costs the same one request per key.
/// </para>
/// </summary>
public sealed class ApiCreditsState
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    public sealed record Refusal(DateTimeOffset At, string Why);

    private readonly ConcurrentDictionary<string, Refusal> _refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    public ApiCreditsState(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public void Refused(string name, string why) => _refused[name] = new Refusal(_now(), why);

    /// <summary>The refusal still believed for this key, or null when it may be asked.</summary>
    public Refusal? RefusalOf(string name) =>
        _refused.TryGetValue(name, out var r) && _now() - r.At < RetryAfter ? r : null;

    public void Clear(string name) => _refused.TryRemove(name, out _);
}
