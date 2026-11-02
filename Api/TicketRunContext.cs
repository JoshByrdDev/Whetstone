using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.EntityFrameworkCore;

namespace Whetstone.Api;

/// <summary>How close one run came to its model's context window.</summary>
/// <param name="Window">Null when the run's provider did not report one; the share is then unknown,
/// not small.</param>
/// <param name="Compactions">Times the conversation was compacted to fit. Null when not counted.</param>
/// <param name="Largest">The run's biggest tool results, so a warning can say what filled it.</param>
public record RunContextDto(int RunId, string Phase, DateTime StartedAt, string? Model,
    int? Peak, int? Window, int? Compactions, IReadOnlyList<MetricsService.ToolResultSize> Largest);

/// <summary>
/// The context each of a ticket's runs reached, for the drawer's warning.
/// <para>
/// Its own endpoint for the reason <see cref="TicketJourney"/> is: the ticket detail shape is what
/// <c>get_ticket</c> hands every worker, and how full earlier runs got is nothing a worker can act on.
/// Which share counts as a warning is the client's to say, in one place (<c>contextLevel</c>), so the
/// drawer and the Runs page cannot disagree.
/// </para>
/// </summary>
public static class TicketRunContext
{
    public static void MapRunContextEndpoint(this WebApplication app) =>
        app.MapGet("/api/tickets/{id:int}/run-context", async (int id, DashboardDbContext db) =>
            Results.Ok(await ForTicketAsync(db, id)));

    public static async Task<List<RunContextDto>> ForTicketAsync(DashboardDbContext db, int ticketId)
    {
        var runs = await db.CycleRuns.AsNoTracking()
            .Where(c => c.TicketId == ticketId)
            .OrderBy(c => c.StartedAt)
            .ToListAsync();
        return runs.Select(c => new RunContextDto(c.Id, c.Phase.ToString(), c.StartedAt, c.Model,
            c.PeakContextTokens, c.ContextWindowTokens, c.Compactions,
            MetricsService.LargestToolResultsOf(c).Take(3).ToList())).ToList();
    }
}
