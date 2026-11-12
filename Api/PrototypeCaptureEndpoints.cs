using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>
/// The board's screenshots of a ticket's prototype and design links (<see cref="PrototypeCaptureService"/>),
/// for the drawer: what was captured, what could not be and why, and a way to try again.
/// <para>
/// Not in <see cref="QaIsolation"/>'s route table, on purpose. A QA copy has to be able to prove the
/// capture end to end, and what would reach past the copy is the page fetched, not the route: so the
/// service refuses every link that is not on this machine when the copy is isolated, and records the
/// refusal as the capture's failure, the same way a sign-in wall is recorded.
/// </para>
/// </summary>
public static class PrototypeCaptureEndpoints
{
    public sealed record CaptureDto(string Url, string FoundIn, string? LinkText, string? File, string? ImageUrl,
        string? Error, DateTime At, string? Title);

    public static void MapPrototypeCaptureEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tickets/{id:int}/prototype-captures", async (int id, TicketService svc, ProjectRegistry registry,
            DashboardPaths paths, PrototypeCaptureService captures) =>
        {
            var t = await svc.GetAsync(id);
            if (t is null) return Results.NotFound();
            return Results.Ok(View(id, registry.GetProjectConfig(t.ProjectKey).PrototypeCapture, captures,
                PrototypeCaptureService.Read(paths, id)));
        });

        // Captures again now, every link, even one captured before: the owner's way to pick up a
        // prototype that changed, or one a sign-in wall has since come down from.
        app.MapPost("/api/tickets/{id:int}/prototype-captures", async (int id, TicketService svc, ProjectRegistry registry,
            PrototypeCaptureService captures, CancellationToken ct) =>
        {
            var t = await svc.GetAsync(id);
            if (t is null) return Results.NotFound();
            var cfg = registry.GetProjectConfig(t.ProjectKey).PrototypeCapture;
            if (!cfg.IsOn)
                return Results.BadRequest(new { error = "This project captures no links: set prototypeCapture.hosts in its project.json." });
            var got = await captures.CaptureAsync(t, force: true, ct);
            return Results.Ok(View(id, cfg, captures, got));
        });
    }

    private static object View(int id, PrototypeCaptureConfig cfg, PrototypeCaptureService captures,
        IReadOnlyList<PrototypeCaptureService.Entry> entries) => new
    {
        enabled = cfg.IsOn,
        hosts = cfg.Hosts,
        unavailable = cfg.IsOn ? captures.Unavailable() : null,
        captures = entries.Select(e => new CaptureDto(e.Url, e.FoundIn, e.LinkText, e.File,
            e.Ok ? $"/api/tickets/{id}/issue-attachments/{PrototypeCaptureService.Folder}/{Uri.EscapeDataString(e.File!)}" : null,
            e.Error, e.At, e.Title)).ToList(),
    };
}
