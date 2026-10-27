using System.ComponentModel;
using System.Text.Json;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Whetstone.Services.Tracker;
using ModelContextProtocol.Server;

namespace Whetstone.Mcp;

/// <summary>One QA shot the worker hands us: an absolute path to a screenshot + its caption.</summary>
public record QaShotInput(
    [Description("Absolute path to the screenshot file (PNG/JPG/WebP), e.g. <repo>/.playwright-mcp/feature.png.")] string ImagePath,
    [Description("Caption describing what the screenshot shows — 2 sentences max.")] string Caption,
    [Description("Optional but expected whenever the proof is a small part of the frame: the rectangle, in the screenshot's own pixels, that shows what the caption claims - the new row, the error message, the changed value. A walkthrough zooms to it. Leave it out only when the whole frame is the point.")] QaFocusInput? Focus = null);

/// <summary>A rectangle on a QA shot, in the image's own pixels (x/y from the top-left corner).</summary>
public record QaFocusInput(
    [Description("Left edge, in image pixels from the left.")] int X,
    [Description("Top edge, in image pixels from the top.")] int Y,
    [Description("Width in image pixels.")] int W,
    [Description("Height in image pixels.")] int H);

/// <summary>
/// The local ticketing tools exposed over MCP. The heartbeat's Claude session
/// uses these to read and drive local tickets — they are the write path that
/// replaces the tracker's own write tools. The external tracker is reachable only for read-only
/// context (get_issue_context). All operations go through TicketService so the
/// state machine is enforced identically to the REST API.
/// </summary>
[McpServerToolType]
public class TicketTools
{
    [McpServerTool, Description("List the projects this dashboard manages (key + display name).")]
    public static object ListProjects(ProjectRegistry registry) =>
        registry.Projects.Select(k => new { key = k, displayName = registry.GetProjectConfig(k).DisplayName }).ToList();

    [McpServerTool, Description("List tickets currently in an actionable queue phase (NeedsSpeculation, NeedsImplementation, NeedsQa). These are the tickets the heartbeat should work. Optionally filter by project key.")]
    public static async Task<object> ListActionableTickets(TicketService svc, LinkedIssueResolver resolver,
        [Description("Optional project key to filter by; omit for all projects.")] string? project = null)
    {
        var tickets = await svc.ListActionableAsync(project);
        var resolved = await resolver.ResolveManyAsync(tickets);
        return tickets.Select(t => TicketMapper.ToSummary(t, resolved[t.Id].Title, resolved[t.Id].Unavailable)).ToList();
    }

    [McpServerTool, Description("Get the full detail of one ticket: description, work items, QA items, comments, current phase, legal next phases, any linked external issue key, and any issue attachments already downloaded to local paths you can open with Read. For a linked ticket the title/description come live from the tracker (also call get_issue_context for full acceptance criteria). trackerName says which tracker the project uses.")]
    public static async Task<object> GetTicket(TicketService svc, LinkedIssueResolver resolver, IssueAttachmentStore store,
        TicketFileStore ticketFiles, IssueTrackerFactory trackers,
        [Description("The local ticket id.")] int ticketId)
    {
        var t = await svc.GetAsync(ticketId);
        if (t is null) return new { error = $"Ticket {ticketId} not found" };
        var r = await resolver.ResolveAsync(t);
        var cached = store.ListCached(t.Id);
        var tracker = trackers.For(t.ProjectKey);
        return TicketMapper.ToDetail(t, r.Title, r.Description, r.Unavailable,
            attachments: cached.Select(a => new TicketAttachmentDto(a.FileName, a.LocalPath, a.Bytes, a.SourceIssue)).ToList(),
            attachmentsNote: AttachmentsNote(t, cached, tracker),
            files: ticketFiles, tracker: tracker,
            // The one caller that must have this: it is how a QA run learns which of the green items
            // on its checklist actually describe the branch it is looking at.
            lastBackwardAt: await svc.LastBackwardAtAsync(t.Id));
    }

