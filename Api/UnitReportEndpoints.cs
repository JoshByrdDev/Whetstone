using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Whetstone.Services.UnitReports;
using Whetstone.Services.Walkthrough;

namespace Whetstone.Api;

public record UnitReportMetaDto(
    int Id, string Headline, DateTime GeneratedAt, string BaseRef, string FromSha, string ToSha,
    int RangeCommits, int OwnCommits, string? Model,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheCreationTokens, int DurationMs);

public record UnitReportGapDto(int TicketId, string Kind, string Why);

/// <param name="Shots">The report's own copies, as the Classroom's shot shape: <c>order</c> is the
/// number the document cites.</param>
/// <param name="Stale">Why this no longer describes the unit, in sentences. Empty when it still does.</param>
public record UnitReportDto(
    UnitReportMetaDto Meta, AnalysisDocument.Doc Document, IReadOnlyList<QaShotDto> Shots,
    IReadOnlyList<UnitReportGapDto> Gaps, IReadOnlyList<string> Stale);

/// <summary>One ticket of the unit as it stands now, for the part of the report the server writes.</summary>
public record UnitReportTicketDto(
    int Id, string Title, string Phase, IReadOnlyList<string> Outcomes, DateTime? LandedAt,
    IReadOnlyList<BounceDto> Bounces, TicketCostDto Cost);

/// <param name="Queue"><c>queued</c>, <c>running</c> or null: where a report for this unit is now.</param>
/// <param name="QueueNote">The hold reason while queued, the run's last step while running.</param>
/// <param name="LastError">Why the last attempt since the latest report wrote nothing.</param>
public record UnitReportViewDto(
    UnitReportDto? Latest, IReadOnlyList<UnitReportMetaDto> History, IReadOnlyList<UnitReportTicketDto> Tickets,
    string RepoPath, string IdeUrl, string? Queue, string? QueueNote, string? LastError);

