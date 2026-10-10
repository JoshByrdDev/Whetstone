using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Api;

public record GenerateAnalysisRequest(string? Lens);

/// <summary>Enough of an analysis to list it without shipping the whole document.</summary>
/// <param name="HasUserFlow">Whether the run drew the user's path. Reported rather than left
/// implicit: the section is meant to be absent on work no user can see, and an absent section is
/// honest but silent — this is what lets a reader notice it going missing on tickets that plainly
/// have a UI.</param>
/// <param name="DescriptionChanged">The ticket has been re-worded since this was read. Reported
/// separately from <paramref name="Stale"/> because it is a different kind of doubt: stale says the
/// work moved on, this says the REQUIREMENTS did — which is what makes a coverage map a set of
/// rulings against words that are no longer there. False whenever either side is unknown (an
/// analysis written before the hash existed, a tracker that would not answer), because "cannot tell"
/// must not present as "changed".</param>
public record AnalysisMetaDto(
    int Id, string Lens, string PhaseAtGeneration, string Headline,
    string? HeadSha, string? Branch, DateTime GeneratedAt,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens,
    int DurationMs, string? Model, bool Stale, bool HasUserFlow, bool DescriptionChanged);

public record AnalysisDto(AnalysisMetaDto Meta, AnalysisDocument.Doc Document);

public record StudiableTicketDto(
    int Id, string ProjectKey, string Title, string Phase, string? Branch, string? IssueKey,
    int AnalysisCount, DateTime? LatestAnalysisAt, DateTime UpdatedAt);

/// <summary>What this ticket cost the pipeline, summed over its worker runs.</summary>
public record TicketCostDto(int Runs, long InputTokens, long OutputTokens,
    long CacheReadTokens, long CacheCreationTokens, double Minutes);

/// <param name="RepoPath">The HUB checkout, not the ticket's worktree — an editor link should open
/// the file the reader actually works in, not an agent's scratch copy that may be pruned.</param>
/// <param name="IdeUrl">Template with {path} and {line}, for turning a citation into a click.</param>
/// <param name="ImplementationSummary">What the implement phase said it delivered, passed through
/// untouched. The Classroom shows the pipeline's own record next to the analysis of it rather than
/// having the analysis restate it — one is evidence, the other is the reading of that evidence, and
/// a reader is entitled to see both.</param>
/// <param name="QaShots">The screenshots QA captured, with their captions. Same reasoning.</param>
/// <param name="QaShotsWaiver">Why there are none, when QA said so. Shown in their place, because
/// the reader's question is "did anyone look?" and this is the answer to it.</param>
public record ClassroomTicketDto(
    int Id, string ProjectKey, string Title, string Phase, string Track, string? Branch, string? WorktreePath,
    bool Eligible, string? Reason, string? HeadSha, string LearningLabel,
    string RepoPath, string IdeUrl,
    string? ImplementationSummary, IReadOnlyList<QaShotDto> QaShots, string? QaShotsWaiver,
    IReadOnlyList<AnalysisMetaDto> Analyses, IReadOnlyList<JourneyLegDto> Journey, TicketCostDto Cost,
    IReadOnlyList<BounceDto> Bounces, string? IssueKey = null);

