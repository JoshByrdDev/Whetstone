using Whetstone.Models;
using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>
/// What got past the pipeline: the escapes a person logged by sending finished work back
/// (<see cref="EscapeEvent"/>), and their classification. Classifying is a person's call, so it is a
/// REST route with no MCP tool behind it. Everything here reads and writes the board's own
/// database and nothing else.
/// </summary>
public static class EscapeEndpoints
{
    public sealed record ClassifyRequest(string? Kind, string? Source, string? Note);

    public sealed record EscapeDto(
        int Id, int TicketId, string? TicketTitle, string ProjectKey, string Track,
        string FromPhase, string ToPhase, string? Reason, int? NoteCommentId, string? Note,
        string? Build, DateTime At, bool Backfilled,
        string? Kind, string? Source, string? ClassifierNote, DateTime? ClassifiedAt);

    private static EscapeDto ToDto(TicketService.EscapeView v) => new(
        v.Event.Id, v.Event.TicketId, v.TicketTitle, v.Event.ProjectKey, v.Event.Track.ToString(),
        v.Event.FromPhase.ToString(), v.Event.ToPhase.ToString(), v.Event.Reason, v.Event.NoteCommentId, v.Note,
        v.Event.Build, v.Event.At, v.Event.Backfilled,
        v.Event.Kind?.ToString(), v.Event.Source?.ToString(), v.Event.ClassifierNote, v.Event.ClassifiedAt);

    public static void MapEscapeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/escapes", async (string? project, DateTime? since, DateTime? until, bool? unclassified,
            int? limit, TicketService svc) =>
        {
            var rows = await svc.EscapesAsync(project, since?.ToUniversalTime(), until?.ToUniversalTime(),
                unclassified ?? false, limit ?? 200);
            return Results.Ok(new
            {
                kinds = Enum.GetNames<EscapeKind>(),
                sources = Enum.GetNames<EscapeSource>(),
                escapes = rows.Select(ToDto),
            });
        });

        app.MapPut("/api/escapes/{id:int}/classification", async (int id, ClassifyRequest req, TicketService svc) =>
        {
            EscapeKind? kind = null;
            if (!string.IsNullOrWhiteSpace(req.Kind))
            {
                if (!Enum.TryParse<EscapeKind>(req.Kind, true, out var k) || !Enum.IsDefined(k))
                    return Results.BadRequest(new { error = $"Unknown kind '{req.Kind}'." });
                kind = k;
            }
            EscapeSource? source = null;
            if (!string.IsNullOrWhiteSpace(req.Source))
            {
                if (!Enum.TryParse<EscapeSource>(req.Source, true, out var s) || !Enum.IsDefined(s))
                    return Results.BadRequest(new { error = $"Unknown source '{req.Source}'." });
                source = s;
            }

            var e = await svc.ClassifyEscapeAsync(id, kind, source, req.Note);
            return e is null
                ? Results.NotFound()
                : Results.Ok(new { e.Id, kind = e.Kind?.ToString(), source = e.Source?.ToString(), e.ClassifierNote, e.ClassifiedAt });
        });
    }
}
