using Whetstone.Models;
using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>The owner's verdict on one proposal: an edit, a decision, or both.</summary>
public record DiscoveryVerdictRequest(
    string? Status = null, string? Title = null, string? Why = null, string? Body = null, IReadOnlyList<string>? WorkItems = null,
    string? Kind = null, string? RejectReason = null, string? RejectNote = null, DateTime? SnoozeUntil = null, string? Answer = null,
    bool? NewToMe = null);

/// <summary>The owner's edit of a route at its gate. Each field absent is left as Chart wrote it.</summary>
public record DiscoveryRouteEditRequest(string? Persona = null, IReadOnlyList<string>? Jobs = null, string? SignedInAs = null,
    IReadOnlyList<string>? StartCases = null, IReadOnlyList<string>? Widths = null, string? Notes = null, IReadOnlyList<string>? Areas = null);

/// <summary>
/// The owner's side of a discovery (docs/discovery-track-plan.md). Reading the record, and the
/// verdict and Complete - which have no MCP tool behind them, so only a person approves, files or
/// answers. Nothing here reaches a stack, an agent or anything off this machine.
/// </summary>
public static class DiscoveryEndpoints
{
    public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tickets/{id:int}/discovery", async (int id, DiscoveryService svc) =>
        {
            // A discovery waiting on work that settled while nothing was watching moves on now.
            await svc.SettleAsync(id);
            var v = await svc.GetAsync(id);
            if (v is null) return Results.NotFound();
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
                    o.CreatedAt, o.Round,
                }),
                visits = v.Visits.Select(x => new { x.Id, x.Url, x.RoutePattern, x.Width, x.SignedInAs, x.Seconds, x.Area, x.Case, x.At }),
                proposals = v.Proposals.Select(p => new
                {
                    p.Id, p.Rank, kind = p.Kind.ToString(), status = p.Status.ToString(), p.Title, p.Why, p.Body, p.WorkItems,
                    p.Size, p.Confidence, p.Area, p.ObservationIds, p.DropReason, rejectReason = p.RejectReason?.ToString(),
                    p.RejectNote, p.SnoozeUntil, p.Answer, p.FiledTicketId, p.FiledNote, p.Edited, p.CreatedAt, p.DecidedAt,
                    p.NewToMe, draftKind = p.DraftKind.ToString(), p.DraftTitle, p.DraftWhy, p.DraftBody, p.DraftWorkItems,
                    p.Round, recheckKind = p.RecheckKind?.ToString(), p.GoalId,
                }),
                filed = v.Filed,
                goalChecks = v.GoalChecks.Select(g => new
                {
                    g.Id, goalId = g.ProposalId, g.FiledTicketId, g.Round, outcome = g.Outcome.ToString(), g.Note, g.ObservationIds, g.At,
                }),
                // Which recheck it is on (0 before the first), how many get a verdict, and the goals
                // the next (or current) recheck asks about.
                recheck = new { round = v.Round, roundsWithVerdict = v.RecheckRounds, goals = v.Goals },
            });
        });

        app.MapGet("/api/tickets/{id:int}/discovery/shots/{name}", (int id, string name, DiscoveryService svc) =>
        {
            var disk = svc.ShotDiskPath(id, name);
            return disk is null ? Results.NotFound() : Results.File(disk, QaShotStore.ContentType(name));
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
