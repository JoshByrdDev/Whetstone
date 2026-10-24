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
        "Otherwise it answers with the mode, why this run was started, any answers and standing instructions the owner has " +
        "given since the last run that ended properly, and the owner's own changes to the register of decisions " +
        "(changedDecisions: recorded, withdrawn, or now replacing another) - each is a decision made: apply them first. " +
        "When it says the last run did not finish, that run may have acted on some of them already: check the board before " +
        "acting again. " +
        "It also hands over the result of bringing the base branch up to date, and the board's status. In 'propose' mode every tool that changes " +
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
        "and how much of each is used; the plan's units and which are agreed; how far the unit being worked is; every outcome " +
        "not yet Done in every agreed unit, with its state, note and tickets; what is waiting on the owner; your open ticket notes (ticketNotes), your rulings in force, facts " +
        "not yet in the notes store (learnedPending), and what the last three runs did (recentRuns). Changes nothing.")]
    public static Task<StewardReply> Status(StewardService s, [Description("The project key.")] string project) =>
        s.StatusAsync(project);

    [McpServerTool(Name = "steward_decisions"), Description(
        "The register of decisions: what the owner has decided and is still in force, each by its number (#n). It holds " +
        "the plan's decisions, every unit's settled decisions, the answers to your questions and the owner's standing " +
        "words, in one place, with one status each. By default only what is in force comes back; a provisional one is " +
        "marked with what it lasts until. An answer comes with every option it was picked from ('offered': picked, your " +
        "own pick, or neither), because what was turned down is decided too. Narrow it by unit (that slice's decisions " +
        "and the project's), by outcome within a unit, or by a query - search it before answering a blocked ticket, " +
        "writing a ticket's criteria or asking the owner. Pass history to see replaced and withdrawn decisions too, " +
        "each with what replaced it; never act on one of those. 'next' is the number a draft's next open decision takes.")]
    public static Task<StewardReply> Decisions(StewardService s,
        [Description("The project key.")] string project,
        [Description("Optional text to look for in the rule, the question, its key or its options.")] string? query = null,
        [Description("Optional unit of the plan being worked: its decisions, and the project's.")] int? unit = null,
        [Description("Optional outcome id within that unit, like A2: only the unit's decisions that apply to it.")] string? outcome = null,
        [Description("True to include replaced and withdrawn decisions, as history.")] bool history = false) =>
        s.DecisionsAsync(project, query, unit, outcome, history);

    [McpServerTool(Name = "steward_review"), Description(
        "A finished ticket's record: what it asked, the summary, work items, each check and whether it carries a current pass, " +
        "the independent review's findings, how often it has been sent back, the files changed, and FLAGS. A flag marked " +
        "'owner' is never yours to wave through: ask about it, with the ticket's id, and leave the ticket unmerged - unless it " +
        "is listed under clearedByOwner, which only the owner sets, from the board. A flag marked 'blocks' means the " +
        "work is not proved: send it back saying what the flag says.")]
    public static Task<StewardReply> Review(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("Optional: one part of a long review - record (description, summary, work items, comments), checks " +
                     "(QA items), findings (independent review), or flags. Leave it out first: a review that fits comes whole, " +
                     "and one that does not comes as its head and flags with each part's size.")] string? part = null) =>
        s.ReviewAsync(project, ticketId, part);

    [McpServerTool(Name = "steward_close_unchanged"), Description(
        "Close a ticket at NeedsReview that has nothing to merge, because what it asked for is already true on the base " +
        "(steward_merge answered 'nothing-to-merge'). Only when its review holds - its checks passed on the base - and only in " +
        "full mode; in manage mode closing is the owner's: ask, with the ticket's id. Say why: what on the base already does " +
        "it. That goes on the record for the owner. Then set its outcomes and note anything for release, as after a merge.")]
    public static Task<StewardReply> CloseUnchanged(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("Why there is nothing to merge: what on the base already does what it asked, and where.")] string why) =>
        s.CloseUnchangedAsync(project, ticketId, why);

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
        "'owner' - ask, and leave it unmerged (the owner clears a flag from the board; an answer does not); 'not-proved' - send it back saying what the flags say; 'behind' - rebase, " +
        "'nothing-to-merge' - its answer says what to do (steward_close_unchanged when the work is already on the base); " +
        "then merge again; 'check-failed' - send it back to NeedsImplementation with the output; 'check-could-not-run' - the " +
        "machine, not the ticket: leave it at review and ask the owner once; 'conflict' - send it back to NeedsImplementation " +
        "naming the files; 'not-in-this-mode' - merging is not yours here. None of them means look for another way. " +
        "'checking' is not a refusal: the project's own checks can run for minutes and a call cannot wait that long, so the " +
        "merge carries on without you. Call merge for the same ticket again until it answers with one of the others - each " +
        "call joins the same merge - and do not end the run, rebase the ticket or ask the owner about it while it is checking.")]
    public static Task<StewardReply> Merge(StewardMerges merges,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        CancellationToken ct) =>
        merges.MergeAsync(project, ticketId, ct);

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
        "and quote the decision that settles it by its number (#n), or the spec and section. Your " +
        "answer is handed to the run that picks the ticket up. When something merely broke and the cause has passed, the " +
        "answer is 'retry'. Anything no decision settles is the owner's: leave it blocked and ask.")]
    public static Task<StewardReply> AnswerBlocked(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("The choice, and the decision that settles it (#n), quoted.")] string answer) =>
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
        "in a draft may be ticketed or used to answer a question. Number what it leaves open from the 'next' that " +
        "steward_decisions gives: a number the register already holds is refused ('number-taken').")]
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
        "else moving. When what you need is the owner's answer to decisions a unit's spec leaves open under \"To decide\", " +
        "name the unit and those decisions' numbers instead of summarising them: the board shows the owner each one as the " +
        "spec writes it, with its options and your pick, and writes the answers into the spec under \"Decided\". " +
        "Before asking, search steward_decisions for the subject, and say in 'touches' every decision in force the question " +
        "bears on - an empty list is your claim that none does. The owner is shown each touched decision beside the " +
        "question, and an answer that changes one replaces it in the register; say on each option which it would change.")]
    public static Task<StewardReply> AskOwner(StewardService s,
        [Description("The project key.")] string project,
        [Description("A short name for this question, lowercase with hyphens, like sms-provider. Yours for good.")] string key,
        [Description("The question, as one line a person can answer.")] string question,
        [Description("Why it matters now, with whatever fact the owner needs to choose.")] string why,
        [Description("What is held up until this is answered.")] string heldUp,
        [Description("The numbers of the decisions in force this question bears on, from steward_decisions. Required: an empty list when none does.")] int[] touches,
        [Description("At least two options. Put what each costs and rules out in its detail, and mark exactly one - the one you would pick - as recommended. Leave out when you name decisions: those are their own options.")] StewardOption[]? options = null,
        [Description("The ticket that raised it, when one did.")] int? ticketId = null,
        [Description("The unit of the plan whose open decisions this asks about.")] int? unit = null,
        [Description("Those decisions' numbers, as the spec numbers them under \"To decide\".")] int[]? decisions = null) =>
        s.AskAsync(project, new StewardAsk(key, question, why,
            options?.Select(o => new BlockOption(o.Label, o.Detail, o.Recommended)).ToList(), heldUp, ticketId, unit, decisions, touches,
            options?.Where(o => o.Changes is { Length: > 0 }).ToDictionary(o => o.Label, o => (IReadOnlyList<int>)o.Changes!)));

    [McpServerTool(Name = "steward_record_call"), Description(
        "Record a call you made for the owner about one ticket - an answer you gave it, a choice you made in filing it, what " +
        "you kept or sent back at its review - in two or three lines: what was asked or chosen, the choice, the place that " +
        "decides it. It is for the owner to read back and reverse. You will not be shown it again, so nothing you need later " +
        "belongs here.")]
    public static Task<StewardReply> RecordCall(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("What was asked or chosen, the choice, and the place that decides it.")] string text) =>
        s.RecordAsync(project, StewardEntryKind.Call, ticketId, text);

    [McpServerTool(Name = "steward_note_ticket"), Description(
        "Write down one thing you mean to do about one ticket, for your later runs: 'when this merges, set C4 Done and note " +
        "its words for release', 'when it is back at review, merge it if only the rebase changed'. Write it the moment you " +
        "decide it, not at the end of the run: a run that stops part-way keeps what it wrote. It is shown first by " +
        "steward_review for that ticket and in every run's status (ticketNotes) until you close it with steward_done_with. " +
        "One thing per note. Not a fact about the repo, and not a call for the owner (that is steward_record_call).")]
    public static Task<StewardReply> NoteTicket(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("What is to be done, and when.")] string text) =>
        s.NoteTicketAsync(project, ticketId, text);

    [McpServerTool(Name = "steward_done_with"), Description(
        "Close a ticket note, by the id steward_note_ticket gave it: what it said is done, or is no longer to be done. " +
        "Close it in the same breath as doing it, so the next run is never told to do it again.")]
    public static Task<StewardReply> DoneWith(StewardService s,
        [Description("The project key.")] string project, [Description("The note's id.")] int noteId) =>
        s.DoneWithAsync(project, noteId);

    [McpServerTool(Name = "steward_rule"), Description(
        "Record a standing call you will apply again: something you have taken as settled for tickets still to be written " +
        "or reviews still to come, like 'a by-inspection pass on an infra/ change is fine when a test asserts the config'. " +
        "Say what it rests on - the decisions it reads by number (#n), or the spec and section - because a ruling " +
        "is only a reading of decisions already made, never a new one: anything no decision settles is the owner's to " +
        "answer, not yours to rule. It applies at once and every run is handed every ruling in force (status: rulings). " +
        "The owner sees each one and may reverse it; a reversal reaches your next run with what should hold instead. " +
        "Record it the moment you make the call. Not a call about one ticket (steward_record_call), not a fact about the repo.")]
    public static Task<StewardReply> Rule(StewardService s,
        [Description("The project key.")] string project,
        [Description("The ruling, as one sentence you could apply without context.")] string ruling,
        [Description("What it rests on: the decisions it reads, by number (#n), or the spec and section.")] string restsOn,
        [Description("The ticket that raised it, when one did.")] int? ticketId = null) =>
        s.RuleAsync(project, ruling, restsOn, ticketId);

    [McpServerTool(Name = "steward_learned"), Description(
        "Put a fact about the repo into the project's notes store: where a screen or a function lives, how a check or the " +
        "stack behaves, what the seed holds, a port that is taken. Something a worker or the owner would be just as wrong " +
        "without - first-hand, not a guess. It is saved the moment you call this, and placed in the store after the run, " +
        "beside or folded into the note that already covers it; until then status lists it under learnedPending. One fact " +
        "per call, stated so it stands on its own. Not a ruling (steward_rule), not something to do (steward_note_ticket).")]
    public static Task<StewardReply> Learned(StewardService s,
        [Description("The project key.")] string project,
        [Description("The fact, in one or two sentences, with the paths and names that make it useful.")] string fact,
        [Description("Optional short name for its note, lowercase with hyphens, like visit-day-read.")] string? key = null) =>
        s.LearnedAsync(project, fact, key);

    [McpServerTool(Name = "steward_note_for_release"), Description(
        "After merging a ticket that leaves the owner something to read before the base branch is released: a migration and " +
        "what it adds, a line a worker wrote for the owner's own documents (copy it as written, with the file and section it " +
        "names), a step to do by hand. A few lines, facts only. A ticket that leaves nothing gets no entry. One note is one " +
        "kind of thing, because the owner reads them by kind: a ticket that leaves a migration and wording to check gets two " +
        "notes. Start with the one sentence that would do if it were all the owner read.")]
    public static Task<StewardReply> NoteForRelease(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("What the owner should read or do, in a few lines.")] string text,
        [Description("What kind of thing it is. 'migration': the merge carries a migration or any change to the schema or data " +
            "that runs on release. 'config': releasing needs something set up outside the code - a variable, a secret, " +
            "infrastructure, an account. 'action': something for the owner to do, check or approve - wording to read, a line " +
            "for their own documents, a step by hand. 'note': only to know.")] string kind) =>
        s.RecordAsync(project, StewardEntryKind.BeforeRelease, ticketId, text, kind);
}

/// <summary>
/// One option of a steward's question to the owner. Its own shape rather than the worker's
/// <see cref="BlockOption"/>, because only a steward's question says which decisions an option changes.
/// </summary>
public record StewardOption(
    [Description("The choice itself, as a short phrase a person can pick off a list.")] string Label,
    [Description("What taking it costs and rules out, in a line.")] string? Detail = null,
    [Description("Set on the ONE option you would pick.")] bool Recommended = false,
    [Description("Which of the question's touched decisions picking this would change, by number. Empty when it leaves them as they are.")] int[]? Changes = null);
