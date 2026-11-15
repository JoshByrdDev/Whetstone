using System.Text.Json;
using Whetstone.Models;
using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>What a runner reports on every check-in: everything it is holding, every time.</summary>
/// <param name="WaitSeconds">How long the check-in may wait for an order when none is queued (0..25).</param>
/// <param name="DataRoot">The runner's own data root - where that machine keeps a ticket's files (see HostDataMirror).</param>
/// <param name="UsageLimitUntil">One pause for the whole host. Runners before per-account limits sent only this.</param>
/// <param name="ClaudeLogins">Who each Claude login on that machine is signed in as - identity only, never a folder or a token.</param>
/// <param name="UsageLimits">Each provider's pause there, naming the Claude account that hit one.</param>
/// <param name="Inventory">That machine's hardware: sent on a runner's first check-in and hourly after, stored only when it changed.</param>
/// <param name="Load">Its load, read at most once a minute; the board keeps the latest for its minute sample.</param>
/// <param name="PsVersion">The PowerShell the runner is on.</param>
/// <param name="WindowsPowerShell">Whether it, or the supervisor that started it, is Windows PowerShell 5.1 - a
/// task set up before the fleet left 5.1. The runner moves itself to 7 once idle; until then the Heartbeat page says so.</param>
/// <param name="Disks">Free space on the drives holding its worktree roots: below <see cref="DiskFloor"/> it is given nothing new.
/// Sent every few minutes, not on every check-in; absent keeps what it last said.</param>
public record HostCheckinRequest(string? Commit, HostRun[]? Runs, DateTime? UsageLimitUntil, int? WaitSeconds, string? DataRoot = null,
    HostClaudeLogin[]? ClaudeLogins = null, HostUsageLimit[]? UsageLimits = null,
    MachineInventoryReport? Inventory = null, MachineLoadReport? Load = null,
    string? PsVersion = null, bool? WindowsPowerShell = null, HostDisk[]? Disks = null);

/// <summary>A runner asking which of one project's worktrees on its machine may go. See <see cref="HostWorktrees"/>.</summary>
public record HostWorktreesRequest(string Project, HostWorktreeItem[]? Worktrees);

/// <summary>The switch for all remote work, as the Heartbeat page sets it.</summary>
public record HostSwitchRequest(bool Enabled);

/// <summary>An order the orchestrator places for one of a host's slots.</summary>
public record HostOrderRequest(string Kind, int Slot, int RunId, JsonElement? Claim);

/// <summary>A worker host handing a ticket's branch back. See <see cref="TicketGitService.AcceptWorkerBranchAsync"/>.</summary>
public record BranchAcceptRequest(int RunId, string Branch, string Sha, string IncomingRef, string? ExpectedOld);

/// <summary>
/// The worker host routes (docs/worker-hosts-plan.md, piece 2). A runner checks in and receives its
/// orders; the orchestrator places orders and reads every host's state; a worker on a worker host
/// hands its branch back and fetches its project's kit.
/// <para>
/// None of this is authentication. A host is named in the path and the firewall rule on the board
/// host is what keeps other machines out - see the plan, piece 7.
/// </para>
/// </summary>
public static class WorkerHostEndpoints
{
    /// <summary>
    /// A check-in's answer. A configured host is also told the board host's commit: a runner that is
    /// on another one gets no work (see <see cref="WorkerHostView.Dispatchable"/>), and brings itself
    /// onto this one once it is idle - see Get-UpdateAction in data/runner.ps1. A name the config does
    /// not have is told nothing but that.
    /// </summary>
    public static IResult CheckinResult(string name, IReadOnlyList<HostOrder>? orders, int[] revoked, string? boardCommit) =>
        orders is null
            ? Results.NotFound(new { error = $"'{name}' is not a worker host in this board's config (workerHosts)." })
            : Results.Ok(new { orders, revoked, boardCommit });

    /// <summary>
    /// What a check-in carries about the machine itself: its load is held for the next minute's
    /// sample, its hardware is kept only when it changed. docs/machine-limits-plan.md.
    /// </summary>
    public static async Task RecordMachineAsync(Persistence.DashboardDbContext db, MachineLoadBook book, string host,
        MachineInventoryReport? inventory, MachineLoadReport? load, DateTime now, CancellationToken ct = default)
    {
        if (load is not null) book.Set(host, load, now);
        if (inventory is not null) await MachineInventoryStore.RecordAsync(db, host, inventory, now, ct);
    }

