using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>The owner's verdict on one proposal: an edit, a decision, or both.</summary>
public record DiscoveryVerdictRequest(
    string? Status = null, string? Title = null, string? Why = null, string? Body = null, IReadOnlyList<string>? WorkItems = null,
    string? Kind = null, string? RejectReason = null, string? RejectNote = null, DateTime? SnoozeUntil = null, string? Answer = null,
    bool? NewToMe = null);

/// <summary>The owner's pin on a proposal's media.</summary>
public record DiscoveryPinRequest(bool Pinned);

/// <summary>The owner's edit of a route at its gate. Each field absent is left as Chart wrote it.</summary>
public record DiscoveryRouteEditRequest(string? Persona = null, IReadOnlyList<string>? Jobs = null, string? SignedInAs = null,
    IReadOnlyList<string>? StartCases = null, IReadOnlyList<string>? Widths = null, string? Notes = null, IReadOnlyList<string>? Areas = null);

/// <summary>The owner's taste file, written whole.</summary>
public record DiscoveryTasteEditRequest(string Text);

/// <summary>The owner's "fold at the next run", or taking it back.</summary>
public record DiscoveryTasteFoldAsk(bool Requested);

/// <summary>A worker run asking whether a fold is due, and on which ticket's run it rides.</summary>
public record DiscoveryTasteFoldBeginRequest(int? TicketId = null);

/// <summary>The fold's answer from the run that holds the claim, or the error it got instead of one.</summary>
public record DiscoveryTasteFoldFinishRequest(string Claim, string? Reply = null, string? Error = null, int? TicketId = null);

