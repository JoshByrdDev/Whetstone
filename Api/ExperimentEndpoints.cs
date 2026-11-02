using Whetstone.Persistence;
using Whetstone.Services;

namespace Whetstone.Api;

public record StopExperimentRequest(string? Reason = null);
public record DeclareExperimentRequest(string? Winner, string? Note = null);

/// <summary>
/// Experiments' routes (docs/bugs-and-experiments-plan.md, 2.8). Starting, stopping and declaring
/// are here and only here: no MCP tool reaches an experiment, and a worker is never told which arm
/// it is in beyond the skill text it is handed. <c>ExperimentTests.No_tool_can_confirm_a_bug_link_or_steer_an_experiment</c> holds that.
/// </summary>
public static class ExperimentEndpoints
{
    /// <summary>A variant file of a skill, in the project's folder: <c>skills/&lt;skill&gt;/variants/&lt;name&gt;.md</c>.</summary>
    public static string VariantPath(DashboardPaths paths, string project, string skill, string name) =>
        Path.Combine(paths.SkillsDir(project), skill, "variants", name + ".md");

    public static void MapExperimentEndpoints(this WebApplication app)
    {
        app.MapGet("/api/experiments", async (string? project, ExperimentService svc) =>
            Results.Ok(await svc.ListAsync(project)));

        app.MapGet("/api/experiments/{id:int}", async (int id, ExperimentService svc) =>
            await svc.ViewAsync(id) is { } v ? Results.Ok(v) : Results.NotFound());

        // The variant files on disk for one skill, and the providers an arm may switch to, for the
        // create form.
        app.MapGet("/api/experiments/options", (string project, string? skill, DashboardPaths paths, ProjectRegistry registry) =>
        {
            if (!registry.Projects.Contains(project)) return Results.BadRequest(new { error = $"Unknown project '{project}'" });
            var skills = PhaseMachine.AllSkills;
            var variants = skills.ToDictionary(s => s, s =>
            {
                var dir = Path.Combine(paths.SkillsDir(project), s, "variants");
                return Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*.md").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n).ToArray()
                    : Array.Empty<string>();
            });
            return Results.Ok(new
            {
                skills,
                variants,
                providers = ProvidersInUse(registry),
                efforts = AgentRoles.Efforts,
                roles = skills.ToDictionary(s => s, s => AgentRoles.Resolve(registry.GetGlobalConfig(), s)),
            });
        });

        app.MapPost("/api/experiments", async (ExperimentService.CreateSpec spec, ExperimentService svc,
            DashboardPaths paths, ProjectRegistry registry) =>
        {
            if (!registry.Projects.Contains(spec.ProjectKey)) return Results.BadRequest(new { error = $"Unknown project '{spec.ProjectKey}'" });
            try
            {
                var exp = await svc.CreateAsync(spec,
                    (skill, name) => File.Exists(VariantPath(paths, spec.ProjectKey, skill, name)),
                    ProvidersInUse(registry));
                return Results.Ok(await svc.ViewAsync(exp.Id));
            }
            catch (ExperimentService.Refused ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/experiments/{id:int}/stop", async (int id, StopExperimentRequest? req, ExperimentService svc) =>
        {
            try { return await svc.StopAsync(id, req?.Reason) is null ? Results.NotFound() : Results.Ok(await svc.ViewAsync(id)); }
            catch (ExperimentService.Refused ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/experiments/{id:int}/declare", async (int id, DeclareExperimentRequest req, ExperimentService svc) =>
        {
            try { return await svc.DeclareAsync(id, req.Winner, req.Note) is null ? Results.NotFound() : Results.Ok(await svc.ViewAsync(id)); }
            catch (ExperimentService.Refused ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    /// <summary>Providers some role the heartbeat runs is configured on - the only ones an arm may name.</summary>
    public static string[] ProvidersInUse(ProjectRegistry registry)
    {
        var cfg = registry.GetGlobalConfig();
        return AgentRoles.HeartbeatRoles.Select(r => AgentRoles.Resolve(cfg, r).Provider).Distinct().ToArray();
    }
}
