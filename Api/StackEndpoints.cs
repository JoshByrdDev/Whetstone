using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>
/// Start / stop / status for each project's local stack - the board's answer to "is the stack up, and
/// can I have it". See <see cref="ProjectStackService"/> for why start and stop go through the same
/// resource lease the QA phase takes.
/// </summary>
public static class StackEndpoints
{
    public static void MapStackEndpoints(this WebApplication app)
    {
        // Every configured stack in one read - the board polls this, not one request per project.
        app.MapGet("/api/projects/stacks", async (ProjectStackService stacks) =>
            Results.Ok(await stacks.SnapshotAllAsync()));

        app.MapGet("/api/projects/{key}/stack", async (string key, ProjectStackService stacks) =>
            Results.Ok(await stacks.SnapshotAsync(key)));

        // Both of these return as soon as the work is accepted. A cold `compose up` is minutes; the
        // caller watches the snapshot rather than holding a request open for it.
        app.MapPost("/api/projects/{key}/stack/start", async (string key, ProjectStackService stacks) =>
            Respond(await stacks.StartAsync(key), await stacks.SnapshotAsync(key)));

        app.MapPost("/api/projects/{key}/stack/stop", async (string key, ProjectStackService stacks) =>
            Respond(await stacks.StopAsync(key), await stacks.SnapshotAsync(key)));
    }

    /// <summary>
    /// 409 when someone else holds the stack, so the UI can say who rather than just "failed" - that
    /// distinction is the entire point of routing this through the lease.
    /// </summary>
    private static IResult Respond(ProjectStackService.ActionResult result, StackSnapshot snapshot)
    {
        if (result.Accepted) return Results.Ok(snapshot);
        var body = new { error = result.Error, stack = snapshot };
        return result.Conflict ? Results.Conflict(body) : Results.BadRequest(body);
    }
}
