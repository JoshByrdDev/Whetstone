using Whetstone.Models;
using Whetstone.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Api;

/// <summary>One leg of a ticket's trip, straight out of <c>PhaseTransitions</c>.</summary>
/// <param name="From">Where the move came from. Only the first leg's is load-bearing — it is the
/// only record of where the ticket started, which the chart needs to draw its first point.</param>
/// <param name="Minutes">How long the ticket stayed. Null on the leg it is still in.</param>
public record JourneyLegDto(string From, string Phase, DateTime EnteredAt, double? Minutes, string Actor, bool Backward);

/// <summary>
/// A ticket's trip through the phases, shared by the board drawer and the Classroom.
/// <para>
/// Deliberately its own endpoint rather than a field on <c>TicketDetailDto</c>. That shape is also
/// what the MCP <c>get_ticket</c> tool hands to every worker on every phase, and twenty legs of
/// transition history is context a worker has no use for and pays for on every call.
/// </para>
/// </summary>
public static class TicketJourney
{
    public static void MapJourneyEndpoint(this WebApplication app) =>
        app.MapGet("/api/tickets/{id:int}/journey", async (int id, DashboardDbContext db) =>
            Results.Ok(await ForTicketAsync(db, id)));

    /// <summary>
    /// The trip as legs: which phase the ticket entered, when, and how long it stayed. The last leg
    /// is left open-ended (null minutes) because the ticket is still in it.
    /// </summary>
    public static async Task<List<JourneyLegDto>> ForTicketAsync(DashboardDbContext db, int ticketId)
    {
        var moves = await db.PhaseTransitions.AsNoTracking()
            .Where(p => p.TicketId == ticketId)
            .OrderBy(p => p.At)
            .ToListAsync();

        var legs = new List<JourneyLegDto>(moves.Count);
        for (var i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            double? minutes = i + 1 < moves.Count ? (moves[i + 1].At - m.At).TotalMinutes : null;
            legs.Add(new JourneyLegDto(m.From.ToString(), m.To.ToString(), m.At, minutes, m.Actor.ToString(), m.Backward));
        }
        return legs;
    }

    /// <summary>
    /// Every backward move on the ticket, with the notes that explain it.
    /// <para>
    /// The notes are chosen by time, not by reading them. A bounce's "why" is whatever was written
    /// while the ticket sat in the phase it bounced out of — the QA run's findings, the system's
    /// merge-conflict account — plus anything a human added before the rework was picked up, which
    /// is where an owner sending a ticket back from review puts their instructions. The "reply" is
    /// the rework run's first note, so the question and what was done about it sit together.
    /// </para>
    /// <para>
    /// The "Phase changed" and resume notices are left out: the move's own reason already says what
    /// the first one would, and the second says nothing about why.
    /// </para>
    /// </summary>
    public static async Task<List<BounceDto>> BouncesAsync(DashboardDbContext db, int ticketId)
    {
        var moves = await db.PhaseTransitions.AsNoTracking()
            .Where(p => p.TicketId == ticketId)
            .OrderBy(p => p.At)
            .ToListAsync();
        if (!moves.Any(m => m.Backward)) return new();

        var notes = (await db.Comments.AsNoTracking()
                .Include(c => c.Attachments)
                .Where(c => c.TicketId == ticketId)
                .ToListAsync())
            .Where(c => !(c.Author == AuthorKind.System
                && (c.Body.StartsWith("Phase changed ") || c.Body.StartsWith("Resuming in-progress"))))
            .OrderBy(c => c.CreatedAt)
            .ToList();

        var bounces = new List<BounceDto>();
        for (var i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            if (!m.Backward) continue;

            // When the ticket arrived in the phase it bounced out of. The first move has no arrival
            // to look back to, so anything before it counts.
            var arrived = moves.Take(i).LastOrDefault(p => p.To == m.From)?.At ?? DateTime.MinValue;
            // Until the rework is picked up: the next move after the bounce, usually its claim.
            var pickedUp = i + 1 < moves.Count ? moves[i + 1].At : DateTime.MaxValue;
            var reworkEnds = i + 2 < moves.Count ? moves[i + 2].At : DateTime.MaxValue;

            var why = notes
                .Where(c => c.CreatedAt >= arrived && c.CreatedAt < pickedUp)
                // A worker's note after the bounce belongs to whatever happens next, not to this.
                .Where(c => c.Author != AuthorKind.Claude || c.CreatedAt <= m.At)
                .Select(c => TicketEndpoints.ToCommentDto(ticketId, c))
                .ToList();

            var reply = notes.FirstOrDefault(c =>
                c.Author == AuthorKind.Claude && c.CreatedAt >= pickedUp && c.CreatedAt < reworkEnds);

            bounces.Add(new BounceDto(
                i, m.From.ToString(), m.To.ToString(), m.At, m.Actor.ToString(), m.Reason, why,
                reply is null ? null : TicketEndpoints.ToCommentDto(ticketId, reply)));
        }
        return bounces;
    }
}

/// <summary>One backward move and the notes around it — see <see cref="TicketJourney.BouncesAsync"/>.</summary>
/// <param name="Leg">Index into the journey's legs, so the two can be matched up.</param>
/// <param name="Why">Notes written in the phase it bounced out of, oldest first.</param>
/// <param name="Reply">The rework run's first note, when it has written one.</param>
public record BounceDto(int Leg, string From, string To, DateTime At, string Actor, string? Reason,
    IReadOnlyList<CommentDto> Why, CommentDto? Reply);