/// <summary>
/// The Classroom's API. Read-only over the pipeline: nothing here transitions a ticket, comments on
/// one, or writes anything a worker will later read. The one mutation it owns is its own table.
/// </summary>
public static class AnalysisEndpoints
{
    public static void MapAnalysisEndpoints(this WebApplication app)
    {
        // Which tickets can be studied. Post-implementation only — see TicketAnalysisService.Eligibility.
        app.MapGet("/api/classroom/tickets", async (TicketAnalysisService svc, LinkedIssueResolver resolver, string? project) =>
        {
            var rows = await svc.StudiableAsync(project);
            var resolved = await resolver.ResolveManyAsync(rows.Select(r => r.Ticket));
            return Results.Ok(rows.Select(r => new StudiableTicketDto(
                r.Ticket.Id, r.Ticket.ProjectKey,
                resolved[r.Ticket.Id].Title, r.Ticket.Phase.ToString(), r.Ticket.Branch, r.Ticket.IssueKey,
                r.Count, r.Latest, r.Ticket.UpdatedAt)));
        });

        // Everything the Classroom needs about one ticket: its analyses (metadata only), the trip it
        // took, what it cost, and whether the newest analysis still describes the current commit.
        app.MapGet("/api/classroom/tickets/{id:int}", async (
            int id, TicketAnalysisService svc, TicketService tickets, LinkedIssueResolver resolver,
            ProjectRegistry registry, DashboardDbContext db) =>
        {
            var t = await tickets.GetAsync(id);
            if (t == null) return Results.NotFound();

            var (eligible, reason) = TicketAnalysisService.Eligibility(t);
            var cfg = registry.GetProjectConfig(t.ProjectKey);

            // Only shell out to git when there is something to compare against. On an unstudied
            // ticket the answer would be thrown away.
            var analyses = await svc.ForTicketAsync(id);
            var headSha = analyses.Count > 0 && Directory.Exists(AnalysisRunner.ReadDir(t, cfg))
                ? AnalysisRunner.HeadSha(AnalysisRunner.ReadDir(t, cfg))
                : null;
            var treeSha = headSha is null ? null : AnalysisRunner.TreeSha(AnalysisRunner.ReadDir(t, cfg));

            var resolved = await resolver.ResolveAsync(t);
            // One call, two answers, and the resolver's own 60-second cache is what keeps a board
            // render from asking the tracker once per analysis.
            var descriptionHash = resolved.Unavailable
                ? null
                : DescriptionSegmenter.Fingerprint(resolved.Description);

            return Results.Ok(new ClassroomTicketDto(
                t.Id, t.ProjectKey, resolved.Title, t.Phase.ToString(), t.Track.ToString(), t.Branch, t.WorktreePath,
                eligible, reason, headSha, cfg.ResolvedLearningLabel,
                cfg.ProjectPath.Replace('\\', '/').TrimEnd('/'), cfg.ResolvedIdeUrl,
                t.ImplementationSummary,
                t.QaShots.OrderBy(s => s.Order).Select(s => QaShotDto.From(t.Id, s)).ToList(),
                t.QaShotsWaiver,
                analyses.Select(a => Meta(a, t, headSha, descriptionHash, treeSha)).ToList(),
                await TicketJourney.ForTicketAsync(db, id),
                await CostAsync(db, id),
                await TicketJourney.BouncesAsync(db, id),
                t.IssueKey));
        });

        app.MapGet("/api/classroom/analyses/{id:int}", async (
            int id, TicketAnalysisService svc, TicketService tickets, ProjectRegistry registry,
            LinkedIssueResolver resolver) =>
        {
            var a = await svc.GetAsync(id);
            if (a == null) return Results.NotFound();

            var doc = AnalysisDocument.Deserialize(a.BlocksJson);
            if (doc == null) return Results.Problem("This analysis could not be read back.", statusCode: 500);

            var t = await tickets.GetAsync(a.TicketId);
            if (t == null) return Results.Ok(new AnalysisDto(Meta(a, null, null), doc));

            var dir = AnalysisRunner.ReadDir(t, registry.GetProjectConfig(t.ProjectKey));
            var headSha = Directory.Exists(dir) ? AnalysisRunner.HeadSha(dir) : null;
            var treeSha = headSha is null ? null : AnalysisRunner.TreeSha(dir);

            // The one view where re-wording actually matters: this is the response carrying the
            // coverage map, and a map is a set of rulings against words that may since have changed.
            return Results.Ok(new AnalysisDto(
                Meta(a, t, headSha, await CurrentDescriptionHashAsync(resolver, t), treeSha), doc));
        });

        // One file's diff, for a stop the reader opened on a user-flow diagram.
        //
        // Pinned to the analysis's own HeadSha rather than to the branch tip: the diagram was drawn
        // against that commit, and showing today's diff beside a week-old reading of it would put
        // two things on screen that quietly disagree.
        app.MapGet("/api/classroom/analyses/{id:int}/diff", async (
            int id, string? path, TicketAnalysisService svc, TicketService tickets,
            TicketGitService git, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { message = "No file was asked for." });

            var a = await svc.GetAsync(id);
            if (a == null) return Results.NotFound();

            var t = await tickets.GetAsync(a.TicketId);
            if (t == null) return Results.NotFound();

            var head = a.HeadSha ?? a.Branch ?? t.Branch ?? "";
            return Results.Ok(await git.FileDiffAsync(t.ProjectKey, head, path, ct));
        });

