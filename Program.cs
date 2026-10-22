using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Whetstone.Api;
using Whetstone.Mcp;
using Whetstone.Persistence;
using Whetstone.Hubs;
using Whetstone.Models;
using Whetstone.Services;
using Whetstone.Services.Tracker;

// Every agent this app launches - heartbeat workers, one-shot runs, the improvement applier - is its
// own top-level Claude session. Started from inside Claude Code (`dotnet run` in a session's shell),
// the app inherits that session's variables and passes them to all of them: CLAUDECODE=1 makes claude
// refuse to launch ("cannot be launched inside another Claude Code session"), and a desktop session's
// ANTHROPIC_BASE_URL / CLAUDE_CODE_* route agents through its proxy and auth. Only when CLAUDECODE
// says we are under a session, and only what the host injected: a variable the user set persistently
// (User or Machine scope) is theirs and stays. worker.ps1 applies the same rule.
if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CLAUDECODE") is not null)
{
    foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList())
    {
        if (!name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase)) continue;
        if (Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is null
            && Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) is null)
            Environment.SetEnvironmentVariable(name, null);
    }
}

var builder = WebApplication.CreateBuilder(args);

// An isolated QA copy - this program started from a worktree to prove a change to itself - refuses
// everything that would reach beyond its own throwaway folder. See QaIsolation. It will not start
// at all over a checkout's data or on the live port: those are the two ways a copy becomes the live
// board by accident.
var isolation = QaIsolation.FromEnvironment();
if (isolation.On && QaIsolation.StartupProblem(
        Environment.GetEnvironmentVariable("DASHBOARD_DATA_ROOT"), builder.Configuration["urls"]) is { } notACopy)
{
    Console.Error.WriteLine($"Refusing to start as a QA copy: {notACopy}");
    Environment.Exit(2);
}
builder.Services.AddSingleton(isolation);

// A worker host reaches this board over the LAN (docs/worker-hosts-plan.md): with lanUrl set in the
// global config, that one address is listened on beside the ones the app was given. Read here,
// before the host is built, because the addresses cannot change once it is.
try
{
    var bootGlobal = new ProjectRegistry(new DashboardPaths(builder.Environment)).GetGlobalConfig();
    if (LanBinding.Urls(builder.Configuration["urls"], bootGlobal, isolation.On, out var lanWhy) is { } lanUrls)
    {
        builder.WebHost.UseUrls(lanUrls);
        Console.WriteLine($"Listening for worker hosts on: {string.Join(", ", lanUrls)}");
    }
    else if (lanWhy is not null)
        Console.Error.WriteLine($"lanUrl is set but not used: {lanWhy}.");
}
catch (Exception ex) { Console.Error.WriteLine($"lanUrl could not be read: {ex.Message}"); }

// Register services — all state resolves from the self-contained data/ root;
// the dashboard never reaches into the projects it monitors (only their
// projectPath is referenced, as a working directory at run time).
builder.Services.AddSingleton<DashboardPaths>();
builder.Services.AddSingleton<ProjectRegistry>();
builder.Services.AddSingleton<SkillDriftService>();
// Machines that run workers for this board (docs/worker-hosts-plan.md). In memory; see the class.
builder.Services.AddSingleton<BoardCommit>();
builder.Services.AddSingleton<WorkerHostService>();
builder.Services.AddSingleton<EventStore>();
// Per-project tracker access (Jira or Azure Boards, one per project) + live title/description
// resolution for linked tickets. There is no container-wide tracker: which system a project's
// issues live in — and the credentials for it — are properties of the project, so every caller
// names one.
builder.Services.AddSingleton<IssueTrackerFactory>();
builder.Services.AddSingleton<LinkedIssueResolver>();
builder.Services.AddSingleton<HeartbeatManager>();
// Notices the dashboard raises itself. Goes through the heartbeat's own notifier script.
builder.Services.AddSingleton<NotificationSender>();
// Which agent CLI the one-shot runs below shell out to, and how. Reads the provider off global
// config on every call rather than caching it, so the Settings toggle takes effect without a
// restart — which here would also kill every in-flight worker. The heartbeat's own runs have the
// same abstraction on the PowerShell side, in data/providers.ps1.
builder.Services.AddSingleton<AgentProviderFactory>();
builder.Services.AddSingleton<CodexUsageService>();
builder.Services.AddSingleton<ImprovementApplier>();
// The Classroom's generator: an on-demand, read-only agent run that teaches one ticket. It takes no
// lease and creates no worktree, so it never contends with the heartbeat's workers for a ticket.
// Executes one analysis and nothing else — when it runs is not its decision.
builder.Services.AddSingleton<Whetstone.Services.Walkthrough.Narrator>();
builder.Services.AddSingleton<SetupCheckService>(sp => new SetupCheckService(
    sp.GetRequiredService<ProjectRegistry>(), sp.GetRequiredService<DashboardPaths>(),
    sp.GetRequiredService<Whetstone.Services.Walkthrough.Narrator>(),
    sp.GetRequiredService<HeartbeatManager>()));