/// <summary>
/// A unit report: read it, ask for one, and the files it shows. The owner's reading - none of this is
/// on the steward's MCP door, so nothing a report says can become a reason for a merge.
/// </summary>
public static class UnitReportEndpoints
{
    public static void MapUnitReportEndpoints(this WebApplication app)
    {
        // The latest report on one unit, with its history, the unit's tickets as they stand, and
        // where any report for it is in the queue.
        app.MapGet("/api/steward/plan/units/{unit:int}/report", async (
            int unit, string project, string? plan, DashboardDbContext db, ProjectRegistry registry,
            TicketGitService git, AnalysisQueue queue, CancellationToken ct) =>
        {
            var subject = new UnitSubject(project, plan ?? "", unit);
            var reports = await db.UnitReports.AsNoTracking()
                .Where(r => r.ProjectKey == project && r.Plan == subject.Plan && r.Unit == unit)
                .OrderByDescending(r => r.GeneratedAt).ToListAsync(ct);
            var tickets = await UnitTicketsAsync(db, subject, ct);
            var latest = reports.FirstOrDefault();

            var (current, pending, recent) = queue.Snapshot();
            string? state = null, note = null;
            if (current is { Unit: { } cu } && cu == subject) (state, note) = ("running", current.Activity);
            else if (pending.FirstOrDefault(p => p.Unit == subject && p.WalkthroughOf is null) is { } p) (state, note) = ("queued", p.HoldReason);
            var failed = recent.FirstOrDefault(r => r.Unit == subject);
            var lastError = failed is { Error: { } e } && (latest is null || failed.FinishedAt > latest.GeneratedAt) ? e : null;

            var cfg = registry.GetProjectConfig(project);
            return Results.Ok(new UnitReportViewDto(
                latest is null ? null : await ReportAsync(latest, db, git, tickets, ct),
                reports.Select(Meta).ToList(),
                tickets,
                cfg.ProjectPath.Replace('\\', '/').TrimEnd('/'), cfg.ResolvedIdeUrl,
                state, note, lastError));
        });

        // One report from the history.
        app.MapGet("/api/unit-reports/{id:int}", async (int id, DashboardDbContext db, TicketGitService git, CancellationToken ct) =>
        {
            var r = await db.UnitReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return Results.NotFound();
            var tickets = await UnitTicketsAsync(db, new UnitSubject(r.ProjectKey, r.Plan, r.Unit), ct);
            return Results.Ok(await ReportAsync(r, db, git, tickets, ct));
        });

        // Join the queue. Asked for by a person, so it runs over an unfinished unit and says so.
        app.MapPost("/api/steward/plan/units/{unit:int}/report", (int unit, string project, string? plan, AnalysisQueue queue) =>
        {
            var (ok, message) = queue.EnqueueUnit(new UnitSubject(project, plan ?? "", unit), manual: true);
            return ok ? Results.Ok(new { message }) : Results.BadRequest(new { message });
        });

        app.MapDelete("/api/steward/plan/units/{unit:int}/report", (int unit, string project, string? plan, AnalysisQueue queue) =>
            queue.CancelUnit(new UnitSubject(project, plan ?? "", unit))
                ? Results.Ok(new { cancelled = true })
                : Results.NotFound(new { message = "No report on that unit is queued or running." }));

        app.MapDelete("/api/unit-reports/{id:int}", async (int id, DashboardDbContext db, DashboardPaths paths) =>
        {
            var r = await db.UnitReports.FirstOrDefaultAsync(x => x.Id == id);
            if (r is null) return Results.NotFound();
            db.UnitReports.Remove(r);
            await db.SaveChangesAsync();
            try { Directory.Delete(Path.GetDirectoryName(paths.UnitReportShotsDir(id))!, recursive: true); } catch { /* already gone */ }
            return Results.Ok(new { deleted = true });
        });

        // A screenshot the report cites, from its own copy. Never rewritten once the report exists.
        app.MapGet("/api/unit-reports/{id:int}/shots/{n:int}", async (int id, int n, DashboardDbContext db, DashboardPaths paths, HttpContext http) =>
        {
            var r = await db.UnitReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (r is null || UnitReportRunner.ShotsOf(r).FirstOrDefault(s => s.N == n) is not { } shot) return Results.NotFound();
            var path = Path.Combine(paths.UnitReportShotsDir(id), shot.File);
            if (!File.Exists(path)) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(path, QaShotStore.ContentType(shot.File));
        });

        // One file's change across the unit's range: what a flow stop or a code citation opens.
        app.MapGet("/api/unit-reports/{id:int}/diff", async (int id, string? path, DashboardDbContext db, TicketGitService git, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { message = "No file was asked for." });
            var r = await db.UnitReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return Results.NotFound();
            return Results.Ok(await git.FileDiffAsync(r.ProjectKey, r.ToSha, path, ct, fromRef: r.FromSha));
        });

        app.MapGet("/api/unit-reports/{id:int}/walkthrough", async (int id, DashboardDbContext db) =>
        {
            if (!await db.UnitReports.AnyAsync(r => r.Id == id)) return Results.NotFound();
            var w = await db.AnalysisWalkthroughs.AsNoTracking().FirstOrDefaultAsync(x => x.UnitReportId == id);
            return Results.Ok(WalkthroughEndpoints.Dto(w));
        });

