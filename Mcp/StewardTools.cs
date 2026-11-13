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
        "It also hands over the board's status. In 'propose' mode every tool that changes " +
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
        "and how much of each is used; the slices and which are agreed; every outcome " +
        "not yet Done in every agreed slice, with its state, note and tickets; what is waiting on the owner; your open ticket notes (ticketNotes), your rulings in force, facts " +
        "not yet in the notes store (learnedPending), and what the last three runs did (recentRuns). Changes nothing.")]
    public static Task<StewardReply> Status(StewardService s, [Description("The project key.")] string project) =>
        s.StatusAsync(project);

    [McpServerTool(Name = "steward_decisions"), Description(
        "The register of decisions: what the owner has decided and is still in force, each by its number (#n). It holds " +
        "every slice's settled decisions, the conventions, the answers to your questions and the owner's standing " +
        "words, in one place, with one status each. By default only what is in force comes back; a provisional one is " +
        "marked with what it lasts until. An answer comes with every option it was picked from ('offered': picked, your " +
        "own pick, or neither), because what was turned down is decided too. Narrow it by slice (passed as unit), " +
        "by outcome within a slice, or by a query - search it before answering a blocked ticket, " +
        "writing a ticket's criteria or asking the owner. Pass history to see replaced, withdrawn and retired decisions too, " +
        "each with what replaced it; never act on one of those. A slice's decisions " +
        "retire when it is released - its code is the decision then - and a slice's view is its own, its parent's and " +
        "the conventions. 'next' is the number a draft's next open decision takes.")]
    public static Task<StewardReply> Decisions(StewardService s,
        [Description("The project key.")] string project,
        [Description("Optional text to look for in the rule, the question, its key or its options.")] string? query = null,
        [Description("Optional slice number: its decisions, its parent's and the conventions.")] int? unit = null,
        [Description("Optional outcome id within that slice, like A2: only the slice's decisions that apply to it.")] string? outcome = null,
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
        "then merge again; 'check-failed' - send it back to NeedsImplementation with the output; 'check-timed-out' - a check ran " +
        "out of time with no failure in its output, a busy machine and not the ticket: do not send it back, leave it at review " +
        "and a later run merges it again; 'check-could-not-run' - the machine, not the ticket (including a second time-out on " +
        "the same tip): leave it at review and ask the owner once; 'conflict' - send it back to NeedsImplementation " +
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

    [McpServerTool(Name = "steward_approve"), Description(
        "Pass a ticket at one of the two review gates, in the owner's place. At NeedsApproval its plan goes on to be built; at " +
        "NeedsImplementationReview its build goes on to QA. Read it first with steward_review (part 'record' holds the plan's " +
        "work items, each with its scope) and, for a build, steward_diff. An open Expansion item is work beyond what the ticket " +
        "asked for: approve it by leaving it, or name it in 'decline' to take it out of the plan before the rest is built. Keep " +
        "one only when an agreed slice or a decision in force asks for it, or the ticket's own ask cannot be met without it; " +
        "anything a user would notice that nothing decided is the owner's: decline it, or ask. A plan wrong in any other way " +
        "is sent back (steward_send_back to NeedsSpeculation), a build to NeedsImplementation. 'owner' means the owner put the " +
        "ticket at the gate by hand to look themselves: leave it. Your reason goes on the ticket and on the record the owner reads back.")]
    public static Task<StewardReply> Approve(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("Why it holds: what you read, and what makes it right to build (or to test) as it stands; for an expansion " +
                     "item kept or declined, the decision (#n) or the reason.")] string why,
        [Description("Ids of open Expansion work items to take out of the plan before approving it. Only at NeedsApproval.")] int[]? decline = null) =>
        s.ApproveAsync(project, ticketId, why, decline);

    [McpServerTool(Name = "steward_send_back"), Description(
        "Return a ticket at NeedsReview, or at a review gate, with a note. From NeedsReview: NeedsImplementation when code must change; " +
        "NeedsRefinement when the code is right but a check or the record is missing (it adds the check and sends the ticket on to be " +
        "tested); NeedsSpeculation when the plan was wrong. From NeedsApproval (a plan waiting for its yes): NeedsSpeculation, and the " +
        "replan stops at the gate again for you. From NeedsImplementationReview (a build before QA): NeedsImplementation. The note is an instruction the next worker must answer, so it says what is " +
        "wrong, where, and what done looks like. 'limit' means this ticket has come back too often and the next call is the " +
        "owner's: ask. " +
        "kind says what the send-back is for, from NeedsReview: 'change' (the default) when something in the work is wrong; " +
        "'rebase' when the review holds and only the rebase failed - it goes to NeedsImplementation as a conflict, and the run that " +
        "rebases it judges whether QA and refinement must run again or it comes straight back to you; 'rerun' when the review holds " +
        "and a merge check failed only on something the ticket does not touch - nothing moves, the ticket stays at NeedsReview and a " +
        "later run merges it again, once per branch tip.")]
    public static Task<StewardReply> SendBack(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("NeedsImplementation, NeedsRefinement or NeedsSpeculation.")] string phase,
        [Description("What is wrong, where, and what done looks like.")] string note,
        [Description("change (default), rebase, or rerun.")] string? kind = null)
    {
        var k = StewardService.SendBackKind.Change;
        if (!string.IsNullOrWhiteSpace(kind) && (!Enum.TryParse(kind, true, out k) || !Enum.IsDefined(k)))
            return Task.FromResult(new StewardReply("bad-request", $"Unknown kind '{kind}'. Use change, rebase or rerun."));
        return s.SendBackAsync(project, ticketId, phase, note, k);
    }

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
        "Correct a ticket that was itself wrong: a criterion that was false, two that disagree, testing set the wrong way, or a " +
        "resource its testing needs and was not given (a ticket blocked because its tester had no emulator is fixed here, then unblocked). " +
        "Give only what changes. It leaves a note on the ticket that a worker mid-flight will be shown.")]
    public static Task<StewardReply> Amend(StewardService s,
        [Description("The project key.")] string project, [Description("The ticket id.")] int ticketId,
        [Description("The new title, or omit to leave it.")] string? title = null,
        [Description("The new description in full, or omit to leave it.")] string? description = null,
        [Description("True: its tester reads the diff and runs nothing. False: it is tested in full. Omit to leave it.")] bool? codeReviewOnly = null,
        [Description("Resources its testing needs whatever its branch changes, named as the project's resourcePaths names them, like " +
                     "[\"android:emulator\"]. Such a resource is given only to a branch that changes its paths, so a ticket " +
                     "whose acceptance criteria are proved on it (screenshots inside the phone app) but which changes none of its " +
                     "code must name it, or its tester never gets it and can only block." + " The whole list: [] clears it. Omit to leave it.")] string[]? needs = null,
        [Description("True: it stops at its plan (NeedsApproval) for you to read before it is built. False: it goes straight on. Omit to leave it.")] bool? planReview = null,
        [Description("True: it stops once built (NeedsImplementationReview) for you to read before QA. False: it goes straight on. Omit to leave it.")] bool? implementationReview = null) =>
        s.AmendAsync(project, ticketId, title, description, codeReviewOnly, needs, planReview, implementationReview);

    [McpServerTool(Name = "steward_create"), Description(
        "File a ticket against an AGREED slice. The agent that plans it has the repo and your description and " +
        "nothing else, so the description is whole: what is wanted and why; 'Acceptance criteria' a tester can check one by " +
        "one; what is 'Out of scope'; and where the decision behind it is recorded. Name the tickets it depends on so nothing " +
        "starts on work that is not merged. 'limit' means stop creating: the next run continues. 'unit-not-agreed' means " +
        "exactly that, however obvious the next slice looks.")]
    public static Task<StewardReply> Create(StewardService s,
        [Description("The project key.")] string project,
        [Description("The number of the agreed slice this ticket belongs to.")] int unit,
        [Description("Which of that slice's outcomes this ticket delivers, by id, like [\"A2\"].")] string[] outcomes,
        [Description("One line, 10 to 110 characters.")] string title,
        [Description("The whole ask, with 'Acceptance criteria' and 'Out of scope' parts.")] string description,
        [Description("Ids of tickets that must be merged before this one starts.")] int[]? dependsOn = null,
        [Description("Lowest, Low, Normal or High. Omit for Normal. Critical is the owner's.")] string? priority = null,
        [Description("True when nothing in the change can be exercised by running the app, so its tester reads the diff and " +
                     "runs nothing and its own tests are the proof. When unsure, leave it false.")] bool codeReviewOnly = false,
        [Description("Resources its testing needs whatever its branch changes, named as the project's resourcePaths names them, like " +
                     "[\"android:emulator\"]. Such a resource is given only to a branch that changes its paths, so a ticket " +
                     "whose acceptance criteria are proved on it (screenshots inside the phone app) but which changes none of its " +
                     "code must name it, or its tester never gets it and can only block.")] string[]? needs = null,
        [Description("True: it stops at its plan (NeedsApproval) for you to read before it is built. Most tickets need neither gate.")] bool planReview = false,
        [Description("True: it stops once built (NeedsImplementationReview) for you to read the diff before QA spends time on it - " +
                     "for a risky or gray-area change. Most tickets need neither gate.")] bool implementationReview = false) =>
        s.CreateAsync(project, new StewardTicketDraft(unit, outcomes, title, description, dependsOn, priority, codeReviewOnly, needs,
            planReview, implementationReview));

    [McpServerTool(Name = "steward_set_outcome"), Description(
        "Record where one outcome of an agreed slice stands. This, and only this, is what calls an outcome done: set it when " +
        "every ticket it needs is merged. While a ticket for it is still to be written, say so with moreToFile and put what " +
        "is left - and what it waits on - in the note, one line. What a merged ticket built is in the code; do not describe it.")]
    public static Task<StewardReply> SetOutcome(StewardService s,
        [Description("The project key.")] string project,
        [Description("The slice's number.")] int unit,
        [Description("The outcome's id, like A2.")] string outcome,
        [Description("NotStarted, InProgress or Done.")] string state,
        [Description("True while a ticket for this outcome is still to be written.")] bool moreToFile = false,
        [Description("One line for the next run: what is left to file and what it waits on.")] string? note = null) =>
        s.SetOutcomeAsync(project, unit, outcome, state, moreToFile, note);

    [McpServerTool(Name = "steward_draft_unit"), Description(
        "Write or revise the DRAFT spec for the slice after the last agreed one, committed to the base branch. One draft " +
        "ahead, never two. Where there are ideas, it expands the next idea IN PLACE - same file, the idea's paragraph " +
        "kept at the top as the story's seed - and a slice no idea stands for is refused ('no-idea'). Its status line must " +
        "say it is a draft: only the owner agrees a slice, and until they do nothing in a draft may be ticketed or used to " +
        "answer a question. Its story ('The walk-through this slice makes possible') is checked: every sentence cites the " +
        "outcomes that make it true, like (A1) or (B1, B2), every id cited is an outcome of the draft, and it is at most " +
        "250 words - otherwise refused ('story'). The answer says what it is past 180 words and which outcomes no step " +
        "cites: say why each is not part of the walk-through. Number what it leaves open from the 'next' that " +
        "steward_decisions gives: a number the register already holds is refused ('number-taken').")]
    public static Task<StewardReply> DraftUnit(StewardService s,
        [Description("The project key.")] string project,
        [Description("The file's name, numbered like the other slices.")] string fileName,
        [Description("The whole spec.")] string text) =>
        s.DraftUnitAsync(project, fileName, text);

    [McpServerTool(Name = "steward_ask_owner"), Description(
        "Put ONE question to the owner, once. Only for a real decision: a choice no slice or convention makes, anything that " +
        "changes a slice or costs money, a flag marked 'owner', a ticket that has come back too often. Not progress and not " +
        "good news. Write it for someone reading on a phone with no context. It is shown on the board and announced; the " +
        "answer reaches a later run as a decision made. 'already-asked' means it was - never re-ask. Then keep everything " +
        "else moving. When what you need is the owner's answer to decisions a slice's spec leaves open under \"To decide\", " +
        "name the slice (as unit) and those decisions' numbers instead of summarising them: the board shows the owner each one as the " +
        "spec writes it, with its options and your pick, and writes the answers into the spec under \"Decided\". " +
        "Before asking, search steward_decisions for the subject, and say in 'touches' every decision in force the question " +
        "bears on - an empty list is your claim that none does. The owner is shown each touched decision beside the " +
        "question, and an answer that changes one replaces it in the register; say on each option which it would change. " +
        "To propose an idea - a new slice, one split in two, one reworded - pass idea with its file and whole text: the owner " +
        "is asked to commit it as written, and the board commits it on a yes. Only the owner changes ideas. For a question " +
        "about a slice being built whose answer changes a step of its story, pass unit and story: the sentence as it stands " +
        "and its rewording under each option; the board commits the rewording with the decision. A built slice's story " +
        "is never rewritten. Every answer decides for one slice or is a convention: " +
        "a question that names no unit and comes from no ticket of a slice says which, with slice or convention.")]
    public static Task<StewardReply> AskOwner(StewardService s,
        [Description("The project key.")] string project,
        [Description("A short name for this question, lowercase with hyphens, like sms-provider. Yours for good.")] string key,
        [Description("The question, as one line a person can answer.")] string question,
        [Description("Why it matters now, with whatever fact the owner needs to choose.")] string why,
        [Description("What is held up until this is answered.")] string heldUp,
        [Description("The numbers of the decisions in force this question bears on, from steward_decisions. Required: an empty list when none does.")] int[] touches,
        [Description("At least two options. Put what each costs and rules out in its detail, and mark exactly one - the one you would pick - as recommended. Leave out when you name decisions: those are their own options.")] StewardOption[]? options = null,
        [Description("The ticket that raised it, when one did.")] int? ticketId = null,
        [Description("The slice whose open decisions this asks about.")] int? unit = null,
        [Description("Those decisions' numbers, as the spec numbers them under \"To decide\".")] int[]? decisions = null,
        [Description("The open follow-ups this question takes up, by id: they stop waiting once it is asked.")] int[]? followUps = null,
        [Description("An idea to propose: the slice file's name (number first, no folder) and its whole text, with its status line saying idea. Asked on its own, without options: the owner commits it or leaves the ideas as they are.")] StewardIdeaProposal? idea = null,
        [Description("For a slice being built (with unit): the sentence of its story the answer changes, quoted exactly, and its rewording under each option that changes it, by the option's label.")] StewardStoryChange? story = null,
        [Description("The slice the answer decides for, when the question names no unit and its ticket is of no slice. Its decision retires when that slice is released.")] int? slice = null,
        [Description("The answer is a convention - a rule every slice keeps, never retired - rather than one slice's.")] bool convention = false) =>
        s.AskAsync(project, new StewardAsk(key, question, why,
            options?.Select(o => new BlockOption(o.Label, o.Detail, o.Recommended)).ToList(), heldUp, ticketId, unit, decisions, touches,
            options?.Where(o => o.Changes is { Length: > 0 }).ToDictionary(o => o.Label, o => (IReadOnlyList<int>)o.Changes!), followUps,
            idea, story, slice, convention));

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
        [Description("The numbers of the register's decisions it reads. Required: an empty list when it reads only a spec. When one is replaced or withdrawn, the ruling is flagged.")] int[] reads,
        [Description("The ticket that raised it, when one did.")] int? ticketId = null) =>
        s.RuleAsync(project, ruling, restsOn, ticketId, reads);

    [McpServerTool(Name = "steward_close_instruction"), Description(
        "Close one of the owner's instructions whose end has come - 'hold merges until that test is fixed', and the test is " +
        "fixed - saying how it came. Until it is closed it is in force and every run is handed it (status: instructions). " +
        "An instruction is not a decision: it is never in the register, and closing it changes nothing decided.")]
    public static Task<StewardReply> CloseInstruction(StewardService s,
        [Description("The project key.")] string project,
        [Description("The instruction's id, from status or the briefing.")] int id,
        [Description("How its end came: what is now true, and where to see it.")] string how) =>
        s.CloseInstructionAsync(project, id, how);

    [McpServerTool(Name = "steward_follow_up"), Description(
        "Record something the owner left open in an answer - 'let's talk about a dynamic role', 'so this will need to be " +
        "a permission' - against the decision the answer made. Read every answer for these: the owner fills in no extra " +
        "field. Each waits on the register page until a question you ask (followUps) or an idea takes it up, or the owner " +
        "drops it; every run is handed the open ones (briefing: followUps).")]
    public static Task<StewardReply> FollowUp(StewardService s,
        [Description("The project key.")] string project,
        [Description("The decision whose answer left it, by number.")] int decision,
        [Description("What was left open, in a line, quoting the owner where you can.")] string text) =>
        s.FollowUpAsync(project, decision, text);

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

    [McpServerTool(Name = "steward_suggest_promote"), Description(
        "Mark a decision of an open slice as one that outlives the slice: a rule about money, data, or how every page must " +
        "behave, rather than how this slice's feature works. Only a suggestion - when the owner releases the slice they see " +
        "its decisions with yours marked, and tick what goes into the project's conventions file. Mark few. withdraw takes " +
        "a mark back. Refused on a project with no conventions file.")]
    public static Task<StewardReply> SuggestPromote(StewardService s,
        [Description("The project key.")] string project,
        [Description("The decision, by number.")] int decision,
        [Description("Why it outlives its slice, in a line.")] string? why = null,
        [Description("Take the mark back instead.")] bool withdraw = false) =>
        s.SuggestPromoteAsync(project, decision, why, withdraw);

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