builder.Services.AddSingleton<Whetstone.Services.Walkthrough.WalkthroughService>();
builder.Services.AddSingleton<AnalysisRunner>();
builder.Services.AddSingleton<Whetstone.Services.UnitReports.UnitReportRunner>();
builder.Services.AddSingleton<IAnalysisExecutor>(sp => sp.GetRequiredService<AnalysisRunner>());
// What the queue asks about a ticket at the moment its turn comes: is it still worth studying, and
// is a heartbeat agent in its worktree right now. Reads the pipeline's own ticket lease to answer
// the second — it never takes one.
builder.Services.AddSingleton<IAnalysisGate, AnalysisGate>();
// The waiting line. A singleton because the "one analysis at a time" cap, the pending list and the
// recent outcomes are all its state — the cap has not changed, only where it lives and the fact
// that it now queues rather than refuses. See AnalysisQueue.
builder.Services.AddSingleton<AnalysisQueue>();

// Local ticketing database (SQLite under the self-contained data/ root).
builder.Services.AddDbContext<DashboardDbContext>((sp, options) =>
{
    var dbPath = sp.GetRequiredService<DashboardPaths>().DatabaseFile;
    options.UseSqlite($"Data Source={dbPath}");
});
builder.Services.AddScoped<TicketService>();
// A project's steward: what it may do and every limit on it. Its git is the ticket git service,
// reached through an interface so the rules can be tested without a repository.
builder.Services.AddSingleton<IStewardGit>(sp => sp.GetRequiredService<TicketGitService>());
builder.Services.AddSingleton<IStewardProjectFlags, StewardFlagScript>();
builder.Services.AddScoped<StewardService>();
// A steward's merge outlives the call that asked for it: a project's checks run longer than a tool
// call may, and a call that dropped used to take the merge down with it. See StewardMerges.
builder.Services.AddSingleton<StewardMerges>();
// What holds a ticket in NeedsRefinement until its independent analysis exists. Registered on its
// own so TicketService picks it up; see ClassroomGate for why refinement waits for one.
builder.Services.AddScoped<IClassroomGate, ClassroomGate>();
// Pipeline metrics (cycle runs, phase-transition history). Read/write-own-tables only.
builder.Services.AddScoped<MetricsService>();
// What each ticket is waiting on: the tracker's blocking links, refreshed live, with local overrides
// that outrank them. TicketService takes it so the claim can pass over a ticket that is blocked.
builder.Services.AddScoped<TicketDependencyService>();
// Exclusive use of the things workers share (the QA stack, the local DB). This is what caps how
// many can safely run at once, and TicketService takes leases in the same breath as the claim.
builder.Services.AddScoped<ResourceLeaseService>();
// Stored Classroom analyses, and the one rule about which tickets can have one.
builder.Services.AddScoped<TicketAnalysisService>();
// The owner's filing tags. Its own service rather than more surface on TicketService, because
// nothing a worker can reach may ever read one - see Models/Labels.cs.
builder.Services.AddScoped<TicketLabelService>();
// On-disk storage for QA screenshot shots (stateless — copies files, resolves paths).
builder.Services.AddSingleton<QaShotStore>();
builder.Services.AddSingleton<TicketFileStore>();
builder.Services.AddSingleton<IssueAttachmentStore>();
builder.Services.AddSingleton<IssueSnapshotService>();
builder.Services.AddSingleton<IssueContextBuilder>();
// Reads and cleans up the per-ticket git worktrees the heartbeat creates. Creation lives in the
// heartbeat (it owns branch naming); this side only lists, prunes and removes.
builder.Services.AddSingleton<GitWorktreeService>();
// Reclaims the worktree of a ticket that has been Done for worktreeRetentionDays: on start, then daily.
// Not in a QA copy, nor are the two probes below: nothing a copy runs by itself should be anything
// but what a request asked for.
if (!isolation.On) builder.Services.AddHostedService<WorktreeReaper>();
// The human's own git moves on a ticket: check its branch out in the hub repo, and raise its PR.
// Separate from the worktree service above because it targets the user's checkout, not the agents'.
builder.Services.AddSingleton<TicketGitService>();
// Start/stop for each project's local stack. Takes the same lease the qa-test phase takes, so a
// person poking at the running app and a QA agent are contenders for it rather than collisions.
builder.Services.AddSingleton<ProjectStackService>();
if (!isolation.On) builder.Services.AddHostedService<StackProbeService>();

// How much subscription rate-limit headroom is left, polled once for every open tab. Reads Claude
// Code's own login, and renews it when it ages out — see ClaudeUsageService.
builder.Services.AddSingleton<ClaudeUsageService>();
if (!isolation.On) builder.Services.AddHostedService<ClaudeUsageProbe>();

// The probes above are meters. The default for a background service that throws is to stop the
// host, and the host is what the heartbeat and every worker hang off - so one slow usage request
// used to end every run in flight. A meter that dies is logged; the app it measures stays up.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Local ticketing MCP server (Streamable HTTP at /mcp). The heartbeat's headless
// Claude session connects here and drives local tickets via these tools instead
// of writing to the external tracker.
//
// One server, two doors. A worker's session connects at /mcp and is shown the ticket tools; a
// project's steward connects at /mcp/steward and is shown only its own (see StewardTools). Each
// session's list is cut here, by the path it came in on, so neither kind of agent is ever offered
// the other's tools.
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.ConfigureSessionOptions = (http, mcp, _) =>
    {
        if (mcp.ToolCollection is not { } all) return Task.CompletedTask;
        var steward = http.Request.Path.StartsWithSegments("/mcp/steward");
        var mine = new ModelContextProtocol.Server.McpServerPrimitiveCollection<ModelContextProtocol.Server.McpServerTool>();
        foreach (var tool in all)
            if (tool.ProtocolTool.Name.StartsWith(StewardTools.Prefix, StringComparison.Ordinal) == steward)
                mine.Add(tool);
        mcp.ToolCollection = mine;
        return Task.CompletedTask;
    })
    .WithTools<TicketTools>()
    .WithTools<StewardTools>();