        app.MapPost("/api/unit-reports/{id:int}/walkthrough", async (int id, DashboardDbContext db, AnalysisQueue queue) =>
        {
            var r = await db.UnitReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (r is null) return Results.NotFound();
            var (ok, message) = queue.EnqueueUnitWalkthrough(new UnitSubject(r.ProjectKey, r.Plan, r.Unit), id);
            return ok ? Results.Accepted(value: new { message }) : Results.Conflict(new { message });
        });
    }

    private static UnitReportMetaDto Meta(UnitReport r) => new(
        r.Id, r.Headline, r.GeneratedAt, r.BaseRef, r.FromSha, r.ToSha, r.RangeCommits, r.OwnCommits, r.Model,
        r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheCreationTokens, r.DurationMs);

    private record Read(int TicketId, int? AnalysisId);

    private static async Task<UnitReportDto> ReportAsync(UnitReport r, DashboardDbContext db, TicketGitService git,
        IReadOnlyList<UnitReportTicketDto> now, CancellationToken ct)
    {
        var doc = AnalysisDocument.Deserialize(r.BlocksJson) ?? new AnalysisDocument.Doc(r.Headline, Array.Empty<AnalysisDocument.Section>());
        var shots = UnitReportRunner.ShotsOf(r)
            .Select(s => new QaShotDto(s.N, $"#{s.TicketId}: {s.Caption}", WalkthroughService.UnitShotUrl(r.Id, s.N), s.Focus))
            .ToList();
        var gaps = Json<List<UnitReportGapDto>>(r.GapsJson) ?? new();
        return new UnitReportDto(Meta(r), doc, shots, gaps, await StaleAsync(r, db, git, now, ct));
    }

    /// <summary>
    /// What has happened to the unit since the report was written, in words. Only what changes what
    /// the report would say: a ticket added to the unit, one of its tickets merged again or re-studied,
    /// and the base moving under it - the last said as a count, because a busy base is not by itself
    /// a reason to rewrite anything.
    /// </summary>
    private static async Task<IReadOnlyList<string>> StaleAsync(UnitReport r, DashboardDbContext db, TicketGitService git,
        IReadOnlyList<UnitReportTicketDto> now, CancellationToken ct)
    {
        var reasons = new List<string>();
        var read = (Json<List<Read>>(r.ReadJson) ?? new()).ToDictionary(x => x.TicketId, x => x.AnalysisId);

        var added = now.Where(t => !read.ContainsKey(t.Id)).Select(t => $"#{t.Id}").ToList();
        if (added.Count > 0) reasons.Add($"Filed against this unit since: {string.Join(", ", added)}.");

        var landed = now.Where(t => t.LandedAt > r.GeneratedAt).Select(t => $"#{t.Id}").ToList();
        if (landed.Count > 0) reasons.Add($"Landed since: {string.Join(", ", landed)}.");

        var ids = read.Keys.ToList();
        var newer = await db.TicketAnalyses.AsNoTracking()
            .Where(a => ids.Contains(a.TicketId) && a.GeneratedAt > r.GeneratedAt)
            .Select(a => a.TicketId).Distinct().ToListAsync(ct);
        if (newer.Count > 0) reasons.Add($"Studied again since: {string.Join(", ", newer.OrderBy(i => i).Select(i => $"#{i}"))}.");

        if (await git.BaseSinceAsync(r.ProjectKey, r.ToSha, ct) is { Count: > 0 } since)
            reasons.Add($"The base has gained {since.Count} commit{(since.Count == 1 ? "" : "s")} since.");

        return reasons;
    }

    /// <summary>The unit's tickets as they stand: what the server writes of the report itself.</summary>
    private static async Task<IReadOnlyList<UnitReportTicketDto>> UnitTicketsAsync(DashboardDbContext db, UnitSubject s, CancellationToken ct)
    {
        var tickets = (await db.Tickets.AsNoTracking()
                .Where(t => t.ProjectKey == s.Project && t.PlanUnit == s.Unit).ToListAsync(ct))
            .Where(t => (t.PlanKey ?? "") == s.Plan).OrderBy(t => t.Id).ToList();
        var ids = tickets.Select(t => t.Id).ToList();
        var done = (await db.PhaseTransitions.AsNoTracking()
                .Where(p => ids.Contains(p.TicketId) && p.To == TicketPhase.Done)
                .Select(p => new { p.TicketId, p.At }).ToListAsync(ct))
            .GroupBy(p => p.TicketId).ToDictionary(g => g.Key, g => g.Max(p => p.At));

        var list = new List<UnitReportTicketDto>();
        foreach (var t in tickets)
            list.Add(new UnitReportTicketDto(t.Id, t.Title, t.Phase.ToString(), StewardProgress.OutcomesOf(t),
                t.MergedAt ?? (t.Phase == TicketPhase.Done && done.TryGetValue(t.Id, out var at) ? at : null),
                await TicketJourney.BouncesAsync(db, t.Id), await AnalysisEndpoints.CostAsync(db, t.Id)));
        return list;
    }

    private static T? Json<T>(string json) where T : class
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