    /// <summary>
    /// Say what the attachment list does and does not cover.
    /// <para>
    /// The failure this exists to prevent: a refinement phase reasoned about a design PDF it never
    /// opened, because the only tool that mentions attachments is get_issue_context and it had no
    /// reason to call one. Attachments now ride along on the call every phase already makes, and the
    /// note is what turns a list of paths into something the reader knows to act on.
    /// </para>
    /// </summary>
    private static string? AttachmentsNote(Ticket t, IReadOnlyList<IssueAttachmentStore.OnDisk> cached,
        IIssueTracker tracker)
    {
        if (string.IsNullOrWhiteSpace(t.IssueKey)) return null;

        if (cached.Count == 0)
            return $"No {tracker.DisplayName} attachments have been downloaded for this ticket yet. Call get_issue_context "
                 + $"to fetch any the {tracker.IssueNoun} - or its parent - has. A mockup or design PDF is usually the "
                 + "clearest statement of what was asked for, and a plan built without it is guesswork.";

        var note = "Open each of these with the Read tool at its localPath before you decide what the "
                 + "ticket asked for. A design PDF or annotated screenshot routinely settles a question "
                 + "the description leaves open.";

        // An inherited attachment is worth reading and worth not overstating: the parent story's
        // mockup shows what the parent asked for, which may be wider than this ticket's slice of it.
        var inherited = cached.Where(a => a.SourceIssue is not null).ToArray();
        if (inherited.Length > 0)
            note += $" {inherited.Length} of them (sourceIssue set) came from a parent issue, not from "
                  + "this ticket - read them as context for what the parent asked for, and check "
                  + "get_issue_context's parentChain for which requirement they belong to.";

        // Named rather than counted: "IRA_Statement.pdf is missing" is actionable, "one is missing"
        // just tells the reader to distrust the list. Only this ticket's own attachments count as
        // cover - a parent that happens to hold a same-named file has not supplied ours.
        var have = cached.Where(a => a.SourceIssue is null)
            .Select(a => a.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = IssueSnapshotService.SnapshotAttachmentNames(t.IssueSnapshot)
            .Where(n => !have.Contains(n)).ToArray();
        if (missing.Length > 0)
            note += $" Not downloaded: {string.Join(", ", missing)} - call get_issue_context to fetch "
                  + "them, and check its attachmentsNotCached for anything it could not.";

        return note;
    }

    [McpServerTool, Description("Move a ticket to a new phase. The move must be legal for the current phase (see nextPhases). " +
        "Blocked is legal from EVERY phase and is how you reach the ticket's owner: use it when something outside your control " +
        "stopped the work, or when the ticket itself does not add up (it contradicts itself, or cannot be built without guessing " +
        "at something central). A block must say something - pass 'questions' when you are asking the owner to decide, or 'reason' " +
        "when you are only reporting what stopped you. What you send is what the human answers, and their answer is given to the " +
        "run that picks the ticket up. Returns the updated ticket, or an error listing the allowed phases.")]
    public static async Task<object> SetPhase(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("Target phase name. It must be one of the ticket's own nextPhases (see get_ticket) - do not guess: 'Done' in particular is a human sign-off reachable only from NeedsReview.")] string phase,
        [Description("Reason for the move. When moving to Blocked WITHOUT questions this is required and must stand on its own to " +
            "someone who has not read your run: what you were doing, what stopped you, and the exact question or contradiction you " +
            "need settled. 'Blocked', 'stuck' and 'unclear' are rejected. When you DO pass questions, this is the short framing " +
            "sentence that goes above them and is optional. Optional for every other move.")] string? reason = null,
        [Description("The block as a list of questions, each with the choices you would accept. USE THIS whenever the block is a " +
            "decision rather than a fault - a contradiction in the ticket, a missing spec, anything with more than one defensible " +
            "reading. EVERY QUESTION MUST BE ONE WHOSE ANSWER CHANGES WHAT A LATER RUN DOES: the answer is not delivered to " +
            "anyone, it is quoted into the prompt of the run that picks this ticket up, and that run can only write code and " +
            "call add_comment. It cannot message a person and the tracker is read-only, so a question whose real answer is a " +
            "human acting outside the pipeline ('should X be told about this', 'should this go in the release notes') orders a " +
            "run to do what it has no tool for. If the only thing 'yes' changes is what a person does next, it is an " +
            "add_comment and not a question. " +
            "The owner answers by picking, so four questions with three options each costs them four clicks instead of a " +
            "page of prose. Mark one option 'recommended' on every question where you have a defensible default: unanswered " +
            "questions fall back to it, so a good recommendation is what lets them answer only the ones that need them. Put the " +
            "evidence in each question's 'why' (markdown, with file:line citations) and keep the question itself to one line. " +
            "Ignored unless phase is Blocked.")] IReadOnlyList<BlockQuestion>? questions = null,
        [Description("Things you need a person to go and find rather than decide - 'the parent epic', 'a sample of the vendor " +
            "payload'. Shown as a separate checklist under the questions. Ignored unless phase is Blocked.")] IReadOnlyList<string>? needs = null)
    {
        if (!Enum.TryParse<TicketPhase>(phase, true, out var to))
            return new { error = $"Unknown phase '{phase}'" };
        try
        {
            // Both halves ride in one payload so the reason can serve as the questions' preamble
            // rather than competing with them for the same field.
            var payload = questions is { Count: > 0 } ? new BlockPayload(reason, questions, needs) : null;
            var t = await svc.TransitionAsync(ticketId, to, reason, TransitionActor.Worker, payload);
            return t is null ? new { error = $"Ticket {ticketId} not found" } : (object)TicketMapper.ToDetail(t);
        }
        catch (HandoffRequirementException ex)
        {
            // A legal move refused because the ticket is missing something only this worker can
            // write. Say so in those terms - the caller that reads this as "illegal move" goes
            // looking for a different phase to move to, which is the one wrong response to it.
            return new
            {
                error = ex.Message,
                retryable = true,
                blockedPhase = ex.To.ToString(),
                note = $"The move to {ex.To} is legal and still the right one. It was refused only "
                     + "because this ticket is not carrying what that hand-off requires. Do the thing "
                     + "named above, then call set_phase again with the same phase.",
            };
        }
        catch (InvalidTransitionException ex)
        {
            // Hand back what each legal move is FOR, not just its name. A bare list invites the
            // caller to read every option as a failure path and park the ticket where it is.
            return new
            {
                error = ex.Message,
                allowed = ex.Allowed
                    .Select(p => new { phase = p.ToString(), meaning = PhaseMachine.Meaning(ex.From, p) })
                    .ToArray(),
            };
        }
    }

