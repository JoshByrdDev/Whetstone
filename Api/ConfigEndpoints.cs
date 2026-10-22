using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>
/// Global settings (dashboard URL, notifications, heartbeat timing, off-hours) and project
/// management (list / add / edit / unregister / re-register). Tracker + QA are per-project;
/// everything else is one global set. See <see cref="ProjectRegistry"/>.
/// </summary>
public static class ConfigEndpoints
{
    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    private static readonly string[] GlobalKeys =
    {
        "dashboardUrl", "baseHeartbeatMinutes",
        "heartbeatIdleMultiplier", "heartbeatMaxMinutes", "offHoursStart", "offHoursEnd",
        // Where notifications go, which ones are sent, and what each channel needs. Every channel's
        // fields are kept whichever is selected, so switching back does not mean re-pasting a URL.
        "notificationChannel", "notificationEvents", "slackWebhookUrl",
        "ntfyServerUrl", "ntfyTopic", "ntfyToken",
        // Reliability guards + the two eligibility filters for the one global heartbeat: which
        // projects it may claim from, and which priority bands. Both empty = no restriction.
        "heartbeatMaxAttempts", "heartbeatWorkerTimeoutMinutes", "heartbeatWorkerStallMinutes", "heartbeatProjects", "heartbeatPriorities",
        // How many agents the one orchestrator runs at once (1..HeartbeatManager.MaxWorkers), and how
        // many copies of a parallelStacks project's stack this machine runs at once (qaLeases).
        "heartbeatWorkers", "qaLeases",
        // Whether the Heartbeat tab shows its Worker Hosts section. Display only: off hides it and
        // changes nothing about hosts already set up.
        "showWorkerHosts",
        // Branch enforcement: base branch new feature branches fork from, and their name prefix.
        "baseBranch", "branchPrefix",
        // Which CLI, model and effort each kind of run takes, one entry per role. See AgentRoles.
        "agentRoles",
        // The dashboard's own Claude login, for the rate-limit meter. See ClaudeUsageService.
        "claudeOauthToken",
        // The Claude logins the fleet can be switched between. Which one is in force is not here:
        // that is switched from the usage chip, through /api/usage/account. See ClaudeAccounts.
        "claudeAccounts",
    };

    /// <summary>One entry in a provider's model dropdown, as the Settings panel reads it.</summary>
    private sealed record ModelChoice(string value, string label);

    /// <summary>
    /// One provider, as the Settings panel reads it. Records rather than anonymous objects because
    /// the defaults are READ from this table as well as served from it - see <see cref="Provider"/>.
    /// Property names are lower-case on purpose: they are the JSON that goes over the wire.
    /// </summary>
    private sealed record AgentProviderOption(
        string key,
        string label,
        string executable,
        string defaultWorkerModel,
        string defaultReflectionModel,
        bool supportsReasoningEffort,
        bool reportsUsage,
        bool hasGuard,
        ModelChoice[] workerModels,
        ModelChoice[] reflectionModels,
        // Shown in Settings under the provider picker; null for one with nothing to warn about.
        string? note = null);

    /// <summary>
    /// What each provider is called, and what it should run when nothing has been chosen. Kept here
    /// rather than in the client so the two halves of the app cannot disagree about which models
    /// exist; the same table is mirrored in <c>data/providers.ps1</c> for the heartbeat's own runs.
    /// </summary>
    private static readonly AgentProviderOption[] AgentProviders =
    {
        new(
            key: "claude",
            label: "Anthropic Claude Code",
            executable: "claude",
            defaultWorkerModel: ClaudeAgentProvider.DefaultModel,
            defaultReflectionModel: ClaudeAgentProvider.DefaultReflectionModel,
            // --effort. Claude Code's own default moves with the model (Opus 5: high, Opus 5.5:
            // medium), so an unset field would mean a different thing after every model change.
            supportsReasoningEffort: true,
            reportsUsage: true,
            // The only provider whose runs pass through data/worker-guard.ps1. The others have no
            // hook the guard can sit in, so the prompt is all that keeps them local.
            hasGuard: true,
            // The family aliases first: Claude Code resolves each to the newest model in it, so these
            // never go stale. The pinned ids below them stay for a run that has to be reproducible.
            workerModels: new[]
            {
                new ModelChoice("opus", "Latest Opus"),
                new ModelChoice("sonnet", "Latest Sonnet (cheaper)"),
                new ModelChoice("claude-opus-5-5", "Opus 5.5"),
                new ModelChoice("claude-opus-5", "Opus 5"),
                new ModelChoice("claude-sonnet-5-5", "Sonnet 5.5 (cheaper)"),
                new ModelChoice("claude-fable-5-1", "Fable 5.1 (most capable)"),
            },
            reflectionModels: new[]
            {
                new ModelChoice("haiku", "Latest Haiku"),
                new ModelChoice("sonnet", "Latest Sonnet"),
            }),
        new(
            key: "codex",
            label: "OpenAI Codex",
            executable: "codex",
            defaultWorkerModel: CodexAgentProvider.DefaultModel,
            defaultReflectionModel: CodexAgentProvider.DefaultReflectionModel,
            supportsReasoningEffort: true,
            // No cross-provider equivalent of the rate-limit meter, so the chip hides itself.
            reportsUsage: false,
            hasGuard: false,
            workerModels: new[]
            {
                new ModelChoice("gpt-5.6-sol", "GPT-5.6 Sol (flagship)"),
                new ModelChoice("gpt-5.6-terra", "GPT-5.6 Terra (balanced)"),
                new ModelChoice("gpt-5.6-luna", "GPT-5.6 Luna (fast)"),
            },
            reflectionModels: new[]
            {
                new ModelChoice("gpt-5.6-luna", "GPT-5.6 Luna"),
                new ModelChoice("gpt-5.6-terra", "GPT-5.6 Terra"),
            }),
        new(
            key: "gemini",
            label: "Google Gemini CLI",
            executable: "gemini",
            // The routing ALIASES rather than dated slugs. Google's concrete ids carry a version and
            // a -preview suffix that both move, and a default naming one goes stale silently: the
            // run fails on a model name the CLI no longer serves. `pro` and `flash` do not move.
            defaultWorkerModel: GeminiAgentProvider.DefaultModel,
            defaultReflectionModel: GeminiAgentProvider.DefaultReflectionModel,
            // Thinking is a per-model property here rather than a CLI dial, so there is nothing for
            // the Reasoning Effort field to set and it hides itself.
            supportsReasoningEffort: false,
            reportsUsage: false,
            hasGuard: false,
            workerModels: new[]
            {
                new ModelChoice("pro", "Pro (most capable)"),
                new ModelChoice("auto", "Auto (routes by task complexity)"),
                new ModelChoice("flash", "Flash (fast)"),
                new ModelChoice("gemini-3.1-pro-preview", "Gemini 3.1 Pro (preview, pinned)"),
            },
            reflectionModels: new[]
            {
                new ModelChoice("flash", "Flash"),
                new ModelChoice("flash-lite", "Flash-Lite (cheapest)"),
                new ModelChoice("pro", "Pro"),
            },
            note: "This provider was written from Google's documentation and has never been run from "
                + "here. Everything it needs is in place - a per-run config directory, the read-only "
                + "tool allowlist, the event mapping - but expect to fix something the first time "
                + "you select it."),
    };

