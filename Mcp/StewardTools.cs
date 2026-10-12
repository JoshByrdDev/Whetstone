using System.ComponentModel;
using ModelContextProtocol.Server;
using Whetstone.Models;
using Whetstone.Services;

namespace Whetstone.Mcp;

/// <summary>
/// The tools a project's steward works through, and the only way it reaches the board or git.
/// <para>
/// Served at <c>/mcp/steward</c> and nowhere else: a worker's session is given <c>/mcp</c>, which
/// does not list these, and a steward's is given this endpoint, which lists nothing but these. That
/// is separation by absence - the same kind the worker tools already rely on for "there is no tool
/// that signs a ticket off" - and it is the right size for what it guards against, which is an
/// agent reaching for a tool because it is there.
/// </para>
/// <para>
/// Every tool is a thin door onto <see cref="StewardService"/>, where the rules are. A tool that
/// answers with a refusal ("owner", "limit", "not-in-this-mode") is the limit working: the
/// descriptions say what to do next, and none of them is "try another way".
/// </para>
/// </summary>
[McpServerToolType]
public class StewardTools
{
    /// <summary>Every tool here carries this prefix; it is what the endpoint filter keys on.</summary>
    public const string Prefix = "steward_";

    [McpServerTool(Name = "steward_begin"), Description(
        "Open a run. Call this first, once. 'locked' means another run is still going: stop now and do nothing. " +
        "Otherwise it answers with the mode, why this run was started, any answers the owner has given since the last run, " +
        "the result of bringing the base branch up to date, and the board's status. In 'propose' mode every tool that changes " +
        "something only says what it would have done: carry on exactly as normal, so the owner can read what you would do.")]
    public static Task<StewardReply> Begin(StewardService s, [Description("The project key.")] string project) =>
        s.BeginAsync(project);

    [McpServerTool(Name = "steward_end"), Description(
        "Close the run and release its lock. ALWAYS call this last, even when a step failed: a run that ends without it " +
        "stops every run after it until the lock goes stale. Set nothingToFile only when this run leaves nothing the next " +
        "could start by itself - every outcome you could ticket is ticketed or waits on a merge or on the owner, no draft is " +
        "owed, and no limit stopped you short. The next run is then not started until a ticket comes for review, one blocks, " +
        "the owner answers, or the base branch moves. When in doubt leave it false: a run costs only tokens.")]
    public static Task<StewardReply> End(StewardService s,
        [Description("The project key.")] string project,
        [Description("One line: what this run did (merged, sent back, answered, created, asked).")] string summary,
        [Description("True when nothing is left for the next run to start by itself.")] bool nothingToFile = false) =>
        s.EndAsync(project, summary, nothingToFile);

    [McpServerTool(Name = "steward_status"), Description(
        "The board as it stands: every open ticket with its phase, what it waits on and any question it asked; the limits " +
        "and how much of each is used; the plan's units and which are agreed; how far the unit being worked is; each outcome's " +
        "state and tickets; what is waiting on the owner; and your own memo from the last run. Changes nothing.")]
    public static Task<StewardReply> Status(StewardService s, [Description("The project key.")] string project) =>
        s.StatusAsync(project);

    [McpServerTool(Name = "steward_decisions"), Description(
        "What the owner has decided through you: every answer to a question you asked and every standing instruction they " +
        "left, oldest first. Each is a decision made. Pass a query to get only those that mention it - the full list grows " +
        "for as long as the project runs, so search it before answering a blocked ticket or writing a ticket's criteria.")]
    public static Task<StewardReply> Decisions(StewardService s,
        [Description("The project key.")] string project,
        [Description("Optional text to look for in the question, its key or the answer.")] string? query = null) =>
        s.DecisionsAsync(project, query);

    [McpServerTool(Name = "steward_review"), Description(
        "A finished ticket's record: what it asked, the summary, work items, each check and whether it carries a current pass, " +
        "the independent review's findings, how often it has been sent back, the files changed, and FLAGS. A flag marked " +
        "'owner' is never yours to wave through: ask about it and leave the ticket unmerged. A flag marked 'blocks' means the " +
        "work is not proved: send it back saying what the flag says.")]
    public static Task<StewardReply> Review(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId) =>
        s.ReviewAsync(project, ticketId);