    [McpServerTool, Description("Send this ticket back to speculation because the linked external issue changed in a way that invalidates the plan, and switch the human speculation review on for the replan. Use this ONLY for a material change to what is being asked for (new/removed/altered requirements) — not for typos, formatting, or extra detail that does not change the work, and not because the work turned out to be hard. One call does everything; do not also call set_phase.")]
    public static async Task<object> RequestRespeculation(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("What changed in the linked issue and why the existing plan no longer fits it.")] string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new { error = "A reason is required — say what changed and why the plan no longer fits." };
        try
        {
            var t = await svc.RequestRespeculationAsync(ticketId, reason);
            return t is null ? new { error = $"Ticket {ticketId} not found" } : (object)TicketMapper.ToDetail(t);
        }
        catch (InvalidTransitionException ex)
        {
            return new { error = ex.Message, allowed = ex.Allowed.Select(p => p.ToString()).ToArray() };
        }
    }

    [McpServerTool, Description(
        "Add one or more work items to a ticket (the implementation checklist). Typically used by the speculate phase. "
      + "Every item carries a scope saying how it relates to what the ticket asked for: "
      + "'required' produces the asked-for outcome; 'consequence' was not asked for, but without it the ask is still wrong in a reachable case or something that works today breaks; "
      + "'expansion' adds something a user sees, or a business rule, that the ticket never asked for. "
      + "Any expansion item stops the ticket at NeedsApproval for a human yes, whatever the ticket's review setting.")]
    public static async Task<object> AddWorkItems(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The work item descriptions to add, in order.")] string[] items,
        [Description("One scope per item, in the same order: 'required', 'consequence' or 'expansion'.")] string[] scopes)
    {
        if (await NotOnReviewTrack(svc, ticketId,
                "work items are a plan for code WE are going to write, and this ticket reviews someone "
              + "else's branch. Record what you find with add_review_comment instead") is { } refusal)
            return refusal;

        // Checked whole before anything is written: a list half-added before a bad scope is a plan
        // the retry then duplicates.
        if (scopes is null || scopes.Length != items.Length)
            return new { error = $"Give exactly one scope per item: {items.Length} items, {scopes?.Length ?? 0} scopes." };
        var parsed = new WorkItemScope[items.Length];
        for (var i = 0; i < items.Length; i++)
            if (!Enum.TryParse(scopes[i]?.Trim(), ignoreCase: true, out parsed[i]) || !Enum.IsDefined(parsed[i]))
                return new { error = $"Scope '{scopes[i]}' for item {i + 1} is not one of required, consequence, expansion." };

        var added = new List<object>();
        for (var i = 0; i < items.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(items[i])) continue;
            var wi = await svc.AddWorkItemAsync(ticketId, items[i], parsed[i]);
            if (wi is null) return new { error = $"Ticket {ticketId} not found" };
            added.Add(new { wi.Id, wi.Text, wi.Done, wi.Order, Scope = wi.Scope.ToString() });
        }
        return new { added };
    }

    [McpServerTool, Description(
        "Say which work item you are starting, BEFORE you write any code for it. Call this each time you pick an item up — including when you come back to redo one you had already ticked off. " +
        "It moves the ticket's single 'working on this now' marker onto this item and off whatever it was on, so the board can show a human where you are in the list while the run is still going. " +
        "One item at a time: there is no call to release an item, because starting the next one releases the last.")]
    public static async Task<object> StartWorkItem(TicketService svc,
        [Description("The work item id you are about to work on.")] int workItemId)
    {
        var wi = await svc.StartWorkItemAsync(workItemId);
        return wi is null ? new { error = $"Work item {workItemId} not found" } : new { wi.Id, wi.Text, startedAt = wi.StartedAt };
    }

    [McpServerTool, Description(
        "Mark a work item done, or reopen one. Call this the moment an item is finished and COMMITTED — not in a batch at the end of the run. " +
        "Marking as you go is what lets an interrupted run be resumed: the next run reads done:true and skips that item, and a run that ends part-way still shows the ticket as having advanced. " +
        "Never tick an item whose commit has not landed — an item marked done is treated as work already in the branch.")]
    public static async Task<object> SetWorkItemDone(TicketService svc,
        [Description("The work item id.")] int workItemId,
        [Description("True to mark done, false to reopen.")] bool done)
    {
        var wi = await svc.UpdateWorkItemAsync(workItemId, null, done, null);
        return wi is null ? new { error = $"Work item {workItemId} not found" } : new { wi.Id, wi.Done };
    }

    [McpServerTool, Description(
        "Add one or more QA test items to a ticket (the QA checklist). Typically used when defining QA for a ticket. "
      + "Refused on a ticket marked code-review-only, which deliberately has no checklist at all — see the error text if it comes back.")]
    public static async Task<object> AddQaItems(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The QA item descriptions to add, in order.")] string[] items)
    {
        // The one place the code-review-only setting is actually enforced, and it has to be here
        // rather than in the skills: authoring QA items was never something a skill file asked for.
        // Speculation and QA both do it because the tool is in front of them and an empty checklist
        // invites filling, so prose asking them to stop would be exactly as reliable as the prose
        // that never asked them to start. The dashboard's own endpoint stays open — the human who
        // set the flag may still add items by hand, and the flag decides whether they get tested.
        var ticket = await svc.GetAsync(ticketId);
        if (ticket is null) return new { error = $"Ticket {ticketId} not found" };
        if (ticket.SkipQaTesting)
            return new
            {
                error = $"Ticket {ticketId} is marked code-review-only, so it has no QA checklist and nothing here "
                      + "would ever be tested. This is a deliberate setting on the ticket, not an oversight: QA reviews "
                      + "the diff for this one and does not run the app. Carry on with the phase you are in. If the work "
                      + "turns out to need real testing after all, say so in add_comment and let the human clear "
                      + "'Skip QA testing' in the dashboard - you cannot clear it yourself.",
            };

        var added = new List<object>();
        foreach (var text in items)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var qi = await svc.AddQaItemAsync(ticketId, text);
            if (qi is null) return new { error = $"Ticket {ticketId} not found" };
            added.Add(new { qi.Id, qi.Text, status = qi.Status.ToString(), qi.Order });
        }
        return new { added };
    }

    [McpServerTool, Description(
        "Say which QA item you are testing, BEFORE you start exercising it. Call this each time you pick one up, including when you come back to retest one. " +
        "It moves the ticket's single 'testing this now' marker onto this item and off whatever it was on, so a human watching the board can see where you are in the list while the run is still going — a QA item reads Todo both before anyone looks at it and while it is being tested, and this is the only thing that tells those apart. " +
        "Retesting drops the item back to Todo, because an item under test does not have a current verdict yet; any note on it is kept.")]
    public static async Task<object> StartQaItem(TicketService svc,
        [Description("The QA item id you are about to test.")] int qaItemId)
    {
        var qi = await svc.StartQaItemAsync(qaItemId);
        return qi is null ? new { error = $"QA item {qaItemId} not found" } : new { qi.Id, qi.Text, startedAt = qi.StartedAt };
    }

    [McpServerTool, Description(
        "Set the outcome of a QA item after testing: Todo, Passed, Failed, or Blocked. Provide a note explaining Failed/Blocked outcomes. " +
        "Record each outcome the moment you have it, not in a batch at the end of the run — an outcome sitting in your head is invisible to anyone watching, and lost entirely if the run is cut short. " +
        "A Passed here means YOU exercised this item against the branch as it stands now. If you settled it by reading the code instead, pass passedByInspection true and say in the note what you read and why the app could not be driven — that is a legitimate outcome for some items, but it is a different claim and it is recorded as one.")]
    public static async Task<object> SetQaStatus(TicketService svc,
        [Description("The QA item id.")] int qaItemId,
        [Description("Outcome: Todo, Passed, Failed, or Blocked.")] string status,
        [Description("Explanation for a Failed or Blocked outcome, or for a pass settled by inspection.")] string? note = null,
        [Description("True when this Passed was argued from the code rather than driven through the app. Requires a note saying what you read. Ignored for any outcome other than Passed.")] bool passedByInspection = false)
    {
        if (!Enum.TryParse<QaStatus>(status, true, out var st))
            return new { error = $"Unknown QA status '{status}'" };

        // The flag exists to make the weaker claim visible, which it only does if it comes with the
        // reasoning. Refused rather than defaulted: a bare "passed by inspection" is the shape of
        // exactly the evidence this is meant to stop being invisible.
        if (passedByInspection && st == QaStatus.Passed && string.IsNullOrWhiteSpace(note))
            return new { error = "A pass by inspection needs a note saying what you read and why you could not drive it." };

        var qi = await svc.UpdateQaItemAsync(qaItemId, null, st, note, null, passedByInspection);
        return qi is null
            ? new { error = $"QA item {qaItemId} not found" }
            : (object)new { qi.Id, status = qi.Status.ToString(), qi.Note, qi.PassedByInspection, qi.VerifiedAt };
    }

    [McpServerTool, Description("Post a progress note (comment) on a ticket, authored by Claude. This is the async communication channel that replaces tracker comments.")]
    public static async Task<object> AddComment(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The progress note body.")] string body)
    {
        var c = await svc.AddCommentAsync(ticketId, body, AuthorKind.Claude);
        return c is null ? new { error = $"Ticket {ticketId} not found" } : new { c.Id, author = c.Author.ToString(), c.Body };
    }

    [McpServerTool, Description(
        "Answer one note the ticket's OWNER left for you, saying what you actually did about it. This is the only way to clear a note, and clearing every note is what unlocks your phase's hand-off — set_phase is refused while any is unanswered. " +
        "The owner's notes are in get_ticket's comments with author \"Human\" and acknowledged false; each may carry attachments (a screenshot of what is wrong, a log dump) at a localPath you open with the Read tool. " +
        "DO THE WORK FIRST, then call this. A note from the owner outranks the ticket description, the work items, the linked issue and your skill's defaults — it is the most recent statement of what this ticket is for. " +
        "Say what you changed in one or two full sentences, specific enough that the owner can tell it from every other note on the ticket: they read this instead of re-reviewing the branch. 'Done', 'fixed' and 'acknowledged' are rejected. " +
        "If the note asks for something you cannot or should not do, this is still how you say so — answer it explaining why, then set_phase to Blocked with the question. Blocking is never gated on this.")]
    public static async Task<object> AcknowledgeNote(TicketService svc,
        [Description("The note's id — the 'id' of the comment in get_ticket's comments list.")] int noteId,
        [Description("What you did about this note, in one or two full sentences. Name the actual change (files, behaviour, decision), or the reason you did not make one.")] string response)
    {
        var (note, problem) = await svc.AcknowledgeNoteAsync(noteId, response);
        if (note is null) return new { error = $"Note {noteId} not found" };
        if (problem is not null) return new { error = problem, retryable = true, noteId };
        return new
        {
            noteId = note.Id,
            acknowledged = true,
            note.Acknowledgement,
            note = "Recorded against the owner's note, and shown underneath it in the dashboard. If "
                 + "there are other unanswered notes on this ticket, answer those too - the hand-off "
                 + "stays refused until every one of them is cleared.",
        };
    }

    [McpServerTool, Description("Set the feature branch recorded on a ticket (during implementation).")]
    public static async Task<object> SetBranch(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The git branch name.")] string branch)
    {
        if (await NotOnReviewTrack(svc, ticketId,
                "a review ticket does not own a branch - it reads one that already exists, recorded "
              + "when the ticket was created and not yours to change") is { } refusal)
            return refusal;

        var t = await svc.UpdateAsync(ticketId, null, null, null, null, branch, null);
        return t is null ? new { error = $"Ticket {ticketId} not found" } : new { t.Id, t.Branch };
    }

    [McpServerTool, Description("Set the ticket's implementation summary: a short, plain-language description of the delivered solution, mixing the business what/why with light technical detail — easily digestible, not deeply technical. About four sentences' worth of substance. Write it as markdown, because it is rendered as markdown: a line or two of prose for what was delivered and why, then `-` bullets for the pieces that changed, `**bold**` on the few terms that carry the point, and `##` headings only when there is genuinely more than one section. Format it so it can be scanned; do not pad the content to fill the shape. Rewrite it at the end of every implement run so it always reflects the current state of the work.")]
    public static async Task<object> SetImplementationSummary(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The summary text, as markdown (about 4 sentences' worth; business + light technical, easy to read).")] string summary)
    {
        if (await NotOnReviewTrack(svc, ticketId,
                "this ticket implements nothing - it reviews someone else's branch. Use "
              + "set_review_verdict to say what you concluded about their change") is { } refusal)
            return refusal;

        var t = await svc.SetImplementationSummaryAsync(ticketId, summary);
        return t is null ? new { error = $"Ticket {ticketId} not found" } : new { t.Id, t.ImplementationSummary };
    }

    [McpServerTool, Description(
        "Write the ticket's learning brief: reading material for the HUMAN who is about to review this ticket, so they learn this part of the codebase's domain instead of only trusting the pipeline. Written by the refine phase, once, on the clean hand-off to NeedsReview (skip it when sending the ticket back for rework). Rewrites in full each time. " +
        "It is NOT the commit message (what changed, for the repo) and NOT the implementation summary (what was delivered) - it is what someone would have needed to KNOW to make this change themselves. " +
        "350-600 words of plain markdown, in five short sections: (1) the area - the subsystem in domain terms, what it is for, where it lives; (2) how it worked before; (3) what changed and the business reason, tied to the acceptance criteria rather than the diff; (4) why this way - the alternative considered and the constraint that ruled it out; (5) where to look - 3-5 file:line pointers ordered as a reading path, plus [[note]] links to the repo's existing notes for this area. End with one question worth sitting with. " +
        "Be concrete: name real files, real rules, real numbers. A brief that would read the same on any ticket is worthless.")]
    public static async Task<object> SetLearningBrief(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The brief, as markdown (350-600 words, the five sections above).")] string brief)
    {
        if (string.IsNullOrWhiteSpace(brief))
            return new { error = "The brief is empty - write it, or skip the call entirely if this ticket is going back for rework." };
        var t = await svc.SetLearningBriefAsync(ticketId, brief);
        return t is null ? new { error = $"Ticket {ticketId} not found" } : new { t.Id, words = brief.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length };
    }

    [McpServerTool, Description(
        "Rule on one finding of the independent Classroom analysis this refinement pass was handed (get_ticket's independentReview). REQUIRED for every finding before Refining can hand off to NeedsReview. " +
        "The analysis read the change from the system's side - how it worked before, the route a user takes, the requirements line by line - rather than against the plan every earlier phase checked it against, so its findings are exactly the ones those phases were positioned to miss. It can also be wrong. Your job is to check each claim against the code and decide. " +
        "'Defect' = it is real and belongs to this change: send the ticket to NeedsImplementation with a comment saying what to fix. 'NotADefect' = you checked and it is not, or it is not this ticket's to fix; the reason must carry the evidence (the line that handles it, the requirement that does not ask for it) - 'the plan says so' is not evidence, because the plan is what the finding is questioning.")]
    public static async Task<object> RuleOnFinding(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The finding's id, from get_ticket's independentReview.findings.")] int findingId,
        [Description("'Defect' or 'NotADefect'.")] string ruling,
        [Description("Why, in one to three sentences, with the file:line or requirement that settles it. Read by the person signing the ticket off.")] string reason)
    {
        if (!Enum.TryParse<GateRuling>(ruling, ignoreCase: true, out var r))
            return new { error = $"Unknown ruling '{ruling}'. Use Defect or NotADefect." };
        var (f, error) = await svc.RuleOnFindingAsync(ticketId, findingId, r, reason);
        return f is null ? new { error } : new { f.Id, ruling = f.Ruling.ToString(), f.RuledAt };
    }

    [McpServerTool, Description(
        "Attach the ticket's QA shots: up to 6 screenshots of the COMPLETED, working feature, each with a short caption (2 sentences max), so a reviewer can eyeball what was verified at a glance. This REPLACES any existing shots — rewrite the full set each QA run. Aim for 3-6: three whenever the ticket has more than one screen worth of result, and more than that whenever another shot shows something the earlier ones do not. Three that each show a different thing beat six that repeat one view. Pass the ABSOLUTE path to each screenshot file you captured with Playwright (e.g. <repo>/.playwright-mcp/<name>.png). Extras beyond 6 are ignored. " +
        "CALLING THIS IS NOT OPTIONAL: QaTesting cannot hand off to NeedsRefinement until the ticket carries either shots or a recorded reason for having none. " +
        "If the work genuinely has nothing to photograph — a stored-procedure or migration change, a build fix, an API-only change with no screen behind it — pass an EMPTY shots list and a noShotsReason instead. Screenshots are much the better answer wherever the work is visible in the app; the empty path is for work that has no visible surface, not for a run that ran short of time.")]
    public static async Task<object> SetQaShots(TicketService svc, QaShotStore store,
        [Description("The local ticket id.")] int ticketId,
        [Description("Up to 6 shots (aim for 3-6), in display order. Each has imagePath (absolute path to a PNG/JPG/WebP screenshot) and caption (2 sentences max describing what it shows). Pass an empty list ONLY when this ticket's work has no visible surface at all — and then noShotsReason is required.")] QaShotInput[] shots,
        [Description("Required when shots is empty: what kind of change this is and why no screen shows it, in one sentence (e.g. 'stored-procedure change with no UI surface — verified by running the proc and checking the returned rows'). Ignored when shots are supplied. 'I did not take any' is not a reason.")] string? noShotsReason = null)
    {
        var t = await svc.GetAsync(ticketId);
        if (t is null) return new { error = $"Ticket {ticketId} not found" };

        var incoming = shots ?? Array.Empty<QaShotInput>();
        if (incoming.Length == 0 && string.IsNullOrWhiteSpace(noShotsReason))
            return new
            {
                error = "No shots and no reason. Attach 3-6 captioned screenshots of the finished "
                      + "feature, or - if this ticket's work has no visible surface at all - call again "
                      + "with an empty list and a noShotsReason saying what kind of change it is and why "
                      + "no screen shows it. This call is what unlocks the hand-off to NeedsRefinement.",
                existingShots = t.QaShots.Count,
            };

        // Replace semantics: clear the old files first, then copy in the new set.
        store.Clear(ticketId);
        var accepted = new List<TicketService.QaShotSave>();
        var errors = new List<string>();
        var order = 0;
        // A walk-through captures every step of its story, which a 250-word story keeps well under 40.
        foreach (var shot in incoming.Take(t.Walkthrough ? 40 : 6))
        {
            try
            {
                var file = await store.SaveAsync(ticketId, order, shot.ImagePath);
                // A focus that misses the image is dropped, not fatal: the shot is still good proof,
                // and the error tells the worker exactly which numbers were wrong.
                ImageRect? focus = null;
                if (shot.Focus is { } f)
                {
                    var rect = new ImageRect(f.X, f.Y, f.W, f.H);
                    var size = store.DiskPath(ticketId, file) is { } p ? ImageInfo.Size(p) : null;
                    var problem = size is { } s ? rect.ProblemWithin(s.Width, s.Height) : "could not read the image's size";
                    if (problem is null) focus = rect;
                    else errors.Add($"{shot.ImagePath}: focus {problem} - shot kept without a focus");
                }
                accepted.Add(new(file, shot.Caption ?? "", focus));
                order++;
            }
            catch (Exception ex)
            {
                errors.Add($"{shot.ImagePath}: {ex.Message}");
            }
        }

        // A run that meant to attach shots and had every one of them fail to save must not slip
        // through on the reason it never gave. Only an empty REQUEST records a waiver.
        var waiver = incoming.Length == 0 ? noShotsReason : null;
        await svc.SetQaShotsAsync(ticketId, accepted, waiver);
        return new { ticketId, saved = accepted.Count, focused = accepted.Count(a => a.Focus is not null), noShotsReason = waiver, errors };
    }

    /// <summary>
    /// The refusal to return when a tool that only makes sense for our own work is called on a
    /// review ticket, or null when the call may go ahead.
    /// <para>
    /// Refused here rather than left for the skill file to avoid, for the same reason
    /// <c>add_qa_items</c> refuses on a code-review-only ticket: the tool is in front of the model
    /// and a plausible-looking call invites making it. Every message says what to use instead — a
    /// refusal that only says no teaches a run to try a different wrong tool.
    /// </para>
    /// </summary>
    private static async Task<object?> NotOnReviewTrack(TicketService svc, int ticketId, string because)
    {
        var track = await svc.TrackOfAsync(ticketId);
        if (track is null) return new { error = $"Ticket {ticketId} not found" };
        if (track != TicketTrack.Review) return null;
        return new { error = $"Ticket {ticketId} is a peer review ticket: {because}." };
    }

    // ---- Peer review: staging feedback on someone else's branch ---------------
    //
    // THERE IS NO submit_review TOOL, AND THERE MUST NEVER BE ONE. Everything here writes to the
    // local ticket; the only thing that reaches a pull request is a button in the dashboard that a
    // person presses. That is not a policy the skill file is asked to observe - it is the absence of
    // a tool, which is checkable by reading this list, the same guarantee master notes and priority
    // already rest on. A run that decides feedback is ready has said so and stopped, which is
    // exactly as far as it should get.

    [McpServerTool, Description(
        "Stage one piece of feedback on the branch under review. Staged only: this writes to the local ticket and is NOT sent anywhere. The ticket's owner reads, edits, dismisses and sends the set themselves - there is no tool that posts to the pull request, by design. " +
        "Call it once per finding, as you find it, rather than batching at the end: an outcome you are holding in your head is invisible to anyone watching and lost outright if the run is cut short. " +
        "Anchor it to a file and line wherever you can - feedback a reader has to go hunting for is feedback that gets skimmed. Leave both out only for a point about the change as a whole (\"this needs a migration and there isn't one\"). " +
        "Be specific about the consequence, not just the observation: name the input or state that reaches the problem and what happens then.")]
    public static async Task<object> AddReviewComment(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The comment, as markdown. Say what is wrong and what happens because of it. Address the author, not the ticket - this text is going to be read by the person who wrote the code.")] string body,
        [Description("How much this is claiming. 'Blocking' = this is wrong and must change before merge. 'Question' = the author should answer; may or may not be a defect. 'Nit' = worth saying, not worth blocking on. 'Praise' = worth saying because it is good. Be honest with this: a review that marks everything Blocking is one the human has to re-triage by hand, which is most of the work you are here to save.")] string severity = "Question",
        [Description("Repo-relative path of the file this is about, e.g. 'backend/Core/Services/PlanService.cs' - relative to the repo under review, where the pull request is, even when your workspace holds several. Omit for a comment about the whole change.")] string? filePath = null,
        [Description("1-based line number in that file. Omit for a comment about the file as a whole.")] int? line = null,
        [Description("Which side of the diff: 'Head' (the branch as proposed - nearly always this) or 'Base' for a comment about something the change removed.")] string side = "Head",
        [Description("The id of the QA item this came out of, when a drive-test found it. A failing check plus its screenshot is the most useful comment you can leave.")] int? qaItemId = null,
        [Description("Slot (0-5) of the QA shot that shows this, when one does.")] int? qaShotOrder = null)
    {
        var t = await svc.GetAsync(ticketId);
        if (t is null) return new { error = $"Ticket {ticketId} not found" };
        if (t.Track != TicketTrack.Review)
            return new
            {
                error = $"Ticket {ticketId} is a {t.Track} ticket - our own work, not someone else's branch - so there "
                      + "is nobody to send review feedback to. If you have found something wrong with the code, the "
                      + "phase you are in has its own way to record it: add_comment for an observation, or the "
                      + "hand-off back to implementation for a defect.",
            };
        if (string.IsNullOrWhiteSpace(body))
            return new { error = "The comment is empty. Say what is wrong and what happens because of it." };
        if (!Enum.TryParse<ReviewSeverity>(severity, ignoreCase: true, out var sev))
            return new { error = $"Unknown severity '{severity}'. Use Blocking, Question, Nit or Praise." };
        if (!Enum.TryParse<DiffSide>(side, ignoreCase: true, out var diffSide))
            return new { error = $"Unknown side '{side}'. Use Head or Base." };

        var c = await svc.AddReviewCommentAsync(ticketId, body, sev, AuthorKind.Claude,
            filePath, line, diffSide, qaItemId, qaShotOrder);
        return c is null
            ? new { error = $"Ticket {ticketId} not found" }
            : (object)new { c.Id, severity = c.Severity.ToString(), file = c.FilePath, c.Line, anchoredAt = c.AnchorSha };
    }

    [McpServerTool, Description(
        "Point an already-staged review comment at the QA shot that shows it, so the reader sees the screenshot beside the finding instead of hunting for it in the grid at the bottom of the drawer. " +
        "This exists because of the order the phase runs in: you stage a comment the moment a check fails, but shots are attached later, in ONE set_qa_shots call, so the slot number does not exist yet at the moment the finding does. Stage first, attach the set, then come back here for each finding a shot shows. " +
        "Cite the frame that shows THIS finding. A shot that merely happens to contain the screen is not evidence for the claim, and a citation that does not hold up costs the reader more than no citation at all - one frame can legitimately back several findings, and many findings have no frame.")]
    public static async Task<object> CiteQaShot(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The id add_review_comment returned for the comment you are citing from.")] int commentId,
        [Description("Slot of the shot that shows it, as displayed: 0 is the first shot you passed to set_qa_shots. Pass -1 to remove a citation you got wrong.")] int qaShotOrder)
    {
        var t = await svc.GetAsync(ticketId);
        if (t is null) return new { error = $"Ticket {ticketId} not found" };

        var comment = t.ReviewComments.FirstOrDefault(c => c.Id == commentId);
        if (comment is null)
            return new { error = $"Ticket {ticketId} has no review comment {commentId}. Use the id add_review_comment gave you." };
        if (comment.Status == ReviewCommentStatus.Submitted)
            return new { error = "That comment has already been sent to the pull request and is now a matter of record." };

        if (qaShotOrder < 0)
        {
            await svc.UpdateReviewCommentAsync(commentId, null, null, null, clearQaShot: true);
            return new { commentId, citation = "cleared" };
        }

        // Checked against the set that is actually attached, because the slot is only meaningful
        // relative to it - and set_qa_shots REPLACES the set each run, so a number remembered from
        // an earlier pass is a number about shots that are no longer there.
        var slots = t.QaShots.Select(s => s.Order).OrderBy(o => o).ToList();
        if (!slots.Contains(qaShotOrder))
            return new
            {
                error = slots.Count == 0
                    ? "This ticket has no QA shots attached yet. Call set_qa_shots with the whole set first, then cite from it."
                    : $"There is no shot in slot {qaShotOrder}. The attached set is {string.Join(", ", slots)}.",
            };

        await svc.UpdateReviewCommentAsync(commentId, null, null, null, qaShotOrder);
        return new { commentId, citedShot = qaShotOrder };
    }

    [McpServerTool, Description(
        "Record what this review pass concluded about the branch as a whole. REQUIRED before PeerReviewing can hand off - the staged comments say what is wrong with individual lines, and this is the only place the run says what it thinks of the change overall. " +
        "It is a recommendation to the ticket's owner, not a decision: nothing is sent, and they may disagree. Choose it on the evidence you actually gathered, and remember that finding defects is a SUCCESSFUL review - RequestChanges is a normal outcome, not a failure of your run.")]
    public static async Task<object> SetReviewVerdict(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("One of: 'Approve' (nothing outstanding), 'ApproveWithComments' (fine to merge, but there are notes worth reading), 'RequestChanges' (something must change first), 'CannotTell' (the branch cannot be judged - it would not build, or the requirements contradict each other; say which in the summary).")] string verdict,
        [Description("2-4 sentences for the ticket's timeline: what you read, what you exercised, and why you landed on this verdict. Written for the ticket's owner, who is about to decide whether to send your feedback.")] string summary)
    {
        var t = await svc.GetAsync(ticketId);
        if (t is null) return new { error = $"Ticket {ticketId} not found" };
        if (t.Track != TicketTrack.Review)
            return new { error = $"Ticket {ticketId} is a {t.Track} ticket, so there is no external branch to pass a verdict on." };
        if (!Enum.TryParse<ReviewVerdict>(verdict, ignoreCase: true, out var v))
            return new { error = $"Unknown verdict '{verdict}'. Use Approve, ApproveWithComments, RequestChanges or CannotTell." };
        if (string.IsNullOrWhiteSpace(summary))
            return new { error = "The summary is required: a verdict with no reasoning is a vote, and the owner cannot weigh a vote." };

        var r = await svc.SetReviewVerdictAsync(ticketId, v);
        if (r is null) return new { error = $"Ticket {ticketId} has no review target." };
        await svc.AddCommentAsync(ticketId, $"**Review verdict: {v}**\n\n{summary.Trim()}", AuthorKind.Claude);
        return new { ticketId, verdict = v.ToString(), r.VerdictAt };
    }

    [McpServerTool, Description(
        "Record which commit this review pass actually read. Call it once, at the start, straight after confirming your workspace - every comment you stage afterwards is anchored to it, and that anchor is what tells a later pass which findings still describe the branch and which were written against code the author has since pushed over.")]
    public static async Task<object> SetReviewedCommit(TicketService svc,
        [Description("The local ticket id.")] int ticketId,
        [Description("The full SHA of the commit you are reviewing - `git rev-parse HEAD` in your worktree.")] string sha)
    {
        var t = await svc.GetAsync(ticketId);
        if (t is null) return new { error = $"Ticket {ticketId} not found" };
        if (t.Track != TicketTrack.Review)
            return new { error = $"Ticket {ticketId} is a {t.Track} ticket, so it has no branch under review." };
        if (string.IsNullOrWhiteSpace(sha))
            return new { error = "No sha given. Run `git rev-parse HEAD` in your worktree and pass what it prints." };

        // Both, from the one call: the pass has just read this commit, and it is also what origin was
        // carrying when the worktree was prepared moments ago. They diverge later, when the author
        // pushes, and that divergence is exactly what marks the staged comments stale.
        var r = await svc.SetReviewShasAsync(ticketId, sha.Trim(), sha.Trim());
        return r is null
            ? new { error = $"Ticket {ticketId} has no review target." }
            : (object)new { ticketId, reviewedAt = r.ReviewedAtSha };
    }

    // Named get_issue_context by the method name, the way every other tool here is.
    [McpServerTool, Description("Read a ticket's linked external issue in full, for context (READ-ONLY — we never write to the tracker). Works the same whether the project is on Jira or Azure Boards. Returns the description, any acceptance-criteria field, every comment, the parent chain with each ancestor's full description, sibling issues, linked issues with their summaries, and any attachments downloaded to local paths you can open with Read. On a sub-task the parent's description usually holds the acceptance criteria, and attachments are often the most informative part of a ticket, so check both.")]
    public static async Task<object> GetIssueContext(TicketService svc, IssueTrackerFactory factory, IssueContextBuilder builder,
        [Description("The local ticket id (the external issue is taken from its issueKey).")] int ticketId)
    {
        var t = await svc.GetAsync(ticketId);
        if (t is null) return new { error = $"Ticket {ticketId} not found" };
        if (string.IsNullOrWhiteSpace(t.IssueKey))
            return new { error = $"Ticket {ticketId} has no linked issue." };

        var tracker = factory.For(t.ProjectKey);
        if (!tracker.IsConfigured)
            return new { error = $"No issue tracker is configured for project '{t.ProjectKey}'; no read-only context available." };
        try
        {
            var issue = await tracker.GetIssueAsync(t.IssueKey);
            if (issue is null)
                return new { error = $"{tracker.DisplayName} {tracker.IssueNoun} {t.IssueKey} not found" };
            return await builder.BuildAsync(t.Id, issue, tracker);
        }
        catch (HttpRequestException ex)
        {
            return new { error = $"{tracker.DisplayName} read failed: {ex.Message}" };
        }
    }
}