    /// <summary>
    /// What each role is called on screen, which group it sits in, and what choosing for it buys.
    /// Pipeline phases are keyed by SKILL name, which is what the worker has in hand when it
    /// resolves its role.
    /// </summary>
    private static readonly Dictionary<string, (string Label, string Group, string Description)> RoleLabelling = new()
    {
        ["speculate"] = ("Speculate", "pipeline",
            "Writes the plan every later phase implements. A small share of the runs, and the cheapest " +
            "place to catch a wrong assumption."),
        ["implement"] = ("Implement", "pipeline", "Writes the code against that plan. Most of a ticket's runs and tokens."),
        ["qa-test"] = ("QA test", "pipeline", "Drives the running app in a browser and files what it finds."),
        ["refine"] = ("Refine", "pipeline", "Answers the QA findings and tidies the branch before hand-off."),
        ["review-pr"] = ("Peer review", "pipeline",
            "Reads someone else's branch, drives it in a browser, and stages feedback for you to send."),
        [AgentRoles.Steward] = ("Steward", "fleet",
            "Stands in for a ticket's owner between runs, on projects that switch it on. Reads and decides; changes no file."),
        [AgentRoles.Reflection] = ("Reflection", "fleet", "Writes the lessons-learned note after each worker run."),
        [AgentRoles.Analysis] = ("Classroom", "dashboard", "A ticket's teaching analysis and walkthrough, and a unit's report."),
        [AgentRoles.Applier] = ("Improvement applier", "dashboard", "Applies the improvements you approved to a project's skills, notes and prompts, for you to review."),
    };

    /// <summary>
    /// Every role, in the order Settings lists them. runsCommands marks the heartbeat runs that act
    /// on a worktree, which is where a missing command guard matters. Reflection runs locked down
    /// (read-only, or with approvals on), so it cannot act, and the dashboard's two never had the
    /// guard on any provider. Derived from <see cref="AgentRoles.All"/>, so a skill added to the
    /// pipeline appears under its own bare name: a visible gap, not a silent one.
    /// </summary>
    private static readonly object[] RoleCatalog = AgentRoles.All
        .Select(role => RoleLabelling.TryGetValue(role, out var t)
            ? new { key = role, label = t.Label, group = t.Group, description = t.Description, reflection = role == AgentRoles.Reflection, runsCommands = t.Group == "pipeline" || role == AgentRoles.Steward }
            : new { key = role, label = role, group = "pipeline", description = "", reflection = false, runsCommands = true })
        .ToArray();

    /// <summary>
    /// Every notification the system knows how to send, and what to call it on screen. The Settings
    /// panel renders its checkbox list straight from this, and the heartbeat matches on the same
    /// keys - so a notification exists exactly when it appears in both halves, and the UI can never
    /// offer a tickbox for something nothing sends.
    /// </summary>
    private static readonly object[] NotificationEvents =
    {
        new
        {
            key = "heartbeat_sleep",
            label = "Heartbeat started sleeping",
            description = "Once, when the fleet finishes its work and the board goes quiet. Waking " +
                          "to an empty board and going straight back to sleep does not send another.",
        },
        // The two below are sent by the dashboard (StewardService, through data/notify-send.ps1),
        // not by the heartbeat script - the same notifier, reached from the other side.
        new
        {
            key = "steward_question",
            label = "A steward needs a decision",
            description = "Once per question, when a project's steward asks its owner something only " +
                          "they can decide. Carries the question, the steward's own pick, and what is held up.",
        },
        new
        {
            key = "steward_reminder",
            label = "A steward is still waiting",
            description = "At most once a day per project, while a question asked on an earlier day " +
                          "is still unanswered.",
        },
    };

    /// <summary>One place a notification can be delivered, as the Settings panel reads it.</summary>
    private sealed record NotificationChannelOption(string key, string label, string description);

    /// <summary>
    /// Where the heartbeat can send its notifications. The Settings panel renders its picker from
    /// this; <c>data/notifiers.ps1</c> holds the matching sender for each key, and falls back to
    /// Slack for a key it does not know - so does <see cref="NormalizeChannel"/>. Adding a channel
    /// is one entry here, one there, and the fields it needs in the panel.
    /// </summary>
    private static readonly NotificationChannelOption[] NotificationChannels =
    {
        new("slack", "Slack",
            "Posts to a Slack incoming webhook. Create one at api.slack.com/apps under Incoming Webhooks."),
        new("ntfy", "ntfy",
            "Pushes to an ntfy topic - the ntfy phone app or ntfy.sh in a browser, subscribed to the " +
            "same topic, receives it. On the public server anyone who knows the topic name can read " +
            "it, so pick one nobody would guess, or use your own server and an access token."),
    };

