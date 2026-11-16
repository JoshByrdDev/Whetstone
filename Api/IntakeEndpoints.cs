using Whetstone.Services.Intake;

namespace Whetstone.Api;

/// <summary>
/// Intake's routes (docs/intake-plan.md). The owner's alone: no MCP tool reaches intake, so no
/// worker or steward can mute a report, read a queue or see a source's credential -
/// <c>IntakeRouteTests.No_tool_reaches_intake</c> holds that.
/// </summary>
public static class IntakeEndpoints
{
    public static void MapIntakeEndpoints(this WebApplication app)
    {
        // "Never file this again", from an intake ticket's drawer: mutes its issue and closes the
        // ticket, unless a run is working it.
        app.MapPost("/api/tickets/{id:int}/intake/mute", async (int id, IntakeService intake, CancellationToken ct) =>
            await intake.MuteFromTicketAsync(id, ct) is { } r
                ? Results.Ok(r)
                : Results.BadRequest(new { error = $"Ticket #{id} was not filed by intake, so there is no report to mute." }));

        // Muting and unmuting from the Intake page, by issue.
        app.MapPut("/api/intake/issues/{id:int}/muted", async (int id, SetIntakeMutedRequest req, IntakeService intake, CancellationToken ct) =>
            await intake.SetMutedAsync(id, req.Muted, ct) is { } issue
                ? Results.Ok(new { issue.Id, issue.Muted, issue.MutedAt })
                : Results.NotFound());
    }
}

public record SetIntakeMutedRequest(bool Muted);
