using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>
/// The Skills page's "the template has moved on" cards, and the count on its tab. Everything here
/// reads and writes inside one project's folder under the data root; nothing reaches past it.
/// See <see cref="SkillDriftService"/>.
/// </summary>
public static class SkillDriftEndpoints
{
    public sealed record FileRequest(string? File, string? How);

    public static void MapSkillDriftEndpoints(this WebApplication app)
    {
        // Per project, how many copies are behind their template or have no baseline. Polled by the tab.
        app.MapGet("/api/skill-drift", (SkillDriftService drift) => Results.Ok(drift.Summary()));

        app.MapGet("/api/skill-drift/{project}", (string project, ProjectRegistry registry, SkillDriftService drift) =>
            registry.AllProjects.Contains(project)
                ? Results.Ok(drift.Items(project))
                : Results.NotFound(new { error = $"Unknown project '{project}'." }));

        // Three-way merge the template's change into the copy. Writes only when it is clean.
        app.MapPost("/api/skill-drift/{project}/apply", (string project, FileRequest req, ProjectRegistry registry, SkillDriftService drift) =>
        {
            if (!registry.AllProjects.Contains(project)) return Results.NotFound(new { error = $"Unknown project '{project}'." });
            if (string.IsNullOrWhiteSpace(req.File)) return Results.BadRequest(new { error = "Which file?" });
            var result = drift.Apply(project, req.File);
            return result.Error is { } e && result.Conflicts == 0 && !result.Ok
                ? Results.BadRequest(new { error = e })
                : Results.Ok(result);
        });

        // Move the baseline up without touching the copy: ported by hand, or not wanted here.
        app.MapPost("/api/skill-drift/{project}/mark-synced", (string project, FileRequest req, ProjectRegistry registry, SkillDriftService drift) =>
        {
            if (!registry.AllProjects.Contains(project)) return Results.NotFound(new { error = $"Unknown project '{project}'." });
            if (string.IsNullOrWhiteSpace(req.File)) return Results.BadRequest(new { error = "Which file?" });
            var how = req.How is "ported" or "skipped" ? req.How : "ported";
            return drift.MarkSynced(project, req.File, how) is { } error
                ? Results.BadRequest(new { error })
                : Results.Ok(new { ok = true });
        });
    }
}
