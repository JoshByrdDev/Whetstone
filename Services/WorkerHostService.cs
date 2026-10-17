using System.Text.Json;

namespace Whetstone.Services;

/// <summary>
/// One worker host as the board host's global config describes it (<c>workerHosts</c>). A worker
/// host is another machine that runs workers for this board and hands their branches back; the one
/// orchestrator stays here. See docs/worker-hosts-plan.md.
/// </summary>
/// <param name="FirstSlot">
/// The agent slot its first berth is numbered. Board host workers are 1..3 and the steward is 4, so
/// worker hosts are numbered from 5 in blocks, in the order the config lists them.
/// </param>
/// <param name="QaLeases">
/// How many copies of one project's stack this host may run at once - its own capacity for a
/// resource like <c>alpha:app-stack@bravo-host</c>. Applies only to a project that declares
/// <c>parallelStacks</c>; any other gets one, whatever this says. Phase 2.
/// </param>
public record WorkerHostConfig(string Name, bool Enabled, int Slots, string[] Skills, string[] Projects, int FirstSlot, int QaLeases = 1)
{
    /// <summary>The first slot past the board host's workers and the steward.</summary>
    public const int FirstRemoteSlot = HeartbeatManager.MaxWorkers + 2;

    /// <summary>Most berths one worker host may have. Its RAM is the real limit; this stops a typo.</summary>
    public const int MaxSlots = 8;

    /// <summary>
    /// What phase 1 lets a worker host run when its entry names nothing: the phases that take no
    /// stack lease. QA there needs per-host stack leases, which do not exist yet.
    /// </summary>
    public static readonly string[] DefaultSkills = { "speculate", "implement", "refine" };

