using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

public partial class StewardService
{
    /// <summary>The fleet's berths as they stand; null where the app was not given one (tests).</summary>
    private CapacitySnapshot? Capacity => _capacity;

    /// <summary>Reasons a place has free berths that no ticket could fill however many were ready.</summary>
    private static readonly HashSet<string> CannotTakeWork = new(StringComparer.Ordinal)
    {
        CapacityReasons.Stopped, CapacityReasons.OffHours, CapacityReasons.HostOff, CapacityReasons.HostOffline,
        CapacityReasons.UsageLimit, CapacityReasons.AccountFull, CapacityReasons.HostBehind,
    };

    /// <summary>
    /// How much of the fleet this project's queue can use right now: free berths against the
    /// tickets a worker could start. The steward files from it - when berths sit free and the ready
    /// queue is short of them, the plan's next independent outcomes are what fills them. Berths are
    /// the fleet's, shared with every project it works, so this is an upper bound on what is free
    /// for this one.
    /// </summary>
    private async Task<object> CapacityAsync(IReadOnlyList<Ticket> all)
    {
        var open = all.Where(t => t.Phase != TicketPhase.Done && t.Phase != TicketPhase.Backlog).ToList();
        var waiting = _deps is null || open.Count == 0
            ? new HashSet<int>()
            : await _deps.BlockedTicketIdsAsync(open.Select(t => t.Id).ToList());
        var queued = open.Count(t => !waiting.Contains(t.Id) && t.Phase is TicketPhase.NeedsSpeculation
            or TicketPhase.NeedsImplementation or TicketPhase.NeedsQa or TicketPhase.NeedsRefinement or TicketPhase.NeedsPeerReview);
        var working = open.Count(t => PhaseMachine.IsInProgress(t.Phase));
        var atReview = open.Count(t => t.Phase is TicketPhase.NeedsReview or TicketPhase.NeedsApproval
            or TicketPhase.NeedsImplementationReview);
        var waitingCount = open.Count(t => waiting.Contains(t.Id));

        int? free = null;
        object? places = null;
        if (Capacity is { } snapshot)
        {
            var now = DateTime.UtcNow;
            var read = snapshot.Read().Select(p =>
            {
                var reason = CapacityClassifier.Classify(p, now).Reason;
                var busy = Math.Min(p.Busy, p.Slots);
                var canWork = reason is null || !CannotTakeWork.Contains(reason);
                return new { place = p.Place, slots = p.Slots, busy, free = canWork ? p.Slots - busy : 0, reason };
            }).ToList();
            free = read.Sum(p => p.free);
            places = read;
        }

        var shortBy = free is { } f ? Math.Max(0, f - queued) : (int?)null;
        return new
        {
            freeBerths = free,
            readyToStart = queued,
            working,
            atReviewOrGate = atReview,
            waitingOnAnother = waitingCount,
            shortBy,
            say = free is null
                ? "The fleet's berths could not be read; file by the plan as usual."
                : shortBy > 0
                    ? $"{free} berth(s) are free and only {queued} ticket(s) are ready to start: the queue is {shortBy} short. File the plan's next outcomes that do not wait on unmerged work."
                    : $"{queued} ticket(s) are ready for {free} free berth(s): the queue covers the fleet.",
            places,
        };
    }
}
