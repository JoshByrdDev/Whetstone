using System.Collections.Concurrent;
using System.Diagnostics;
using Whetstone.Models;
using Whetstone.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Services;

/// <summary>What the dashboard believes a project's stack is doing right now.</summary>
public enum StackState
{
    /// <summary>No <c>stack</c> block in this project's config - nothing to show.</summary>
    NoStack,
    /// <summary>Nothing answering the health URL.</summary>
    Stopped,
    /// <summary>An up command is running, or has run and the URL has not answered yet.</summary>
    Starting,
    /// <summary>The health URL answered.</summary>
    Running,
    /// <summary>A down command is running.</summary>
    Stopping,
    /// <summary>The last start or stop failed. The output tail says why.</summary>
    Error,
}

/// <summary>Who holds the stack lease: a person at the dashboard, or an agent mid-run.</summary>
public record StackHolder(string Kind, int TicketId, string? TicketKey, string? Skill, DateTime Since);

/// <summary>Everything the board's stack chip needs for one project, in one read.</summary>
public record StackSnapshot(
    string Key,
    string DisplayName,
    bool Configured,
    string State,
    DateTime? ProbedAt,
    StackHolder? Holder,
    bool CanStart,
    bool CanStop,
    string? Blocked,
    IReadOnlyList<StackLink> Open,
    string? LastError,
    IReadOnlyList<string> Output,
    int ActionSeconds,
    int WaitingQa);

/// <summary>
/// Starts, stops and watches each project's local stack, and makes the person doing it a
/// first-class holder of the same lease the QA phase takes.
///
/// <para>
/// The lease is the reason this is a service and not a button. A project's qa-test skill can rebuild the
/// app tier itself, on fixed ports, from its own worktree, and <c>phaseResources</c> already declares
/// that as <c>myproject:app-stack</c> so two agents can never both have it. A start button wired
/// straight to <c>docker compose up</c> would reintroduce exactly that collision with a human on one
/// side of it - and it would fail the way those failures always look, as a flaky QA run half an hour
/// later. So Start takes the lease, Stop gives it back, and while an agent holds it the human waits,
/// the same as any other contender.
/// </para>
///
/// <para>
/// What the lease does NOT cover is the infrastructure the app tier sits on - the database above all.
/// Every phase queries it and none of them lease it, because leasing a thing every phase reads would
/// serialise the pipeline behind QA for no gain. That is why the configured <c>down</c> command names
/// services rather than taking the whole compose project down: Stop is meant to release what this
/// control holds, and a project-wide teardown would also cut three other agents off from a database
/// nobody was contending for.
/// </para>
///
/// <para>
/// Running is decided by polling the health URL, not by asking docker. Agents run their compose from
/// a per-ticket worktree, so the container names differ from the ones the hub repo would produce and
/// a container-level check would be blind to half the cases. It also answers a stricter question:
/// containers existing is not the same as ingress serving, and "is it actually up" is the only
/// question the control is asked.
/// </para>
/// </summary>
public class ProjectStackService
{
    /// <summary>
    /// How long a human's hold survives without the dashboard renewing it. Short on purpose: the
    /// probe pushes it out every few seconds for as long as the stack answers, so the only thing a
    /// short TTL shortens is how long a dead dashboard can block the queue.
    /// </summary>
    private static readonly TimeSpan HumanTtl = TimeSpan.FromMinutes(5);

    /// <summary>Label written on human leases - <see cref="ResourceLease.HolderLabel"/> being set is what marks one.</summary>
    public const string HumanHolder = "dashboard";

    private readonly ProjectRegistry _registry;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ProjectStackService> _log;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Runtime> _runtimes = new();