/// <summary>
/// The owner's side of a discovery (docs/discovery-track-plan.md). Reading the record, and the
/// verdict and Complete - which have no MCP tool behind them, so only a person approves, files or
/// answers. Nothing here reaches a stack, an agent or anything off this machine.
/// </summary>
public static class DiscoveryEndpoints
{
    private static string VideoType(string path) =>
        path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "video/webm";

    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tickets/{id:int}/discovery", async (int id, DiscoveryService svc, DashboardPaths paths, DashboardDbContext db) =>
        {
            // A discovery waiting on work that settled while nothing was watching moves on now.
            await svc.SettleAsync(id);
            var v = await svc.GetAsync(id);
            if (v is null) return Results.NotFound();
            var dir = paths.DiscoveryTicketDir(id);
            var ffmpeg = svc.FfmpegPath is not null;
            return Results.Ok(new
            {
                route = v.Route is null ? null : new
                {
                    v.Route.Persona, v.Route.SignedInAs, v.Route.Jobs, v.Route.StartCases, v.Route.Widths,
                    v.Route.Notes, v.Route.Areas, v.Route.WrittenAt, v.Route.ApprovedAt, v.Route.OwnerEdited,
                },
                observations = v.Observations.Select(o => new
                {
                    o.Id, kind = o.Kind.ToString(), o.Title, o.Saw, o.Tried, o.Job, o.Url, o.Width, o.SignedInAs, o.Area,
                    shot = o.ShotPath, focus = o.FocusW is null ? null : new { x = o.FocusX, y = o.FocusY, w = o.FocusW, h = o.FocusH },
                    o.CreatedAt, o.Round, o.ShotPrunedAt, o.RecordingId, o.VideoAt,
                }),
                visits = v.Visits.Select(x => new { x.Id, x.Url, x.RoutePattern, x.Width, x.SignedInAs, x.Seconds, x.Area, x.Case, x.At }),
                proposals = v.Proposals.Select(p => new
                {
                    p.Id, p.Rank, kind = p.Kind.ToString(), status = p.Status.ToString(), p.Title, p.Why, p.Body, p.WorkItems,
                    p.Size, p.Confidence, p.Area, p.ObservationIds, p.DropReason, rejectReason = p.RejectReason?.ToString(),
                    p.RejectNote, p.SnoozeUntil, p.Answer, p.FiledTicketId, p.FiledNote, p.Edited, p.CreatedAt, p.DecidedAt,
                    p.NewToMe, draftKind = p.DraftKind.ToString(), p.DraftTitle, p.DraftWhy, p.DraftBody, p.DraftWorkItems,
                    p.Round, recheckKind = p.RecheckKind?.ToString(), p.GoalId,
                    clip = DiscoveryMedia.StateOf(p, v.Observations, v.Recordings, dir, ffmpeg).ToString(), p.ClipPrunedAt, p.Pinned,
                }),
                // The runs' videos, each with its chapters; a pruned one keeps its row and says when it went.
                recordings = v.Recordings.Select(r => new
                {
                    r.Id, r.Round, r.StartedAt, r.Seconds, r.Bytes, r.VerdictAt, r.PrunedAt,
                    chapters = v.Chapters.Where(c => c.RecordingId == r.Id).Select(c => new { c.Id, c.Title, c.Job, c.VideoAt }),
                }),
                // Whether this machine can cut clips at all - the drawer says so rather than showing players that fail.
                media = new { ffmpeg },
                filed = v.Filed,
                goalChecks = v.GoalChecks.Select(g => new
                {
                    g.Id, goalId = g.ProposalId, g.FiledTicketId, g.Round, outcome = g.Outcome.ToString(), g.Note, g.ObservationIds, g.At,
                }),
                // Which recheck it is on (0 before the first), how many get a verdict, and the goals
                // the next (or current) recheck asks about.
                recheck = new { round = v.Round, roundsWithVerdict = v.RecheckRounds, goals = v.Goals },
                // Which taste version each Sift and Recheck run of this discovery read (step 4).
                tasteReads = await db.DiscoveryTasteReads.AsNoTracking().Where(r => r.TicketId == id).OrderBy(r => r.Id)
                    .Select(r => new { r.Id, r.RunId, r.Round, r.Phase, r.VersionId, r.VersionNumber, r.At }).ToListAsync(),
            });
        });

        app.MapGet("/api/tickets/{id:int}/discovery/shots/{name}", async (int id, string name, DiscoveryService svc, DashboardDbContext db) =>
        {
            var disk = svc.ShotDiskPath(id, name);
            if (disk is not null) return Results.File(disk, QaShotStore.ContentType(name));
            // Pruned is said, not broken: whatever only has the URL shows when it went.
            var pruned = await db.DiscoveryObservations.AsNoTracking().Where(o => o.TicketId == id && o.ShotPath == name && o.ShotPrunedAt != null)
                .Select(o => o.ShotPrunedAt).FirstOrDefaultAsync();
            return pruned is { } at
                ? Results.Text(CaptureRetention.RemovedPlaceholderSvg(at, "Screenshot", "no proposal kept it; what the scout saw is still on the discovery"), "image/svg+xml")
                : Results.NotFound();
        });

        // A run's whole recording, for scrubbing while deciding. Ranges, so a browser can seek in it.
        app.MapGet("/api/tickets/{id:int}/discovery/recordings/{rec:int}", async (int id, int rec, DiscoveryService svc) =>
        {
            var (path, pruned) = await svc.RecordingAsync(id, rec);
            if (path is not null) return Results.File(path, VideoType(path), enableRangeProcessing: true);
            return pruned is null ? Results.NotFound() : Results.StatusCode(StatusCodes.Status410Gone);
        });

        // A proposal's clip, cut from its recording the first time it is asked for. Nothing past the
        // data folder: ffmpeg reads one file under it and writes another.
        app.MapGet("/api/discovery/proposals/{id:int}/clip", async (int id, DiscoveryService svc, CancellationToken ct) =>
        {
            var (path, state, error) = await svc.ClipAsync(id, ct);
            if (path is not null) return Results.File(path, VideoType(path), enableRangeProcessing: true);
            return state switch
            {
                DiscoveryMedia.ClipState.Removed or DiscoveryMedia.ClipState.RecordingRemoved => Results.StatusCode(StatusCodes.Status410Gone),
                DiscoveryMedia.ClipState.Cuttable => Results.Problem(error ?? "The clip could not be cut.", statusCode: 500),
                _ => Results.NotFound(new { error = state.ToString() }),
            };
        });

        // The owner's pin: a proposal's clip and shots outlive every retention rule until unpinned.
        app.MapPut("/api/discovery/proposals/{id:int}/pin", async (int id, DiscoveryPinRequest req, DiscoveryService svc) =>
        {
            try
            {
                var p = await svc.PinAsync(id, req.Pinned);
                return Results.Ok(new { p.Id, p.Pinned });
            }
            catch (DiscoveryService.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        app.MapPut("/api/discovery/proposals/{id:int}", async (int id, DiscoveryVerdictRequest req, DiscoveryService svc) =>
        {
            try
            {
                var p = await svc.DecideAsync(id, new DiscoveryService.VerdictInput(req.Status, req.Title, req.Why, req.Body,
                    req.WorkItems, req.Kind, req.RejectReason, req.RejectNote, req.SnoozeUntil, req.Answer, req.NewToMe));
                return Results.Ok(new { p.Id, status = p.Status.ToString(), p.Edited });
            }
            catch (DiscoveryService.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        app.MapPost("/api/discovery/proposals/{id:int}/restore", async (int id, DiscoveryService svc) =>
        {
            try
            {
                var p = await svc.RestoreAsync(id);
                return Results.Ok(new { p.Id, status = p.Status.ToString(), p.Rank });
            }
            catch (DiscoveryService.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        // The route gate: the owner's edit and approval, with no tool behind either.
        app.MapPut("/api/tickets/{id:int}/discovery/route", async (int id, DiscoveryRouteEditRequest req, DiscoveryService svc) =>
        {
            try
            {
                var r = await svc.EditRouteAsync(id, new DiscoveryService.RouteEdit(req.Persona, req.Jobs, req.SignedInAs,
                    req.StartCases, req.Widths, req.Notes, req.Areas));
                return Results.Ok(new { r.TicketId, r.OwnerEdited });
            }
            catch (DiscoveryService.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        app.MapPost("/api/tickets/{id:int}/discovery/route/approve", async (int id, DiscoveryService svc) =>
        {
            try { return Results.Ok(new { phase = (await svc.ApproveRouteAsync(id)).ToString() }); }
            catch (DiscoveryService.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        // ---- the taste file (step 4) -----------------------------------------------------------
        // The owner's routes - reading, editing, reverting, asking for a fold - have no tool behind
        // them. The fold's begin and finish are called by the worker's script around the model call
        // it makes, not by an agent: the board writes, commits and versions the file either way. None
        // reaches past the data folder - the commit is in the project's own folder - and the board
        // starts no model here, so none needs QaIsolation's table.
        static object Line(DiscoveryTasteVersion v) => new
        {
            v.Id, v.Number, source = v.Source.ToString(), v.Hash, v.Commit, v.CommitNote, v.Verdicts, events = v.EventIds.Count,
            v.Suggestions, v.RevertOf, v.RodeOnTicketId, v.At,
        };

        app.MapGet("/api/discovery/projects/{key}/taste", async (string key, DiscoveryTaste taste) =>
        {
            try { return Results.Ok(await taste.StatusAsync(key)); }
            catch (DiscoveryTaste.Refused e) { return Results.NotFound(new { error = e.Message }); }
        });

        app.MapGet("/api/discovery/projects/{key}/taste/versions/{vid:int}", async (string key, int vid, DiscoveryTaste taste) =>
        {
            try
            {
                var v = await taste.VersionAsync(key, vid);
                return Results.Ok(new { v.Id, v.Number, source = v.Source.ToString(), v.Text, v.Hash, v.Commit, v.Suggestions, v.At });
            }
            catch (DiscoveryTaste.Refused e) { return Results.NotFound(new { error = e.Message }); }
        });

        app.MapPut("/api/discovery/projects/{key}/taste", async (string key, DiscoveryTasteEditRequest req, DiscoveryTaste taste) =>
        {
            try { return Results.Ok(Line(await taste.OwnerEditAsync(key, req.Text))); }
            catch (DiscoveryTaste.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        app.MapPost("/api/discovery/projects/{key}/taste/versions/{vid:int}/restore", async (string key, int vid, DiscoveryTaste taste) =>
        {
            try { return Results.Ok(Line(await taste.RevertAsync(key, vid))); }
            catch (DiscoveryTaste.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        app.MapPut("/api/discovery/projects/{key}/taste/fold-request", async (string key, DiscoveryTasteFoldAsk req, DiscoveryTaste taste) =>
        {
            try { return Results.Ok(new { requested = await taste.RequestFoldAsync(key, req.Requested) }); }
            catch (DiscoveryTaste.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        app.MapPost("/api/discovery/projects/{key}/taste/fold/begin", async (string key, DiscoveryTasteFoldBeginRequest? req, DiscoveryTaste taste) =>
        {
            try { return Results.Ok(await taste.BeginFoldAsync(key, req?.TicketId)); }
            catch (DiscoveryTaste.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        app.MapPost("/api/discovery/projects/{key}/taste/fold/finish", async (string key, DiscoveryTasteFoldFinishRequest req, DiscoveryTaste taste) =>
        {
            try { return Results.Ok(Line(await taste.FinishFoldAsync(key, req.Claim, req.Reply, req.Error, req.TicketId))); }
            catch (DiscoveryTaste.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });

        app.MapPost("/api/tickets/{id:int}/discovery/complete", async (int id, DiscoveryService svc) =>
        {
            try
            {
                var r = await svc.CompleteAsync(id);
                return Results.Ok(r);
            }
            catch (DiscoveryService.Refused e) { return Results.BadRequest(new { error = e.Message }); }
        });
    }
}
