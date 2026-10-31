using System.Text.Json;
using System.Text.Json.Nodes;

namespace Whetstone.Services;

/// <summary>One step of the smart switch's list: an account, and how full it may get before the next takes over.</summary>
public sealed record SmartSwitchRung(string Account, double Weekly, double Session);

/// <summary>The smart switch as configured: whether it is on, and the accounts in the order they are preferred.</summary>
public sealed record SmartSwitchSettings(bool Enabled, IReadOnlyList<SmartSwitchRung> Chain)
{
    public static readonly SmartSwitchSettings Off = new(false, Array.Empty<SmartSwitchRung>());
}

/// <summary>
/// What is known of one account's windows: the last numbers read for it, and - separately - a limit
/// a run actually hit there, which outranks any number until its reset.
/// </summary>
public sealed record AccountReading(
    DateTimeOffset? FetchedAt,
    IReadOnlyList<UsageLimit> Limits,
    DateTimeOffset? BlockedUntil = null);

/// <summary>Where the fleet should be. <see cref="SwitchTo"/> is null when it should stay where it is.</summary>
public sealed record SmartSwitchDecision(string? SwitchTo, string Reason);

/// <summary>
/// Which Claude account the fleet should be spending, given how full each one is.
///
/// <para>
/// The rule is one sentence: the first account on the list that is under both of its limits. That
/// one rule covers moving on (the current account crossed a limit) and coming back (an earlier one's
/// window reset), and it cannot flap - an account the fleet has left only fills further, through the
/// owner's own sessions, until its window resets and it is genuinely available again.
/// </para>
///
/// <para>
/// A window whose reset has passed counts as empty whatever the last reading said. That is what
/// makes the switch work without a fresh reading of an account nobody is running: its login may
/// have aged out, but the time its window rolls was known when it was left.
/// </para>
///
/// <para>
/// It is never a cut. A switch is the same act as the header's picker - it names the account the
/// NEXT run starts on - so a run already out finishes on the account it began with.
/// </para>
/// </summary>
public static class ClaudeSmartSwitch
{
    public const string ConfigKey = "claudeSmartSwitch";
    public const double DefaultWeekly = 95;
    public const double DefaultSession = 90;

    public static SmartSwitchSettings Read(IReadOnlyDictionary<string, JsonElement> config)
    {
        if (!config.TryGetValue(ConfigKey, out var v) || v.ValueKind != JsonValueKind.Object)
            return SmartSwitchSettings.Off;
        var enabled = v.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        var chain = new List<SmartSwitchRung>();
        if (v.TryGetProperty("chain", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in c.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var name = r.TryGetProperty("account", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(name)) continue;
                if (chain.Any(x => x.Account.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                chain.Add(new SmartSwitchRung(name, Percent(r, "weekly", DefaultWeekly), Percent(r, "session", DefaultSession)));
            }
        }
        return new SmartSwitchSettings(enabled, chain);
    }

    /// <summary>The setting as it should be stored, from whatever the Settings form sent.</summary>
    public static JsonObject Normalize(JsonNode? incoming)
    {
        var enabled = incoming?["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var on) && on;
        var chain = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (incoming?["chain"] is JsonArray rungs)
        {
            foreach (var r in rungs.OfType<JsonObject>())
            {
                var name = r["account"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n.Trim() : "";
                if (name.Length == 0 || !seen.Add(name)) continue;
                chain.Add(new JsonObject
                {
                    ["account"] = name,
                    ["weekly"] = Clamp(r["weekly"], DefaultWeekly),
                    ["session"] = Clamp(r["session"], DefaultSession),
                });
            }
        }
        return new JsonObject { ["enabled"] = enabled, ["chain"] = chain };
    }

    /// <summary>
    /// How full an account is now: the fullest session window and the fullest weekly one (scoped
    /// weekly windows included - a model's own weekly cap stops that model's runs just the same).
    /// A window past its reset counts as empty.
    /// </summary>
    public static (double Session, double Weekly) Load(AccountReading? reading, DateTimeOffset now)
    {
        double session = 0, weekly = 0;
        if (reading is null) return (0, 0);
        foreach (var l in reading.Limits)
        {
            if (l.ResetsAt is { } reset && reset <= now) continue;
            if (l.Group == "session") session = Math.Max(session, l.Percent);
            else weekly = Math.Max(weekly, l.Percent);
        }
        return (session, weekly);
    }

    /// <summary>Why this account should not take new runs right now, or null when it can.</summary>
    public static string? Over(SmartSwitchRung rung, AccountReading? reading, DateTimeOffset now)
    {
        if (reading?.BlockedUntil is { } until && until > now)
            return $"hit its usage limit (resets {until.ToLocalTime():MMM d, h:mm tt})";
        var (session, weekly) = Load(reading, now);
        if (session >= rung.Session) return $"session {Math.Round(session)}% >= {rung.Session}%";
        if (weekly >= rung.Weekly) return $"weekly {Math.Round(weekly)}% >= {rung.Weekly}%";
        return null;
    }

    /// <summary>
    /// The account the fleet should be on. Accounts the list names that do not exist, or have never
    /// been signed in to, are passed over. An account with no reading at all is taken at its word:
    /// a run that hits its limit there writes a pause, which marks it full, and the next pass moves on.
    /// When every account is at its limit the fleet stays where it is - moving would gain nothing,
    /// and the heartbeat's own usage-limit pause is already holding the runs that would fail.
    /// </summary>
    public static SmartSwitchDecision Decide(
        SmartSwitchSettings settings,
        IReadOnlyList<ClaudeAccount> accounts,
        Func<ClaudeAccount, bool> hasLogin,
        IReadOnlyDictionary<string, AccountReading> readings,
        string current,
        DateTimeOffset now)
    {
        if (!settings.Enabled || settings.Chain.Count == 0) return new(null, "Smart switch is off.");

        string? currentWhy = null;
        foreach (var rung in settings.Chain)
        {
            if (ClaudeAccounts.Find(accounts, rung.Account) is not { } account) continue;
            if (!hasLogin(account)) continue;
            readings.TryGetValue(account.Name, out var reading);
            var why = Over(rung, reading, now);
            var isCurrent = account.Name.Equals(current, StringComparison.OrdinalIgnoreCase);
            if (why is not null)
            {
                if (isCurrent) currentWhy = why;
                continue;
            }
            if (isCurrent) return new(null, $"{account.Name} is under its limits.");
            return new(account.Name, currentWhy is not null
                ? $"{current} {currentWhy}"
                : $"{account.Name} is the first account on the list under its limits");
        }
        return new(null, "Every account on the list is at its limit; staying put.");
    }

    private static double Percent(JsonElement obj, string name, double fallback) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? Math.Clamp(d, 1, 100) : fallback;

    private static double Clamp(JsonNode? node, double fallback)
    {
        try { return node is JsonValue v ? Math.Clamp(v.GetValue<double>(), 1, 100) : fallback; }
        catch { return fallback; }
    }
}
