using Whetstone.Services;

namespace Whetstone.Api;

public record OwnerTaskCreate(string? Project, string? Title, string? Body, string? Kind, string? Key, int? TicketId,
    bool? HoldsRelease, int[]? NotDuplicateOf, string? By, bool? AfterRelease = null);
public record OwnerTaskEdit(string? Title, string? Body, string? Kind, string? Project, bool? HoldsRelease, string? By, bool? AfterRelease = null);
public record OwnerTaskWord(string? Note, string? Because, string? On, DateTime? Until, string? Why, string? Text, string? By);

/// <summary>
/// The owner's side of the to-do list (docs/owner-todo-plan.md). Marking an item done, dropping it,
/// waiting on it, reopening and editing it are here and nowhere else - no MCP tool reaches them - so
/// an agent can fill the list and never empty it. A Claude Code session the owner is driving calls
/// these for them, which is why every one has a plain-text reading as well as JSON.
/// </summary>
public static class OwnerTaskEndpoints
{
    /// <summary>"owner" from the page; a session says "session" so the log can tell the two apart.</summary>
    private static string By(string? by) => by?.Trim().ToLowerInvariant() == "session" ? "session" : "owner";

    private static string BoardUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    private static IResult Reply(OwnerTaskReply r) => r.Ok || r.Answer == "recorded"
        ? Results.Ok(new { answer = r.Answer, why = r.Why, data = r.Data })
        : r.Answer == "not-found" ? Results.NotFound(new { answer = r.Answer, error = r.Why })
        : Results.BadRequest(new { answer = r.Answer, error = r.Why, data = r.Data });

    /// <summary>
    /// The slice each release-holding item holds, read per project from the steward's plan - only for
    /// projects with such an item, since reading a plan is a git read.
    /// </summary>
    private static async Task<Dictionary<int, OwnerTaskSlice>> SlicesAsync(IEnumerable<Whetstone.Models.OwnerTask> items,
        StewardService steward, CancellationToken ct)
    {
        var all = new Dictionary<int, OwnerTaskSlice>();
        foreach (var p in items.Where(t => (t.HoldsRelease || t.AfterRelease) && t.ProjectKey is not null).Select(t => t.ProjectKey!).Distinct())
        {
            try { foreach (var (id, s) in await steward.ReleaseSliceOfTodosAsync(p, ct)) all[id] = s; }
            catch (Exception) { /* a plan that will not read leaves the slice unnamed, not the list unread */ }
        }
        return all;
    }

    public static void MapOwnerTaskEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/owner-todos");

        g.MapGet("", async (string? project, string? status, OwnerTaskService s, StewardService steward, CancellationToken ct) =>
        {
            var list = await s.ListAsync(project, status, ct);
            var slices = await SlicesAsync(list, steward, ct);
            return Results.Ok(new { open = await s.OpenCountAsync(ct), items = list.Select(t => OwnerTaskService.Detail(t, slices.GetValueOrDefault(t.Id))) });
        });

        // The badge: open items, and waiting ones whose look-again date has come.
        g.MapGet("/count", async (OwnerTaskService s, CancellationToken ct) => Results.Ok(new { open = await s.OpenCountAsync(ct) }));

        // The open list as markdown, for a Claude Code session.
        g.MapGet("/text", async (string? project, string? status, OwnerTaskService s, StewardService steward, HttpContext http, CancellationToken ct) =>
        {
            var list = await s.ListAsync(project, status, ct);
            return Results.Text(OwnerTaskService.ListMarkdown(list, BoardUrl(http), await SlicesAsync(list, steward, ct)), "text/markdown; charset=utf-8");
        });