// Add SignalR
builder.Services.AddSignalR();

// CORS for Vite dev server
builder.Services.AddCors(options =>
{
    options.AddPolicy("ViteDev", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:5338")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// A QA copy's front door: the routes in QaIsolation's table are refused before any handler runs,
// with a sentence that says why. The services behind them refuse again, for a caller this misses.
if (isolation.On)
{
    app.Logger.LogWarning("Isolated QA copy ({Var}): data root {Root}. Heartbeat, steward, agents, tracker, notifications and the Claude login are refused.",
        QaIsolation.EnvVar, app.Services.GetRequiredService<DashboardPaths>().DataRoot);
    app.Use(async (ctx, next) =>
    {
        if (QaIsolation.RefusedRoute(ctx.Request.Method, ctx.Request.Path.Value) is { } what)
        {
            var refusal = QaIsolation.Refusal(what);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = refusal, message = refusal, isolated = true });
            return;
        }
        await next();
    });
}

// Whether this process is an isolated QA copy - so a check, or a person, can tell the two apart.
app.MapGet("/api/qa-isolation", (DashboardPaths p) => Results.Ok(new { isolated = isolation.On, dataRoot = isolation.On ? p.DataRoot : null }));

var paths = app.Services.GetRequiredService<DashboardPaths>();
var registry = app.Services.GetRequiredService<ProjectRegistry>();
// Every project-scoped endpoint takes ?project= and is refused without it.
//
// There is no default. Projects are peers - the board shows them together, the one heartbeat works
// them together - so "which project did you mean" has no answer the server can supply. Falling back
// to whichever one sorts first is worse than an error: a skill read, an improvement ticked off or an
// apply run launched against the wrong project all succeed silently and look exactly like success.
// Every caller in the app already sends the parameter; this is what happens when one stops.
IResult? NoProject(string? project) =>
    registry.IsEnabled(project)
        ? null
        : Results.BadRequest(new { error = $"Unknown or missing project '{project}'. Every project-scoped call must name an enabled project." });

// Apply migrations on startup. No demo seeding — real tickets are created via the UI/API,
// and an intentionally-emptied board must stay empty across restarts.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
    db.Database.Migrate();

    // One-off: the linked-issue attachment cache used to be called jira-attachments, back when Jira
    // was the only tracker there could be. Moved rather than abandoned so a ticket already mid-flight
    // does not silently lose the design PDF a later phase was going to be told about.
    scope.ServiceProvider.GetRequiredService<DashboardPaths>().MigrateLegacyAttachmentCache();

    app.Logger.LogInformation(
        "Local ticketing DB ready: {Tickets} ticket(s) across {Projects} project(s)",
        db.Tickets.Count(), db.Tickets.Select(t => t.ProjectKey).Distinct().Count());

    // One-off: recover the phase history that only ever existed as comment prose, so the metrics
    // have something to say on the day they ship rather than a fortnight later. No-ops forever
    // after the first run (see BackfillTransitionsAsync).
    var backfilled = await scope.ServiceProvider.GetRequiredService<MetricsService>().BackfillTransitionsAsync();
    if (backfilled > 0)
        app.Logger.LogInformation("Backfilled {Count} phase transition(s) from historical comments.", backfilled);
}

// Clear registrations for worktrees whose directories are gone. The user is free to delete a
// worktree folder by hand, and a stale entry left behind would stop the heartbeat re-creating one
// at the same path when that ticket next comes round. Cheap, local, and safe: prune only ever drops
// records of directories that no longer exist.
{
    var worktrees = app.Services.GetRequiredService<GitWorktreeService>();
    foreach (var project in registry.Projects)
    {
        var repo = registry.GetProjectConfig(project).ProjectPath;
        if (!string.IsNullOrWhiteSpace(repo)) worktrees.Prune(repo);
    }
}

app.UseCors("ViteDev");

// Serve the built React app from wwwroot.
// Cache policy: the hashed /assets/* bundles are content-addressed, so cache them forever
// (immutable); index.html must revalidate every load or a deploy won't take effect until the
// user hard-refreshes. Reused for the SPA fallback below so it gets the same headers.
var spaStaticFiles = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var res = ctx.Context.Response;
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            res.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
            res.Headers.CacheControl = "public, max-age=31536000, immutable";
    }
};
app.UseDefaultFiles();
app.UseStaticFiles(spaStaticFiles);

// SignalR hub
app.MapHub<HeartbeatHub>("/hub/heartbeat");

// Local ticketing API (tickets, work items, QA items, comments, transitions).
app.MapTicketEndpoints();

// The Classroom: on-demand teaching analyses of a ticket. Read-only over the pipeline.
app.MapAnalysisEndpoints();
app.MapWalkthroughEndpoints();
app.MapUnitReportEndpoints();

