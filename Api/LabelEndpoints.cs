using Whetstone.Models;
using Whetstone.Services;

namespace Whetstone.Api;

public record LabelNameRequest(string Name);

/// <summary>
/// The owner's filing tags, served on their own routes and out of their own DTOs.
/// <para>
/// Nothing here touches <see cref="TicketMapper"/>, and that is the whole design. Those shapes are
/// the ones every worker receives over MCP, so a label on <c>TicketDetailDto</c> would be a label in
/// every phase's prompt — and a tag the agents can read is a tag that quietly starts meaning
/// something to them. Keeping the two apart costs the board one extra request and buys a guarantee
/// that holds without anybody remembering it (see <see cref="Label"/>).
/// </para>
/// </summary>
public static class LabelEndpoints
{
    public static void MapLabelEndpoints(this WebApplication app)
    {
        // The whole filing system in one response: what labels exist (with their usage, so the
        // picker can lead with the ones actually in use) and who is wearing what. Both halves are
        // wanted at the same moment by the same caller — the board draws chips and the picker offers
        // suggestions off one fetch — and the table is small enough that splitting them would cost
        // more in round trips than it saves in bytes.
        app.MapGet("/api/labels", async (TicketLabelService labels) => Results.Ok(new
        {
            labels = await labels.ListAsync(),
            assignments = await labels.AssignmentsAsync(),
        }));

        app.MapGet("/api/tickets/{id:int}/labels", async (int id, TicketLabelService labels) =>
            Results.Ok(await labels.ForTicketAsync(id)));

        // Assign by NAME, never by id, so both halves of the picker are this one call: a name that
        // exists is matched (case-insensitively), a name that does not is created on the spot. The
        // client never has to know which of the two it is doing, which is what lets the input accept
        // a typed word and a clicked suggestion through the same path.
        app.MapPost("/api/tickets/{id:int}/labels", async (int id, LabelNameRequest req, TicketLabelService labels) =>
        {
            if (TicketLabelService.Normalize(req.Name) is null)
                return Results.BadRequest(new { error = "A label needs a name." });
            var result = await labels.AssignAsync(id, req.Name);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapDelete("/api/tickets/{id:int}/labels/{labelId:int}", async (int id, int labelId, TicketLabelService labels) =>
        {
            var result = await labels.UnassignAsync(id, labelId);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // Rename everywhere at once — the fix for a typo already filed on nine tickets. Merges when
        // the new name is already taken; see TicketLabelService.RenameAsync.
        app.MapPut("/api/labels/{id:int}", async (int id, LabelNameRequest req, TicketLabelService labels) =>
        {
            if (TicketLabelService.Normalize(req.Name) is null)
                return Results.BadRequest(new { error = "A label needs a name." });
            var renamed = await labels.RenameAsync(id, req.Name);
            return renamed is null ? Results.NotFound() : Results.Ok(renamed);
        });

        // Retire a label from every ticket wearing it. Taking the last one off a ticket already
        // deletes the label, so this is for the other case: a label still in use that has stopped
        // being worth filing under.
        app.MapDelete("/api/labels/{id:int}", async (int id, TicketLabelService labels) =>
            await labels.DeleteAsync(id) ? Results.Ok(new { deleted = true }) : Results.NotFound());
    }
}