    /// <summary>The stored channel as one the catalogue knows; Slack, the incumbent, otherwise.</summary>
    private static string NormalizeChannel(string? stored)
    {
        var key = (stored ?? "").Trim().ToLowerInvariant();
        return NotificationChannels.Any(c => c.key == key) ? key : "slack";
    }

    /// <summary>
    /// The ticked notification events. <c>notificationEvents</c> is the key now; <c>slackEvents</c>
    /// is what a config.json written before there was a choice of channel still holds, and the
    /// local file is asked for either before the committed defaults are - otherwise a user who had
    /// unticked everything under the old name would find it ticked again by the defaults' new one.
    /// The heartbeat resolves it the same way (Get-NotificationConfig).
    /// </summary>
    private static string[] TickedEvents(DashboardPaths paths, Dictionary<string, JsonElement> merged)
    {
        var local = LoadObject(paths.GlobalConfigPath);
        foreach (var key in new[] { "notificationEvents", "slackEvents" })
            if (local[key] is JsonArray arr)
                return arr.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                          .Where(s => s is not null).Select(s => s!).ToArray();
        return StrArray(merged, "notificationEvents");
    }

    public static void MapConfigEndpoints(this WebApplication app)
    {
        // ---- Global settings ------------------------------------------------

        app.MapGet("/api/config", (ProjectRegistry registry, DashboardPaths paths) =>
        {
            var g = registry.GetGlobalConfig();
            return Results.Ok(new
            {
                dashboardUrl = Str(g, "dashboardUrl", "http://localhost:5338"),
                // Where notifications go, and each channel's own settings.
                notificationChannel = NormalizeChannel(Str(g, "notificationChannel", "slack")),
                // Whether a webhook is saved, NOT the webhook: it is the whole credential for posting
                // to the channel, and this API has no authentication. Same rule as claudeOauthTokenSet.
                slackWebhookUrlSet = !string.IsNullOrWhiteSpace(Str(g, "slackWebhookUrl", "")),
                // Blank means the public server, which is what the heartbeat sends to.
                ntfyServerUrl = Str(g, "ntfyServerUrl", ""),
                ntfyTopic = Str(g, "ntfyTopic", ""),
                ntfyTokenSet = !string.IsNullOrWhiteSpace(Str(g, "ntfyToken", "")),
                // Which notifications are ticked, by key from the catalogue below. Empty is a real
                // state and not a missing one: a channel that is configured but silent, which is why
                // this is stored rather than inferred from whether a channel is set up.
                notificationEvents = TickedEvents(paths, g),
                baseHeartbeatMinutes = Num(g, "baseHeartbeatMinutes", 3),
                heartbeatIdleMultiplier = Num(g, "heartbeatIdleMultiplier", 2),
                heartbeatMaxMinutes = Num(g, "heartbeatMaxMinutes", 60),
                offHoursStart = Str(g, "offHoursStart", "17:00"),
                offHoursEnd = Str(g, "offHoursEnd", "06:30"),
                heartbeatMaxAttempts = Num(g, "heartbeatMaxAttempts", 3),
                heartbeatWorkerTimeoutMinutes = Num(g, "heartbeatWorkerTimeoutMinutes", 120),
                heartbeatWorkerStallMinutes = Num(g, "heartbeatWorkerStallMinutes", 30),
                // How many agents work at once. Clamped on the way out as well as on the way in, so
                // a hand-edited config file can never put the UI and the orchestrator at odds.
                heartbeatWorkers = Math.Clamp((int)Num(g, "heartbeatWorkers", HeartbeatManager.DefaultWorkers), 1, HeartbeatManager.MaxWorkers),
                maxHeartbeatWorkers = HeartbeatManager.MaxWorkers,
                // Copies of one project's stack this machine runs at once, for projects that set
                // parallelStacks. Clamped on the way out for the same reason heartbeatWorkers is.
                qaLeases = ResourceLeaseService.QaLeases(g),
                maxQaLeases = ResourceLeaseService.MaxQaLeases,
                // Eligible projects for the heartbeat; empty = all (including any added later).
                heartbeatProjects = StrArray(g, "heartbeatProjects"),
                // Eligible priority bands (TicketPriority names); empty = all five.
                heartbeatPriorities = StrArray(g, "heartbeatPriorities"),
                // Branch enforcement: feature branches fork from baseBranch and are named branchPrefix + key.
                baseBranch = Str(g, "baseBranch", "main"),
                branchPrefix = Str(g, "branchPrefix", ""),
                // Every role as it will actually run, with each gap filled - including a config
                // written before roles existed, read through the settings that came before. So the
                // panel shows what runs, never a blank the run would fill differently.
                agentRoles = AgentRoles.All.ToDictionary(r => r, r =>
                {
                    var role = AgentRoles.Resolve(g, r);
                    return new { provider = role.Provider, model = role.Model, effort = role.Effort };
                }),
                // The roles, labelled and grouped, so a stale bundle cannot offer one nothing runs.
                agentRoleCatalog = RoleCatalog,
                agentEfforts = AgentRoles.Efforts,
                // Whether the dashboard has a Claude login of its own, NOT the token itself: the
                // Settings panel only needs to know whether to offer "set" or "replace", and a
                // long-lived subscription token has no business being in a GET body every time
                // somebody opens a tab.
                claudeOauthTokenSet = !string.IsNullOrWhiteSpace(Str(g, "claudeOauthToken", "")),
                // The accounts beyond the CLI's own login, and whether each has ever been signed in
                // to - the one thing that has to be true before switching to it does any good.
                claudeAccounts = ClaudeAccounts.All(g).Where(a => a.ConfigDir is not null)
                    .Select(a => new { name = a.Name, configDir = a.ConfigDir, signedIn = ClaudeAccounts.HasLogin(a) }),
                // The catalogue the Settings panel renders from.
                agentProviders = AgentProviders,
                notificationEventCatalog = NotificationEvents,
                notificationChannels = NotificationChannels,
                showWorkerHosts = ShowWorkerHosts(g),
                // While remote work is in use the section cannot be hidden; Settings locks the box.
                workerHostsInUse = WorkerHostsInUse(g),
            });
        });

        app.MapPut("/api/config", async (HttpRequest request, DashboardPaths paths, ProjectRegistry registry) =>
        {
            var incoming = await ReadObject(request);
            if (incoming is null) return Results.BadRequest(new { error = "Invalid JSON body" });

            // Preserve any unknown keys already in the local override.
            var target = LoadObject(paths.GlobalConfigPath);
            var before = Snapshot(target);
            foreach (var key in GlobalKeys)
                if (incoming.ContainsKey(key))
                    target[key] = incoming[key]?.DeepClone();

            // The legacy name goes once the new one is written, so the file never carries two
            // answers to "which notifications" with only precedence deciding which one is live.
            if (target.ContainsKey("notificationEvents")) target.Remove("slackEvents");
            if (target.ContainsKey("notificationChannel"))
                target["notificationChannel"] = NormalizeChannel(target["notificationChannel"]?.GetValue<string>());

            // The agent count is the one setting that can hurt the machine it runs on, so it is
            // clamped where it is STORED rather than only where it is read. The orchestrator and the
            // dashboard read this file independently; a value only one of them corrected would put
            // them permanently out of step about how many agents exist.
            if (target["heartbeatWorkers"] is JsonNode workers)
            {
                try
                {
                    var n = (int)workers.GetValue<double>();
                    target["heartbeatWorkers"] = JsonValue.Create(Math.Clamp(n, 1, HeartbeatManager.MaxWorkers));
                }
                catch { target.Remove("heartbeatWorkers"); }   // not a number — fall back to the default
            }
            // The Worker Hosts section cannot be hidden while remote work is in use: hiding it would
            // put the switch that stops that work out of sight. Settings locks the box; this is the
            // same rule for a body that ignores the lock.
            if (target["showWorkerHosts"] is JsonValue show && show.TryGetValue<bool>(out var on) && !on
                && WorkerHostsInUse(registry.GetGlobalConfig()))
                target["showWorkerHosts"] = true;

            if (target["qaLeases"] is JsonNode qa)
            {
                try
                {
                    var n = (int)qa.GetValue<double>();
                    target["qaLeases"] = JsonValue.Create(Math.Clamp(n, 1, ResourceLeaseService.MaxQaLeases));
                }
                catch { target.Remove("qaLeases"); }
            }

            // Every role is stored whole, and the settings it replaced go once it is. A role left
            // out of the body keeps what it runs today, read through those older settings before
            // they are removed, so a partial body cannot silently move a role to the defaults.
            if (incoming.ContainsKey("agentRoles"))
            {
                var current = registry.GetGlobalConfig();
                var sent = incoming["agentRoles"] as JsonObject;
                var roles = new JsonObject();
                foreach (var role in AgentRoles.All)
                {
                    var was = AgentRoles.Resolve(current, role);
                    var entry = sent?[role] as JsonObject;
                    var provider = entry?["provider"] is JsonValue p && p.TryGetValue<string>(out var ps)
                        ? AgentRoles.NormalizeProvider(ps) : was.Provider;
                    // A blank model, or one that is unmistakably another provider's, would fail
                    // every run on a model name; it becomes the provider's default instead.
                    var model = entry?["model"] is JsonValue m && m.TryGetValue<string>(out var ms) ? ms.Trim() : null;
                    if (entry is null) model = was.Model;
                    if (string.IsNullOrEmpty(model) || !AgentModels.BelongsTo(provider, model))
                        model = AgentRoles.DefaultModel(provider, role);
                    var effort = entry?["effort"] is JsonValue e && e.TryGetValue<string>(out var es) ? es.Trim() : null;
                    if (entry is null) effort = was.Effort;
                    if (!AgentRoles.Efforts.Contains(effort)) effort = AgentProviderFactory.DefaultEffort;
                    roles[role] = new JsonObject { ["provider"] = provider, ["model"] = model, ["effort"] = effort };
                }
                target["agentRoles"] = roles;
                foreach (var key in AgentRoles.LegacyKeys) target.Remove(key);
            }

            // Stored with every directory spelled out, and not at all when there are none. Removing
            // the account the fleet is on takes the choice with it, so the file never names a login
            // that no longer exists - both readers would fall back to the default, but silently.
            if (target.ContainsKey("claudeAccounts"))
            {
                var accounts = ClaudeAccounts.Normalize(target["claudeAccounts"]);
                var active = target["claudeAccount"] is JsonValue chosen && chosen.TryGetValue<string>(out var name)
                    ? name : null;
                if (active is not null && !accounts.Any(a => string.Equals(
                        a?["name"]?.GetValue<string>(), active, StringComparison.OrdinalIgnoreCase)))
                    target.Remove("claudeAccount");
                if (accounts.Count > 0) target["claudeAccounts"] = accounts;
                else target.Remove("claudeAccounts");
            }

            SaveObject(paths.GlobalConfigPath, target, before);
            return Results.Ok(new { saved = true });
        });

        // Send one test notification using the channel settings in the body - the Settings form's
        // current values, saved or not, so a topic can be tried before it is committed. The send
        // itself is done by data/notify-test.ps1 through notifiers.ps1, the same code the heartbeat
        // runs, rather than by a C# copy of it that could pass while the real one fails.
        app.MapPost("/api/notifications/test", async (HttpRequest request, DashboardPaths paths, ProjectRegistry registry) =>
        {
            var incoming = await ReadObject(request);
            if (incoming is null) return Results.BadRequest(new { error = "Invalid JSON body" });

            var script = Path.Combine(paths.DataRoot, "notify-test.ps1");
            if (!File.Exists(script))
                return Results.Ok(new { ok = false, message = $"Script not found: {script}" });

            // Only the channel's own fields go into the file: it holds a webhook or a token for the
            // few seconds it exists, and nothing else on the form has any business being in it.
            var settings = new JsonObject();
            foreach (var key in new[] { "notificationChannel", "slackWebhookUrl", "ntfyServerUrl", "ntfyTopic", "ntfyToken" })
                if (incoming[key] is JsonNode v) settings[key] = v.DeepClone();
            // The webhook and the token are never served to the form, so it sends one only when one
            // was typed. Absent means "the one already saved" - the same reading the PUT gives it.
            var stored = registry.GetGlobalConfig();
            foreach (var key in new[] { "slackWebhookUrl", "ntfyToken" })
                if (!settings.ContainsKey(key) && Str(stored, key, "") is { Length: > 0 } saved) settings[key] = saved;

            var temp = Path.Combine(Path.GetTempPath(), $"whetstone-notify-{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllTextAsync(temp, settings.ToJsonString());
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // notify-test.ps1 switches its console to UTF-8 to match.
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };
                foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                                             "-ConfigFile", temp, "-DefaultsFile", paths.GlobalConfigDefaultsPath })
                    psi.ArgumentList.Add(arg);

                using var proc = System.Diagnostics.Process.Start(psi)!;
                // Every channel times out at 10s; this is that plus PowerShell's own start-up.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
                try { await proc.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return Results.Ok(new { ok = false, message = "Timed out waiting for the channel to answer." });
                }

                var verdict = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
                if (verdict is not null)
                {
                    try
                    {
                        var v = JsonNode.Parse(verdict)!;
                        return Results.Ok(new { ok = v["ok"]?.GetValue<bool>() ?? false, message = v["message"]?.GetValue<string>() ?? "" });
                    }
                    catch (JsonException) { }
                }
                var err = (await stderr).Trim();
                return Results.Ok(new { ok = false, message = err.Length > 0 ? err : "The test script said nothing." });
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        });

        // ---- Project management --------------------------------------------

        // Full editable detail for every project, including unregistered ones.
        app.MapGet("/api/projects/manage", async (ProjectRegistry registry, DashboardPaths paths, DashboardDbContext db) =>
        {
            var open = await EnabledTracks.OpenByTrackAsync(db);
            var list = registry.AllProjects.Select(key => BuildDetail(key, registry, paths, open)).ToList();
            return Results.Ok(new { projects = list });
        });

        // Create a new project (registration only — skills + heartbeat are set up separately).
        app.MapPost("/api/projects", async (ProjectUpsertRequest req, ProjectRegistry registry, DashboardPaths paths, DashboardDbContext db, QaIsolation isolation) =>
        {
            var key = req.Key?.Trim().ToLowerInvariant() ?? "";
            if (!KeyPattern.IsMatch(key))
                return Results.BadRequest(new { error = "Key must be lowercase letters, numbers, dashes or underscores." });
            if (registry.AllProjects.Contains(key))
                return Results.BadRequest(new { error = $"Project '{key}' already exists." });
            if (EscapesQaCopy(key, req, paths, isolation) is { } escape) return escape;

            Directory.CreateDirectory(paths.ProjectDir(key));
            WriteProjectFiles(key, req, paths);
            // Lay down the required skills + operational files so the project is immediately
            // workable by the heartbeat (generic templates the user can then customise).
            ProjectScaffolder.ScaffoldMissing(paths, key, req.DisplayName ?? key);
            return Results.Ok(BuildDetail(key, registry, paths, await EnabledTracks.OpenByTrackAsync(db)));
        });

        // Copy any missing required skills / operational files from the templates into a project
        // (additive — never overwrites a customised skill). Returns the updated detail.
        app.MapPost("/api/projects/{key}/scaffold-skills", async (string key, ProjectRegistry registry, DashboardPaths paths, DashboardDbContext db) =>
        {
            if (!registry.AllProjects.Contains(key))
                return Results.NotFound(new { error = $"Unknown project '{key}'." });
            var displayName = registry.GetMergedConfig(key).TryGetValue("displayName", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? key : key;
            var created = ProjectScaffolder.ScaffoldMissing(paths, key, displayName);
            return Results.Ok(new { created, project = BuildDetail(key, registry, paths, await EnabledTracks.OpenByTrackAsync(db)) });
        });

        // Update an existing project's per-project settings (path, tracker, QA, display, prefix).
        app.MapPut("/api/projects/{key}", async (string key, ProjectUpsertRequest req, ProjectRegistry registry, DashboardPaths paths, DashboardDbContext db, QaIsolation isolation) =>
        {
            if (!registry.AllProjects.Contains(key))
                return Results.NotFound(new { error = $"Unknown project '{key}'." });
            if (EscapesQaCopy(key, req, paths, isolation) is { } escape) return escape;
            // The one setting on this form that can strand work, so it is the one the server checks
            // rather than trusting the checkbox to have been disabled. See EnabledTracks.RefusalAsync.
            if (await EnabledTracks.RefusalAsync(
                    key, registry.GetProjectConfig(key).Tracks, EnabledTracks.Parse(req.Tracks), db) is { } refusal)
                return Results.BadRequest(new { error = refusal });
            WriteProjectFiles(key, req, paths);
            return Results.Ok(BuildDetail(key, registry, paths, await EnabledTracks.OpenByTrackAsync(db)));
        });

        // Unregister (reversible — leaves the data folder on disk).
        app.MapPost("/api/projects/{key}/disable", async (string key, ProjectRegistry registry, DashboardPaths paths, DashboardDbContext db) =>
        {
            if (!registry.AllProjects.Contains(key))
                return Results.NotFound(new { error = $"Unknown project '{key}'." });
            File.WriteAllText(paths.DisabledMarker(key),
                "Unregistered from the dashboard. Delete this file (or re-enable in Settings) to restore.\n");
            return Results.Ok(BuildDetail(key, registry, paths, await EnabledTracks.OpenByTrackAsync(db)));
        });

        // Re-register a previously unregistered project.
        app.MapPost("/api/projects/{key}/enable", async (string key, ProjectRegistry registry, DashboardPaths paths, DashboardDbContext db) =>
        {
            if (!registry.AllProjects.Contains(key))
                return Results.NotFound(new { error = $"Unknown project '{key}'." });
            var marker = paths.DisabledMarker(key);
            if (File.Exists(marker)) File.Delete(marker);
            return Results.Ok(BuildDetail(key, registry, paths, await EnabledTracks.OpenByTrackAsync(db)));
        });

        // Native Windows folder picker — pops a shell dialog on the machine running the
        // server (the local dashboard) and returns the chosen absolute path.
        app.MapPost("/api/system/pick-folder", async (HttpRequest request) =>
        {
            if (!OperatingSystem.IsWindows())
                return Results.BadRequest(new { error = "Folder picker is only available on Windows." });

            string? initial = null;
            if (request.ContentLength is > 0)
            {
                var body = await ReadObject(request);
                if (body is not null && body.TryGetPropertyValue("initialPath", out var ip))
                    initial = ip?.GetValue<string>();
            }

            var path = FolderPicker.Pick(initial);
            return path is null ? Results.Ok(new { cancelled = true }) : Results.Ok(new { path });
        });

    }

    // ---- helpers -----------------------------------------------------------

    private static object BuildDetail(string key, ProjectRegistry registry, DashboardPaths paths,
        IReadOnlyDictionary<string, int> openByTrack)
    {
        var cfg = registry.GetMergedConfig(key);
        var typed = registry.GetProjectConfig(key);
        // Read through the typed config rather than off the raw JSON, so a project still carrying
        // the legacy "jira" block is presented as what it actually is - a Jira-backed tracker -
        // instead of showing an empty form that would wipe it on the next save.
        var tracker = Services.Tracker.TrackerConfig.From(cfg);
        var qa = cfg.TryGetValue("qaLogin", out var q) && q.ValueKind == JsonValueKind.Object ? q : default;
        return new
        {
            key,
            displayName = Str(cfg, "displayName", key),
            projectPath = Str(cfg, "projectPath", ""),
            // Empty = inherit the global baseBranch (default "main").
            baseBranch = Str(cfg, "baseBranch", ""),
            // Empty = inherit the global branchPrefix.
            branchPrefix = Str(cfg, "branchPrefix", ""),
            // Which pipelines this project runs, so the board knows which lanes to draw and the New
            // Ticket form knows which tracks to offer. Read through the typed config so a malformed
            // or missing list arrives here as delivery-only rather than as an empty set of
            // checkboxes the form would round-trip straight back into the file.
            tracks = typed.Tracks.Select(t => t.ToString()).ToArray(),
            skipClassroomAnalysisByDefault = typed.SkipClassroomAnalysisByDefault,
            mergeInsteadOfPr = typed.MergeInsteadOfPr,
            // Every track that exists, so the form can render a checkbox for one this project does
            // not have on. Served rather than hard-coded in the client for the same reason the agent
            // provider catalogue is: the two halves of the app must not be able to disagree.
            allTracks = Enum.GetValues<TicketTrack>().Select(t => t.ToString()).ToArray(),
            // How much unfinished work each track is carrying here, so the settings form can grey
            // out a checkbox that cannot be unticked yet and say why. The server refuses the same
            // change anyway (see EnabledTracks.RefusalAsync); this is only what stops the reader
            // finding that out by having a save fail.
            openTickets = Enum.GetValues<TicketTrack>()
                .ToDictionary(t => t.ToString(), t => openByTrack.GetValueOrDefault(EnabledTracks.Key(key, t), 0)),
            // Agent worktrees: where they live (empty = derived beside the repo) and what a fresh
            // one needs before it can build — see ProjectConfig.ResolvedWorktreeRoot.
            worktreeRoot = Str(cfg, "worktreeRoot", ""),
            resolvedWorktreeRoot = typed.ResolvedWorktreeRoot,
            worktreeSetup = StrArray(cfg, "worktreeSetup"),
            worktreeCopyFiles = StrArray(cfg, "worktreeCopyFiles"),
            disabled = paths.IsDisabled(key),
            // Required-skill readiness — drives the "set up skills" affordance in Settings.
            skills = ProjectScaffolder.SkillStatuses(paths, key)
                .Select(s => new { name = s.Name, present = s.Present }),
            skillsReady = ProjectScaffolder.SkillsReady(paths, key),
            tracker = new
            {
                kind = Services.Tracker.TrackerConfig.KindToString(tracker.Kind),
                baseUrl = tracker.BaseUrl,
                email = tracker.Email,
                // Whether a token is saved, NOT the token, for the same reason as claudeOauthTokenSet:
                // this API has no authentication, and anything that can reach it - a worker's curl
                // to localhost included - would otherwise be handed the tracker login in plain text.
                tokenSet = !string.IsNullOrWhiteSpace(tracker.Token),
                project = tracker.Project,
                // The EFFECTIVE lists, with the provider defaults filled in. The settings form round-
                // trips whatever it is given, so handing it a null would either crash the field or
                // have a no-op save silently write "nothing blocks" - and showing a team the values
                // actually in force is the more useful answer anyway.
                blockingLinkTypes = tracker.EffectiveBlockingLinkTypes,
                doneStates = tracker.EffectiveDoneStates,
            },
            // What each provider's two vocabulary settings default to, so the form can reset them
            // when the tracker is switched. Served rather than hard-coded in the client for the same
            // reason the agent-provider catalogue above is: the two halves of the app must not be
            // able to disagree about it. Without this, switching a project from Jira to Azure Boards
            // wrote Jira's ["Blocks"] into the Azure config, where it matches no relation at all -
            // safe, silently wrong, and indistinguishable in the file from a deliberate choice.
            trackerDefaults = new[] { Services.Tracker.TrackerKind.Jira, Services.Tracker.TrackerKind.AzureDevOps }
                .ToDictionary(
                    Services.Tracker.TrackerConfig.KindToString,
                    k => new
                    {
                        blockingLinkTypes = new Services.Tracker.TrackerConfig { Kind = k }.EffectiveBlockingLinkTypes,
                        doneStates = new Services.Tracker.TrackerConfig { Kind = k }.EffectiveDoneStates,
                    }),
            qaApiPort = Num(cfg, "qaApiPort", 53536),
            qaWebPort = Num(cfg, "qaWebPort", 53537),
            // The username is served; the password is reported as set or not, like the tracker token.
            qaLogin = new
            {
                username = StrEl(qa, "username"),
                passwordSet = !string.IsNullOrWhiteSpace(StrEl(qa, "password")),
            },
        };
    }

    /// <summary>
    /// A QA copy registers only projects that live inside its own data root - placeholder repos made
    /// for it - so nothing it is asked to do can land on a real repository. Null when this one may.
    /// </summary>
    private static IResult? EscapesQaCopy(string key, ProjectUpsertRequest req, DashboardPaths paths, QaIsolation isolation)
    {
        if (!isolation.On) return null;
        var cfg = new ProjectConfig { Name = key, ProjectPath = req.ProjectPath?.Trim() ?? "", WorktreeRoot = req.WorktreeRoot?.Trim() ?? "" };
        return QaIsolation.Escapes(cfg, paths.DataRoot) is { } why
            ? Results.BadRequest(new { error = $"This is an isolated QA copy ({QaIsolation.EnvVar}): a project here must live inside its data root, and {why}." })
            : null;
    }

    private static void WriteProjectFiles(string key, ProjectUpsertRequest req, DashboardPaths paths)
    {
        // Committed, non-secret defaults. Snapshotted before the edits so an untouched file is left
        // exactly as its author formatted it — this one is tracked, and carries the long
        // `_comment_*` fields explaining why each setting is what it is.
        var defaults = LoadObject(paths.ProjectConfigDefaults(key));
        var defaultsBefore = Snapshot(defaults);
        if (req.DisplayName is not null) defaults["displayName"] = req.DisplayName.Trim();
        if (req.BaseBranch is not null) defaults["baseBranch"] = req.BaseBranch.Trim();
        if (req.BranchPrefix is not null) defaults["branchPrefix"] = req.BranchPrefix.Trim();
        // Which pipelines this project runs. A property of the team's work rather than of this
        // machine, so it rides in the committed defaults beside displayName. Normalised on the way
        // in as well as on the way out, so a hand-typed name cannot sit in the file looking like a
        // setting - the same rule the per-phase models get.
        if (req.Tracks is not null)
        {
            // Never written empty. A project with no track at all can hold no ticket and draws a
            // board with no lanes on it, and the create path does not go through the refusal below
            // that says so - so the floor is enforced here too, where every save passes.
            var tracks = EnabledTracks.Parse(req.Tracks)!;
            if (tracks.Length == 0) tracks = EnabledTracks.Default;
            defaults["tracks"] = ToJsonArray(tracks.Select(t => t.ToString()));
        }
        // Worktree bootstrap is a property of the repo, not the machine, so it is shareable.
        if (req.WorktreeSetup is not null) defaults["worktreeSetup"] = ToJsonArray(req.WorktreeSetup);
        if (req.WorktreeCopyFiles is not null) defaults["worktreeCopyFiles"] = ToJsonArray(req.WorktreeCopyFiles);
        if (req.QaApiPort is not null) defaults["qaApiPort"] = req.QaApiPort.Value;
        if (req.QaWebPort is not null) defaults["qaWebPort"] = req.QaWebPort.Value;
        // How much the team wants the independent analysis before refinement - a property of the
        // project's work, not this machine, so it is committed beside tracks.
        if (req.SkipClassroomAnalysisByDefault is not null)
            defaults["skipClassroomAnalysisByDefault"] = req.SkipClassroomAnalysisByDefault.Value;
        // How the team delivers - by PR or by merging to the base itself. Theirs, not this machine's.
        if (req.MergeInsteadOfPr is not null) defaults["mergeInsteadOfPr"] = req.MergeInsteadOfPr.Value;
        SaveObject(paths.ProjectConfigDefaults(key), defaults, defaultsBefore);

        // Local, gitignored — paths + secrets. Preserve any extra keys (assignedMarker, custom fields).
        var local = LoadObject(paths.ProjectConfig(key));
        var localBefore = Snapshot(local);
        if (req.ProjectPath is not null) local["projectPath"] = req.ProjectPath.Trim();
        // A machine path, like projectPath — belongs with it, not in the committed defaults.
        if (req.WorktreeRoot is not null) local["worktreeRoot"] = req.WorktreeRoot.Trim();

        if (req.Tracker is not null)
        {
            // Preserve whatever else is already in the block - blockingLinkTypes and doneStates are
            // routinely hand-edited, and a save from the settings form must not silently drop them.
            var tracker = local["tracker"]?.AsObject() ?? new JsonObject();
            tracker["kind"] = req.Tracker.Kind ?? "none";
            tracker["baseUrl"] = req.Tracker.BaseUrl ?? "";
            tracker["email"] = req.Tracker.Email ?? "";
            // Write-only: the GET never serves it, so a save that did not type one sends none, and
            // that keeps the stored token. An explicit empty string clears it.
            if (req.Tracker.Token is not null) tracker["token"] = req.Tracker.Token;
            tracker["project"] = req.Tracker.Project ?? "";
            if (req.Tracker.BlockingLinkTypes is not null)
                tracker["blockingLinkTypes"] = ToJsonArray(req.Tracker.BlockingLinkTypes);
            if (req.Tracker.DoneStates is not null)
                tracker["doneStates"] = ToJsonArray(req.Tracker.DoneStates);
            local["tracker"] = tracker;

            // The legacy block is removed once a tracker block is written. Leaving both would give
            // the project two sets of credentials with only TrackerConfig.From's precedence deciding
            // which one is live - and the losing half would sit there looking authoritative to the
            // next person who opened the file.
            local.Remove("jira");
        }
        if (req.QaLogin is not null)
        {
            var qa = local["qaLogin"]?.AsObject() ?? new JsonObject();
            qa["username"] = req.QaLogin.Username ?? "";
            // Write-only, as the tracker token above: absent keeps it, empty clears it.
            if (req.QaLogin.Password is not null) qa["password"] = req.QaLogin.Password;
            local["qaLogin"] = qa;
        }
        SaveObject(paths.ProjectConfig(key), local, localBefore);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values.Select(x => x?.Trim()).Where(x => !string.IsNullOrEmpty(x)))
            arr.Add(v);
        return arr;
    }

    private static async Task<JsonObject?> ReadObject(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();
        try { return JsonNode.Parse(body)?.AsObject(); }
        catch (JsonException) { return null; }
    }

    internal static JsonObject LoadObject(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    /// <summary>
    /// How a config file is written back.
    /// <para>
    /// The relaxed encoder is the point. <c>System.Text.Json</c>'s default escapes anything that
    /// could be dangerous in an HTML document - an apostrophe becomes <c>'</c>, an ampersand
    /// <c>&</c>, a plus <c>+</c>. These files are never served to a browser; they are
    /// checked into git and read by people, and <c>project.defaults.json</c> in particular carries
    /// long <c>_comment_*</c> fields explaining why each setting is what it is. Saving one project
    /// from Settings was turning "the repo's durable notes" into "the repo's durable notes"
    /// across the whole file.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Write a config file, but only when its CONTENT changed.
    /// <para>
    /// The comparison is against <paramref name="original"/> — the same object as it was loaded,
    /// serialised the same way — rather than against the file's own text, because these files are
    /// hand-formatted and the writer is not. <c>project.defaults.json</c> keeps its arrays inline
    /// and its line endings LF; re-serialising it produces different bytes for identical content
    /// every single time. Comparing text would therefore never match, and a save that touched only
    /// this project's credentials would still reformat a tracked, hand-written document — burying
    /// the real change in a whole-file diff.
    /// </para>
    /// </summary>
    internal static void SaveObject(string path, JsonObject obj, string? original = null)
    {
        var json = obj.ToJsonString(WriteOptions);
        if (original is not null && json == original) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>A loaded object's serialised form, to hand back to <see cref="SaveObject"/> so it
    /// can tell a real edit from a no-op.</summary>
    internal static string Snapshot(JsonObject obj) => obj.ToJsonString(WriteOptions);

    /// <summary>
    /// Whether the Heartbeat tab shows its Worker Hosts section. Off unless set: most boards run on
    /// one machine and never lend work out, and the section is a lot of page for a feature not in use.
    /// Always on while remote work is in use, so the switch that stops it can never be out of sight.
    /// </summary>
    internal static bool ShowWorkerHosts(IReadOnlyDictionary<string, JsonElement> cfg) =>
        WorkerHostsInUse(cfg) || (cfg.TryGetValue("showWorkerHosts", out var v) && v.ValueKind == JsonValueKind.True);

    /// <summary>
    /// Remote work is switched on and there is a worker host for it to reach. The switch alone is
    /// not enough: it reads as on until someone turns it off, on every board, hosts or none.
    /// </summary>
    internal static bool WorkerHostsInUse(IReadOnlyDictionary<string, JsonElement> cfg) =>
        WorkerHostConfig.RemoteEnabled(cfg) && WorkerHostConfig.Read(cfg).Count > 0;

    private static string Str(Dictionary<string, JsonElement> cfg, string key, string fallback) =>
        cfg.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static double Num(Dictionary<string, JsonElement> cfg, string key, double fallback) =>
        cfg.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static string[] StrArray(Dictionary<string, JsonElement> cfg, string key) =>
        cfg.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : Array.Empty<string>();

    private static string StrEl(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}

public record ProjectUpsertRequest(
    string? Key,
    string? DisplayName,
    string? ProjectPath,
    string? BaseBranch,
    string? BranchPrefix,
    string? WorktreeRoot,
    string[]? WorktreeSetup,
    string[]? WorktreeCopyFiles,
    TrackerDto? Tracker,
    int? QaApiPort,
    int? QaWebPort,
    QaLoginDto? QaLogin,
    // TicketTrack names this project runs. Null leaves the stored list alone; defaulted so the
    // create form, which has no such field, still binds.
    string[]? Tracks = null,
    // What a new ticket's "skip classroom analysis" box starts as. Null leaves it alone.
    bool? SkipClassroomAnalysisByDefault = null,
    // Deliver by merging to the base branch instead of raising a PR. Null leaves it alone.
    bool? MergeInsteadOfPr = null);

/// <param name="Kind">"jira", "azureDevOps", or "none".</param>
/// <param name="BaseUrl">The Jira site, or the Azure DevOps organisation URL.</param>
/// <param name="Email">Jira only — the account the API token belongs to.</param>
/// <param name="Token">Jira API token, or Azure DevOps personal access token. Write-only: null
/// keeps the stored one, an empty string clears it.</param>
/// <param name="Project">Azure DevOps only — the team project.</param>
public record TrackerDto(
    string? Kind, string? BaseUrl, string? Email, string? Token, string? Project,
    string[]? BlockingLinkTypes = null, string[]? DoneStates = null);
public record QaLoginDto(string? Username, string? Password);