        g.MapGet("/find", async (string? project, string? q, OwnerTaskService s, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(q) ? Results.BadRequest(new { error = "Say what to look for with ?q=" })
                : Results.Ok((await s.FindAsync(project, q, ct)).Select(x => new { item = OwnerTaskService.Summary(x.Task), likeness = Math.Round(x.Score, 2) })));

        g.MapGet("/{id}", async (string id, OwnerTaskService s, StewardService steward, CancellationToken ct) =>
            OwnerTaskService.ParseRef(id) is { } n && await s.GetAsync(n, ct) is { } t
                ? Results.Ok(OwnerTaskService.Detail(t, (await SlicesAsync(new[] { t }, steward, ct)).GetValueOrDefault(t.Id)))
                : Results.NotFound(new { error = $"There is no {id}." }));

        // One item as a session reads it: everything needed to do it, and how to say it is done.
        g.MapGet("/{id}/text", async (string id, OwnerTaskService s, StewardService steward, HttpContext http, CancellationToken ct) =>
            OwnerTaskService.ParseRef(id) is { } n && await s.GetAsync(n, ct) is { } t
                ? Results.Text(OwnerTaskService.Markdown(t, BoardUrl(http), (await SlicesAsync(new[] { t }, steward, ct)).GetValueOrDefault(t.Id)),
                    "text/markdown; charset=utf-8")
                : Results.NotFound(new { error = $"There is no {id}." }));

        g.MapPost("", async (OwnerTaskCreate b, OwnerTaskService s, CancellationToken ct) =>
            Reply(await s.AddAsync(new OwnerTaskDraft(b.Project, b.Title ?? "", b.Body ?? "", b.Kind, b.Key, b.TicketId,
                null, b.HoldsRelease ?? false, b.NotDuplicateOf, b.AfterRelease ?? false), By(b.By), ct)));

        g.MapPut("/{id}", async (string id, OwnerTaskEdit b, OwnerTaskService s, CancellationToken ct) =>
            OwnerTaskService.ParseRef(id) is { } n
                ? Reply(await s.EditAsync(n, b.Title, b.Body, b.Kind, b.Project, b.HoldsRelease, By(b.By), ct, b.AfterRelease))
                : Results.NotFound(new { error = $"There is no {id}." }));

        g.MapPost("/{id}/done", async (string id, OwnerTaskWord b, OwnerTaskService s, CancellationToken ct) =>
            OwnerTaskService.ParseRef(id) is { } n ? Reply(await s.DoneAsync(n, b.Note, By(b.By), ct)) : Results.NotFound());

        g.MapPost("/{id}/drop", async (string id, OwnerTaskWord b, OwnerTaskService s, CancellationToken ct) =>
            OwnerTaskService.ParseRef(id) is { } n ? Reply(await s.DropAsync(n, b.Because, By(b.By), ct)) : Results.NotFound());

        g.MapPost("/{id}/wait", async (string id, OwnerTaskWord b, OwnerTaskService s, CancellationToken ct) =>
            OwnerTaskService.ParseRef(id) is { } n ? Reply(await s.WaitAsync(n, b.On, b.Until, By(b.By), ct)) : Results.NotFound());

        g.MapPost("/{id}/reopen", async (string id, OwnerTaskWord b, OwnerTaskService s, CancellationToken ct) =>
            OwnerTaskService.ParseRef(id) is { } n ? Reply(await s.ReopenAsync(n, b.Why, By(b.By), ct)) : Results.NotFound());

        // Not a job but reading for a release: words, terms, docs, note or migration.
        g.MapPost("/{id}/to-release-note", async (string id, OwnerTaskWord b, OwnerTaskService s, CancellationToken ct) =>
            OwnerTaskService.ParseRef(id) is { } n ? Reply(await s.ToReleaseNoteAsync(n, b.Text, By(b.By), ct)) : Results.NotFound());

        g.MapPost("/{id}/note", async (string id, OwnerTaskWord b, OwnerTaskService s, CancellationToken ct) =>
            OwnerTaskService.ParseRef(id) is { } n ? Reply(await s.AppendAsync(n, b.Text ?? "", null, By(b.By), ct)) : Results.NotFound());
    }
}
