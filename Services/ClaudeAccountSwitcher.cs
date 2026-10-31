using System.Text.Json;
using System.Text.Json.Nodes;
using Whetstone.Api;

namespace Whetstone.Services;

/// <summary>The last time the account in force changed, and why.</summary>
public sealed record AccountSwitchEvent(string From, string To, string Reason, DateTimeOffset At, bool Manual);

/// <summary>
/// Switches the Claude account the fleet spends - by hand from the header's picker, or by itself
/// when the smart switch is on (<see cref="ClaudeSmartSwitch"/> decides; this acts).
///
/// <para>
/// Either way a switch is one write of <c>claudeAccount</c> to the global config, read fresh by the
/// next run to start. Nothing running is stopped, signalled or moved: an agent already out finishes
/// on the account it began with, which is also why the smart switch can afford to be eager.
/// </para>
///
/// <para>
/// It remembers every account's last numbers, in <c>run/claude-accounts-usage.json</c>, because the
/// meter only ever reads the account in force. Without them the switch could leave an account but
/// never know when to go back. Accounts on the list that are not in force are peeked at, read-only,
/// every few minutes; when that is impossible (a login that aged out while nobody ran it) the reset
/// times from the last reading stand in, which is all "go back" needs.
/// </para>
/// </summary>
public class ClaudeAccountSwitcher
{
    /// <summary>How often an account not in force is asked for its numbers. Slow on purpose: nothing is spending it.</summary>
    private static readonly TimeSpan PeekEvery = TimeSpan.FromMinutes(5);

    private readonly ClaudeUsageService _usage;
    private readonly ProjectRegistry _registry;
    private readonly DashboardPaths _paths;
    private readonly ILogger<ClaudeAccountSwitcher> _log;
    private readonly QaIsolation _isolation;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _tick = new(1, 1);
    private Dictionary<string, AccountReading>? _readings;
    private readonly Dictionary<string, DateTimeOffset> _peekedAt = new(StringComparer.OrdinalIgnoreCase);
    private AccountSwitchEvent? _last;
    private bool _dirty;

    public ClaudeAccountSwitcher(
        ClaudeUsageService usage, ProjectRegistry registry, DashboardPaths paths,
        ILogger<ClaudeAccountSwitcher> log, QaIsolation? isolation = null)
    {
        _usage = usage;
        _registry = registry;
        _paths = paths;
        _log = log;
        _isolation = isolation ?? QaIsolation.Off;
    }

    private string StatePath => Path.Combine(_paths.RunDir, "claude-accounts-usage.json");

    public AccountSwitchEvent? LastSwitch { get { lock (_gate) { Load(); return _last; } } }

    public SmartSwitchSettings Settings => ClaudeSmartSwitch.Read(_registry.GetGlobalConfig());