    /// <summary>
    /// Every entry in <c>workerHosts</c>, slots numbered. An entry with no name, or one repeating a
    /// name already read, is dropped rather than failing the board: a hand-edited config must not be
    /// able to stop the fleet.
    /// </summary>
    public static IReadOnlyList<WorkerHostConfig> Read(IReadOnlyDictionary<string, JsonElement> global)
    {
        var result = new List<WorkerHostConfig>();
        if (!global.TryGetValue("workerHosts", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;

        var next = FirstRemoteSlot;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            if (name.Length == 0 || !IsValidName(name)) continue;
            if (result.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

            var enabled = !e.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
            var slots = e.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 1;
            slots = Math.Clamp(slots, 1, MaxSlots);
            var skills = Strings(e, "skills");
            var projects = Strings(e, "projects");
            var qa = e.TryGetProperty("qaLeases", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : 1;

            result.Add(new WorkerHostConfig(name, enabled, slots, skills.Length > 0 ? skills : DefaultSkills, projects, next,
                Math.Clamp(qa, 1, MaxSlots)));
            next += slots;
        }
        return result;
    }

    /// <summary>Names travel in URLs and log lines: letters, digits, dash, underscore, dot.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public bool OwnsSlot(int slot) => slot >= FirstSlot && slot < FirstSlot + Slots;

    /// <summary>
    /// The switch for all remote work, <c>workerHostsEnabled</c>. Off, no worker host is given anything
    /// new, whatever its own entry says; runs already out finish. Absent means on.
    /// </summary>
    public static bool RemoteEnabled(IReadOnlyDictionary<string, JsonElement> global) =>
        !(global.TryGetValue(WorkerHostSettings.EnabledKey, out var v) && v.ValueKind == JsonValueKind.False);

    /// <summary>
    /// A resource as a worker host holds it: its own, beside the board host's of the same name.
    /// A worker host's docker stack is a different stack from the board host's, so a QA run there
    /// must neither wait for the board host's nor stop it being taken.
    /// </summary>
    public static string HostResource(string resourceKey, string hostName) => $"{resourceKey}@{hostName}";

    /// <summary>The resource and the host from a host-qualified key, or null for the board host's own.</summary>
    public static (string Resource, string Host)? SplitHostResource(string key)
    {
        var at = key.LastIndexOf('@');
        if (at <= 0 || at == key.Length - 1) return null;
        return (key[..at], key[(at + 1)..]);
    }

    private static string[] Strings(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Distinct().ToArray()
            : Array.Empty<string>();
}

/// <summary>One run a worker host's runner says it is holding.</summary>
/// <param name="RunId">The ticket lease's id the claim carried - see <c>TicketService.ClaimResult</c>.</param>
public record HostRun(int Slot, int TicketId, int RunId, string? Project, int? Pid, bool Exited, int? ExitCode, DateTime? StartedAt);

/// <summary>
/// Something the orchestrator wants a runner to do. <c>start</c> carries the claim exactly as
/// claim-next returned it, which the runner rewrites to its own paths; <c>kill</c> names a run.
/// </summary>
public record HostOrder(string Kind, int Slot, int RunId, JsonElement? Claim);

/// <summary>A worker host as the orchestrator and the fleet view see it.</summary>
public record WorkerHostView(
    string Name, bool Enabled, int Slots, int FirstSlot, string[] Skills, string[] Projects,
    bool Online, DateTime? LastCheckin, string? Commit, bool CommitMatches,
    IReadOnlyList<HostRun> Runs, int PendingOrders, DateTime? UsageLimitUntil,
    int QaLeases = 1, bool RemoteOn = true)
{
    /// <summary>
    /// Whether the orchestrator may give it new work: remote work switched on, this host switched on,
    /// answering, and on this board's commit.
    /// </summary>
    public bool Dispatchable => RemoteOn && Enabled && Online && CommitMatches && UsageLimitUntil is null;
}

/// <summary>
/// The worker hosts' state: who checked in when, what each is running, and the orders waiting for
/// it. In memory, like <see cref="EventStore"/>, and for the same reason it can be: every runner
/// reports everything it holds on every check-in, so an API restart is repaired by the next one.
/// <para>
/// The runner pulls; nothing listens on a worker host. A check-in waits a little for an order when
/// there is none, so an order reaches its runner within a second or two of being placed rather than
/// on the next poll.
/// </para>
/// </summary>
public class WorkerHostService
{
    /// <summary>A host that has not checked in for this long is offline.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);

    /// <summary>Longest a check-in is held open waiting for an order.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);

    private sealed class State
    {
        public DateTime? LastCheckin;
        public string? Commit;
        public List<HostRun> Runs = new();
        public readonly Queue<HostOrder> Orders = new();
        public DateTime? UsageLimitUntil;
        public string? DataRoot;
        public TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, State> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly Func<IReadOnlyList<WorkerHostConfig>> _config;
    private readonly Func<string?> _boardCommit;
    private readonly Func<DateTime> _now;
    private readonly Func<bool> _remoteOn;

    /// <summary>
    /// Machines that checked in under a name the config does not have: a runner set up and started
    /// before its owner added it here. Shown on the Heartbeat page with an Add button, and given
    /// nothing - a check-in from an unknown name is still answered 404.
    /// </summary>
    private readonly Dictionary<string, (DateTime At, string? Commit)> _seen = new(StringComparer.OrdinalIgnoreCase);

    public WorkerHostService(ProjectRegistry registry, BoardCommit commit)
        : this(() => WorkerHostConfig.Read(registry.GetGlobalConfig()), commit.Get, () => DateTime.UtcNow,
               () => WorkerHostConfig.RemoteEnabled(registry.GetGlobalConfig())) { }

    public WorkerHostService(Func<IReadOnlyList<WorkerHostConfig>> config, Func<string?> boardCommit, Func<DateTime> now,
        Func<bool>? remoteOn = null)
        => (_config, _boardCommit, _now, _remoteOn) = (config, boardCommit, now, remoteOn ?? (() => true));

    /// <summary>Whether remote work is switched on at all.</summary>
    public bool RemoteOn => _remoteOn();

    /// <summary>A machine that checked in unconfigured, as the Heartbeat page lists it.</summary>
    public record SeenMachine(string Name, DateTime LastSeen, string? Commit);

    /// <summary>Unconfigured machines heard from in the last day, newest first.</summary>
    public IReadOnlyList<SeenMachine> Seen()
    {
        var configured = _config().Select(h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var since = _now() - TimeSpan.FromDays(1);
        lock (_lock)
            return _seen.Where(kv => !configured.Contains(kv.Key) && kv.Value.At >= since)
                .OrderByDescending(kv => kv.Value.At)
                .Select(kv => new SeenMachine(kv.Key, kv.Value.At, kv.Value.Commit)).ToList();
    }

    public WorkerHostConfig? Find(string name) =>
        _config().FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private State StateOf(string name)
    {
        if (!_hosts.TryGetValue(name, out var s)) _hosts[name] = s = new State();
        return s;
    }

    /// <summary>
    /// Record what a runner reports, and hand it whatever is waiting for it - waiting up to
    /// <paramref name="wait"/> for something to arrive when nothing is. Returns null for a host the
    /// config does not name, which the route answers with 404 so a misconfigured runner says so.
    /// </summary>
    public async Task<IReadOnlyList<HostOrder>?> CheckInAsync(
        string name, string? commit, IReadOnlyList<HostRun> runs, DateTime? usageLimitUntil, TimeSpan wait,
        CancellationToken ct = default, string? dataRoot = null)
    {
        if (Find(name) is null)
        {
            if (WorkerHostConfig.IsValidName(name))
                lock (_lock) _seen[name] = (_now(), string.IsNullOrWhiteSpace(commit) ? null : commit.Trim());
            return null;
        }
        if (wait > MaxWait) wait = MaxWait;

        Task signal;
        lock (_lock)
        {
            var s = StateOf(name);
            s.LastCheckin = _now();
            s.Commit = string.IsNullOrWhiteSpace(commit) ? null : commit.Trim();
            s.Runs = runs.ToList();
            s.UsageLimitUntil = usageLimitUntil is { } u && u > _now() ? u : null;
            if (!string.IsNullOrWhiteSpace(dataRoot)) s.DataRoot = dataRoot.Trim();
            if (s.Orders.Count > 0 || wait <= TimeSpan.Zero) return Drain(s);
            if (s.Signal.Task.IsCompleted) s.Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = s.Signal.Task;
        }

        try { await signal.WaitAsync(wait, ct); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { return Array.Empty<HostOrder>(); }

        lock (_lock) return Drain(StateOf(name));
    }

    private static IReadOnlyList<HostOrder> Drain(State s)
    {
        var list = s.Orders.ToList();
        s.Orders.Clear();
        return list;
    }

    /// <summary>Queue an order for a host. False for a host the config does not name.</summary>
    public bool Enqueue(string name, HostOrder order)
    {
        if (Find(name) is null) return false;
        lock (_lock)
        {
            var s = StateOf(name);
            s.Orders.Enqueue(order);
            s.Signal.TrySetResult();
        }
        return true;
    }

    /// <summary>
    /// Order every run every host is holding killed - the dashboard's hard Stop, which means both
    /// machines. Returns how many kill orders were queued. A host that is offline gets its orders
    /// on its next check-in, and its runner also kills anything whose lease has gone by then.
    /// </summary>
    public int KillAll()
    {
        var n = 0;
        lock (_lock)
        {
            foreach (var (_, s) in _hosts)
            {
                foreach (var r in s.Runs.Where(r => !r.Exited))
                {
                    s.Orders.Enqueue(new HostOrder("kill", r.Slot, r.RunId, null));
                    n++;
                }
                s.Signal.TrySetResult();
            }
        }
        return n;
    }

    /// <summary>
    /// Every data root a worker host has reported. A path under one of them is that machine's, and
    /// <see cref="HostDataMirror.ToBoardCapture"/> is how a capture named there is found here.
    /// </summary>
    public IReadOnlyList<string> HostDataRoots()
    {
        lock (_lock) return _hosts.Values.Select(s => s.DataRoot).Where(r => !string.IsNullOrEmpty(r)).Select(r => r!).ToList();
    }

    /// <summary>Every configured host, as the orchestrator and the fleet view need it.</summary>
    public IReadOnlyList<WorkerHostView> Snapshot()
    {
        var board = _boardCommit();
        var now = _now();
        var remoteOn = _remoteOn();
        var result = new List<WorkerHostView>();
        lock (_lock)
        {
            foreach (var h in _config())
            {
                _hosts.TryGetValue(h.Name, out var s);
                var online = s?.LastCheckin is { } t && now - t < OfflineAfter;
                var matches = s?.Commit is { } c && board is not null && c.Equals(board, StringComparison.OrdinalIgnoreCase);
                var pause = s?.UsageLimitUntil is { } u && u > now ? u : (DateTime?)null;
                result.Add(new WorkerHostView(h.Name, h.Enabled, h.Slots, h.FirstSlot, h.Skills, h.Projects,
                    online, s?.LastCheckin, s?.Commit, matches,
                    s?.Runs.ToList() ?? new List<HostRun>(), s?.Orders.Count ?? 0, pause, h.QaLeases, remoteOn));
            }
        }
        return result;
    }
}

/// <summary>
/// The commit the board host's own checkout is on - what a worker host's Whetstone checkout has to
/// match before it is given work, because it runs its own copies of worker.ps1, the prompt and the
/// guard. Read from git in the checkout above the data root, and cached for a minute.
/// </summary>
public class BoardCommit
{
    private readonly string? _repo;
    private string? _cached;
    private DateTime _readAt = DateTime.MinValue;
    private readonly object _lock = new();

    public BoardCommit(DashboardPaths paths) => _repo = Path.GetDirectoryName(Path.GetFullPath(paths.DataRoot).TrimEnd('\\', '/'));

    public string? Get()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _readAt < TimeSpan.FromMinutes(1)) return _cached;
            _readAt = DateTime.UtcNow;
            _cached = Read(_repo);
            return _cached;
        }
    }

    private static string? Read(string? repo)
    {
        if (string.IsNullOrEmpty(repo) || !Directory.Exists(repo)) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("HEAD");
            using var p = System.Diagnostics.Process.Start(psi)!;
            var sha = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 && sha.Length >= 40 ? sha : null;
        }
        catch { return null; }
    }
}
