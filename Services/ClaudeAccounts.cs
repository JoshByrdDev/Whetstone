using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>
/// One Claude subscription the fleet can spend. A null <see cref="ConfigDir"/> is the CLI's own
/// login, wherever the CLI keeps it; anything else is a config directory of that account's own.
/// </summary>
public sealed record ClaudeAccount(string Name, string? ConfigDir);

/// <summary>
/// Which Claude login every <c>claude</c> process the dashboard starts should run as.
///
/// <para>
/// An account is a Claude Code config directory, selected with <c>CLAUDE_CONFIG_DIR</c>. That is the
/// only form of "a second login" that keeps everything else working: the CLI signs in there once,
/// renews its own token there, and the rate-limit meter can read it. A <c>setup-token</c> credential
/// would be simpler to inject and is refused by the usage endpoint, so the meter would go blind on
/// exactly the account being switched to. Swapping the one credential file in place is worse again -
/// every running process writes its renewed token back to it, into whichever account is there now.
/// </para>
///
/// <para>
/// The setting is read in three places that have to agree, or the meter shows one account while the
/// fleet spends another: here, for the dashboard's own runs and the meter; <c>Get-ClaudeAccountDir</c>
/// in <c>data/heartbeat-common.ps1</c>, for the heartbeat's workers; and whatever starts a
/// <c>claude</c> by hand in <see cref="ClaudeUsageService"/>. All three read it fresh, so a switch
/// reaches the next run without a restart and leaves runs already out on the account they began with.
/// </para>
/// </summary>
public static class ClaudeAccounts
{
    /// <summary>The CLI's own login. Always offered, never stored, and what an unknown name falls back to.</summary>
    public const string DefaultName = "Default";

    /// <summary>
    /// Every account on offer, the default first. An entry without a name or a directory is skipped
    /// rather than guessed at: the heartbeat reads the same list and skips it too, and a guess made
    /// on one side only is how the two halves end up on different accounts.
    /// </summary>
    public static IReadOnlyList<ClaudeAccount> All(IReadOnlyDictionary<string, JsonElement> config)
    {
        var accounts = new List<ClaudeAccount> { new(DefaultName, null) };
        if (!config.TryGetValue("claudeAccounts", out var stored) || stored.ValueKind != JsonValueKind.Array)
            return accounts;

        foreach (var entry in stored.EnumerateArray())
        {
            var name = Text(entry, "name");
            var dir = Text(entry, "configDir");
            if (name is null || dir is null) continue;
            if (accounts.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            accounts.Add(new ClaudeAccount(name, dir));
        }
        return accounts;
    }

    /// <summary>The account in force. A name nothing on the list answers to is the default.</summary>
    public static ClaudeAccount Active(IReadOnlyDictionary<string, JsonElement> config)
    {
        var all = All(config);
        var chosen = config.TryGetValue("claudeAccount", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;
        return Find(all, chosen) ?? all[0];
    }

    public static ClaudeAccount? Find(IReadOnlyList<ClaudeAccount> all, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : all.FirstOrDefault(a => a.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where an account's login lives on disk. For the default that is wherever the CLI itself would
    /// look - a <c>CLAUDE_CONFIG_DIR</c> the user set for themselves included.
    /// </summary>
    public static string DirOf(ClaudeAccount account)
    {
        if (account.ConfigDir is not null) return account.ConfigDir;
        var own = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return !string.IsNullOrWhiteSpace(own)
            ? own
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    /// <summary>Whether the CLI has ever signed in there. Says nothing about whether the login still works.</summary>
    public static bool HasLogin(ClaudeAccount account) =>
        File.Exists(Path.Combine(DirOf(account), ".credentials.json"));

    /// <summary>
    /// The list as it should be stored, from whatever the Settings form sent. This is the one place
    /// a missing directory is filled in - beside the CLI's own, named for the account - so that what
    /// lands in the file is explicit and both readers see the same thing.
    /// </summary>
    public static JsonArray Normalize(JsonNode? incoming)
    {
        var kept = new JsonArray();
        if (incoming is not JsonArray entries) return kept;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultName };
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var name = (entry["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n.Trim() : "";
            if (name.Length == 0 || !seen.Add(name)) continue;
            var dir = (entry["configDir"] as JsonValue)?.TryGetValue<string>(out var d) == true ? d.Trim() : "";
            kept.Add(new JsonObject
            {
                ["name"] = name,
                ["configDir"] = dir.Length > 0 ? dir : DefaultDirFor(name),
            });
        }
        return kept;
    }

    /// <summary><c>~/.claude-&lt;name&gt;</c>, with the name reduced to what is safe in a folder name.</summary>
    public static string DefaultDirFor(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "account";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".claude-{slug}");
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim()
                : null;
}