        // Join the queue. Named "generate" still because that is what the button says and what the
        // caller wants; whether it starts now or in ten minutes is the queue's business. Nothing is
        // checked about the ticket here on purpose — see AnalysisQueue, which re-asks every such
        // question at the moment the item's turn comes.
        app.MapPost("/api/classroom/tickets/{id:int}/generate", (
            int id, GenerateAnalysisRequest? req, AnalysisQueue queue) =>
        {
            if (!Enum.TryParse<AnalysisLens>(req?.Lens ?? nameof(AnalysisLens.Full), true, out var lens))
                return Results.BadRequest(new { message = $"Unknown lens '{req?.Lens}'." });

            var (ok, message) = queue.Enqueue(id, lens);
            return ok ? Results.Ok(new { message }) : Results.BadRequest(new { message });
        });

        // Drop one item. Ticket plus lens, because that pair is what identifies a queue entry — and
        // it addresses the running item and a waiting one identically, which is right: the row a
        // person clicked "x" on can turn from one into the other while they reach for it.
        app.MapDelete("/api/classroom/queue/{ticketId:int}/{lens}", (
            int ticketId, string lens, AnalysisQueue queue) =>
        {
            if (!Enum.TryParse<AnalysisLens>(lens, true, out var parsed))
                return Results.BadRequest(new { message = $"Unknown lens '{lens}'." });

            return queue.Cancel(ticketId, parsed)
                ? Results.Ok(new { cancelled = true })
                : Results.NotFound(new { message = "That is not queued or running." });
        });

        // Empty the waiting line, leaving whatever is running alone.
        app.MapDelete("/api/classroom/queue", (AnalysisQueue queue) =>
            Results.Ok(new { cleared = queue.ClearPending() }));

        app.MapGet("/api/classroom/status", (AnalysisQueue queue) => Results.Ok(queue.GetStatus()));

        app.MapDelete("/api/classroom/analyses/{id:int}", async (int id, TicketAnalysisService svc) =>
            await svc.DeleteAsync(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());
    }

    private static AnalysisMetaDto Meta(TicketAnalysis a, Ticket? t, string? headSha,
        string? descriptionHash = null, string? treeSha = null) => new(
        a.Id, a.Lens.ToString(), a.PhaseAtGeneration.ToString(), a.Headline,
        a.HeadSha, a.Branch, a.GeneratedAt,
        a.InputTokens, a.OutputTokens, a.CacheReadTokens, a.CacheCreationTokens,
        a.DurationMs, a.Model,
        t != null && TicketAnalysisService.IsStale(a, t, headSha, treeSha),
        AnalysisDocument.HasKind(a.BlocksJson, "userFlow"),
        // Both sides have to be known. An analysis from before the hash existed, or a tracker that
        // could not be reached, is a question we cannot answer — and answering it "changed" would
        // put a warning on every analysis on the board for as long as Jira is down.
        a.DescriptionHash is { Length: > 0 } stored
            && descriptionHash is { Length: > 0 } current
            && stored != current);

    /// <summary>
    /// The ticket's requirement text as it stands right now, hashed for comparison against what an
    /// analysis read. Null when the tracker could not be reached — <see cref="LinkedIssueResolver"/>
    /// falls back to a last-known value there, and hashing that would compare an analysis against a
    /// cache rather than against the ticket.
    /// </summary>
    private static async Task<string?> CurrentDescriptionHashAsync(LinkedIssueResolver resolver, Ticket t)
    {
        var resolved = await resolver.ResolveAsync(t);
        return resolved.Unavailable ? null : DescriptionSegmenter.Fingerprint(resolved.Description);
    }

    private static async Task<TicketCostDto> CostAsync(DashboardDbContext db, int ticketId)
    {
        var runs = await db.CycleRuns.AsNoTracking().Where(c => c.TicketId == ticketId).ToListAsync();
        return new TicketCostDto(
            runs.Count,
            runs.Sum(r => (long)(r.InputTokens ?? 0)),
            runs.Sum(r => (long)(r.OutputTokens ?? 0)),
            runs.Sum(r => (long)(r.CacheReadTokens ?? 0)),
            runs.Sum(r => (long)(r.CacheCreationTokens ?? 0)),
            runs.Sum(r => r.DurationSeconds) / 60.0);
    }
}