// The owner's filing tags. Separate routes and separate DTOs from the ticket API, so a label can
// never ride along into a worker's prompt - see Api/LabelEndpoints.cs.
app.MapLabelEndpoints();
// Whether a project's copies of the skill templates have fallen behind them (the Skills tab's badge).
app.MapSkillDriftEndpoints();
app.MapStewardEndpoints();

// A ticket's trip through the phases. Its own endpoint rather than a field on the ticket detail,
// which is also what every worker receives through MCP — see TicketJourney.
app.MapJourneyEndpoint();

// Local ticketing MCP endpoint (JSON-RPC over Streamable HTTP).
app.MapMcp("/mcp");
// The same server through its other door: a project's steward, and only its tools.
app.MapMcp("/mcp/steward");

// POST /api/events — receive event from heartbeat.ps1
app.MapPost("/api/events", async (HeartbeatEvent evt, EventStore store, IHubContext<HeartbeatHub> hub) =>
{
    store.Add(evt);
    await hub.Clients.All.SendAsync("ReceiveEvent", evt);
    await hub.Clients.All.SendAsync("ReceiveState", store.GetState());
    return Results.Ok(new { received = true, id = evt.Id });
});

// GET /api/events/recent — return last N events
app.MapGet("/api/events/recent", (EventStore store, int? count) =>
{
    return Results.Ok(store.GetRecent(count ?? 50));
});

// GET /api/state — return current heartbeat state
app.MapGet("/api/state", (EventStore store) =>
{
    return Results.Ok(store.GetState());
});

// GET /api/skills — list a project's skills with name + description
app.MapGet("/api/skills", (string? project) =>
{
    if (NoProject(project) is { } bad) return bad;
    var skillsDir = paths.SkillsDir(project!);
    if (!Directory.Exists(skillsDir))
        return Results.Ok(Array.Empty<object>());

    var skills = new List<object>();
    foreach (var dir in Directory.GetDirectories(skillsDir).OrderBy(d => d))
    {
        var skillFile = Path.Combine(dir, "skill.md");
        if (!File.Exists(skillFile)) continue;

        var content = File.ReadAllText(skillFile);
        var name = Path.GetFileName(dir);
        var description = "";

        // Parse frontmatter for description
        if (content.StartsWith("---"))
        {
            var endIdx = content.IndexOf("---", 3);
            if (endIdx > 0)
            {
                var frontmatter = content[3..endIdx];
                foreach (var line in frontmatter.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("description:"))
                        description = line.Split(':', 2)[1].Trim();
                }
            }
        }

        skills.Add(new { name, description });
    }
    return Results.Ok(skills);
});

// GET /api/skills/{name} — return full skill file content
app.MapGet("/api/skills/{name}", (string name, string? project) =>
{
    // Sanitize to prevent path traversal
    if (name.Contains("..") || name.Contains('/') || name.Contains('\\'))
        return Results.BadRequest("Invalid skill name");
    if (NoProject(project) is { } bad) return bad;

    var skillFile = Path.Combine(paths.SkillsDir(project!), name, "skill.md");
    if (!File.Exists(skillFile))
        return Results.NotFound();

    var content = File.ReadAllText(skillFile);
    return Results.Ok(new { name, content });
});

// GET /api/heartbeat-prompt — return the global heartbeat dispatcher prompt (readonly)
app.MapGet("/api/heartbeat-prompt", () =>
{
    var promptPath = paths.GlobalHeartbeatPrompt;
    if (!File.Exists(promptPath))
        return Results.NotFound();

    var content = File.ReadAllText(promptPath);
    return Results.Ok(new { name = "heartbeat-prompt", content });
});

// GET /api/heartbeat/status — check if heartbeat is running
app.MapGet("/api/heartbeat/status", (HeartbeatManager hb, EventStore store) =>
{
    // draining tells the two halves of a graceful stop apart. A stop that has been asked for while
    // three agents are still finishing their phases looks, to this endpoint, exactly like one with
    // nothing left to wait for — and they are minutes and hours apart, so the button that says
    // "stopping" has to be able to say what it is stopping for.
    return Results.Ok(new
    {
        running = hb.IsRunning,
        scriptExists = hb.ScriptExists,
        // Why the last start was refused by the script itself; null while running or when it wasn't.
        startupError = hb.StartupError,
        stopRequested = hb.StopRequested,
        draining = hb.StopRequested && hb.IsRunning ? store.BusyAgentCount : 0,
    });
});

// GET /api/setup-check - what a fresh install needs and this machine lacks: agent CLIs, git and
// its identity, Node, the narrator, and why the heartbeat last refused to start. Reads PATH and git
// config; starts nothing. See SetupCheckService and docs/setup.md.
app.MapGet("/api/setup-check", (SetupCheckService check) => Results.Ok(check.Run()));