    /// <summary>Every account's last known numbers, for the Settings panel to show what the switch sees.</summary>
    public IReadOnlyDictionary<string, AccountReading> Readings
    {
        get { lock (_gate) return new Dictionary<string, AccountReading>(Load(), StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// One pass: note the meter's latest numbers, peek at accounts that are due, and switch if the
    /// list says so. Called by <see cref="ClaudeUsageProbe"/> straight after each poll.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (_isolation.On) return;
        if (!await _tick.WaitAsync(0, ct)) return;
        try
        {
            Note(_usage.Current);

            var config = _registry.GetGlobalConfig();
            var settings = ClaudeSmartSwitch.Read(config);
            if (!settings.Enabled) return;

            var accounts = ClaudeAccounts.All(config);
            var active = ClaudeAccounts.Active(config);
            var now = DateTimeOffset.UtcNow;

            ReadPause(accounts, active);

            foreach (var rung in settings.Chain)
            {
                if (ClaudeAccounts.Find(accounts, rung.Account) is not { } account) continue;
                if (account.Name == active.Name || !ClaudeAccounts.HasLogin(account)) continue;
                if (_peekedAt.TryGetValue(account.Name, out var at) && now - at < PeekEvery) continue;
                _peekedAt[account.Name] = now;
                if (await _usage.PeekAsync(account, ct) is { } limits)
                    Record(account.Name, limits, DateTimeOffset.UtcNow);
            }

            Dictionary<string, AccountReading> readings;
            lock (_gate) readings = new(Load(), StringComparer.OrdinalIgnoreCase);
            var decision = ClaudeSmartSwitch.Decide(settings, accounts, ClaudeAccounts.HasLogin, readings, active.Name, now);
            if (decision.SwitchTo is not null)
                await SwitchAsync(decision.SwitchTo, manual: false, decision.Reason, ct);
        }
        finally
        {
            Save();
            _tick.Release();
        }
    }

    /// <summary>
    /// Points the next run at <paramref name="name"/>. A switch by hand while the smart switch is on
    /// turns the smart switch off - otherwise its next pass would quietly undo the person's choice.
    /// </summary>
    public async Task<(ClaudeAccount? Account, bool SmartSwitchTurnedOff)> SwitchAsync(
        string? name, bool manual, string? reason = null, CancellationToken ct = default)
    {
        var config = _registry.GetGlobalConfig();
        if (ClaudeAccounts.Find(ClaudeAccounts.All(config), name) is not { } account) return (null, false);
        var from = ClaudeAccounts.Active(config).Name;

        var stored = ConfigEndpoints.LoadObject(_paths.GlobalConfigPath);
        var before = ConfigEndpoints.Snapshot(stored);
        // The default is stored as an absence, so a config file that has never heard of accounts
        // and one that has been switched back are the same file.
        if (account.ConfigDir is null) stored.Remove("claudeAccount");
        else stored["claudeAccount"] = account.Name;

        var turnedOff = false;
        if (manual && stored[ClaudeSmartSwitch.ConfigKey] is JsonObject smart
            && smart["enabled"] is JsonValue on && on.TryGetValue<bool>(out var enabled) && enabled)
        {
            smart["enabled"] = false;
            turnedOff = true;
        }
        ConfigEndpoints.SaveObject(_paths.GlobalConfigPath, stored, before);

        // A usage-limit pause belongs to the account that hit the limit. Left behind, it would hold
        // Claude's phases idle until the old account's reset - the wait the switch was made to skip.
        // Claude's alone: the other providers' accounts did not change. usage-limit.json is the name
        // from before pauses were per provider, when it was always Claude's.
        foreach (var file in new[] { "usage-limit-claude.json", "usage-limit.json" })
        {
            try { File.Delete(Path.Combine(_paths.RunDir, file)); }
            catch (IOException) { /* the heartbeat is reading it; it re-checks on its next pass */ }
        }

        if (!from.Equals(account.Name, StringComparison.OrdinalIgnoreCase))
        {
            var why = reason ?? "switched by hand";
            lock (_gate) { Load(); _last = new AccountSwitchEvent(from, account.Name, why, DateTimeOffset.UtcNow, manual); _dirty = true; }
            _log.LogInformation("Claude account {From} -> {To}: {Reason}", from, account.Name, why);
        }

        await _usage.RefreshAsync(ct, force: true);
        Note(_usage.Current);
        Save();
        return (account, turnedOff);
    }

    /// <summary>The meter's numbers, kept under the account they were read for (never the one in force now).</summary>
    private void Note(UsageSnapshot snapshot)
    {
        if (snapshot.Status != "ok" || snapshot.Account is null || snapshot.FetchedAt is null) return;
        Record(snapshot.Account, snapshot.Limits, snapshot.FetchedAt.Value);
    }

    private void Record(string account, IReadOnlyList<UsageLimit> limits, DateTimeOffset at)
    {
        lock (_gate)
        {
            var all = Load();
            all.TryGetValue(account, out var was);
            if (was?.FetchedAt is { } prior && prior >= at) return;
            all[account] = new AccountReading(at, limits, was?.BlockedUntil);
            _dirty = true;
        }
    }

    /// <summary>
    /// A run that hit Claude's limit writes a pause naming the login it ran on. That account is full
    /// until the reset, whatever its last numbers said. A pause from the account the fleet has
    /// already left is that account's alone - it is cleared, so the heartbeat does not hold runs that
    /// would go to an account with room.
    /// </summary>
    private void ReadPause(IReadOnlyList<ClaudeAccount> accounts, ClaudeAccount active)
    {
        var path = Path.Combine(_paths.RunDir, "usage-limit-claude.json");
        if (!File.Exists(path)) path = Path.Combine(_paths.RunDir, "usage-limit.json");
        if (!File.Exists(path)) return;

        DateTimeOffset until;
        ClaudeAccount whose;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (!DateTimeOffset.TryParse(node?["resetsAt"]?.GetValue<string>(), out until)) return;
            // Written before pauses named their login: the account in force is the best guess.
            var dir = node?["configDir"] is JsonValue d && d.TryGetValue<string>(out var s) ? s : null;
            whose = dir is null
                ? active
                : accounts.FirstOrDefault(a => SameDir(a.ConfigDir, dir)) ?? active;
        }
        catch { return; }

        if (until <= DateTimeOffset.UtcNow) return;
        lock (_gate)
        {
            var all = Load();
            all.TryGetValue(whose.Name, out var was);
            if (was?.BlockedUntil is { } b && b >= until) { }
            else
            {
                all[whose.Name] = (was ?? new AccountReading(null, Array.Empty<UsageLimit>())) with { BlockedUntil = until };
                _dirty = true;
            }
        }

        if (whose.Name != active.Name)
        {
            try { File.Delete(path); }
            catch (IOException) { /* next pass */ }
        }
    }

    private static bool SameDir(string? configDir, string written) =>
        string.IsNullOrWhiteSpace(written)
            ? configDir is null
            : configDir is not null && string.Equals(
                Path.GetFullPath(configDir).TrimEnd('\\', '/'),
                Path.GetFullPath(written).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

    // ---- persistence: one small file, read once, written when something changed ----

    private sealed record Stored(Dictionary<string, AccountReading>? Accounts, AccountSwitchEvent? LastSwitch);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private Dictionary<string, AccountReading> Load()
    {
        if (_readings is not null) return _readings;
        _readings = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(StatePath)
                && JsonSerializer.Deserialize<Stored>(File.ReadAllText(StatePath), Json) is { } stored)
            {
                foreach (var (k, v) in stored.Accounts ?? new()) _readings[k] = v;
                _last = stored.LastSwitch;
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not read {Path}; starting empty", StatePath); }
        return _readings;
    }

    private void Save()
    {
        string text;
        lock (_gate)
        {
            if (!_dirty || _readings is null) return;
            text = JsonSerializer.Serialize(new Stored(_readings, _last), Json);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(_paths.RunDir);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not write {Path}", StatePath); }
    }
}
