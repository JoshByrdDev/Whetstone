using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;

namespace Whetstone.Api;

public record StewardAnswerRequest(string? Answer);
public record StewardSaidRequest(string? Text);
public record StewardModeRequest(string? Mode);

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

        app.MapGet("/api/steward/entries", async (string? project, string? kind, ProjectRegistry registry,
            DashboardDbContext db, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(project)) return Results.BadRequest(new { error = "Name an enabled project with ?project=" });
            var q = db.StewardEntries.AsNoTracking().Where(e => e.ProjectKey == project);
            if (Enum.TryParse<StewardEntryKind>(kind, true, out var k)) q = q.Where(e => e.Kind == k);
            var rows = await q.OrderByDescending(e => e.At).Take(500).ToListAsync(ct);
            var ids = rows.Where(r => r.TicketId is not null).Select(r => r.TicketId!.Value).Distinct().ToList();
            var titles = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Title, ct);
            return Results.Ok(rows.Select(e => new
            {
                e.Id,
                kind = e.Kind.ToString(),
                e.TicketId,
                ticketTitle = e.TicketId is { } id && titles.TryGetValue(id, out var title) ? title : null,
                e.Text,
                e.At,
            }));
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
        q.AskedAt,
        q.Answer,
        q.AnsweredAt,
    };
}