// POST /api/heartbeat/start — launch heartbeat.ps1
app.MapPost("/api/heartbeat/start", async (HeartbeatManager hb, EventStore store, IHubContext<HeartbeatHub> hub) =>
{
    var (success, message) = hb.Start();
    // Start succeeding proves nothing was alive, so any slot still marked busy is a ghost from a
    // heartbeat that died without freeing it (task manager, crash) — and liveness flipping true is
    // exactly what would put those ghosts back on the board. Clear them; real agents and adopted
    // survivors re-announce themselves within seconds.
    if (success) store.ResetAgents();
    // Liveness changed — push fresh state so the status flips immediately (not on the next event).
    await hub.Clients.All.SendAsync("ReceiveState", store.GetState());
    return success ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

// POST /api/heartbeat/stop — kill the heartbeat process
app.MapPost("/api/heartbeat/stop", async (HeartbeatManager hb, EventStore store, WorkerHostService hosts, IHubContext<HeartbeatHub> hub) =>
{
    var (success, message) = hb.Stop();
    // "Now" means every machine: a worker host's runs are not in the tree the kill just took, so
    // each is ordered killed by its runner. Whether or not the heartbeat was running - a remote run
    // can outlive the orchestrator that started it.
    hosts.KillAll();
    // The kill took every worker with it, and none of them got to report its slot free — clear the
    // slots now or they come back as ghosts the moment the heartbeat next starts.
    if (success) store.ResetAgents();
    // Liveness changed — push fresh state so the status flips to "stopped" immediately.
    await hub.Clients.All.SendAsync("ReceiveState", store.GetState());
    return success ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

// POST /api/heartbeat/stop-after-cycle — signal heartbeat to stop before next sleep
app.MapPost("/api/heartbeat/stop-after-cycle", (HeartbeatManager hb) =>
{
    var (success, message) = hb.RequestStopAfterCycle();
    return success ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

// POST /api/heartbeat/wake — end an idle sleep now, because there is work the nap cannot see
app.MapPost("/api/heartbeat/wake", (HeartbeatManager hb) =>
    hb.Wake()
        ? Results.Ok(new { message = "The heartbeat will look at the queue now" })
        : Results.BadRequest(new { message = "Heartbeat is not running" }));

// POST /api/heartbeat/cancel-stop — remove the stop-after-cycle signal
app.MapPost("/api/heartbeat/cancel-stop", (HeartbeatManager hb) =>
{
    var (success, message) = hb.CancelStopAfterCycle();
    return success ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

// GET /api/issues/unlinked?project=KEY - my open issues that have no local ticket yet.
// This is the picker beside the New Ticket form's issue key field, so the set it returns is exactly
// the set worth clicking: assigned to me, not finished upstream, and not already on a board here.
// Tracker credentials are per project, so the project has to be named - there is no set of
// credentials that belongs to the dashboard itself.
app.MapGet("/api/issues/unlinked", async (string? project, DashboardDbContext db, IssueTrackerFactory trackers) =>
{
    if (NoProject(project) is { } bad) return bad;
    var tracker = trackers.For(project!);
    // Not configured is not an error here: the field still works typed by hand, and the form should
    // just show no picker rather than an error the human cannot act on.
    if (!tracker.IsConfigured)
        return Results.Ok(new { configured = false, tracker = tracker.DisplayName, issues = Array.Empty<object>() });
    try
    {
        var candidates = await tracker.GetMyUnfinishedAsync(50);

        // Already-linked keys are collected across every project, not just this one: the same issue
        // picked up on any board is already being worked, and offering it again forks it.
        var taken = new HashSet<string>(
            await db.Tickets.Where(t => t.IssueKey != null).Select(t => t.IssueKey!).ToListAsync(),
            StringComparer.OrdinalIgnoreCase);

        return Results.Ok(new
        {
            configured = true,
            tracker = tracker.DisplayName,
            issues = candidates
                .Where(i => !taken.Contains(i.Key))
                .Select(i => new { key = i.Key, summary = i.Summary, status = i.Status })
                .ToList(),
        });
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(ex.Message, statusCode: (int?)ex.StatusCode ?? 500);
    }
});

// NOTE: every tracker is READ-ONLY. There is no write endpoint here and no write method on
// IIssueTracker — local tickets own all state.

// Global settings (notifications/timing/off-hours/dashboard URL) + project management
// (list / add / edit / unregister / activate). Tracker + QA are per-project.
app.MapConfigEndpoints();

// Per-project stack control (status, start, stop).
app.MapStackEndpoints();

// Worker hosts: check-in and orders for their runners, branch hand-back, the project kit.
app.MapWorkerHostEndpoints();

// Per-ticket git actions for the human: checkout, and PR preflight + create.
app.MapGitEndpoints();

// Subscription rate-limit headroom (session + weekly windows) for the app bar.
app.MapUsageEndpoints();

// === Improvement Suggestions (per-project, scoped via ?project=) ===

string ImprovementsFileFor(string? project) => paths.ImprovementsFile(project!);

// Tags, parsing and tag-rewriting all live in SuggestionCategory — see that file for why they are
// in one place rather than restated at each use site.
var checkboxPattern = SuggestionCategory.CheckboxPattern;

// GET /api/improvements — parse improvement-suggestions.md into structured items
app.MapGet("/api/improvements", (string? project) =>
{
    if (NoProject(project) is { } bad) return bad;
    var improvementsFilePath = ImprovementsFileFor(project);
    if (!File.Exists(improvementsFilePath))
        return Results.Ok(Array.Empty<object>());

    var lines = File.ReadAllLines(improvementsFilePath);
    var items = new List<object>();
    string currentTimestamp = "";
    int itemId = 0;

    foreach (var line in lines)
    {
        var trimmed = line.Trim();

        if (trimmed.StartsWith("### "))
        {
            currentTimestamp = trimmed[4..].Trim();
            continue;
        }

        var match = checkboxPattern.Match(trimmed);
        if (match.Success)
        {
            var text = match.Groups[3].Value;
            var filedAt = SuggestionCategory.FiledAt(text);
            items.Add(new
            {
                id = itemId++,
                text,
                @checked = match.Groups[1].Value == "x",
                category = match.Groups[2].Success && match.Groups[2].Value != ""
                    ? match.Groups[2].Value
                    : SuggestionCategory.Default,
                timestamp = currentTimestamp,
                filedAt,
                // Null when the item predates stamping, which the UI renders as "unknown" rather
                // than as brand new.
                ageDays = filedAt is null ? (int?)null : (int)(DateTime.Now - filedAt.Value).TotalDays,
            });
        }
    }

    return Results.Ok(items);
});

// PUT /api/improvements/{id} — toggle a specific item's checkbox
app.MapPut("/api/improvements/{id}", async (int id, string? project, HttpRequest request) =>
{
    if (NoProject(project) is { } bad) return bad;
    var improvementsFilePath = ImprovementsFileFor(project);
    if (!File.Exists(improvementsFilePath))
        return Results.NotFound();

    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var payload = JsonSerializer.Deserialize<JsonElement>(body);
    var newChecked = payload.GetProperty("checked").GetBoolean();

    var lines = File.ReadAllLines(improvementsFilePath);
    int currentItemId = 0;
    bool found = false;

    for (int i = 0; i < lines.Length; i++)
    {
        var match = Regex.Match(lines[i].Trim(), @"^-\s*\[([ x])\]\s*.+$");
        if (match.Success)
        {
            if (currentItemId == id)
            {
                lines[i] = newChecked
                    ? lines[i].Replace("- [ ]", "- [x]")
                    : lines[i].Replace("- [x]", "- [ ]");
                found = true;
                break;
            }
            currentItemId++;
        }
    }

    if (!found)
        return Results.NotFound();

    File.WriteAllLines(improvementsFilePath, lines);
    return Results.Ok(new { updated = true });
});

// DELETE /api/improvements/{id} — remove an item from the file
app.MapDelete("/api/improvements/{id}", (int id, string? project) =>
{
    if (NoProject(project) is { } bad) return bad;
    var improvementsFilePath = ImprovementsFileFor(project);
    if (!File.Exists(improvementsFilePath))
        return Results.NotFound();

    var lines = File.ReadAllLines(improvementsFilePath).ToList();
    int currentItemId = 0;
    int targetLine = -1;

    for (int i = 0; i < lines.Count; i++)
    {
        var match = Regex.Match(lines[i].Trim(), @"^-\s*\[([ x])\]\s*.+$");
        if (match.Success)
        {
            if (currentItemId == id)
            {
                targetLine = i;
                break;
            }
            currentItemId++;
        }
    }

    if (targetLine == -1)
        return Results.NotFound();

    lines.RemoveAt(targetLine);

    File.WriteAllLines(improvementsFilePath, lines);
    return Results.Ok(new { deleted = true });
});

// PUT /api/improvements/{id}/category — change an item's category
app.MapPut("/api/improvements/{id}/category", async (int id, string? project, HttpRequest request) =>
{
    if (NoProject(project) is { } bad) return bad;
    var improvementsFilePath = ImprovementsFileFor(project);
    if (!File.Exists(improvementsFilePath))
        return Results.NotFound();

    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var payload = JsonSerializer.Deserialize<JsonElement>(body);
    var newCategory = payload.GetProperty("category").GetString();

    if (!SuggestionCategory.IsValid(newCategory))
        return Results.BadRequest(new { error = $"Category must be one of: {string.Join(", ", SuggestionCategory.All)}" });

    var lines = File.ReadAllLines(improvementsFilePath);
    int currentItemId = 0;
    bool found = false;

    for (int i = 0; i < lines.Length; i++)
    {
        var match = checkboxPattern.Match(lines[i].Trim());
        if (match.Success)
        {
            if (currentItemId == id)
            {
                // Remove existing tag if present, then insert new one after checkbox
                lines[i] = SuggestionCategory.WithCategory(lines[i], newCategory!);
                found = true;
                break;
            }
            currentItemId++;
        }
    }

    if (!found)
        return Results.NotFound();

    File.WriteAllLines(improvementsFilePath, lines);
    return Results.Ok(new { updated = true });
});

// GET /api/knowledge — the index of what this project has actually learned.
//
// This reads the notes store in the project's own REPO, not a dashboard file. Durable facts about a
// repo and its machine belong where every reader finds them — a human, an interactive session, and
// an unattended worker are all equally wrong without them — so the dashboard shows that store rather
// than keeping a private second copy of it, which is what the old lessons-learned.md became.
app.MapGet("/api/knowledge", (string? project, ProjectRegistry registry) =>
{
    if (NoProject(project) is { } bad) return bad;
    var cfg = registry.GetProjectConfig(project!);
    var indexPath = cfg.ResolvedMemoryIndex;
    if (string.IsNullOrEmpty(indexPath) || !File.Exists(indexPath))
        return Results.Ok(new { path = indexPath, content = "" });

    return Results.Ok(new { path = indexPath, content = File.ReadAllText(indexPath) });
});

// POST /api/improvements/apply — launch claude to apply a project's approved improvements
app.MapPost("/api/improvements/apply", async (string? project, ImprovementApplier applier) =>
{
    if (NoProject(project) is { } bad) return bad;
    var (success, message) = await applier.StartAsync(project!);
    return success ? Results.Ok(new { message }) : Results.BadRequest(new { message });
});

// GET /api/improvements/apply-status — check status of apply process
app.MapGet("/api/improvements/apply-status", (ImprovementApplier applier) =>
{
    return Results.Ok(applier.GetStatus());
});

// POST /api/improvements/apply-reset — clear apply status
app.MapPost("/api/improvements/apply-reset", (ImprovementApplier applier) =>
{
    applier.Reset();
    return Results.Ok(new { reset = true });
});

// GET /api/history?days=7 — every worker run in the window, newest first.
//
// Read from CycleRuns, which is the durable record: one row per run, carrying the ticket, phase,
// skill, outcome and spend the run reported about itself. The event log is a second, weaker source,
// used only to recover runs that table has no row for — ones that predate it, or whose report never
// arrived. Those come back marked `partial`, holding nothing beyond what a cycle_start event
// actually said, so the timeline keeps them without inventing numbers for them.
//
// This used to be assembled entirely from ticket_start / skill_start / ticket_skip events, which no
// part of the system has ever emitted: every cycle came back with an empty ticket list, so the page
// reported "no work found" against every run it drew and every per-ticket stat read zero.
app.MapGet("/api/history", async (EventStore store, MetricsService metrics, int? days) =>
{
    var window = Math.Clamp(days ?? 7, 1, 365);
    var cutoff = DateTime.UtcNow.AddDays(-window);

    // SQLite hands these back with an unspecified kind; they were written as UTC, and saying so is
    // what lets the browser render them in the reader's own timezone instead of shifting them.
    static DateTime AsUtc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    var runs = await metrics.CyclesSinceAsync(cutoff);

    var cycles = runs.Select(r => new HistoryCycle(
        AsUtc(r.StartedAt), AsUtc(r.EndedAt), (int)Math.Round(r.DurationSeconds),
        r.TicketId, r.ProjectKey, r.Phase.ToString(), r.PhaseAfter?.ToString(),
        r.Skill, r.Resumed, r.Model, r.Outcome.ToString(), r.Mutations, r.NumTurns,
        (r.InputTokens ?? 0) + (r.OutputTokens ?? 0) + (r.CacheCreationTokens ?? 0),
        r.CacheReadTokens ?? 0, Partial: false)).ToList();

    // Pair the event log's cycle_start/cycle_end by agent slot. A linear scan cannot do this: with
    // several agents out the feed is interleaved, so it would close one agent's cycle on another
    // agent's cycle_end and report both a wrong duration and one run fewer.
    var open = new Dictionary<int, HeartbeatEvent>();
    var paired = new List<(HeartbeatEvent Start, DateTime? End)>();
    foreach (var e in store.GetAllFromDisk().Where(e => e.Timestamp >= cutoff).OrderBy(e => e.Timestamp))
    {
        var slot = e.Agent ?? 0;
        if (e.Type == EventTypes.CycleStart)
        {
            // A slot starting a second cycle without having ended the first lost its worker. That
            // orphan is still a run that happened, and it is kept with no end time rather than
            // dropped — a run that leaves no row behind is one the history cannot report on.
            if (open.TryGetValue(slot, out var orphan)) paired.Add((orphan, null));
            open[slot] = e;
        }
        else if (e.Type == EventTypes.CycleEnd && open.Remove(slot, out var start))
        {
            paired.Add((start, e.Timestamp));
        }
    }
    // Whatever is still open is either in flight right now or was killed. Either way it goes in.
    foreach (var stillOpen in open.Values) paired.Add((stillOpen, null));

    // The same run seen twice. The worker sends cycle_start a moment before it starts the clock it
    // later reports, so a run on the same ticket and phase within a minute of the event is it.
    bool AlreadyRecorded(HeartbeatEvent start) => runs.Any(r =>
        r.TicketId.ToString() == start.TicketId
        && (start.Phase is null || r.Phase.ToString().Equals(start.Phase, StringComparison.OrdinalIgnoreCase))
        && Math.Abs((AsUtc(r.StartedAt) - start.Timestamp).TotalMinutes) <= 1);

    cycles.AddRange(paired.Where(p => !AlreadyRecorded(p.Start)).Select(p => new HistoryCycle(
        p.Start.Timestamp, p.End,
        p.End is null ? null : (int)Math.Round((p.End.Value - p.Start.Timestamp).TotalSeconds),
        int.TryParse(p.Start.TicketId, out var ticketId) ? ticketId : null,
        p.Start.Project, p.Start.Phase,
        PhaseAfter: null, Skill: null, Resumed: false, Model: null, Outcome: null,
        Mutations: null, NumTurns: null, Tokens: null, CacheReadTokens: null, Partial: true)));

    var ordered = cycles.OrderByDescending(c => c.StartTime).ToList();

    // "Today" means the reader's today. Measured against UTC midnight it would report a day that
    // has not started yet for anyone west of Greenwich — this dashboard runs on one desk.
    var todayStart = DateTime.Today.ToUniversalTime();
    var today = ordered.Where(c => c.StartTime >= todayStart).ToList();
    var durations = ordered.Where(c => c.DurationSeconds is > 0).Select(c => c.DurationSeconds!.Value).ToList();
    var phases = ordered.Where(c => c.Phase != null)
        .GroupBy(c => c.Phase!)
        .Select(g => new HistoryPhaseCount(g.Key, g.Count()))
        .OrderByDescending(p => p.Count).ThenBy(p => p.Phase)
        .ToList();

    var stats = new HistoryStats(
        TotalCycles: ordered.Count,
        CyclesToday: today.Count,
        TicketsToday: today.Where(c => c.TicketId != null).Select(c => c.TicketId!.Value).Distinct().Count(),
        AvgDurationSeconds: durations.Count > 0 ? (int)durations.Average() : 0,
        TokensToday: today.Sum(c => (long)(c.Tokens ?? 0)),
        Completed: ordered.Count(c => c.Outcome == nameof(CycleOutcome.Completed)),
        Timeouts: ordered.Count(c => c.Outcome == nameof(CycleOutcome.Timeout)),
        Errors: ordered.Count(c => c.Outcome == nameof(CycleOutcome.Error)),
        PartialCycles: ordered.Count(c => c.Partial),
        TopPhase: phases.FirstOrDefault()?.Phase,
        PhaseBreakdown: phases);

    return Results.Ok(new HistoryResponse(window, ordered, stats));
});

// Shared: read branch + changed-file count for a working directory.
//
// This is polled by the project bar, so it runs whenever the human is doing something else in the
// same repo - including a merge. Three things keep it from getting in the way:
// --no-optional-locks, because a plain `git status` takes index.lock to write back its refreshed
// stat cache, and a status holding that lock is a merge that cannot write its index; stderr drained
// rather than inherited, because refreshing right after a checkout prints a CRLF warning per file
// into Whetstone's own console; and a deadline that kills the process, because a git that never
// exits would otherwise hold whatever it holds forever. Twice on 2026-09-26 a status stuck here held
// a project's lock while a merge ran, and the merge concluded with none of the branch's changes.
static (string branch, int changedFiles, string? error) ReadGitInfo(string projectRoot)
{
    string RunGit(params string[] args)
    {
        using var proc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = projectRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = ProcessText.Utf8,
                StandardErrorEncoding = ProcessText.Utf8,
            }
        };
        proc.StartInfo.ArgumentList.Add("--no-optional-locks");
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.Start();
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(10_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"git {string.Join(' ', args)} did not finish in 10s");
        }
        return stdout.GetAwaiter().GetResult().Trim();
    }

    try
    {
        var branch = RunGit("rev-parse", "--abbrev-ref", "HEAD");
        var statusOutput = RunGit("status", "--porcelain");
        var changedFiles = string.IsNullOrEmpty(statusOutput)
            ? 0
            : statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return (string.IsNullOrEmpty(branch) ? "unknown" : branch, changedFiles, null);
    }
    catch (Exception ex)
    {
        return ("unknown", 0, ex.Message);
    }
}

// GET /api/projects/git — branch + path + changed count for every configured project
app.MapGet("/api/projects/git", () =>
{
    var list = registry.Projects.Select(key =>
    {
        var cfg = registry.GetProjectConfig(key);
        var projectRoot = cfg.ProjectPath;
        var displayName = string.IsNullOrEmpty(cfg.DisplayName) ? key : cfg.DisplayName;
        // repoType so the bar can offer Sync with TFVC beside a TFVC hub's change count.
        if (string.IsNullOrEmpty(projectRoot) || !Directory.Exists(projectRoot))
            return new { key, displayName, branch = "—", changedFiles = 0, projectPath = projectRoot ?? "", repoType = cfg.RepoType };
        var g = ReadGitInfo(projectRoot);
        return new { key, displayName, branch = g.branch, changedFiles = g.changedFiles, projectPath = projectRoot, repoType = cfg.RepoType };
    }).ToList();
    return Results.Ok(list);
});

// PUT /api/improvements/{id}/text — update an item's text
app.MapPut("/api/improvements/{id}/text", async (int id, string? project, HttpRequest request) =>
{
    if (NoProject(project) is { } bad) return bad;
    var improvementsFilePath = ImprovementsFileFor(project);
    if (!File.Exists(improvementsFilePath))
        return Results.NotFound();

    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var payload = JsonSerializer.Deserialize<JsonElement>(body);
    var newText = payload.GetProperty("text").GetString()?.Trim();

    if (string.IsNullOrEmpty(newText))
        return Results.BadRequest(new { error = "Text cannot be empty" });

    var lines = File.ReadAllLines(improvementsFilePath);
    int currentItemId = 0;
    bool found = false;

    for (int i = 0; i < lines.Length; i++)
    {
        var match = checkboxPattern.Match(lines[i].Trim());
        if (match.Success)
        {
            if (currentItemId == id)
            {
                var checkMark = match.Groups[1].Value;
                var category = match.Groups[2].Success && match.Groups[2].Value != ""
                    ? $"[{match.Groups[2].Value}] "
                    : "";
                lines[i] = $"- [{checkMark}] {category}{newText}";
                found = true;
                break;
            }
            currentItemId++;
        }
    }

    if (!found)
        return Results.NotFound();

    File.WriteAllLines(improvementsFilePath, lines);
    return Results.Ok(new { updated = true });
});

// Fallback to index.html for SPA routing (same cache policy: index.html must revalidate).
app.MapFallbackToFile("index.html", spaStaticFiles);

app.Run();
