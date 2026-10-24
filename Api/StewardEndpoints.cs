using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;

namespace Whetstone.Api;

public record StewardAnswerRequest(string? Answer);
public record StewardSaidRequest(string? Text);
public record StewardModeRequest(string? Mode);
public record StewardEntryMark(bool? Handled, string? Release);
public record StewardUnitReleased(bool? Released);
public record StewardReverse(string? Because);
public record StewardDecide(IReadOnlyList<UnitDecisionChoice>? Decisions, bool? Agree, string? Answering);
public record StewardNewPlan(string? Key, string? Name, string? Folder);
/// <summary>A ticket's place in a plan. <c>Unit</c> null takes it out; <c>Plan</c> null is the one being worked.</summary>
public record TicketPlanLink(string? Plan, int? Unit, IReadOnlyList<string>? Outcomes);

/// <summary>
/// The owner's side of a project's steward, and the two calls whatever launches a run needs.
/// <para>
/// Nothing here is a verb the steward itself uses - those are MCP tools (<c>StewardTools</c>).
/// These are how a person reads what their steward did and answers what it asked, which is why
/// answering a question lives on this side and has no tool: the steward can ask, and cannot reply
/// to itself.
/// </para>
/// </summary>
public static class StewardEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The skill a steward's run reads: <c>data/projects/&lt;key&gt;/skills/steward/skill.md</c>.</summary>
    public const string StewardSkill = "steward";

    public static void MapStewardEndpoints(this WebApplication app)
    {
        // Everything the Steward page draws for one project.
        app.MapGet("/api/steward", async (string? project, ProjectRegistry registry, DashboardDbContext db,
            StewardService steward, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(project)) return Results.BadRequest(new { error = "Name an enabled project with ?project=" });
            var cfg = registry.GetProjectConfig(project!);
            var s = await db.StewardStates.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectKey == project, ct);
            var questions = await db.StewardQuestions.AsNoTracking()
                .Where(q => q.ProjectKey == project).OrderBy(q => q.AskedAt).ToListAsync(ct);
            var (due, dueReason) = await steward.DueAsync(project!, ct);

            return Results.Ok(new
            {
                project,
                displayName = cfg.DisplayName,
                mode = cfg.StewardMode.ToString().ToLowerInvariant(),
                modeSetting = cfg.StewardModeSetting.ToString().ToLowerInvariant(),
                // Why full is not on offer, when it is not: said here so the page does not have to know the rule.
                fullUnavailable = cfg.MergeInsteadOfPr && !cfg.IsTfvc ? null
                    : "This project does not deliver by a local merge, so its steward never merges.",
                running = await steward.RunOpenAsync(project!),
                due,
                dueReason,
                lastRunStartedAt = s?.LastRunStartedAt,
                lastRunEndedAt = s?.LastRunEndedAt,
                lastRunSummary = s?.LastRunSummary,
                resting = s?.RestingAtSha is not null,
                memo = s?.Memo ?? "",
                waiting = questions.Where(q => q.Answer is null).Select(Question),
                answered = questions.Where(q => q.Answer is not null).OrderByDescending(q => q.AnsweredAt).Select(Question),
            });
        });

        // The record: the calls it made, what to read before a release, what the owner told it.
        // With ?ticket=, one ticket's lines - which is how a ticket shows what was decided about it.
        app.MapGet("/api/steward/entries", async (string? project, string? kind, int? ticket, ProjectRegistry registry,
            DashboardDbContext db, StewardService steward, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(project)) return Results.BadRequest(new { error = "Name an enabled project with ?project=" });
            var q = db.StewardEntries.AsNoTracking().Where(e => e.ProjectKey == project);
            if (Enum.TryParse<StewardEntryKind>(kind, true, out var k)) q = q.Where(e => e.Kind == k);
            if (ticket is { } only) q = q.Where(e => e.TicketId == only);
            var rows = await q.OrderByDescending(e => e.At).Take(5000).ToListAsync(ct);
            var ids = rows.Where(r => r.TicketId is not null).Select(r => r.TicketId!.Value).Distinct().ToList();
            var tickets = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id))
                .Select(t => new { t.Id, t.Title, t.PlanUnit, t.PlanKey }).ToDictionaryAsync(t => t.Id, ct);
            var goesOutWith = await steward.ReleaseUnitOfNotesAsync(project!, ct);
            return Results.Ok(rows.Select(e =>
            {
                var t = e.TicketId is { } id && tickets.TryGetValue(id, out var found) ? found : null;
                return new
                {
                    e.Id,
                    kind = e.Kind.ToString(),
                    e.TicketId,
                    ticketTitle = t?.Title,
                    // The unit of the plan the line belongs to, by way of its ticket.
                    // For a release note, the unit it will go out with - its ticket's, or the next
                    // to be released when it has none - so a unit's notes here are the ones its
                    // release is held for.
                    unit = goesOutWith.TryGetValue(e.Id, out var with) ? with : t?.PlanUnit,
                    plan = t?.PlanUnit is null ? null : t.PlanKey ?? "",
                    // A note from before notes had kinds is one to know, until its owner says otherwise.
                    release = e.Kind == StewardEntryKind.BeforeRelease ? (e.Release ?? StewardReleaseKind.Note).ToString() : null,
                    e.HandledAt,
                    e.Text,
                    e.At,
                    // A ruling's: what it rests on, and the owner's reversal of it.
                    e.Basis,
                    e.ReversedAt,
                    e.ReversedBecause,
                };
            }));
        });

        // Tick a line off, or say what kind of release note it is. The owner's: there is no tool
        // for either, so a steward cannot clear its own notes out of the way of a release.
        app.MapPut("/api/steward/entries/{id:int}", async (int id, string? project, StewardEntryMark mark,
            StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerMarksEntryAsync(project ?? "", id, mark.Handled, mark.Release, ct);
            return r.Answer == "recorded" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // The owner keeps or reverses one of the steward's rulings. No tool behind either.
        app.MapPost("/api/steward/rulings/{id:int}/keep", async (int id, string? project, StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerKeepsRulingAsync(project ?? "", id, ct);
            return r.Answer == "kept" ? Results.Ok(r) : Results.BadRequest(r);
        });

        app.MapPost("/api/steward/rulings/{id:int}/reverse", async (int id, string? project, StewardReverse body,
            StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerReversesRulingAsync(project ?? "", id, body.Because ?? "", ct);
            return r.Answer == "reversed" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // Sort many release notes at once, { "<id>": "<kind>" }: for a record older than the kinds.
        app.MapPost("/api/steward/entries/sort", async (string? project, Dictionary<int, string> kinds,
            StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerSortsReleaseNotesAsync(project ?? "", kinds, ct);
            return r.Answer == "recorded" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // The plan as its owner reads it: each unit's spec and outcomes, and the document the
        // units are cut from when the project names one.
        // With ?plan=<key>, a plan the project has finished: readable, and worked by nothing.
        app.MapGet("/api/steward/plan", async (string? project, string? plan, ProjectRegistry registry, StewardService steward,
            TicketGitService git, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(project)) return Results.BadRequest(new { error = "Name an enabled project with ?project=" });
            var r = await steward.PlanViewAsync(project!, plan, ct);
            var cfg = registry.GetProjectConfig(project!).Steward;
            var file = (plan is null ? cfg.Plan : cfg.PlanByKey(plan))?.Master ?? "";
            var text = file.Length > 0 ? await git.FileAtBaseAsync(project!, file, ct) : null;
            return Results.Ok(new { r.Answer, r.Why, plan = r.Data, master = text is null ? null : new { file, text } });
        });

        // Agree a draft unit: the commit that has always been what agrees one, made from the board.
        // The owner's, like every route here that a steward's own tools have no counterpart to.
        app.MapPost("/api/steward/plan/units/{unit:int}/agree", async (int unit, string? project, StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerAgreesUnitAsync(project ?? "", unit, ct);
            return r.Answer == "agreed" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // A unit's open decisions, and the owner's answers to them: written into its spec under
        // "Decided", with the unit agreed in the same commit when asked, and the question that
        // asked for them answered. The owner's alone: no steward tool settles a decision.
        app.MapGet("/api/steward/plan/units/{unit:int}/decisions", async (int unit, string? project, StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.UnitDecisionsAsync(project ?? "", unit, ct);
            return r.Answer == "decisions" ? Results.Ok(r) : Results.BadRequest(r);
        });
        app.MapPost("/api/steward/plan/units/{unit:int}/decisions", async (int unit, string? project, StewardDecide body,
            StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerDecidesAsync(project ?? "", unit, body.Decisions ?? Array.Empty<UnitDecisionChoice>(),
                body.Agree ?? false, body.Answering, ct);
            return r.Answer is "agreed" or "decided" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // Say a unit's work has been released, or take that back.
        app.MapPut("/api/steward/plan/units/{unit:int}/released", async (int unit, string? project, StewardUnitReleased body,
            StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerReleasesUnitAsync(project ?? "", unit, body.Released ?? true, ct);
            return r.Answer is "released" or "recorded" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // Start a new plan: its two documents into the repo from the templates, then the project's
        // settings pointed at it. The plan that was being worked moves to pastPlans.
        app.MapPost("/api/steward/plan/new", async (string? project, StewardNewPlan body, ProjectRegistry registry, DashboardPaths paths,
            StewardService steward, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(project)) return Results.BadRequest(new { error = "Name an enabled project with ?project=" });
            var planTemplate = Path.Combine(paths.DataRoot, "plan-templates", "plan.md");
            var unitTemplate = Path.Combine(paths.DataRoot, "plan-templates", "unit.md");
            if (!File.Exists(planTemplate) || !File.Exists(unitTemplate))
                return Results.BadRequest(new { error = $"The plan templates are missing from {Path.GetDirectoryName(planTemplate)}." });

            var r = await steward.OwnerStartsPlanAsync(project!, body.Key, body.Name, body.Folder,
                await File.ReadAllTextAsync(planTemplate, ct), await File.ReadAllTextAsync(unitTemplate, ct), ct);
            if (r.Answer != "started") return Results.BadRequest(r);

            var made = JsonSerializer.SerializeToElement(r.Data, Json);
            var file = paths.ProjectConfigDefaults(project!);
            var failed = await StewardPlanFile.StartAsync(file, made.GetProperty("key").GetString()!, made.GetProperty("name").GetString()!,
                made.GetProperty("units").GetString()!, made.GetProperty("master").GetString()!, ct);
            return failed is null
                ? Results.Ok(new { r.Answer, r.Data, settings = file })
                : Results.BadRequest(new { answer = "documents-only", why = $"The plan's documents are in the repo, but the project's settings were not changed: {failed}" });
        });

        // The plan and how far it is, without a run: the same figures a run is shown.
        app.MapGet("/api/steward/status", async (string? project, StewardService steward, CancellationToken ct) =>
            Results.Ok(await steward.StatusAsync(project ?? "", ct)));

        app.MapPost("/api/steward/questions/{key}/answer", async (string key, string? project, StewardAnswerRequest req,
            StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerAnswersAsync(project ?? "", key, req.Answer ?? "", ct);
            return r.Answer == "recorded" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // A ticket's flags that are the owner's, and clearing one so the steward may merge past it.
        // The owner's: there is no tool behind either, so a steward cannot clear its own way.
        app.MapGet("/api/steward/tickets/{id:int}/flags", async (int id, string? project, StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerFlagsAsync(project ?? "", id, ct);
            return r.Answer == "flags" ? Results.Ok(r) : Results.BadRequest(r);
        });

        app.MapPost("/api/steward/tickets/{id:int}/flags/{flag}/clear", async (int id, string flag, string? project,
            StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerClearsFlagAsync(project ?? "", id, flag, ct);
            return r.Answer == "cleared" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // The owner's link from a ticket to a unit of a plan: set it, change it, or (unit null)
        // take the ticket out of the plan. No tool behind it, like everything else that is the owner's.
        app.MapPut("/api/tickets/{id:int}/plan", async (int id, TicketPlanLink body, StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.LinkTicketAsync(id, body.Plan, body.Unit, body.Outcomes, ct);
            return r.Answer is "linked" or "unchanged" ? Results.Ok(r)
                : r.Answer == "not-found" ? Results.NotFound(r) : Results.BadRequest(r);
        });

        app.MapPost("/api/steward/said", async (string? project, StewardSaidRequest req, StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.OwnerSaysAsync(project ?? "", req.Text ?? "", ct);
            return r.Answer == "recorded" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // The mode: the one switch on a steward, and its owner's alone. Written to the project's
        // machine-local file, never its committed defaults - how far a steward is trusted is a fact
        // about this machine and this week, not about the project.
        app.MapPut("/api/steward/mode", async (string? project, StewardModeRequest req, ProjectRegistry registry,
            DashboardPaths paths, StewardService steward) =>
        {
            if (!registry.IsEnabled(project)) return Results.BadRequest(new { error = "Name an enabled project with ?project=" });
            var name = req.Mode?.Trim() ?? "";
            var mode = StewardConfig.ParseMode(name);
            if (!string.Equals(mode.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "The mode is off, propose, manage or full." });

            var file = paths.ProjectConfig(project!);
            System.Text.Json.Nodes.JsonObject local;
            try
            {
                local = File.Exists(file)
                    ? System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file)) as System.Text.Json.Nodes.JsonObject ?? new()
                    : new();
            }
            catch (JsonException) { return Results.BadRequest(new { error = $"{file} is not valid JSON, so it was left alone." }); }

            local["stewardMode"] = mode.ToString().ToLowerInvariant();
            await File.WriteAllTextAsync(file, local.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

            // Switched on is a reason to look at the board; switched off needs nothing.
            var cfg = registry.GetProjectConfig(project!);
            if (cfg.StewardMode != StewardMode.Off) await steward.MarkDueAsync(project!, $"its mode was set to {cfg.StewardMode.ToString().ToLowerInvariant()}");
            return Results.Ok(new { mode = cfg.StewardMode.ToString().ToLowerInvariant(), modeSetting = cfg.StewardModeSetting.ToString().ToLowerInvariant() });
        });

        // Take in a steward's record that was kept somewhere else. The owner's, and once.
        app.MapPost("/api/steward/import", async (string? project, StewardImport import, StewardService steward, CancellationToken ct) =>
        {
            var r = await steward.ImportAsync(project ?? "", import, ct);
            return r.Answer == "imported" ? Results.Ok(r) : Results.BadRequest(r);
        });

        // ---- for whatever launches a run -------------------------------------------------

        // Which projects' stewards are owed a run right now. Costs a few reads and no agent.
        // Honours the same project filter the claim does (heartbeatProjects, empty = all): a project the
        // fleet is not working is not one whose steward should be filing tickets for it.
        app.MapGet("/api/steward/due", async (ProjectRegistry registry, StewardService steward, CancellationToken ct) =>
        {
            var only = registry.GetGlobalConfig().TryGetValue("heartbeatProjects", out var hp) && hp.ValueKind == JsonValueKind.Array
                ? hp.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                : Array.Empty<string>();
            var due = new List<object>();
            foreach (var key in registry.Projects)
            {
                if (only.Length > 0 && !only.Contains(key)) continue;
                var (isDue, reason) = await steward.DueAsync(key, ct);
                if (isDue) due.Add(new { project = key, reason });
            }
            return Results.Ok(due);
        });

        // A run is about to be launched: stamp the attempt, and hand over what the launcher needs
        // to point an agent at this project. The skill is named whether or not it exists, so the
        // launcher can say which file is missing.
        app.MapPost("/api/steward/launching", async (string? project, ProjectRegistry registry, DashboardPaths paths,
            StewardService steward) =>
        {
            var r = await steward.LaunchingAsync(project ?? "");
            if (r.Answer != "launching") return Results.BadRequest(r);
            var cfg = registry.GetProjectConfig(project!);
            var skill = paths.SkillFile(project!, StewardSkill);
            return Results.Ok(new
            {
                project,
                displayName = cfg.DisplayName,
                mode = cfg.StewardMode.ToString().ToLowerInvariant(),
                projectPath = cfg.ProjectPath,
                skillFile = skill,
                skillExists = File.Exists(skill),
                memoryIndex = cfg.ResolvedMemoryIndex,
                planUnits = cfg.Steward.Plan.UnitsDir,
            });
        });

        // The process that held a run's lease is gone without ending it. With no project named,
        // every project's: an orchestrator starting up has no steward running, so any run still
        // marked open belongs to a process that died with the last one.
        app.MapPost("/api/steward/abandon", async (string? project, string? reason, ProjectRegistry registry, StewardService steward) =>
        {
            var why = reason ?? "the run's process ended without closing it";
            var released = new List<string>();
            foreach (var key in string.IsNullOrWhiteSpace(project) ? registry.Projects : new[] { project })
                if (await steward.AbandonRunAsync(key, why)) released.Add(key);
            return Results.Ok(new { released });
        });
    }

    private static object Question(StewardQuestion q) => new
    {
        q.Key,
        q.Question,
        q.Why,
        options = JsonSerializer.Deserialize<List<BlockOption>>(q.OptionsJson, Json) ?? new(),
        q.HeldUp,
        q.TicketId,
        // The unit and decisions it asks about, when it is answered on the spec's own decisions.
        unit = q.PlanUnit,
        decisions = (q.Decisions ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => int.TryParse(n, out var i) ? i : 0).Where(i => i > 0),
        q.AskedAt,
        q.Answer,
        q.AnsweredAt,
    };
}
