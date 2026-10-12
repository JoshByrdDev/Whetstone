using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;

namespace Whetstone.Api;

public record StewardAnswerRequest(string? Answer);
public record StewardSaidRequest(string? Text);

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

        // ---- for whatever launches a run -------------------------------------------------

        // Which projects' stewards are owed a run right now. Costs a few reads and no agent.
        app.MapGet("/api/steward/due", async (ProjectRegistry registry, StewardService steward, CancellationToken ct) =>
        {
            var due = new List<object>();
            foreach (var key in registry.Projects)
            {
                var (isDue, reason) = await steward.DueAsync(key, ct);
                if (isDue) due.Add(new { project = key, reason });
            }
            return Results.Ok(due);
        });

        // The process that held a run's lease is gone without ending it.
        app.MapPost("/api/steward/abandon", async (string? project, string? reason, StewardService steward) =>
            Results.Ok(new { released = await steward.AbandonRunAsync(project ?? "", reason ?? "the run's process ended without closing it") }));
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
