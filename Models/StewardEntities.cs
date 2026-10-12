namespace Whetstone.Models;

/// <summary>
/// How much a project's steward may do. Set by a person, per project, on this machine - never by
/// the steward, and no tool it holds can change it.
/// <para>
/// The steps are the ones a person would take in trusting it: first only watch what it would do,
/// then let it run the board while every merge stays theirs, then let it merge too.
/// </para>
/// </summary>
public enum StewardMode
{
    /// <summary>No steward. Nothing is scheduled and every steward tool refuses.</summary>
    Off,
    /// <summary>It runs, reads and decides as normal, and every change it would make is only reported.</summary>
    Propose,
    /// <summary>It files, answers, amends and sends back. It never merges.</summary>
    Manage,
    /// <summary>It also merges a finished ticket into the base branch and closes it.</summary>
    Full,
}

/// <summary>
/// One question a steward has put to the project's owner, and the answer once there is one.
/// <para>
/// A steward acts for the owner only where the owner has already decided; everything else it has
/// to ask, and this is the asking. It is a row rather than a section of a file because three
/// things read it and none of them should be parsing prose: the board shows what is waiting, the
/// notifier nudges about what is still waiting tomorrow, and the steward's next run reads the
/// answer as a decision made.
/// </para>
/// <para>
/// Asked once. <see cref="Key"/> is the steward's own short name for the question and is unique
/// within the project for good - answered questions keep theirs - so a run that has forgotten it
/// already asked is refused rather than believed.
/// </para>
/// </summary>
public class StewardQuestion
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";

    /// <summary>Lowercase with hyphens, like <c>sms-provider</c>. The steward's handle on it.</summary>
    public string Key { get; set; } = "";

    public string Question { get; set; } = "";

    /// <summary>Why it matters now, for someone reading it on a phone with no context.</summary>
    public string Why { get; set; } = "";

    /// <summary>
    /// The choices, as a JSON array of <see cref="BlockOption"/> - the shape a blocked ticket's
    /// questions already use, so the board answers both with the same control. The steward's own
    /// pick is the one marked recommended.
    /// </summary>
    public string OptionsJson { get; set; } = "[]";

    /// <summary>What is held up until this is answered.</summary>
    public string HeldUp { get; set; } = "";

    /// <summary>The ticket that raised it, when one did.</summary>
    public int? TicketId { get; set; }

    public DateTime AskedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The owner's answer, in their words or their click. Null while it waits.</summary>
    public string? Answer { get; set; }
    public DateTime? AnsweredAt { get; set; }

    /// <summary>
    /// Set once a steward run has been handed the answer. An answer is a reason to run; one already
    /// delivered is not, which is what stops an answered question waking the steward for ever.
    /// </summary>
    public DateTime? AnswerDeliveredAt { get; set; }

    /// <summary>The local day (yyyy-MM-dd) a reminder last went out, so there is one a day at most.</summary>
    public string? RemindedOn { get; set; }
}

/// <summary>What kind of line a <see cref="StewardEntry"/> is.</summary>
public enum StewardEntryKind
{
    /// <summary>
    /// A call the steward made for the owner about one ticket: an answer it gave, a choice it made
    /// in filing it, what it kept or sent back at review - with the place that decided it. Written
    /// for the owner to read back and reverse; the steward does not read these again.
    /// </summary>
    Call,
    /// <summary>
    /// Something the owner should read before the base branch is released: a migration, a line a
    /// worker wrote for the owner's own docs, a step to do by hand. Also never read back.
    /// </summary>
    BeforeRelease,
    /// <summary>
    /// A standing instruction from the owner that is not the answer to a question. A decision made,
    /// and handed to every run along with the answers.
    /// </summary>
    OwnerSaid,
}

/// <summary>One line in a steward's record: a call it made, a note for release, or a word from the owner.</summary>
public class StewardEntry
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public StewardEntryKind Kind { get; set; }
    public int? TicketId { get; set; }
    public string Text { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// For <see cref="StewardEntryKind.OwnerSaid"/>: when a run was first handed this instruction.
    /// Until then it is news, and is put at the front of the next run rather than left to be found
    /// among everything the owner has ever decided.
    /// </summary>
    public DateTime? DeliveredAt { get; set; }
}

/// <summary>
/// What a project's steward carries from one run to the next, and what decides whether it runs.
/// <para>
/// A run starts from nothing: the plan, the board and this row. <see cref="Memo"/> is its own
/// note to its next run and is the only free text here, held to a length because every run reads
/// all of it - a memo left to grow is a tax on every run after.
/// </para>
/// </summary>
public class StewardState
{
    /// <summary>One row per project; the project's key is the row's.</summary>
    public string ProjectKey { get; set; } = "";

    public string Memo { get; set; } = "";
    public DateTime? MemoUpdatedAt { get; set; }

    /// <summary>
    /// Why a run is owed, or null when none is. Set by whatever changed (a ticket came for review,
    /// one blocked, the owner answered) and cleared when a run begins.
    /// </summary>
    public string? DueReason { get; set; }
    public DateTime? DueSince { get; set; }

    /// <summary>
    /// The base branch's head at the end of a run that left nothing to file. While the head is
    /// still this and nothing is due, there is nothing for a run to find, and none is started.
    /// </summary>
    public string? RestingAtSha { get; set; }

    public DateTime? LastRunStartedAt { get; set; }
    public DateTime? LastRunEndedAt { get; set; }
    public string? LastRunSummary { get; set; }

    /// <summary>The local day the count below belongs to; a new day starts it again.</summary>
    public string? CreatedDay { get; set; }
    public int CreatedCount { get; set; }
}

/// <summary>Where one outcome of the plan stands, as its steward last judged it.</summary>
public enum StewardOutcomeState
{
    NotStarted,
    InProgress,
    Done,
}

/// <summary>
/// The steward's standing judgment on one outcome of one unit of the plan.
/// <para>
/// Which tickets deliver an outcome is on the tickets themselves (<see cref="Ticket.PlanOutcomes"/>),
/// and the board can count those. What it cannot count is whether they are ALL of it: an outcome
/// with two merged tickets may be finished or may be waiting on a third nobody has written yet.
/// That is a judgment, so it is recorded as one, here, and it is the only thing that calls an
/// outcome done.
/// </para>
/// </summary>
public class StewardOutcome
{
    public int Id { get; set; }
    public string ProjectKey { get; set; } = "";
    public int Unit { get; set; }

    /// <summary>The outcome's id in its spec, like <c>A2</c>.</summary>
    public string Outcome { get; set; } = "";

    public StewardOutcomeState State { get; set; }

    /// <summary>True while a ticket for this outcome is still to be written.</summary>
    public bool MoreToFile { get; set; }

    /// <summary>A line for the next run: what is left to file, and what it waits on.</summary>
    public string? Note { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