    private static readonly object StopGate = new();

    /// <summary>
    /// The orders a check-in carries away, less any start whose claim is no longer live. The
    /// orchestrator gives a start up after two minutes unanswered and releases its claim; the order
    /// itself stayed queued, so a runner that was slow to check in started it anyway - a run nobody
    /// was waiting for, on a ticket that might already be running elsewhere, killed at the next
    /// check-in as revoked, sometimes halfway through making its worktree. Such a start is dropped
    /// here instead, before the runner ever sees it.
    /// </summary>
    public static IReadOnlyList<HostOrder> DropReleasedStarts(IReadOnlyList<HostOrder> orders, ISet<int> liveRunIds, out List<int> dropped)
    {
        dropped = orders.Where(o => o.Kind == "start" && !liveRunIds.Contains(o.RunId)).Select(o => o.RunId).ToList();
        if (dropped.Count == 0) return orders;
        var gone = dropped.ToHashSet();
        return orders.Where(o => !(o.Kind == "start" && gone.Contains(o.RunId))).ToList();
    }

    /// <summary>
    /// Switch Off every host whose "stop after current runs" has drained (<see cref="WorkerHostService.DrainedStops"/>).
    /// Worked out here on the board, from what the runners report, whenever something reads or hears
    /// from the hosts - a check-in, the orchestrator's pass, the page - so neither the orchestrator nor
    /// a runner has to know a pending stop exists. Returns the hosts it switched off.
    /// </summary>
    public static IReadOnlyList<string> FinishDrainedStops(DashboardPaths paths, WorkerHostService hosts)
    {
        var drained = hosts.DrainedStops();
        if (drained.Count == 0) return drained;
        lock (StopGate)
        {
            var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
            var before = ConfigEndpoints.Snapshot(config);
            var done = drained.Where(n => WorkerHostSettings.FinishStop(config, n)).ToList();
            if (done.Count > 0) ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);
            return done;
        }
    }

    public static void MapWorkerHostEndpoints(this WebApplication app)
    {
        app.MapGet("/api/hosts", (WorkerHostService hosts, DashboardPaths paths) =>
        {
            FinishDrainedStops(paths, hosts);
            return Results.Ok(hosts.Snapshot());
        });

        // Where work goes first, read by the orchestrator on every pass - not from config at its own
        // start, because restarting it to apply a change would kill every worker it has out.
        // It also carries this computer's own place (on, stopping, its berths), read the same way and
        // for the same reason - and, being read every pass, is where this machine's own pending stop
        // is finished once its berths are empty.
        app.MapGet("/api/hosts/dispatch", (ProjectRegistry registry, DashboardPaths paths, EventStore events, BoardDisk disk) =>
        {
            FinishBoardStop(paths, events);
            var global = registry.GetGlobalConfig();
            var d = WorkerHostSettings.ReadDispatch(global, WorkerHostConfig.Read(global));
            var place = WorkerHostSettings.ReadBoard(global);
            // The low-disk guard for this machine's own berths: the orchestrator reads diskLow beside
            // enabled and stopPending (Use-BoardPlace), and takes nothing new here while it is set.
            var diskLow = disk.Low();
            return Results.Ok(new
            {
                mode = d.Mode, order = d.Order,
                board = new { place.Enabled, place.StopPending, place.Slots, place.QaLeases,
                    dispatchable = place.Dispatchable && diskLow is null, diskLow },
            });
        });

        // ---- This computer's own place ----
        //
        // On/Off, berths and QA at once for the board host's own workers, and its stop after current
        // runs - the twin of a worker host's, applied on the orchestrator's next pass. None of it
        // touches the heartbeat itself or the steward's berth: the header's stop is for all of that.
        // A change that can only add room wakes an idle heartbeat, which may have backed off for an
        // hour while this computer was off; one that is not running is simply not woken.
        app.MapPut("/api/hosts/settings/board", (WorkerHostSettings.BoardEdit req, DashboardPaths paths, HeartbeatManager hb) =>
        {
            lock (StopGate)
            {
                var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
                var before = ConfigEndpoints.Snapshot(config);
                WorkerHostSettings.SetBoard(config, req);
                ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);
            }
            if (req.Enabled == true || req.Slots is not null) hb.Wake();
            return Results.Ok(new { saved = true });
        });
        app.MapPost("/api/hosts/settings/board/stop-after-runs", (DashboardPaths paths, EventStore events) =>
            SetBoardStop(true, paths, events));
        app.MapPost("/api/hosts/settings/board/cancel-stop", (DashboardPaths paths, EventStore events, HeartbeatManager hb) =>
        {
            var r = SetBoardStop(false, paths, events);
            hb.Wake();
            return r;
        });

        app.MapPut("/api/hosts/settings/dispatch", (WorkerHostSettings.DispatchEdit req, DashboardPaths paths, ProjectRegistry registry) =>
        {
            var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
            var before = ConfigEndpoints.Snapshot(config);
            if (WorkerHostSettings.SetDispatch(config, req, WorkerHostConfig.Read(registry.GetGlobalConfig())) is { } why)
                return Results.BadRequest(new { error = why });
            ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);
            return Results.Ok(new { saved = true });
        });

        // ---- The Heartbeat page's worker hosts section ----
        //
        // Everything it draws in one read: the switch, every configured host with its live state,
        // machines that checked in before being added, and what a host may be given.
        app.MapGet("/api/hosts/settings", (WorkerHostService hosts, ProjectRegistry registry, BoardCommit commit, DashboardPaths paths, EventStore events,
            BoardDisk disk) =>
        {
            FinishDrainedStops(paths, hosts);
            FinishBoardStop(paths, events);
            return Results.Ok(new
            {
                enabled = hosts.RemoteOn,
                // Whether to draw the section at all - a Settings choice, display only.
                shown = ConfigEndpoints.ShowWorkerHosts(registry.GetGlobalConfig()),
                boardCommit = commit.Get(),
                hosts = hosts.Snapshot(),
                dispatch = WorkerHostSettings.ReadDispatch(registry.GetGlobalConfig(), WorkerHostConfig.Read(registry.GetGlobalConfig())),
                seen = hosts.Seen(),
                projects = registry.Projects.Select(k => new { key = k, displayName = registry.GetProjectConfig(k).DisplayName }),
                skills = WorkerHostSettings.HostableSkills,
                // This computer's own card: its place, and what its berths are doing now.
                board = BoardView(registry, events, disk),
            });
        });

        app.MapPut("/api/hosts/settings", (HostSwitchRequest req, DashboardPaths paths) =>
        {
            var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
            var before = ConfigEndpoints.Snapshot(config);
            WorkerHostSettings.SetEnabled(config, req.Enabled);
            ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);
            return Results.Ok(new { enabled = req.Enabled });
        });

        app.MapPut("/api/hosts/settings/hosts/{name}", (string name, WorkerHostSettings.HostEdit req,
            DashboardPaths paths, ProjectRegistry registry, WorkerHostService hosts) =>
        {
            var current = hosts.Find(name);
            if (current is not null && req.Slots is { } slots && Math.Clamp(slots, 1, WorkerHostConfig.MaxSlots) != current.Slots
                && WorkerHostSettings.RenumberingProblem(hosts.Snapshot(), name, removing: false) is { } busy)
                return Results.Conflict(new { error = busy });

            // Under the stop gate, so a board switching a drained host Off cannot write over this edit.
            lock (StopGate)
            {
                var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
                var before = ConfigEndpoints.Snapshot(config);
                if (WorkerHostSettings.Upsert(config, name, req, registry.Projects.ToList()) is { } why)
                    return Results.BadRequest(new { error = why });
                ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);
            }
            return Results.Ok(new { saved = true });
        });

        // A host's own pending stop: it takes nothing new, its runs finish and hand back, and then the
        // board switches it Off - the per-host twin of the heartbeat's stop-after-cycle / cancel-stop.
        // Pressing On or Off also ends it (see WorkerHostSettings.Upsert).
        app.MapPost("/api/hosts/settings/hosts/{name}/stop-after-runs", (string name, DashboardPaths paths, WorkerHostService hosts) =>
            SetStop(name, true, paths, hosts));
        app.MapPost("/api/hosts/settings/hosts/{name}/cancel-stop", (string name, DashboardPaths paths, WorkerHostService hosts) =>
            SetStop(name, false, paths, hosts));

        app.MapDelete("/api/hosts/settings/hosts/{name}", (string name, DashboardPaths paths, WorkerHostService hosts) =>
        {
            if (WorkerHostSettings.RenumberingProblem(hosts.Snapshot(), name, removing: true) is { } busy)
                return Results.Conflict(new { error = busy });
            var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
            var before = ConfigEndpoints.Snapshot(config);
            if (!WorkerHostSettings.Remove(config, name)) return Results.NotFound(new { error = $"No worker host '{name}'." });
            ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);
            return Results.Ok(new { removed = true });
        });

        // A runner's check-in. The response also names the runs it reported whose lease has gone -
        // the orchestrator gave up on them while this host was unreachable - which the runner kills
        // without handing anything back, because their tickets may already be running elsewhere.
        app.MapPost("/api/hosts/{name}/checkin", async (string name, HostCheckinRequest req,
            WorkerHostService hosts, ResourceLeaseService leases, BoardCommit commit, MachineLoadBook book,
            IServiceScopeFactory scopes, DashboardPaths paths, CancellationToken ct) =>
        {
            var runs = req.Runs ?? Array.Empty<HostRun>();
            // Only for a machine the config names, so a stray runner leaves no history behind.
            if (hosts.Find(name) is { } known && (req.Load is not null || req.Inventory is not null))
            {
                using var scope = scopes.CreateScope();
                await RecordMachineAsync(scope.ServiceProvider.GetRequiredService<Persistence.DashboardDbContext>(), book,
                    known.Name, req.Inventory, req.Load, DateTime.UtcNow, ct);
            }
            var live = await leases.LiveRunsAsync(runs.Where(r => !r.Exited).Select(r => r.RunId).ToList());
            var revoked = runs.Where(r => !r.Exited && !live.Contains(r.RunId)).Select(r => r.RunId).ToArray();

            var wait = TimeSpan.FromSeconds(Math.Clamp(req.WaitSeconds ?? 0, 0, (int)WorkerHostService.MaxWait.TotalSeconds));
            var orders = await hosts.CheckInAsync(name, req.Commit, runs, req.UsageLimitUntil, wait, ct, req.DataRoot,
                req.ClaudeLogins, req.UsageLimits, req.PsVersion, req.WindowsPowerShell ?? false, req.Disks);
            if (orders is not null && orders.Any(o => o.Kind == "start"))
            {
                var startIds = orders.Where(o => o.Kind == "start").Select(o => o.RunId).ToList();
                var stillLive = await leases.LiveRunsAsync(startIds);
                orders = DropReleasedStarts(orders, stillLive, out var dropped);
                if (dropped.Count > 0) hosts.ForgetHanded(name, dropped);
            }
            FinishDrainedStops(paths, hosts);
            return CheckinResult(name, orders, revoked, commit.Get());
        });

        // Which of a project's worktrees on a worker host may be removed. The runner names each by its
        // folder and what it holds, never by its path, and removes only what this says it may - after
        // its own checks, which only it can make (data/runner.ps1, Remove-HostWorktrees). It changes
        // nothing here: the answer is read from the tickets.
        app.MapPost("/api/hosts/{name}/worktrees", async (string name, HostWorktreesRequest req,
            WorkerHostService hosts, ProjectRegistry registry, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            if (hosts.Find(name) is null) return Results.NotFound(new { error = $"'{name}' is not a worker host." });
            if (string.IsNullOrWhiteSpace(req.Project) || !registry.AllProjects.Contains(req.Project, StringComparer.OrdinalIgnoreCase))
                return Results.NotFound(new { error = $"No project '{req.Project}'." });
            var items = (req.Worktrees ?? Array.Empty<HostWorktreeItem>())
                .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Name) && i.Name.IndexOfAny(new[] { '/', '\\' }) < 0 && i.Name is not ("." or ".."))
                .ToList();
            var days = WorktreeReaper.RetentionDays(registry.GetGlobalConfig());
            using var scope = scopes.CreateScope();
            var verdicts = await HostWorktrees.DecideAsync(scope.ServiceProvider.GetRequiredService<Persistence.DashboardDbContext>(),
                registry.AllProjects.First(p => p.Equals(req.Project, StringComparison.OrdinalIgnoreCase)),
                items, TimeSpan.FromDays(Math.Max(0, days)), DateTime.UtcNow, ct);
            return Results.Ok(new { retentionDays = days, verdicts });
        });

        app.MapPost("/api/hosts/{name}/orders", (string name, HostOrderRequest req, WorkerHostService hosts) =>
        {
            if (req.Kind is not ("start" or "kill"))
                return Results.BadRequest(new { error = $"Unknown order '{req.Kind}'." });
            var host = hosts.Find(name);
            if (host is null) return Results.NotFound(new { error = $"'{name}' is not a worker host." });
            if (!host.OwnsSlot(req.Slot))
                return Results.BadRequest(new { error = $"Slot {req.Slot} is not one of {name}'s ({host.FirstSlot}..{host.FirstSlot + host.Slots - 1})." });
            if (req.Kind == "start" && req.Claim is null)
                return Results.BadRequest(new { error = "A start order carries the claim." });
            // The account is chosen as the order is placed, not when the run was claimed: the latest
            // reading decides. A start that names none while the board chooses is one the worker
            // turns down if its phase runs on Claude - see "claudeAccount" in data/worker.ps1.
            var claude = req.Kind == "start" ? hosts.ClaudeChoice(name) : null;
            hosts.Enqueue(name, new HostOrder(req.Kind, req.Slot, req.RunId, req.Claim, claude));
            return Results.Accepted();
        });

        // The board host's half of the hand-back. The lease is checked first: a run that is no
        // longer the ticket's run must not move its branch, whatever git would allow.
        app.MapPost("/api/tickets/{id:int}/branch/accept", async (int id, BranchAcceptRequest req,
            TicketService tickets, ResourceLeaseService leases, TicketGitService git, CancellationToken ct) =>
        {
            var ticket = await tickets.GetAsync(id);
            if (ticket is null) return Results.NotFound(new { error = $"Ticket #{id} not found." });
            if (!await leases.IsLiveRunAsync(req.RunId, id))
                return Results.Conflict(new { error = $"Run {req.RunId} no longer holds ticket #{id} - it was released, and the ticket may be running elsewhere." });

            var r = await git.AcceptWorkerBranchAsync(ticket.ProjectKey, req.Branch, req.IncomingRef, req.Sha, req.ExpectedOld, ct);
            // Said on the ticket, because the kept ref is the only copy of what the rebase had got to.
            foreach (var k in r.Stopped ?? Array.Empty<StoppedRebaseKept>())
                await tickets.AddCommentAsync(id, TicketGitService.SayStoppedRebaseKept(k, req.Branch), AuthorKind.System);
            // Said on the ticket, because the stash is the only copy of what was set aside.
            if (r.SetAside is { } s)
            {
                var names = string.Join("\n", s.Files.Take(10).Select(f => $"- `{f}`"));
                var more = s.Files.Count > 10 ? $"\n- and {s.Files.Count - 10} more" : "";
                await tickets.AddCommentAsync(id,
                    $"The board host's own worktree for this ticket, `{s.Path}`, had {s.Files.Count} uncommitted file(s), left by an earlier run here that stopped before it could commit them. " +
                    $"The worker host's hand-back already carries every commit of this branch, so they were set aside in a stash rather than blocking it, and the worktree now matches the branch.\n\n{names}{more}\n\n" +
                    $"They are kept as stash `{s.Sha}` (`git stash list` shows it as: {s.Message}). To look: `git stash show -p --include-untracked {s.Sha}`. Nothing needs doing unless something in them was wanted.",
                    AuthorKind.System);
            }
            return r.Ok ? Results.Ok(new { accepted = true, moved = r.Moved }) : Results.Conflict(new { error = r.Why });
        });

        // A ticket's own files - its owner's attachments and the linked issue's - for a worker host
        // to keep at the same paths under its own root. See HostDataMirror.
        app.MapGet("/api/hosts/ticket-data/{id:int}", (int id, DashboardPaths paths) =>
            Results.File(HostDataMirror.BuildTicketData(paths, id), "application/zip", $"ticket-{id}.zip"));

        // A QA capture from a worker host's run, as it lands, so set_qa_shots finds it here when the
        // worker names it by its own path. Only for the run that holds the ticket.
        app.MapPut("/api/hosts/{name}/qa-captures/{id:int}/{**file}", async (string name, int id, string file, int runId,
            HttpRequest request, WorkerHostService hosts, ResourceLeaseService leases, DashboardPaths paths) =>
        {
            if (hosts.Find(name) is null) return Results.NotFound(new { error = $"'{name}' is not a worker host." });
            if (HostDataMirror.SafeCaptureName(file) is not { } safe)
                return Results.BadRequest(new { error = $"'{file}' is not a capture (an image, by a plain relative path)." });
            if (!await leases.IsLiveRunAsync(runId, id))
                return Results.Conflict(new { error = $"Run {runId} no longer holds ticket #{id}." });
            if (request.ContentLength is > HostDataMirror.MaxCaptureBytes)
                return Results.BadRequest(new { error = "A capture over 10MB is not a screenshot." });

            var dest = Path.Combine(new[] { paths.QaCaptureTicketDir(id) }.Concat(safe.Split('/')).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var temp = dest + ".part";
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await request.Body.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > HostDataMirror.MaxCaptureBytes) { output.Close(); File.Delete(temp); return Results.BadRequest(new { error = "A capture over 10MB is not a screenshot." }); }
                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            // Renamed into place whole, so a set_qa_shots waiting for it never copies half a file.
            File.Move(temp, dest, overwrite: true);
            return Results.Ok(new { stored = safe });
        });

        app.MapGet("/api/hosts/project-kit/{key}", (string key, ProjectRegistry registry, DashboardPaths paths) =>
        {
            if (!registry.AllProjects.Contains(key, StringComparer.OrdinalIgnoreCase))
                return Results.NotFound(new { error = $"No project '{key}'." });
            var cfg = registry.GetProjectConfig(key);
            var zip = ProjectKit.Build(paths.ProjectDir(key), cfg.ResolvedMemoryDir);
            return Results.File(zip, "application/zip", $"{key}-kit.zip");
        });
    }

    /// <summary>
    /// Switch this computer Off once its pending stop has drained: none of slots 1..MaxWorkers running
    /// (<see cref="EventStore.BoardBusyCount"/>) and the grace since the press over. True when it did.
    /// </summary>
    public static bool FinishBoardStop(DashboardPaths paths, EventStore events, DateTime? now = null)
    {
        lock (StopGate)
        {
            var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
            var before = ConfigEndpoints.Snapshot(config);
            if (!WorkerHostSettings.FinishBoardStop(config, events.BoardBusyCount, now ?? DateTime.UtcNow)) return false;
            ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);
            return true;
        }
    }

    /// <summary>This computer's card, as the Heartbeat page draws it.</summary>
    private static object BoardView(ProjectRegistry registry, EventStore events, BoardDisk disk)
    {
        var place = WorkerHostSettings.ReadBoard(registry.GetGlobalConfig());
        return new
        {
            place.Enabled, place.StopPending, place.Slots, place.QaLeases,
            disks = disk.Disks(), diskLow = disk.Low(),
            maxSlots = HeartbeatManager.MaxWorkers,
            maxQaLeases = ResourceLeaseService.MaxQaLeases,
            @out = events.BoardBusyCount,
            runs = events.BoardRuns(),
        };
    }

    private static IResult SetBoardStop(bool pending, DashboardPaths paths, EventStore events)
    {
        string? why;
        lock (StopGate)
        {
            var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
            var before = ConfigEndpoints.Snapshot(config);
            why = WorkerHostSettings.SetBoardStop(config, pending, DateTime.UtcNow);
            if (why is null) ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);
        }
        return why is null ? Results.Ok(new { stopPending = pending }) : Results.BadRequest(new { error = why });
    }

    private static IResult SetStop(string name, bool pending, DashboardPaths paths, WorkerHostService hosts)
    {
        string? why;
        lock (StopGate)
        {
            var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
            var before = ConfigEndpoints.Snapshot(config);
            why = WorkerHostSettings.SetStopPending(config, name, pending);
            if (why is null) ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);
        }
        if (why is not null) return hosts.Find(name) is null ? Results.NotFound(new { error = why }) : Results.BadRequest(new { error = why });
        // A host with nothing out goes Off at once rather than on the next read.
        var off = pending && FinishDrainedStops(paths, hosts).Contains(name, StringComparer.OrdinalIgnoreCase);
        return Results.Ok(new { stopPending = pending && !off, enabled = !off });
    }

}
