using Whetstone.Models;
using Whetstone.Services;

namespace Whetstone.Api;

public record SetBugRequest(bool IsBug);
public record ConfirmBugLinkRequest(string? FoundBy, string? MissedBy, string? Note = null);
public record RejectBugLinkRequest(string? Note = null);
public record AddBugLinkRequest(int SourceTicketId, string? FoundBy, string? MissedBy, string? Note = null);

/// <summary>
/// Bug tracing's routes (docs/bugs-and-experiments-plan.md, 1.6). Every verdict - confirm, reject,
/// reopen, name a source - is here and only here: no MCP tool writes a bug link, so neither a worker
/// nor a steward can clear a bug traced to its own work. <c>HumanOnlyLimitTests</c> holds that.
/// </summary>
public static class BugEndpoints
{
    public static void MapBugEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tickets/{id:int}/bug", async (int id, BugTraceService bugs, CancellationToken ct) =>
            await bugs.ForBugAsync(id, ct) is { } view ? Results.Ok(view) : Results.NotFound());

        app.MapGet("/api/tickets/{id:int}/bugs-traced-here", async (int id, BugTraceService bugs, CancellationToken ct) =>
            Results.Ok(await bugs.TracedHereAsync(id, ct)));

        // Marking a ticket as a bug. A bug that is already Done is traced at once, so marking one
        // after the fact is enough to get its suggestions.
        app.MapPut("/api/tickets/{id:int}/bug", async (int id, SetBugRequest req, BugTraceService bugs, BugTraceQueue queue) =>
        {
            var t = await bugs.SetBugAsync(id, req.IsBug);
            if (t is null) return Results.NotFound();
            if (t.IsBug && (t.Phase == TicketPhase.Done || t.MergeSha is not null)) queue.Enqueue(id);
            return Results.Ok(new { id = t.Id, isBug = t.IsBug });
        });

        // Run the trace now, and answer with what it found. In the request rather than the queue,
        // because the person asking is waiting to read the answer.
        app.MapPost("/api/tickets/{id:int}/bug/trace", async (int id, BugTraceService bugs, CancellationToken ct) =>
        {
            var trace = await bugs.TraceAsync(id, ct);
            if (trace is null) return Results.BadRequest(new { error = "Only a ticket marked as a bug can be traced." });
            return Results.Ok(await bugs.ForBugAsync(id, ct));
        });

        app.MapPost("/api/bug-links/{id:int}/confirm", async (int id, ConfirmBugLinkRequest req, BugTraceService bugs) =>
        {
            if (!TryFoundBy(req.FoundBy, out var foundBy))
                return Results.BadRequest(new { error = $"Say who found it: one of {string.Join(", ", Enum.GetNames<BugFoundBy>())}." });
            try { return await bugs.ConfirmAsync(id, foundBy, req.MissedBy ?? "", req.Note) is { } l ? Results.Ok(new { l.Id, status = l.Status.ToString() }) : Results.NotFound(); }
            catch (BugTraceService.LinkRefused ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/bug-links/{id:int}/reject", async (int id, RejectBugLinkRequest? req, BugTraceService bugs) =>
            await bugs.RejectAsync(id, req?.Note) is { } l ? Results.Ok(new { l.Id, status = l.Status.ToString() }) : Results.NotFound());

        app.MapPost("/api/bug-links/{id:int}/reopen", async (int id, BugTraceService bugs) =>
            await bugs.ReopenAsync(id) is { } l ? Results.Ok(new { l.Id, status = l.Status.ToString() }) : Results.NotFound());

        app.MapPost("/api/tickets/{id:int}/bug/links", async (int id, AddBugLinkRequest req, BugTraceService bugs) =>
        {
            if (!TryFoundBy(req.FoundBy, out var foundBy))
                return Results.BadRequest(new { error = $"Say who found it: one of {string.Join(", ", Enum.GetNames<BugFoundBy>())}." });
            try { return await bugs.AddManualAsync(id, req.SourceTicketId, foundBy, req.MissedBy ?? "", req.Note) is { } l ? Results.Ok(new { l.Id, status = l.Status.ToString() }) : Results.NotFound(); }
            catch (BugTraceService.LinkRefused ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static bool TryFoundBy(string? s, out BugFoundBy foundBy) =>
        Enum.TryParse(s?.Replace(" ", "").Replace("-", ""), ignoreCase: true, out foundBy) && Enum.IsDefined(foundBy);
}