    [McpServerTool(Name = "steward_diff"), Description(
        "The ticket's change itself, as one diff against its base. A long diff is cut and says so: ask again with a path " +
        "for one file's share of it.")]
    public static Task<StewardReply> Diff(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("Optional repo-relative path, for one file of the diff.")] string? path = null) =>
        s.DiffAsync(project, ticketId, path);

    [McpServerTool(Name = "steward_merge"), Description(
        "Check a ticket at NeedsReview, then merge it into the base branch and close it. Call it only once you have read the " +
        "review and the diff and are signing the work off. Every answer short of 'merged' names what to do instead: " +
        "'owner' - ask, and leave it unmerged; 'not-proved' - send it back saying what the flags say; 'behind' - rebase, " +
        "then merge again; 'check-failed' - send it back to NeedsImplementation with the output; 'check-could-not-run' - the " +
        "machine, not the ticket: leave it at review and ask the owner once; 'conflict' - send it back to NeedsImplementation " +
        "naming the files; 'not-in-this-mode' - merging is not yours here. None of them means look for another way.")]
    public static Task<StewardReply> Merge(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId) =>
        s.MergeAsync(project, ticketId);

    [McpServerTool(Name = "steward_rebase"), Description(
        "Rebase a ticket's branch onto the base, when a merge answered 'behind'. A conflict is rolled back and changes nothing.")]
    public static Task<StewardReply> Rebase(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId) =>
        s.RebaseAsync(project, ticketId);

    [McpServerTool(Name = "steward_send_back"), Description(
        "Return a ticket at NeedsReview with a note. NeedsImplementation when code must change; NeedsRefinement when the code " +
        "is right but a check or the record is missing (it adds the check and sends the ticket on to be tested); " +
        "NeedsSpeculation when the plan was wrong. The note is an instruction the next worker must answer, so it says what is " +
        "wrong, where, and what done looks like. 'limit' means this ticket has come back too often and the next call is the " +
        "owner's: ask.")]
    public static Task<StewardReply> SendBack(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("NeedsImplementation, NeedsRefinement or NeedsSpeculation.")] string phase,
        [Description("What is wrong, where, and what done looks like.")] string note) =>
        s.SendBackAsync(project, ticketId, phase, note);

    [McpServerTool(Name = "steward_answer_blocked"), Description(
        "Answer a blocked ticket and return it to the queue it stopped in. ONLY from a decision already made: say the choice " +
        "and quote the place that decides it (the spec and section, the file and line, the owner's answer by its key). Your " +
        "answer is handed to the run that picks the ticket up. When something merely broke and the cause has passed, the " +
        "answer is 'retry'. Anything no decision settles is the owner's: leave it blocked and ask.")]
    public static Task<StewardReply> AnswerBlocked(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("The choice, and the place that decides it, quoted.")] string answer) =>
        s.AnswerBlockAsync(project, ticketId, answer);

    [McpServerTool(Name = "steward_amend"), Description(
        "Correct a ticket that was itself wrong: a criterion that was false, two that disagree, or testing set the wrong way. " +
        "Give only what changes. It leaves a note on the ticket that a worker mid-flight will be shown.")]
    public static Task<StewardReply> Amend(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("The new title, or omit to leave it.")] string? title = null,
        [Description("The new description in full, or omit to leave it.")] string? description = null,
        [Description("True: its tester reads the diff and runs nothing. False: it is tested in full. Omit to leave it.")] bool? codeReviewOnly = null) =>
        s.AmendAsync(project, ticketId, title, description, codeReviewOnly);

    [McpServerTool(Name = "steward_create"), Description(
        "File a ticket against an AGREED unit of the plan. The agent that plans it has the repo and your description and " +
        "nothing else, so the description is whole: what is wanted and why; 'Acceptance criteria' a tester can check one by " +
        "one; what is 'Out of scope'; and where the decision behind it is recorded. Name the tickets it depends on so nothing " +
        "starts on work that is not merged. 'limit' means stop creating: the next run continues. 'unit-not-agreed' means " +
        "exactly that, however obvious the next unit looks.")]
    public static Task<StewardReply> Create(StewardService s,
        [Description("The project key.")] string project,
        [Description("The number of the agreed unit this ticket belongs to.")] int unit,
        [Description("Which of that unit's outcomes this ticket delivers, by id, like [\"A2\"].")] string[] outcomes,
        [Description("One line, 10 to 110 characters.")] string title,
        [Description("The whole ask, with 'Acceptance criteria' and 'Out of scope' parts.")] string description,
        [Description("Ids of tickets that must be merged before this one starts.")] int[]? dependsOn = null,
        [Description("Lowest, Low, Normal or High. Omit for Normal. Critical is the owner's.")] string? priority = null,
        [Description("True when nothing in the change can be exercised by running the app, so its tester reads the diff and " +
                     "runs nothing and its own tests are the proof. When unsure, leave it false.")] bool codeReviewOnly = false) =>
        s.CreateAsync(project, new StewardTicketDraft(unit, outcomes, title, description, dependsOn, priority, codeReviewOnly));

    [McpServerTool(Name = "steward_set_outcome"), Description(
        "Record where one outcome of an agreed unit stands. This, and only this, is what calls an outcome done: set it when " +
        "every ticket it needs is merged. While a ticket for it is still to be written, say so with moreToFile and put what " +
        "is left - and what it waits on - in the note, one line. What a merged ticket built is in the code; do not describe it.")]
    public static Task<StewardReply> SetOutcome(StewardService s,
        [Description("The project key.")] string project,
        [Description("The unit's number.")] int unit,
        [Description("The outcome's id, like A2.")] string outcome,
        [Description("NotStarted, InProgress or Done.")] string state,
        [Description("True while a ticket for this outcome is still to be written.")] bool moreToFile = false,
        [Description("One line for the next run: what is left to file and what it waits on.")] string? note = null) =>
        s.SetOutcomeAsync(project, unit, outcome, state, moreToFile, note);

    [McpServerTool(Name = "steward_draft_unit"), Description(
        "Write or revise the DRAFT spec for the unit after the last agreed one, committed to the base branch. One draft " +
        "ahead, never two. Its status line must say it is a draft: only the owner agrees a unit, and until they do nothing " +
        "in a draft may be ticketed or used to answer a question.")]
    public static Task<StewardReply> DraftUnit(StewardService s,
        [Description("The project key.")] string project,
        [Description("The file's name, numbered like the plan's other units.")] string fileName,
        [Description("The whole spec.")] string text) =>
        s.DraftUnitAsync(project, fileName, text);

    [McpServerTool(Name = "steward_ask_owner"), Description(
        "Put ONE question to the owner, once. Only for a real decision: a choice the plan does not make, anything that " +
        "changes the plan or costs money, a flag marked 'owner', a ticket that has come back too often. Not progress and not " +
        "good news. Write it for someone reading on a phone with no context. It is shown on the board and announced; the " +
        "answer reaches a later run as a decision made. 'already-asked' means it was - never re-ask. Then keep everything " +
        "else moving.")]
    public static Task<StewardReply> AskOwner(StewardService s,
        [Description("The project key.")] string project,
        [Description("A short name for this question, lowercase with hyphens, like sms-provider. Yours for good.")] string key,
        [Description("The question, as one line a person can answer.")] string question,
        [Description("Why it matters now, with whatever fact the owner needs to choose.")] string why,
        [Description("At least two options. Put what each costs and rules out in its detail, and mark exactly one - the one you would pick - as recommended.")] BlockOption[] options,
        [Description("What is held up until this is answered.")] string heldUp,
        [Description("The ticket that raised it, when one did.")] int? ticketId = null) =>
        s.AskAsync(project, new StewardAsk(key, question, why, options, heldUp, ticketId));

    [McpServerTool(Name = "steward_record_call"), Description(
        "Record a call you made for the owner about one ticket - an answer you gave it, a choice you made in filing it, what " +
        "you kept or sent back at its review - in two or three lines: what was asked or chosen, the choice, the place that " +
        "decides it. It is for the owner to read back and reverse. You will not be shown it again, so nothing you need later " +
        "belongs here.")]
    public static Task<StewardReply> RecordCall(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("What was asked or chosen, the choice, and the place that decides it.")] string text) =>
        s.RecordAsync(project, StewardEntryKind.Call, ticketId, text);

    [McpServerTool(Name = "steward_note_for_release"), Description(
        "After merging a ticket that leaves the owner something to read before the base branch is released: a migration and " +
        "what it adds, a line a worker wrote for the owner's own documents (copy it as written, with the file and section it " +
        "names), a step to do by hand. A few lines, facts only. A ticket that leaves nothing gets no entry.")]
    public static Task<StewardReply> NoteForRelease(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("What the owner should read or do, in a few lines.")] string text) =>
        s.RecordAsync(project, StewardEntryKind.BeforeRelease, ticketId, text);

    [McpServerTool(Name = "steward_save_memo"), Description(
        "Replace your memo to the next run, which starts from nothing but the plan, the board and this. It holds only what " +
        "the next run needs and cannot read anywhere else: what you took as decided for tickets still to be written, and two " +
        "short paragraphs on what this run did and what to pick up. Where an outcome stands is set on the outcome; a call " +
        "about one ticket goes on the record. It has a length limit because every run reads all of it: 'saved-too-long' " +
        "means cut it and save again.")]
    public static Task<StewardReply> SaveMemo(StewardService s,
        [Description("The project key.")] string project,
        [Description("The whole memo, replacing what was there.")] string memo) =>
        s.SaveMemoAsync(project, memo);
}
