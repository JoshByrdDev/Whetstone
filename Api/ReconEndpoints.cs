using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>The owner's Not now: a suggestion's key, and optionally the date it may come back by.</summary>
public record ReconDismissRequest(string? Key, DateTime? Until = null);

/// <summary>The owner taking a Not now back.</summary>
public record ReconUndismissRequest(string? Key);

/// <summary>The owner asking for directions to be drafted for one suggestion.</summary>
public record ReconDraftRequest(string? Key);

/// <summary>
/// The Recon tab (docs/recon-tab-plan.md, "Routes"): where to point the next scout. Reads go
/// through <see cref="ReconService"/> to the published marts, filtered and never aggregated, with
/// the two live filters applied on the board's own tables. Dismiss and undismiss are owner actions:
/// REST only, with no MCP tool behind them, like the verdict. No MCP tool reads any of this either -
/// a worker has no use for it, and the steward takes no part in Discovery.
/// <para>
/// QA isolation: none of these reach past the data folder but the draft, an agent run, which is in
/// <see cref="QaIsolation"/>'s table. The rest read the copy's own marts and database and write
/// local tables; the
/// refresh behind the marts is the Metrics page's, and on a QA copy its git reads are of the
/// copy's placeholder projects.
/// </para>
/// </summary>
public static class ReconEndpoints
{
    public static void MapReconEndpoints(this WebApplication app)
    {
        // Projects with a discovery block (none: the tab is not shown), and the analytics status the
        // page needs to say when Recon was built and to refresh it when stale.
        app.MapGet("/api/recon/projects", (ReconService r, AnalyticsService a) =>
        {
            var status = a.GetStatus();
            return Results.Ok(new { projects = r.Projects(), status, log = status.LastOk == false ? a.LogTail() : null });
        });

        app.MapGet("/api/recon/{project}/suggestions", (string project, ReconService r, CancellationToken ct) =>
            Run(async () => Results.Ok(await r.SuggestionsAsync(project, DateTime.UtcNow, ct))));

        app.MapGet("/api/recon/{project}/coverage", (string project, ReconService r) =>
            Run(() => Task.FromResult(Results.Ok(new { cells = r.Coverage(project) }))));

        app.MapGet("/api/recon/{project}/areas/{area}", (string project, string area, ReconService r) =>
            Run(() => Task.FromResult(Results.Ok(r.Area(project, area)))));

        app.MapGet("/api/recon/{project}/yield", (string project, int? days, ReconService r) =>
            Run(() => Task.FromResult(Results.Ok(r.Yield(project, days)))));

        app.MapPost("/api/recon/{project}/dismiss", (string project, ReconDismissRequest req, ReconService r, CancellationToken ct) =>
            Run(async () => Results.Ok(await r.DismissAsync(project, req.Key, req.Until, DateTime.UtcNow, ct))));

        // Drafted directions (ReconDrafter): the last draft for a key, and a new one. The draft is an
        // agent run, asked for by the owner from the Scout form - a REST route with no MCP tool behind
        // it, refused on a QA copy (QaIsolation). Nothing is filed: the owner picks, edits and files.
        app.MapGet("/api/recon/{project}/drafts", (string project, string? key, ReconDrafter d, CancellationToken ct) =>
            Run(async () => Results.Ok(new { draft = await d.LatestAsync(project, key, ct) })));

        app.MapPost("/api/recon/{project}/drafts", (string project, ReconDraftRequest req, ReconDrafter d, CancellationToken ct) =>
            Run(async () => Results.Ok(new { draft = await d.DraftAsync(project, req.Key, ct) })));

        app.MapPost("/api/recon/{project}/undismiss", (string project, ReconUndismissRequest req, ReconService r, CancellationToken ct) =>
            Run(async () => Results.Ok(await r.UndismissAsync(project, req.Key, DateTime.UtcNow, ct))));
    }

    private static async Task<IResult> Run(Func<Task<IResult>> f)
    {
        try { return await f(); }
        catch (ReconService.Refused e) when (e.NotFound) { return Results.NotFound(new { error = e.Message }); }
        catch (ReconService.Refused e) { return Results.BadRequest(new { error = e.Message }); }
    }
}
