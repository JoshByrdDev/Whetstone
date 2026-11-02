using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>
/// The Metrics page's analytics (docs/analytics.md). Every route but <c>refresh</c> only reads the
/// published parquet marts; <c>refresh</c> starts the rebuild as a child process and returns at once.
/// Scope parameters are the same everywhere: <c>days</c> (7, 30, 90, or 0 for all), <c>project</c>
/// (a key, or empty for all) and <c>track</c> (Delivery by default, <c>*</c> for all).
/// </summary>
public static class AnalyticsEndpoints
{
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/analytics/status", (AnalyticsService a) =>
            Results.Ok(new { status = a.GetStatus(), log = a.GetStatus().LastOk == false ? a.LogTail() : null }));

        app.MapPost("/api/analytics/refresh", (AnalyticsService a) =>
        {
            var (started, message) = a.StartRefresh();
            return Results.Ok(new { started, message, status = a.GetStatus() });
        });

        // Everything the page draws on first load, in one round trip: each phase as a whole, its
        // time split, its rework by cause, first-pass QA, the daily trend, and the two marts that
        // only fill in from runs recorded with run tracking.
        app.MapGet("/api/analytics/overview", (AnalyticsService a, int? days, string? project, string? track) =>
        {
            var s = AnalyticsService.Scope.Of(days, project, track);
            return Results.Ok(new
            {
                scope = s,
                status = a.GetStatus(),
                phases = a.Mart("mart_phase_breakdown", s, Where(("dimension", "all"))),
                timeSplit = a.Mart("mart_time_split", s),
                rework = a.Mart("mart_rework", s, orderBy: "phase, runs DESC"),
                firstPass = a.Mart("mart_first_pass_qa", s, orderBy: "dimension, dim_value"),
                daily = a.Mart("mart_daily", s, orderBy: "day"),
                startRebase = a.Mart("mart_start_rebase", s, orderBy: "phase, runs DESC"),
                diffSize = a.Mart("mart_diff_size", s, orderBy: "phase, size_order"),
            });
        });

        // A phase cut by one dimension - provider, model, skill version... - for the "split by" view.
        app.MapGet("/api/analytics/breakdown", (AnalyticsService a, int? days, string? project, string? track,
            string? phase, string? dimension) =>
            Results.Ok(a.Mart("mart_phase_breakdown", AnalyticsService.Scope.Of(days, project, track),
                Where(("phase", phase), ("dimension", dimension ?? "all")), "runs DESC")));

        // Confirmed bugs by what let them through, and the causes traced in the window.
        app.MapGet("/api/analytics/bugs", (AnalyticsService a, int? days, string? project, string? track, string? dimension) =>
        {
            var s = AnalyticsService.Scope.Of(days, project, track);
            return Results.Ok(new
            {
                summary = a.Mart("mart_bugs", s, Where(("dimension", "all"))).FirstOrDefault(),
                split = a.Mart("mart_bugs", s, Where(("dimension", dimension ?? "missed_by")), "bugs DESC, links DESC"),
                links = a.BugLinks(s),
            });
        });

        // Every experiment with its arms, read whole rather than by window.
        app.MapGet("/api/analytics/experiments", (AnalyticsService a, string? project) =>
            Results.Ok(a.Experiments(project)));

        app.MapGet("/api/analytics/tools", (AnalyticsService a, int? days, string? project, string? track, string? phase) =>
            Results.Ok(a.Mart("mart_tool_time", AnalyticsService.Scope.Of(days, project, track),
                Where(("phase", phase)), "total_seconds DESC")));

        app.MapGet("/api/analytics/versions", (AnalyticsService a, int? days, string? project, string? track,
            string? phase, string? kind) =>
            Results.Ok(a.Mart("mart_version_trend", AnalyticsService.Scope.Of(days, project, track),
                Where(("phase", phase), ("kind", kind)), "phase, kind, version_seq")));

        // The runs behind any number: filtered by the same names the marts cut by.
        app.MapGet("/api/analytics/runs", (HttpRequest req, AnalyticsService a, int? days, string? project,
            string? track, string? sort, int? limit) =>
        {
            var filters = AnalyticsService.RunFilters.Keys
                .Where(k => req.Query.ContainsKey(k))
                .ToDictionary(k => k, k => req.Query[k].ToString());
            return Results.Ok(a.Runs(new AnalyticsService.RunQuery(days, project, track, filters, sort, limit ?? 50)));
        });

        app.MapGet("/api/analytics/runs/{id:int}", (AnalyticsService a, int id) =>
            a.Run(id) is { } r ? Results.Ok(new { run = r.Run, calls = r.Calls }) : Results.NotFound());
    }

    private static Dictionary<string, string?> Where(params (string Col, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrEmpty(p.Value)).ToDictionary(p => p.Col, p => p.Value);
}