    public ProjectStackService(ProjectRegistry registry, IServiceScopeFactory scopes, ILogger<ProjectStackService> log)
    {
        _registry = registry;
        _scopes = scopes;
        _log = log;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(4),
        };
    }

    /// <summary>Per-project mutable state. Lives in memory: it describes this process's view, not a fact worth keeping.</summary>
    private sealed class Runtime
    {
        public readonly object Sync = new();
        /// <summary>"start", "stop", or empty when nothing is in flight. Also the double-click guard.</summary>
        public string Action = "";
        public DateTime ActionStartedAt;
        /// <summary>Last probe result; null until the first one lands.</summary>
        public bool? Healthy;
        public DateTime? ProbedAt;
        public string? LastError;
        public List<string> Output = new();
    }

    private Runtime RuntimeFor(string key) => _runtimes.GetOrAdd(key, _ => new Runtime());

    /// <summary>Projects that declare a stack - what the board renders a chip for, and what the prober walks.</summary>
    public IReadOnlyList<string> StackProjects() =>
        _registry.Projects.Where(k => _registry.GetProjectConfig(k).Stack.IsConfigured).ToList();

    // ---- read ---------------------------------------------------------------

    public async Task<StackSnapshot> SnapshotAsync(string key)
    {
        var cfg = _registry.GetProjectConfig(key);
        var display = string.IsNullOrWhiteSpace(cfg.DisplayName) ? key : cfg.DisplayName;

        if (!cfg.Stack.IsConfigured)
            return new StackSnapshot(key, display, false, nameof(StackState.NoStack), null, null,
                false, false, null, Array.Empty<StackLink>(), null, Array.Empty<string>(), 0, 0);

        var rt = RuntimeFor(key);
        string action;
        bool? healthy;
        DateTime? probedAt;
        string? lastError;
        string[] output;
        int actionSeconds;
        lock (rt.Sync)
        {
            action = rt.Action;
            healthy = rt.Healthy;
            probedAt = rt.ProbedAt;
            lastError = rt.LastError;
            output = rt.Output.ToArray();
            actionSeconds = action.Length == 0 ? 0 : (int)(DateTime.UtcNow - rt.ActionStartedAt).TotalSeconds;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var leases = scope.ServiceProvider.GetRequiredService<ResourceLeaseService>();

        var lease = await leases.HolderOfAsync(cfg.StackResourceKey);
        StackHolder? holder = null;
        if (lease is not null)
        {
            if (lease.HolderLabel is not null)
            {
                holder = new StackHolder("you", 0, null, null, lease.AcquiredAt);
            }
            else
            {
                var t = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == lease.TicketId);
                holder = new StackHolder("agent", lease.TicketId, t?.IssueKey ?? $"#{lease.TicketId}", lease.Skill, lease.AcquiredAt);
            }
        }

        // Only what is queued behind the stack, not what is already using it: a QaTesting ticket has
        // an agent on it, so counting it would tell a human holding the lease to hurry up on behalf
        // of the very run they cannot be blocking.
        var waiting = await db.Tickets.CountAsync(t => t.ProjectKey == key && t.Phase == TicketPhase.NeedsQa);

        var state = action switch
        {
            "start" => StackState.Starting,
            "stop" => StackState.Stopping,
            _ when lastError is not null => StackState.Error,
            _ when healthy == true => StackState.Running,
            _ => StackState.Stopped,
        };

        var agentHolds = holder?.Kind == "agent";
        var idle = action.Length == 0;
        var blocked = agentHolds
            ? $"{holder!.TicketKey} is using the stack ({holder.Skill ?? "an agent"})"
            : null;

        return new StackSnapshot(
            key, display, true, state.ToString(), probedAt, holder,
            CanStart: idle && !agentHolds && state != StackState.Running,
            CanStop: idle && !agentHolds && !string.IsNullOrWhiteSpace(cfg.Stack.Down) && state == StackState.Running,
            Blocked: blocked,
            Open: cfg.Stack.Open,
            LastError: lastError,
            Output: output,
            ActionSeconds: actionSeconds,
            WaitingQa: waiting);
    }

    public async Task<List<StackSnapshot>> SnapshotAllAsync()
    {
        var list = new List<StackSnapshot>();
        foreach (var key in StackProjects()) list.Add(await SnapshotAsync(key));
        return list;
    }

    // ---- start / stop -------------------------------------------------------

    /// <summary>Outcome of a start or stop request. <paramref name="Conflict"/> means someone else holds the stack.</summary>
    public record ActionResult(bool Accepted, string? Error = null, bool Conflict = false);

    public async Task<ActionResult> StartAsync(string key)
    {
        var cfg = _registry.GetProjectConfig(key);
        if (!cfg.Stack.IsConfigured) return new ActionResult(false, "This project has no stack configured.");

        var workDir = cfg.Stack.ResolvedWorkingDir(cfg.ProjectPath);
        if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
            return new ActionResult(false, $"Working directory not found: {workDir}");

        var rt = RuntimeFor(key);
        lock (rt.Sync)
        {
            if (rt.Action.Length > 0) return new ActionResult(false, $"Already {rt.Action}ing.");
            rt.Action = "start";
            rt.ActionStartedAt = DateTime.UtcNow;
            rt.LastError = null;
            rt.Output = new List<string>();
        }

        // Take the lease BEFORE running anything. Losing this race has to cost nothing, which it
        // only does if the command has not started yet.
        using (var scope = _scopes.CreateScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<ResourceLeaseService>();
            var taken = await leases.TryAcquireAllAsync(
                new[] { cfg.StackResourceKey }, ticketId: 0, skill: "manual", HumanTtl, holderLabel: HumanHolder);
            if (taken is null)
            {
                var holder = await leases.HolderOfAsync(cfg.StackResourceKey);
                ClearAction(rt);
                return new ActionResult(false,
                    holder is null
                        ? "The stack lease is busy."
                        : $"Ticket #{holder.TicketId} is using the stack ({holder.Skill ?? "an agent"}). Wait for it to finish.",
                    Conflict: true);
            }
        }

        _ = Task.Run(() => RunStartAsync(key, cfg, workDir, rt));
        return new ActionResult(true);
    }

    public async Task<ActionResult> StopAsync(string key)
    {
        var cfg = _registry.GetProjectConfig(key);
        if (!cfg.Stack.IsConfigured) return new ActionResult(false, "This project has no stack configured.");
        if (string.IsNullOrWhiteSpace(cfg.Stack.Down)) return new ActionResult(false, "This project has no stop command configured.");

        var workDir = cfg.Stack.ResolvedWorkingDir(cfg.ProjectPath);
        if (string.IsNullOrEmpty(workDir) || !Directory.Exists(workDir))
            return new ActionResult(false, $"Working directory not found: {workDir}");

        var rt = RuntimeFor(key);
        lock (rt.Sync)
        {
            if (rt.Action.Length > 0) return new ActionResult(false, $"Already {rt.Action}ing.");
            rt.Action = "stop";
            rt.ActionStartedAt = DateTime.UtcNow;
            rt.LastError = null;
            rt.Output = new List<string>();
        }

        using (var scope = _scopes.CreateScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<ResourceLeaseService>();
            var holder = await leases.HolderOfAsync(cfg.StackResourceKey);
            if (holder is not null && holder.HolderLabel is null)
            {
                // An agent is mid-run against this stack. Taking it down under them is the one thing
                // the lease exists to prevent, and a human doing it by hand is not a special case.
                ClearAction(rt);
                return new ActionResult(false,
                    $"Ticket #{holder.TicketId} is using the stack ({holder.Skill ?? "an agent"}). Wait for it to finish.",
                    Conflict: true);
            }
            if (holder is null)
            {
                // Running but unclaimed - left behind by a finished agent, or started outside the
                // dashboard. Take the lease for the length of the stop so nothing claims it halfway.
                var taken = await leases.TryAcquireAllAsync(
                    new[] { cfg.StackResourceKey }, ticketId: 0, skill: "manual", HumanTtl, holderLabel: HumanHolder);
                if (taken is null)
                {
                    ClearAction(rt);
                    return new ActionResult(false, "Something else claimed the stack just now. Try again.", Conflict: true);
                }
            }
        }

        _ = Task.Run(() => RunStopAsync(key, cfg, workDir, rt));
        return new ActionResult(true);
    }

    private async Task RunStartAsync(string key, ProjectConfig cfg, string workDir, Runtime rt)
    {
        try
        {
            var deadline = TimeSpan.FromSeconds(cfg.Stack.StartupSeconds);
            var run = await RunCommandAsync(cfg.Stack.Up, workDir, deadline, line => Append(rt, line));

            if (!run.Ok)
            {
                await FailStartAsync(key, cfg, rt, run.Error ?? $"'{cfg.Stack.Up}' exited with code {run.ExitCode}.");
                return;
            }

            // The command returning is not the stack serving. Poll until the URL answers, because
            // that is what "started" was defined as everywhere else in here.
            var until = DateTime.UtcNow.Add(deadline);
            while (DateTime.UtcNow < until)
            {
                if (await ProbeAsync(cfg.Stack.HealthUrl))
                {
                    lock (rt.Sync)
                    {
                        rt.Healthy = true;
                        rt.ProbedAt = DateTime.UtcNow;
                        rt.Action = "";
                    }
                    Append(rt, $"[dashboard] {cfg.Stack.HealthUrl} is answering.");
                    return;
                }
                await Task.Delay(2000);
            }

            await FailStartAsync(key, cfg, rt,
                $"Command finished but {cfg.Stack.HealthUrl} never answered within {cfg.Stack.StartupSeconds}s.");
        }
        catch (Exception ex)
        {
            await FailStartAsync(key, cfg, rt, ex.Message);
        }
    }

    /// <summary>
    /// A start that did not end with a serving stack gives the lease back. Holding it would park
    /// every QA ticket behind a stack that is not there.
    /// </summary>
    private async Task FailStartAsync(string key, ProjectConfig cfg, Runtime rt, string error)
    {
        _log.LogWarning("Stack start failed for {Project}: {Error}", key, error);
        Append(rt, $"[dashboard] {error}");
        await ReleaseHumanAsync(cfg, "start failed - the stack is not running");
        lock (rt.Sync)
        {
            rt.LastError = error;
            rt.Healthy = false;
            rt.ProbedAt = DateTime.UtcNow;
            rt.Action = "";
        }
    }

    private async Task RunStopAsync(string key, ProjectConfig cfg, string workDir, Runtime rt)
    {
        try
        {
            var run = await RunCommandAsync(cfg.Stack.Down, workDir, TimeSpan.FromMinutes(5), line => Append(rt, line));
            var healthy = await ProbeAsync(cfg.Stack.HealthUrl);

            // Release even when the command reported failure, as long as nothing is answering: the
            // truth on the wire outranks the exit code, and a stack that is down but leased is the
            // worst of both.
            if (!healthy) await ReleaseHumanAsync(cfg, "stopped from the dashboard");

            lock (rt.Sync)
            {
                rt.Healthy = healthy;
                rt.ProbedAt = DateTime.UtcNow;
                rt.LastError = run.Ok
                    ? (healthy ? $"'{cfg.Stack.Down}' ran but {cfg.Stack.HealthUrl} is still answering." : null)
                    : run.Error ?? $"'{cfg.Stack.Down}' exited with code {run.ExitCode}.";
                rt.Action = "";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stack stop failed for {Project}", key);
            Append(rt, $"[dashboard] {ex.Message}");
            lock (rt.Sync)
            {
                rt.LastError = ex.Message;
                rt.Action = "";
            }
        }
    }

    private static void ClearAction(Runtime rt)
    {
        lock (rt.Sync) rt.Action = "";
    }

    private async Task ReleaseHumanAsync(ProjectConfig cfg, string reason)
    {
        using var scope = _scopes.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<ResourceLeaseService>();
        await leases.ReleaseHumanLeaseAsync(cfg.StackResourceKey, reason);
    }

    // ---- probing ------------------------------------------------------------

    /// <summary>
    /// Poll every configured stack and reconcile the human lease with what is actually running.
    /// Called on a timer by <see cref="StackProbeService"/>.
    /// </summary>
    public async Task ProbeAllAsync(CancellationToken ct)
    {
        foreach (var key in StackProjects())
        {
            if (ct.IsCancellationRequested) return;
            var cfg = _registry.GetProjectConfig(key);
            var rt = RuntimeFor(key);

            var healthy = await ProbeAsync(cfg.Stack.HealthUrl);
            bool busy;
            lock (rt.Sync)
            {
                busy = rt.Action.Length > 0;
                if (!busy)
                {
                    rt.Healthy = healthy;
                    // A successful probe retires whatever the last failure said. Leaving the error up
                    // next to a running stack is just noise the user has to learn to ignore.
                    if (healthy) rt.LastError = null;
                }
                rt.ProbedAt = DateTime.UtcNow;
            }

            // Mid-action the stack is legitimately half up or half down, so nothing here can be
            // read as evidence about the lease.
            if (busy) continue;

            try
            {
                using var scope = _scopes.CreateScope();
                var leases = scope.ServiceProvider.GetRequiredService<ResourceLeaseService>();
                var holder = await leases.HolderOfAsync(cfg.StackResourceKey);
                if (holder?.HolderLabel is null) continue;   // free, or an agent's - neither is ours to touch

                if (healthy)
                    await leases.ExtendHumanLeaseAsync(cfg.StackResourceKey, HumanTtl);
                else
                    // Stopped from Docker Desktop, or it fell over. The running stack is the truth and
                    // the lease follows it, so the queue frees itself without anyone pressing Stop.
                    await leases.ReleaseHumanLeaseAsync(cfg.StackResourceKey, "the stack stopped answering");
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Lease reconcile failed for {Project}", key);
            }
        }
    }

    /// <summary>
    /// True when anything at all answers. A 401 or a 404 from the proxy still proves it is listening
    /// and routing; insisting on a 2xx would report a healthy stack as down whenever its root path
    /// is not a page, which is the normal case behind a reverse proxy.
    /// </summary>
    private async Task<bool> ProbeAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- command execution --------------------------------------------------

    private record RunOutcome(bool Ok, int ExitCode, string? Error);

    /// <summary>
    /// Run one configured command in <paramref name="workDir"/>, streaming its output to
    /// <paramref name="onLine"/>. Through the shell so a config line can be written the way it would
    /// be typed. These commands come from the same project config files that already supply
    /// <c>worktreeSetup</c>, which the heartbeat runs verbatim - this is not a new trust boundary,
    /// and the dashboard only listens on localhost.
    /// </summary>
    private static async Task<RunOutcome> RunCommandAsync(
        string command, string workDir, TimeSpan timeout, Action<string> onLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        psi.ArgumentList.Add(command);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };

        onLine($"[dashboard] {workDir}> {command}");
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new RunOutcome(false, -1, ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new RunOutcome(false, -1, $"'{command}' did not finish within {(int)timeout.TotalSeconds}s.");
        }

        return new RunOutcome(proc.ExitCode == 0, proc.ExitCode, null);
    }

    /// <summary>
    /// Keep the tail of the command's output. Bounded because a compose pull is thousands of
    /// progress lines and only the end of it ever explains a failure.
    /// </summary>
    private const int MaxOutputLines = 200;

    private static void Append(Runtime rt, string line)
    {
        lock (rt.Sync)
        {
            rt.Output.Add(line);
            if (rt.Output.Count > MaxOutputLines) rt.Output.RemoveRange(0, rt.Output.Count - MaxOutputLines);
        }
    }
}
