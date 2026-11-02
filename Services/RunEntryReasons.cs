using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// Why a worker run happened: what put its ticket in the queue it was claimed from. Stored on
/// <see cref="CycleRun.EntryReason"/> as one of these words.
/// <para>
/// The point is to split a phase's time by cause. A QA run that re-tests work it failed an hour ago
/// and one seeing a ticket for the first time are the same skill doing different jobs, and so are an
/// implement run building from a plan and one resolving a merge conflict - averaged together, they
/// describe neither, and the share that is rework is the number most worth driving down.
/// </para>
/// </summary>
public static class RunEntryReasons
{
    /// <summary>The ticket came forward from the phase before, as the pipeline intends.</summary>
    public const string Forward = "forward";
    /// <summary>QA failed it back to implementation.</summary>
    public const string QaFailed = "qa-failed";
    /// <summary>Refinement sent it back to implementation or QA.</summary>
    public const string RefineSentBack = "refine-sent-back";
    /// <summary>The branch no longer merged with its base, found by a start rebase or the hand-off rebase.</summary>
    public const string Conflict = "conflict";
    /// <summary>A reviewer - the owner or the steward - asked for changes.</summary>
    public const string ReviewChanges = "review-changes";
    /// <summary>It had been blocked and was let go.</summary>
    public const string Unblocked = "unblocked";
    /// <summary>The run picked up a ticket already in its working phase, after an earlier run ended without handing off.</summary>
    public const string Resumed = "resumed";

    /// <summary>
    /// The words the worker's own conflict send-backs use (data/worker.ps1: "Branch conflicts with
    /// &lt;base&gt; and cannot be merged as it stands"). They are what tells a conflict from a reviewer's
    /// changes on the same edge, so a rewording there has to keep them.
    /// </summary>
    public const string ConflictMarker = "conflicts with";

    /// <summary>Which of the reasons above the move that queued a run was.</summary>
    public static string Of(PhaseTransition entry)
    {
        if (entry.From == TicketPhase.Blocked) return Unblocked;
        if (entry.Reason?.Contains(ConflictMarker, StringComparison.OrdinalIgnoreCase) == true) return Conflict;
        if (!entry.Backward) return Forward;
        return entry.From switch
        {
            TicketPhase.QaTesting => QaFailed,
            TicketPhase.Refining => RefineSentBack,
            _ => ReviewChanges,
        };
    }
}
