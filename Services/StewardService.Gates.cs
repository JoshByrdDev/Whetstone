using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// The two review gates between a worker's phases: a plan waiting for its yes (NeedsApproval) and
/// built work waiting for a look before QA spends time on it (NeedsImplementationReview).
/// <para>
/// A steward passes them in the owner's place, as it signs off finished work. Neither gate is
/// where it is meant to spend its runs - most tickets skip both - but a ticket stopped at one with
/// nobody to pass it waits for the owner however small the question, and an expansion item stops
/// a ticket there whatever the ticket's setting says.
/// </para>
/// <para>
/// One gate stays the owner's: a ticket the owner put on it by hand. That is a person saying they
/// want to look themselves, and the move log is where it is written.
/// </para>
/// </summary>
public partial class StewardService
{
    private static readonly TicketPhase[] Gates = { TicketPhase.NeedsApproval, TicketPhase.NeedsImplementationReview };

    /// <summary>What a ticket at a gate is held for, in one line; null when it is at none.</summary>
    public static string? GateNote(Ticket t)
    {
        if (t.Phase == TicketPhase.NeedsApproval)
        {
            var n = WorkItemScopes.OpenExpansionCount(t);
            var plan = $"its plan ({t.WorkItems.Count} work item{(t.WorkItems.Count == 1 ? "" : "s")})";
            return n > 0 && !t.RequiresSpeculationReview
                ? $"{plan} waits for a yes only because of {n} expansion item{(n == 1 ? "" : "s")}"
                : n > 0 ? $"{plan} waits for a yes, with {n} expansion item{(n == 1 ? "" : "s")}"
                : $"{plan} waits for a yes";
        }
        if (t.Phase == TicketPhase.NeedsImplementationReview) return "its build waits for a look before QA";
        return null;
    }

    /// <summary>
    /// Whether the owner moved this ticket onto the gate it stands at by hand: the last move into
    /// its phase was a person's. A gate reached by a worker's hand-off is the steward's to pass.
    /// </summary>
    private async Task<bool> OwnerPlacedAsync(Ticket t, CancellationToken ct)
    {
        var last = await _db.PhaseTransitions.AsNoTracking()
            .Where(p => p.TicketId == t.Id && p.To == t.Phase)
            .OrderByDescending(p => p.At).ThenByDescending(p => p.Id)
            .Select(p => (TransitionActor?)p.Actor).FirstOrDefaultAsync(ct);
        return last == TransitionActor.Human;
    }

    /// <summary>
    /// Pass a ticket's gate: a plan on to be built, or a build on to QA. At the plan's gate any
    /// expansion items named in <paramref name="decline"/> are taken out of the plan first; the
    /// ones left open are approved with it.
    /// </summary>
    public async Task<StewardReply> ApproveAsync(string projectKey, int ticketId, string why,
        IReadOnlyList<int>? decline = null, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        if (string.IsNullOrWhiteSpace(why) || why.Trim().Length < 40)
            return Refuse("bad-request", "Say why it holds: what you read, and what makes it right to build (or to test) as it stands.");

        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (!Gates.Contains(t.Phase))
            return Refuse("not-at-a-gate", $"Ticket {ticketId} is in {t.Phase}. Only a ticket at NeedsApproval or NeedsImplementationReview is approved from here.");
        if (await OwnerPlacedAsync(t, ct))
            return Refuse("owner", $"The owner put ticket {ticketId} at {t.Phase} by hand, so they mean to look themselves. Leave it there.");

        var declined = (decline ?? Array.Empty<int>()).Distinct().ToList();
        if (declined.Count > 0 && t.Phase != TicketPhase.NeedsApproval)
            return Refuse("bad-request", "Expansion items are declined at the plan's gate (NeedsApproval), not after the work is built.");
        var openExpansion = t.WorkItems.Where(w => w.Scope == WorkItemScope.Expansion && !w.Done).ToList();
        if (declined.Where(id => openExpansion.All(w => w.Id != id)).ToList() is { Count: > 0 } notExpansion)
            return Refuse("bad-request", $"Not an open expansion item on ticket {ticketId}: {string.Join(", ", notExpansion)}. "
                + "Only expansion items are declined here; a plan that is wrong otherwise is sent back.");
        var kept = openExpansion.Where(w => !declined.Contains(w.Id)).ToList();

        var to = t.Phase == TicketPhase.NeedsApproval ? TicketPhase.NeedsImplementation : TicketPhase.NeedsQa;
        if (!PhaseMachine.CanTransition(t.Phase, to, t.Track))
            return Refuse("refused", $"The board does not allow {t.Phase} to {to}.");

        var what = t.Phase == TicketPhase.NeedsApproval ? "plan" : "build";
        string Items(IEnumerable<WorkItem> ws) => string.Join("; ", ws.Select(w => $"#{w.Id} {Head(w.Text, 120)}"));
        var parts = new List<string>();
        if (declined.Count > 0) parts.Add($"declined expansion {Items(openExpansion.Where(w => declined.Contains(w.Id)))}");
        if (kept.Count > 0) parts.Add($"approved expansion {Items(kept)}");
        var detail = parts.Count > 0 ? $" ({string.Join("; ", parts)})" : "";

        if (cfg.StewardMode == StewardMode.Propose)
            return Proposed($"approve the {what} of ticket {ticketId}{detail}, on to {to}: {Head(why, 200)}");

        foreach (var id in declined) await _tickets.DeleteWorkItemAsync(id);
        var body = $"Approved the {what}{detail}.\n\n{why.Trim()}";
        await _tickets.AddCommentAsync(ticketId, body, AuthorKind.Steward);
        // The owner reads every call back and can reverse it; an approval is one.
        _db.StewardEntries.Add(new StewardEntry
        {
            ProjectKey = projectKey, Kind = StewardEntryKind.Call, TicketId = ticketId,
            Text = $"Approved the {what} of #{ticketId}{detail}: {why.Trim()}",
        });
        await _db.SaveChangesAsync(ct);
        await _tickets.TransitionAsync(ticketId, to, $"Approved by the steward: {Head(why, 200)}", TransitionActor.Steward);
        return new StewardReply("approved", null, new { to = to.ToString(), declined, approvedExpansion = kept.Select(w => w.Id) });
    }
}
