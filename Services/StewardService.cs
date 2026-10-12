using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// What a steward verb answers. <paramref name="Answer"/> is one word the caller acts on
/// ("merged", "behind", "owner", "proposed"); <paramref name="Why"/> says it in a sentence when the
/// word alone would not; <paramref name="Data"/> carries whatever goes with it.
/// <para>
/// A refusal is an answer, not an error. The steward is meant to read "limit" or "owner" and do
/// the next right thing - ask, wait, move on - and an exception would only tell it that something
/// broke, which is the one reading that sends an agent looking for another way in.
/// </para>
/// </summary>
public record StewardReply(string Answer, string? Why = null, object? Data = null);

/// <summary>What a steward wants filed as a ticket.</summary>
public record StewardTicketDraft(
    int Unit, IReadOnlyList<string> Outcomes, string Title, string Description,
    IReadOnlyList<int>? DependsOn = null, string? Priority = null, bool CodeReviewOnly = false);

/// <summary>What a steward wants to put to the owner.</summary>
public record StewardAsk(
    string Key, string Question, string Why, IReadOnlyList<BlockOption> Options, string HeldUp, int? TicketId = null);

/// <summary>A steward's record kept elsewhere, to be taken in once. See <see cref="StewardService.ImportAsync"/>.</summary>
public record StewardImport(
    IReadOnlyList<StewardImportQuestion>? Questions = null, IReadOnlyList<StewardImportEntry>? Entries = null,
    IReadOnlyList<StewardImportOutcome>? Outcomes = null, IReadOnlyList<StewardImportTicket>? Tickets = null,
    string? Memo = null);

/// <summary>A question as it was asked; with <paramref name="Answer"/> when the owner has answered it.</summary>
public record StewardImportQuestion(string Key, string Question, string? Why = null,
    IReadOnlyList<BlockOption>? Options = null, string? HeldUp = null, DateTime? AskedAt = null,
    string? Answer = null, DateTime? AnsweredAt = null);

/// <param name="Kind">Call, BeforeRelease or OwnerSaid.</param>
public record StewardImportEntry(string Kind, string Text, int? TicketId = null, DateTime? At = null);

public record StewardImportOutcome(int Unit, string Outcome, string State, bool MoreToFile = false, string? Note = null);

public record StewardImportTicket(int Id, int Unit, IReadOnlyList<string> Outcomes, bool FiledBySteward = true);

/// <summary>
/// A project's steward: everything it may do, and every limit on it.
/// <para>
/// A steward stands in for a ticket's owner between worker runs. It decides what is built next
/// from a plan the owner agreed, answers what the plan already answers, reviews what comes back,
/// merges it, and brings the owner only what is the owner's. The judgment is an agent's. Nothing
/// else is: the mode, the limits, the lock, what a merge must prove and what it may never carry
/// all live here, in code, where a prompt cannot talk its way past them.
/// </para>
/// <para>
/// Three rules shape every verb below.
/// <list type="bullet">
/// <item>The mode is the owner's. <see cref="StewardMode.Propose"/> runs every check and then only
/// says what it would have done; <see cref="StewardMode.Manage"/> never merges; nothing here can
/// change it.</item>
/// <item>A change needs a run. Verbs that write refuse unless a run holds the project's steward
/// lease, so two runs cannot interleave and a stray call between runs does nothing.</item>
/// <item>What is the owner's stays the owner's. Answering a question, agreeing a unit of the plan
/// and raising a limit have no verb the steward can reach.</item>
/// </list>
/// </para>
/// </summary>
public class StewardService
{
    /// <summary>A run that died holding the lease stops the next one, not all of them.</summary>
    private static readonly TimeSpan RunLeaseTtl = TimeSpan.FromMinutes(90);

    /// <summary>
    /// With work left to do and nothing new on the board, how long before another run is worth
    /// starting. What a steward waits on is workers, and their arrivals mark it due by themselves;
    /// this is only for what does not announce itself - a limit that has lifted, a new day.
    /// </summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The least time between one run starting and the next. It batches arrivals - three tickets
    /// finishing a minute apart are one run's work, not three - and it is what stops a run that
    /// cannot start (a missing skill, a refused login) from being relaunched every few seconds.
    /// </summary>
    public TimeSpan MinGapBetweenRuns { get; set; } = TimeSpan.FromMinutes(5);

    private const int DiffMaxChars = 120_000;
    private const string HolderLabel = "steward";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DashboardDbContext _db;
    private readonly TicketService _tickets;
    private readonly ProjectRegistry _registry;
    private readonly IStewardGit _git;
    private readonly ResourceLeaseService _leases;
    private readonly TicketDependencyService? _deps;
    private readonly IStewardProjectFlags? _projectFlags;
    private readonly NotificationSender? _notify;
    private readonly HeartbeatManager? _heartbeat;

    public StewardService(DashboardDbContext db, TicketService tickets, ProjectRegistry registry, IStewardGit git,
        ResourceLeaseService leases, TicketDependencyService? deps = null, IStewardProjectFlags? projectFlags = null,
        NotificationSender? notify = null, HeartbeatManager? heartbeat = null)
        => (_db, _tickets, _registry, _git, _leases, _deps, _projectFlags, _notify, _heartbeat)
         = (db, tickets, registry, git, leases, deps, projectFlags, notify, heartbeat);

    public static string LeaseKey(string projectKey) => $"steward:{projectKey}";

    private static string Today() => DateTime.Now.ToString("yyyy-MM-dd");   // this machine's day

    private static StewardReply Refuse(string answer, string why, object? data = null) => new(answer, why, data);

    // ---- the gates every verb goes through -----------------------------------

    private ProjectConfig? Config(string projectKey) =>
        _registry.IsEnabled(projectKey) ? _registry.GetProjectConfig(projectKey) : null;

    /// <summary>Why this project's steward may not act at all, or null when it may.</summary>
    private static StewardReply? Off(ProjectConfig? cfg, string projectKey) =>
        cfg is null ? Refuse("no-project", $"There is no enabled project '{projectKey}'.")
        : cfg.StewardMode == StewardMode.Off ? Refuse("off", $"{cfg.DisplayName} has no steward: its mode is off, and only its owner changes that.")
        : null;

    public async Task<bool> RunOpenAsync(string projectKey) =>
        await _leases.HolderOfAsync(LeaseKey(projectKey)) is not null;

    /// <summary>The gate for a verb that changes something: a steward, and a run in progress.</summary>
    private async Task<(ProjectConfig? Cfg, StewardReply? No)> WritableAsync(string projectKey)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return (null, off);
        if (!await RunOpenAsync(projectKey))
            return (null, Refuse("no-run", "No run is open for this project. A change is only made inside a run: begin one first."));
        return (cfg, null);
    }

    private static StewardReply Proposed(string would) => new("proposed", null, new { would });

    private async Task<(Ticket? T, StewardReply? No)> TicketOfAsync(string projectKey, int ticketId)
    {
        var t = await _tickets.GetAsync(ticketId);
        if (t is null) return (null, Refuse("no-ticket", $"No ticket {ticketId}."));
        if (t.ProjectKey != projectKey) return (null, Refuse("no-ticket", $"Ticket {ticketId} belongs to {t.ProjectKey}, not {projectKey}."));
        return (t, null);
    }

    private Task<int> SendBacksAsync(int ticketId) =>
        _db.PhaseTransitions.CountAsync(p => p.TicketId == ticketId && p.Actor == TransitionActor.Steward && p.Backward);

    // ---- whether a run is owed -----------------------------------------------

    /// <summary>
    /// Whether this project's steward should run now, and why. Asked by the heartbeat before each
    /// pass; it costs a few reads and no agent, which is the point - a board where nothing has
    /// changed starts nothing.
    /// </summary>
    public async Task<(bool Due, string Reason)> DueAsync(string projectKey, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (cfg is null || cfg.StewardMode == StewardMode.Off) return (false, "off");
        if (await RunOpenAsync(projectKey)) return (false, "a run is already open");

        var s = await _db.StewardStates.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectKey == projectKey, ct);
        if (s?.LastRunStartedAt is { } started && DateTime.UtcNow - started < MinGapBetweenRuns)
            return (false, "a run was started a moment ago");
        if (!string.IsNullOrEmpty(s?.DueReason)) return (true, s!.DueReason!);
        if (s?.LastRunEndedAt is null) return (true, "first run");

        if (cfg.Steward.SyncFrom.Length > 0 && await _git.AheadOfBaseAsync(projectKey, cfg.Steward.SyncFrom, ct) > 0)
            return (true, $"{cfg.Steward.SyncFrom} has moved");

        if (s.RestingAtSha is not null)
            return await _git.BaseHeadAsync(projectKey, ct) == s.RestingAtSha
                ? (false, "nothing has changed since a run that left nothing to do")
                : (true, "the base branch has moved");

        return DateTime.UtcNow - s.LastRunEndedAt.Value >= IdleInterval
            ? (true, "work was left for a later run")
            : (false, "the last run was a moment ago");
    }

    /// <summary>Note that a run is owed, and make sure a sleeping heartbeat hears about it.</summary>
    public async Task MarkDueAsync(string projectKey, string reason)
    {
        await StewardDue.MarkAsync(_db, projectKey, reason);
        await _db.SaveChangesAsync();
        _heartbeat?.Wake();
    }

    // ---- a run's beginning and end --------------------------------------------

    /// <summary>
    /// Open a run: take the project's steward lease, bring the base branch up to date with the
    /// branch it follows, and hand over the board as it stands. "locked" means another run is still
    /// going, and the caller stops there.
    /// </summary>
    public async Task<StewardReply> BeginAsync(string projectKey, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;

        var lease = await _leases.TryAcquireAllAsync(new[] { LeaseKey(projectKey) }, 0, "steward", RunLeaseTtl, HolderLabel);
        if (lease is null)
        {
            var held = await _leases.HolderOfAsync(LeaseKey(projectKey));
            return Refuse("locked", "Another run is still going. Stop now and do nothing.",
                new { since = held?.AcquiredAt, minutes = held is null ? 0 : (int)(DateTime.UtcNow - held.AcquiredAt).TotalMinutes });
        }

        var s = await StewardDue.StateAsync(_db, projectKey);
        var why = s.DueReason;
        s.DueReason = null;
        s.DueSince = null;
        s.LastRunStartedAt = DateTime.UtcNow;

        // Every answer the owner has given is in front of this run, so none of them is a reason
        // for another.
        var fresh = await _db.StewardQuestions
            .Where(q => q.ProjectKey == projectKey && q.Answer != null && q.AnswerDeliveredAt == null).ToListAsync(ct);
        foreach (var q in fresh) q.AnswerDeliveredAt = DateTime.UtcNow;
        var said = await _db.StewardEntries
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.OwnerSaid && e.DeliveredAt == null)
            .OrderBy(e => e.At).ToListAsync(ct);
        foreach (var e in said) e.DeliveredAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        SyncFromResult? sync = null;
        if (cfg!.Steward.SyncFrom.Length > 0)
            sync = await _git.SyncFromAsync(projectKey, cfg.Steward.SyncFrom, cfg.StewardMode == StewardMode.Propose, ct);

        return new StewardReply("started", null, new
        {
            mode = cfg.StewardMode.ToString().ToLowerInvariant(),
            because = why ?? "nothing in particular: a look at whether anything is left to file",
            newAnswers = fresh.Select(q => new { q.Key, q.Question, q.Answer }),
            newInstructions = said.Select(e => e.Text),
            sync,
            status = (await StatusAsync(projectKey, ct)).Data,
        });
    }

    /// <summary>
    /// Close a run: record what it did, release the lease, and nudge the owner once a day about
    /// anything still waiting on them. <paramref name="nothingToFile"/> is the run's own word that
    /// it left nothing the next run could start by itself; while that holds and nothing changes,
    /// no further run is started.
    /// </summary>
    public async Task<StewardReply> EndAsync(string projectKey, string? summary, bool nothingToFile, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;

        // Before the head is read below: a run that rests is resting at the commit this leaves.
        var exported = await ExportDecisionsAsync(cfg!, ct);

        var s = await StewardDue.StateAsync(_db, projectKey);
        s.LastRunEndedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(summary)) s.LastRunSummary = summary.Trim();
        // A run that only proposed changed nothing, so it has not earned the board a rest.
        s.RestingAtSha = nothingToFile && cfg!.StewardMode != StewardMode.Propose
            ? await _git.BaseHeadAsync(projectKey, ct) : null;
        await _db.SaveChangesAsync(ct);

        await _leases.ReleaseHumanLeaseAsync(LeaseKey(projectKey), "run ended");
        var reminder = await RemindAsync(cfg!, ct);
        return new StewardReply("ended", null, new { resting = s.RestingAtSha is not null, reminder, decisions = exported });
    }

    /// <summary>
    /// A run is about to be launched for this project. Stamped before the agent starts, so a run
    /// that dies before it can open itself still counts as an attempt and is not retried at once.
    /// </summary>
    public async Task<StewardReply> LaunchingAsync(string projectKey)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;
        var s = await StewardDue.StateAsync(_db, projectKey);
        s.LastRunStartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new StewardReply("launching");
    }

    /// <summary>
    /// Give the lease back without a run having ended properly: the process that held it is gone.
    /// Called by whatever launched the run, never by the steward.
    /// </summary>
    public async Task<bool> AbandonRunAsync(string projectKey, string reason)
    {
        var released = await _leases.ReleaseHumanLeaseAsync(LeaseKey(projectKey), reason);
        if (released)
        {
            var s = await StewardDue.StateAsync(_db, projectKey);
            s.LastRunEndedAt = DateTime.UtcNow;
            s.RestingAtSha = null;
            await _db.SaveChangesAsync();
        }
        return released;
    }

    /// <summary>
    /// Write the owner's decisions out to the project's repo, when it names a file for them.
    /// Best-effort by design: the hub is a person's checkout, and when it is busy or dirty this
    /// says so and leaves it - the next answer, or the end of the next run, writes the whole file
    /// again, so nothing is lost by skipping once.
    /// </summary>
    private async Task<string?> ExportDecisionsAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var file = cfg.Steward.DecisionsFile;
        if (file.Length == 0 || cfg.StewardMode is StewardMode.Off or StewardMode.Propose) return null;

        var answered = await _db.StewardQuestions.AsNoTracking()
            .Where(q => q.ProjectKey == cfg.Name && q.Answer != null).ToListAsync(ct);
        var said = await _db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == cfg.Name && e.Kind == StewardEntryKind.OwnerSaid).ToListAsync(ct);
        if (answered.Count == 0 && said.Count == 0) return null;

        var lines = answered.Select(q => (At: q.AnsweredAt ?? q.AskedAt,
                Text: $"- {(q.AnsweredAt ?? q.AskedAt).ToLocalTime():yyyy-MM-dd}, **{q.Key}**: {q.Question} {q.Answer}"))
            .Concat(said.Select(e => (e.At, Text: $"- {e.At.ToLocalTime():yyyy-MM-dd}: {e.Text.Replace("\n", " ")}")))
            .OrderBy(x => x.At).Select(x => x.Text);
        var text = "# Decisions\n\n"
            + "What this project's owner has decided through its steward: the answers to its questions, and standing "
            + "instructions, oldest first. Written out by the ticket board from its own record each time one is added; "
            + "edits made here are overwritten.\n\n"
            + string.Join("\n\n", lines) + "\n";

        var (sha, unchanged, refusal) = await _git.CommitFileAsync(cfg.Name, file, text, "The owner's decisions, as recorded on the board", ct);
        return refusal is not null ? $"not written to {file}: {refusal}" : unchanged ? null : $"written to {file} at {sha}";
    }

    /// <summary>A question is announced once when asked. After that, one nudge a day while any still waits.</summary>
    private async Task<string?> RemindAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var today = Today();
        var old = await _db.StewardQuestions
            .Where(q => q.ProjectKey == cfg.Name && q.Answer == null).OrderBy(q => q.AskedAt).ToListAsync(ct);
        old = old.Where(q => q.AskedAt.ToLocalTime().ToString("yyyy-MM-dd") != today).ToList();
        if (old.Count == 0 || old.Any(q => q.RemindedOn == today) || _notify is null) return null;

        var list = string.Join("\n", old.Select(q => $"- {q.Question} (held up: {q.HeldUp})"));
        var sent = await _notify.SendAsync("steward_reminder",
            $"{cfg.DisplayName}: {old.Count} question{(old.Count > 1 ? "s" : "")} still waiting", list, ct);
        if (sent.Sent)
        {
            foreach (var q in old) q.RemindedOn = today;
            await _db.SaveChangesAsync(ct);
        }
        return sent.Sent ? "sent" : sent.Message;
    }

    // ---- reading the board -----------------------------------------------------

    /// <summary>
    /// The board as a steward needs it: every open ticket and what it waits on, the limits and
    /// how much of each is used, the plan and how far it is, what is waiting on the owner, and the
    /// steward's own memo to itself.
    /// </summary>
    public async Task<StewardReply> StatusAsync(string projectKey, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;

        var all = await _tickets.ListAsync(projectKey, null);
        var open = all.Where(t => t.Phase != TicketPhase.Done).ToList();
        var waitingOn = _deps is null
            ? new Dictionary<int, List<TicketDependencyService.ResolvedBlocker>>()
            : await _deps.ResolveAsync(open.Select(t => t.Id).ToList());

        var rows = new List<object>();
        foreach (var t in open.OrderBy(t => t.Id))
        {
            var unanswered = HandoffRequirements.UnansweredOwnerNotes(t).Count;
            rows.Add(new
            {
                id = t.Id,
                phase = t.Phase.ToString(),
                title = t.Title,
                updated = t.UpdatedAt,
                outcomes = t.PlanUnit is null ? null : $"{t.PlanUnit}: {t.PlanOutcomes}",
                waitingOn = waitingOn.TryGetValue(t.Id, out var b) && b.Count > 0 ? b : null,
                unansweredNotes = unanswered > 0 ? unanswered : (int?)null,
                blockedIn = t.Phase == TicketPhase.Blocked ? t.PhaseBeforeBlock?.ToString() : null,
                question = t.Phase == TicketPhase.Blocked ? t.BlockedReason : null,
                sentBack = t.Phase is TicketPhase.Blocked or TicketPhase.NeedsReview ? await SendBacksAsync(t.Id) : (int?)null,
            });
        }

        var s = await _db.StewardStates.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectKey == projectKey, ct);
        var created = s?.CreatedDay == Today() ? s.CreatedCount : 0;
        var plan = await _git.PlanAsync(projectKey, ct);
        var states = await _db.StewardOutcomes.AsNoTracking().Where(o => o.ProjectKey == projectKey).ToListAsync(ct);
        var current = StewardProgress.Current(plan, states);
        var memo = s?.Memo ?? "";
        var waiting = await _db.StewardQuestions.AsNoTracking()
            .Where(q => q.ProjectKey == projectKey && q.Answer == null).OrderBy(q => q.AskedAt).ToListAsync(ct);

        return new StewardReply("status", null, new
        {
            mode = cfg!.StewardMode.ToString().ToLowerInvariant(),
            heartbeat = _heartbeat is null ? null : _heartbeat.IsRunning ? "running" : "stopped: nothing is worked until its owner starts it",
            limits = new
            {
                createdToday = $"{created}/{cfg.Steward.CreatedPerDay}",
                open = $"{open.Count}/{cfg.Steward.OpenAtOnce}",
                sendBacksPerTicket = cfg.Steward.SendBacksPerTicket,
            },
            plan = plan.Count == 0
                ? (object)"This project has no plan configured (steward.plan.units), so there is nothing to file tickets from."
                : plan.Select(u => new { unit = u.Number, u.File, u.Agreed, u.Draft, outcomes = u.Outcomes.Count }),
            progress = current is null ? null : StewardProgress.For(current, states, all),
            outcomes = current is null ? null : current.Outcomes.Select(o =>
            {
                var st = states.FirstOrDefault(x => x.Unit == current.Number && x.Outcome == o);
                return new
                {
                    outcome = o,
                    state = (st?.State ?? StewardOutcomeState.NotStarted).ToString(),
                    moreToFile = st?.MoreToFile ?? false,
                    note = st?.Note,
                    tickets = all.Where(t => t.PlanUnit == current.Number && StewardProgress.OutcomesOf(t).Contains(o))
                        .Select(t => $"#{t.Id} {t.Phase}"),
                };
            }),
            waitingOnOwner = waiting.Select(q => new { q.Key, q.Question, q.HeldUp, asked = q.AskedAt }),
            done = all.Count - open.Count,
            tickets = rows,
            memo,
            memoLength = $"{memo.Length}/{cfg.Steward.MemoMaxChars}",
            memoTooLong = memo.Length > cfg.Steward.MemoMaxChars
                ? "Your memo is over its limit, and every run reads all of it. Cut it now and save it again." : null,
        });
    }

    /// <summary>
    /// Everything the owner has decided through the steward: the answers to its questions and
    /// their standing instructions, newest last. With <paramref name="query"/>, only those that
    /// mention it - the full list grows for as long as the project runs.
    /// </summary>
    public async Task<StewardReply> DecisionsAsync(string projectKey, string? query, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;

        var answered = await _db.StewardQuestions.AsNoTracking()
            .Where(q => q.ProjectKey == projectKey && q.Answer != null).ToListAsync(ct);
        var said = await _db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.OwnerSaid).ToListAsync(ct);

        var lines = answered.Select(q => (At: q.AnsweredAt ?? q.AskedAt, Key: (string?)q.Key, Text: $"{q.Question} -> {q.Answer}"))
            .Concat(said.Select(e => (e.At, Key: (string?)null, e.Text)))
            .OrderBy(x => x.At).ToList();
        if (!string.IsNullOrWhiteSpace(query))
            lines = lines.Where(x => $"{x.Key} {x.Text}".Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        return new StewardReply("decisions", null,
            lines.Select(x => new { on = x.At.ToLocalTime().ToString("yyyy-MM-dd"), key = x.Key, decided = x.Text }));
    }

    // ---- finished tickets ----------------------------------------------------------

    private async Task<List<StewardFlag>> FlagsAsync(ProjectConfig cfg, Ticket t, BranchChange change, CancellationToken ct)
    {
        var flags = StewardFlags.BuiltIn(cfg.Steward, t, change, await _tickets.LastBackwardAtAsync(t.Id));
        if (_projectFlags is not null)
        {
            var plan = await _git.PlanAsync(cfg.Name, ct);
            flags.AddRange(await _projectFlags.RunAsync(cfg.Name, new StewardFlagInput(
                cfg.Name, t.Id, t.Branch!, change.BaseRef, cfg.ProjectPath, t.WorktreePath, change.Files, t.SkipQaTesting,
                t.QaShots.OrderBy(q => q.Order).Select(q => q.ImageFile).ToList(),
                plan.Where(u => u.Agreed).Select(u => u.File).ToList()), ct));
        }
        return flags;
    }

    /// <summary>A finished ticket's record, what it changed, and what about it a merge has to weigh.</summary>
    public async Task<StewardReply> ReviewAsync(string projectKey, int ticketId, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;

        var change = string.IsNullOrWhiteSpace(t.Branch) ? null : await _git.BranchChangeAsync(projectKey, t.Branch, ct);
        var lastBackward = await _tickets.LastBackwardAtAsync(t.Id);
        var said = t.Comments.Where(c => c.Author != AuthorKind.System).OrderBy(c => c.CreatedAt).ToList();

        return new StewardReply("review", null, new
        {
            id = t.Id,
            phase = t.Phase.ToString(),
            title = t.Title,
            description = t.Description,
            summary = t.ImplementationSummary,
            workItems = t.WorkItems.OrderBy(w => w.Order).Select(w => new { w.Done, scope = w.Scope.ToString(), w.Text }),
            qaItems = t.QaItems.OrderBy(q => q.Order).Select(q => new
            {
                status = q.Status.ToString(),
                verified = HandoffRequirements.IsVerified(q, lastBackward),
                byInspection = q.PassedByInspection,
                q.Text,
                q.Note,
            }),
            codeReviewOnly = t.SkipQaTesting,
            qaShots = t.QaShots.Count,
            independentReview = t.GateFindings.OrderBy(f => f.Order)
                .Select(f => new { f.Title, ruling = f.Ruling?.ToString(), why = f.RulingNote }),
            sentBackBefore = await SendBacksAsync(t.Id),
            changed = change is null ? (object)"this ticket has no branch" : change,
            flags = change is null ? new List<StewardFlag>() : await FlagsAsync(cfg!, t, change, ct),
            // A worker never edits the owner's own documents: the line it would have written is in
            // its comment under this heading, wherever in the record that comment now sits.
            forTheOwnersDocs = said.Where(c => Regex.IsMatch(c.Body, @"for the owner['’]s docs", RegexOptions.IgnoreCase))
                .Select(c => new { by = c.Author.ToString(), @in = c.Phase?.ToString(), c.Body }),
            // The last things the phases said, newest last: the refinement hand-off is where open questions sit.
            comments = said.TakeLast(4).Select(c => new { by = c.Author.ToString(), @in = c.Phase?.ToString(), c.Body }),
        });
    }

    /// <summary>The ticket's change itself. With <paramref name="path"/>, one file of it.</summary>
    public async Task<StewardReply> DiffAsync(string projectKey, int ticketId, string? path, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;

        var d = string.IsNullOrWhiteSpace(t.Branch) ? null : await _git.BranchDiffAsync(projectKey, t.Branch, path, DiffMaxChars, ct);
        if (d is null) return Refuse("no-branch", $"Ticket {ticketId} has no branch to show.");
        return new StewardReply("diff", d.Value.Cut
            ? $"Cut: the diff is {d.Value.Length} characters. Ask again with a path for one file's share of it."
            : null, d.Value.Text);
    }

    /// <summary>
    /// Check a finished ticket, then merge it into the base branch and close it.
    /// <para>
    /// Every answer short of "merged" names what to do instead, and none of them is an invitation
    /// to find another way: "owner" is asked about, "not-proved" and "check-failed" are sent back,
    /// "behind" is rebased, "check-could-not-run" is the machine and waits for its owner.
    /// </para>
    /// </summary>
    public async Task<StewardReply> MergeAsync(string projectKey, int ticketId, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        // Propose mode runs every check below and then says what it would have done.
        if (cfg.StewardMode == StewardMode.Manage)
            return Refuse("not-in-this-mode", cfg.StewardModeSetting == StewardMode.Full
                ? $"{cfg.DisplayName} does not deliver by a local merge, so its steward never merges: finishing this ticket is its owner's."
                : "The mode is manage, which never merges. Only the owner changes the mode.");

        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (t.Track != TicketTrack.Delivery) return Refuse("not-ready", "Only a delivery ticket is merged.");
        if (t.Phase != TicketPhase.NeedsReview)
            return Refuse("not-ready", $"Ticket {ticketId} is in {t.Phase}. Only a ticket at NeedsReview is merged.");

        var change = string.IsNullOrWhiteSpace(t.Branch) ? null : await _git.BranchChangeAsync(projectKey, t.Branch, ct);
        if (change is null || change.Ahead == 0)
            return Refuse("nothing-to-merge", $"Ticket {ticketId} has no commits that the base lacks.");

        var flags = await FlagsAsync(cfg, t, change, ct);
        if (flags.Where(f => f.Owner).ToList() is { Count: > 0 } owners)
            return Refuse("owner", "These are the owner's to decide, not yours. Ask, and leave the ticket unmerged.", owners);
        if (flags.Where(f => f.Blocks).ToList() is { Count: > 0 } blocking)
            return Refuse("not-proved", "The work is not proved yet. Send it back saying exactly what each of these says.", blocking);
        if (change.Behind > 0)
            return Refuse("behind", $"The base has moved by {change.Behind} commit(s) since this branch was last rebased. Rebase it, then merge again.");

        // The branch holds everything the base has, so its tip is exactly the tree the merge will
        // make: checking it in its own worktree is checking the result.
        var checks = cfg.Steward.MergeChecks
            .Where(c => c.WhenChanged.Length == 0 || change.Files.Any(f => c.WhenChanged.Any(p =>
                f.StartsWith(p.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))).ToList();
        if (checks.Count > 0)
        {
            var wt = t.WorktreePath;
            if (string.IsNullOrWhiteSpace(wt) || !Directory.Exists(wt))
                return Refuse("no-worktree", $"Ticket {ticketId}'s worktree is gone, so the merged result cannot be checked. This one is the owner's.");
            if (!await _git.WorktreeAtTipAsync(projectKey, wt, t.Branch!, ct))
                return Refuse("worktree-elsewhere", $"Ticket {ticketId}'s worktree is not on the tip of {t.Branch}. This one is the owner's.");

            foreach (var c in checks)
            {
                var run = await _git.RunCheckAsync(wt, c.Run, TimeSpan.FromMinutes(c.TimeoutMinutes), ct);
                if (run.Ok) continue;
                // The command never got as far as checking anything. That is the machine, not the ticket.
                if (c.MachineFault.Length > 0 && Regex.IsMatch(run.Output, c.MachineFault, RegexOptions.IgnoreCase))
                    return Refuse("check-could-not-run",
                        $"'{c.Name}' did not get as far as checking anything, so nothing is proved either way. Leave the ticket at NeedsReview and ask the owner once: this is the machine's to fix, not the ticket's.",
                        new { check = c.Name, output = run.Output });
                return Refuse("check-failed",
                    run.TimedOut ? $"'{c.Name}' did not finish in {c.TimeoutMinutes} minutes on the merged result. Send it back to NeedsImplementation with this."
                                 : $"'{c.Name}' fails on the merged result. Send it back to NeedsImplementation with this.",
                    new { check = c.Name, output = run.Output });
            }
        }

        if (cfg.StewardMode == StewardMode.Propose)
            return Proposed($"merge ticket {ticketId} into the base ({change.Stat}) and close it");

        // The merge commit is named for the ticket, as it is when a person merges one from the board.
        var merged = await _git.MergeAsync(projectKey, t.Branch!, $"{t.Title} (#{t.Id})", null, ct);
        if (!merged.Ok)
            return merged.Conflicted
                ? Refuse("conflict", "The merge conflicts and was rolled back. Send it back to NeedsImplementation naming these files.", merged.ConflictedFiles)
                : Refuse("refused", merged.Error ?? "The merge was refused.");

        var sha = merged.MergeSha?[..Math.Min(7, merged.MergeSha.Length)];
        await _tickets.TransitionAsync(ticketId, TicketPhase.Done, $"Merged into {merged.BaseBranch} at {sha}", TransitionActor.Steward);
        return new StewardReply("merged", null, new { commit = sha, closed = true });
    }

    public async Task<StewardReply> RebaseAsync(string projectKey, int ticketId, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (string.IsNullOrWhiteSpace(t.Branch)) return Refuse("no-branch", $"Ticket {ticketId} has no branch.");
        if (t.Phase != TicketPhase.NeedsReview)
            return Refuse("not-ready", $"Ticket {ticketId} is in {t.Phase}. A branch is only rebased from here while it waits at NeedsReview: at any other time a worker may be in it.");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"rebase {t.Branch} onto the base");

        var r = await _git.RebaseAsync(projectKey, t.Branch, ct);
        if (r.Conflicted) return Refuse("conflict", "The rebase conflicts and was rolled back: nothing was changed. Send it back to NeedsImplementation naming these files.", r.ConflictedFiles);
        return r.Ok
            ? new StewardReply("rebased", null, new { branch = t.Branch, from = r.PreviousSha, to = r.NewSha })
            : Refuse("refused", r.Error ?? "The rebase was refused.");
    }

    /// <summary>
    /// Return a finished ticket with a note saying what is wrong, where, and what done looks like.
    /// To NeedsImplementation when code must change, NeedsRefinement when the code is right but a
    /// check or the record is missing, NeedsSpeculation when the plan was wrong.
    /// </summary>
    public async Task<StewardReply> SendBackAsync(string projectKey, int ticketId, string phase, string note, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        var allowed = new[] { TicketPhase.NeedsImplementation, TicketPhase.NeedsRefinement, TicketPhase.NeedsSpeculation };
        if (!Enum.TryParse<TicketPhase>(phase, true, out var to) || !allowed.Contains(to))
            return Refuse("bad-request", $"Send back to one of: {string.Join(", ", allowed)}.");
        if (string.IsNullOrWhiteSpace(note) || note.Trim().Length < 40)
            return Refuse("bad-request", "The note is what the next run works from: say what is wrong, where, and what done looks like.");

        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (t.Phase != TicketPhase.NeedsReview)
            return Refuse("not-ready", $"Ticket {ticketId} is in {t.Phase}. Only a ticket at NeedsReview is sent back from here.");
        var before = await SendBacksAsync(ticketId);
        if (before >= cfg.Steward.SendBacksPerTicket)
            return Refuse("limit", $"Ticket {ticketId} has been sent back {before} time(s) already. The next is the owner's: ask.");
        if (!PhaseMachine.CanTransition(t.Phase, to, t.Track))
            return Refuse("refused", $"The board does not allow {t.Phase} to {to}.");
        if (cfg.StewardMode == StewardMode.Propose)
            return Proposed($"send ticket {ticketId} back to {to} with: {Head(note, 200)}");

        var body = $"Sent back to {to} ({before + 1} of {cfg.Steward.SendBacksPerTicket}).\n\n{note.Trim()}";
        if (to == TicketPhase.NeedsRefinement)
        {
            // Nothing to build: the note is the directive, and the move is a plain one.
            await _tickets.AddCommentAsync(ticketId, body, AuthorKind.Steward);
            await _tickets.TransitionAsync(ticketId, to, $"Sent back: {Head(note, 200)}", TransitionActor.Steward);
        }
        else
        {
            await _tickets.RequestChangesAsync(ticketId, body, null, shortLoop: false,
                replan: to == TicketPhase.NeedsSpeculation, actor: TransitionActor.Steward);
        }
        return new StewardReply("sent-back", null, new { to = to.ToString() });
    }

    // ---- blocked tickets, and tickets that were wrong -------------------------------

    /// <summary>Answer a blocked ticket from a decision already made, and return it to its queue.</summary>
    public async Task<StewardReply> AnswerBlockAsync(string projectKey, int ticketId, string answer, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        if (string.IsNullOrWhiteSpace(answer)) return Refuse("bad-request", "Say the answer, and the place that decides it.");
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (t.Phase != TicketPhase.Blocked) return Refuse("not-blocked", $"Ticket {ticketId} is in {t.Phase}, not Blocked.");

        var to = PhaseMachine.ResumeAfterBlock(t.PhaseBeforeBlock) ?? TicketPhase.NeedsSpeculation;
        if (cfg.StewardMode == StewardMode.Propose)
            return Proposed($"answer ticket {ticketId} and return it to {to} with: {Head(answer, 200)}");
        await _tickets.TransitionAsync(ticketId, to, answer.Trim(), TransitionActor.Steward);
        return new StewardReply("answered", null, new { to = to.ToString() });
    }

    /// <summary>Correct a ticket the steward itself got wrong: its title, its description, how it is tested.</summary>
    public async Task<StewardReply> AmendAsync(string projectKey, int ticketId, string? title, string? description,
        bool? codeReviewOnly, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        description = string.IsNullOrWhiteSpace(description) ? null : description;
        if (title is null && description is null && codeReviewOnly is null)
            return Refuse("bad-request", "Give a title, a description, how it is tested, or more than one.");
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (!string.IsNullOrEmpty(t.IssueKey))
            return Refuse("refused", "This ticket's words belong to its tracker issue, and that is the owner's to edit.");

        var what = string.Join(" and ", new[]
        {
            title is null ? null : "title",
            description is null ? null : "description",
            codeReviewOnly == true ? "testing (now read, not run)" : codeReviewOnly == false ? "testing (now tested in full)" : null,
        }.Where(x => x is not null));
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"amend ticket {ticketId}: {what}");

        // The plan line is the steward's own and is what ties the ticket to its outcomes on a read.
        if (description is not null && PlanLine(t) is { } line && !description.Contains(line)) description = $"{description.TrimEnd()}\n\n{line}";
        await _tickets.UpdateAsync(ticketId, title, description, null, null, null, null, skipQaTesting: codeReviewOnly);
        await _tickets.AddCommentAsync(ticketId, $"Amended the {what}.", AuthorKind.Steward);
        return new StewardReply("amended");
    }

    private static string? PlanLine(Ticket t) =>
        t.PlanUnit is null ? null : $"**Plan**\n\nUnit {t.PlanUnit}: {t.PlanOutcomes}.";

    // ---- the next tickets ---------------------------------------------------------

    /// <summary>
    /// File a ticket against an agreed unit of the plan. Parked first and queued last, because a
    /// ticket in a queue can be claimed within seconds - before its dependencies are on it.
    /// </summary>
    public async Task<StewardReply> CreateAsync(string projectKey, StewardTicketDraft d, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;

        var title = d.Title?.Trim() ?? "";
        if (title.Length is < 10 or > 110) return Refuse("bad-request", "The title is one line, 10 to 110 characters.");
        if ((d.Description?.Length ?? 0) < 200)
            return Refuse("bad-request", "The description is the whole ask. The agent that plans it has this and the repo and nothing else.");
        foreach (var part in new[] { "acceptance criteria", "out of scope" })
            if (!d.Description!.Contains(part, StringComparison.OrdinalIgnoreCase))
                return Refuse("bad-request", $"The description has no \"{part}\" part.");
        var priority = TicketPriority.Normal;
        if (!string.IsNullOrWhiteSpace(d.Priority)
            && (!Enum.TryParse(d.Priority, true, out priority) || !Enum.IsDefined(priority) || priority == TicketPriority.Critical))
            return Refuse("bad-request", "Priority is Lowest, Low, Normal or High. Critical is the owner's.");

        // Only an agreed unit may be worked, and only its own outcomes: the spec is read from git.
        var plan = await _git.PlanAsync(projectKey, ct);
        if (plan.Count == 0) return Refuse("no-plan", "This project has no plan to file tickets from.");
        var unit = plan.FirstOrDefault(u => u.Number == d.Unit);
        if (unit is null) return Refuse("bad-request", $"The plan has no unit numbered {d.Unit}.");
        if (!unit.Agreed) return Refuse("unit-not-agreed", $"{unit.File} is not agreed. Only the owner agrees a unit of the plan.");
        var outcomes = (d.Outcomes ?? Array.Empty<string>()).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();
        if (outcomes.Count == 0) return Refuse("bad-request", "Say which of the unit's outcomes this ticket delivers.");
        if (outcomes.Where(o => !unit.Outcomes.Contains(o)).ToList() is { Count: > 0 } unknown)
            return Refuse("bad-request", $"{unit.File} has no outcome {string.Join(", ", unknown)}.");

        var all = await _tickets.ListAsync(projectKey, null);
        var open = all.Count(t => t.Phase != TicketPhase.Done);
        var s = await StewardDue.StateAsync(_db, projectKey);
        if (s.CreatedDay != Today()) { s.CreatedDay = Today(); s.CreatedCount = 0; }
        if (s.CreatedCount >= cfg.Steward.CreatedPerDay)
            return Refuse("limit", $"{cfg.Steward.CreatedPerDay} tickets have been created today. The next run continues.");
        if (open >= cfg.Steward.OpenAtOnce)
            return Refuse("limit", $"{open} tickets are open, and {cfg.Steward.OpenAtOnce} is the most at once. Finish some first.");
        if (all.FirstOrDefault(t => string.Equals(t.Title?.Trim(), title, StringComparison.OrdinalIgnoreCase)) is { } same)
            return Refuse("duplicate", $"Ticket {same.Id} ({same.Phase}) already has this title.");
        var dependsOn = (d.DependsOn ?? Array.Empty<int>()).Distinct().ToList();
        if (dependsOn.Where(id => all.All(t => t.Id != id)).ToList() is { Count: > 0 } missing)
            return Refuse("bad-request", $"It depends on tickets that do not exist here: {string.Join(", ", missing)}.");

        if (cfg.StewardMode == StewardMode.Propose)
            return Proposed($"create \"{title}\" for unit {unit.Number} {string.Join(", ", outcomes)}"
                + (dependsOn.Count > 0 ? $", after {string.Join(", ", dependsOn)}" : ""));

        var made = await _tickets.CreateAsync(projectKey, title, d.Description, null, null, TicketPhase.Backlog,
            skipQaTesting: d.CodeReviewOnly, priority: priority, skipClassroomAnalysis: cfg.SkipClassroomAnalysisByDefault);
        var row = await _db.Tickets.FirstAsync(t => t.Id == made.Id, ct);
        row.CreatedBy = TransitionActor.Steward;
        row.PlanUnit = unit.Number;
        row.PlanOutcomes = string.Join(", ", outcomes);
        row.Description = $"{d.Description!.TrimEnd()}\n\n{PlanLine(row)}";
        s.CreatedCount++;
        await _db.SaveChangesAsync(ct);

        if (_deps is not null)
            foreach (var dep in dependsOn)
                if (await _deps.AddLocalAsync(made.Id, null, dep, "built on this") is null)
                    return new StewardReply("created-but-parked", $"Could not add the dependency on {dep}. It is in Backlog.", new { id = made.Id });

        await _tickets.TransitionAsync(made.Id, TicketPhase.NeedsSpeculation,
            $"Filed for unit {unit.Number}: {string.Join(", ", outcomes)}", TransitionActor.Steward);
        return new StewardReply("created", null, new { id = made.Id, createdToday = $"{s.CreatedCount}/{cfg.Steward.CreatedPerDay}" });
    }

    /// <summary>Record where one outcome of the plan stands, and whether a ticket for it is still to be written.</summary>
    public async Task<StewardReply> SetOutcomeAsync(string projectKey, int unit, string outcome, string state,
        bool moreToFile, string? note, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        if (!Enum.TryParse<StewardOutcomeState>(state, true, out var st) || !Enum.IsDefined(st))
            return Refuse("bad-request", "State is NotStarted, InProgress or Done.");
        var plan = await _git.PlanAsync(projectKey, ct);
        var u = plan.FirstOrDefault(x => x.Number == unit);
        if (u is null || !u.Agreed) return Refuse("unit-not-agreed", $"Unit {unit} is not an agreed unit of the plan.");
        outcome = outcome?.Trim() ?? "";
        if (!u.Outcomes.Contains(outcome)) return Refuse("bad-request", $"{u.File} has no outcome {outcome}.");
        if (note?.Length > 400)
            return Refuse("bad-request", "The note is a line for the next run: what is left to file and what it waits on. 400 characters at most. What a merged ticket built is in the code.");
        if (st == StewardOutcomeState.Done && moreToFile)
            return Refuse("bad-request", "An outcome with a ticket still to be written is not done.");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"mark {unit} {outcome} as {st}");

        var row = await _db.StewardOutcomes.FirstOrDefaultAsync(
            o => o.ProjectKey == projectKey && o.Unit == unit && o.Outcome == outcome, ct);
        if (row is null) _db.StewardOutcomes.Add(row = new StewardOutcome { ProjectKey = projectKey, Unit = unit, Outcome = outcome });
        row.State = st;
        row.MoreToFile = moreToFile;
        row.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new StewardReply("recorded");
    }

    /// <summary>
    /// Write or revise the draft spec for the unit after the last agreed one. One draft ahead,
    /// never two, and never one that says it is agreed: that is a commit only the owner makes.
    /// </summary>
    public async Task<StewardReply> DraftUnitAsync(string projectKey, string fileName, string text, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        var p = cfg.Steward.Plan;
        if (!p.Configured) return Refuse("no-plan", "This project has no plan folder to draft into.");

        var m = Regex.Match(fileName?.Trim() ?? "", p.UnitFile, RegexOptions.IgnoreCase);
        if (!m.Success || fileName!.Contains('/') || fileName.Contains('\\') || !int.TryParse(m.Groups[1].Value, out var number))
            return Refuse("bad-request", "Name the file the way the plan's other units are named, number first.");
        var plan = await _git.PlanAsync(projectKey, ct);
        var lastAgreed = plan.Where(u => u.Agreed).Select(u => u.Number).DefaultIfEmpty(0).Max();
        if (number != lastAgreed + 1)
            return Refuse("not-next", $"The last agreed unit is {lastAgreed}, so the only draft there can be is unit {lastAgreed + 1}.");
        var path = $"{p.UnitsDir}/{fileName.Trim()}";
        var there = plan.FirstOrDefault(u => u.Number == number);
        if (there is not null && !string.Equals(there.File, path, StringComparison.OrdinalIgnoreCase))
            return Refuse("exists", $"Unit {number} is already drafted as {there.File}. Revise that file, under its own name.");

        var unit = TicketGitService.ReadUnit(p, number, path, text ?? "");
        if (unit.Agreed || !unit.Draft)
            return Refuse("bad-request", "Its status line must say it is a draft. Only the owner agrees a unit.");
        if (unit.Outcomes.Count == 0) return Refuse("bad-request", "The draft has no outcomes written the way the plan's other units write them.");
        if (cfg.StewardMode == StewardMode.Propose)
            return Proposed($"{(there is null ? "write" : "revise")} the draft spec {path} ({unit.Outcomes.Count} outcomes)");

        var (sha, unchanged, refusal) = await _git.CommitFileAsync(projectKey, path, text!,
            there is null ? $"A draft spec for unit {number}" : $"The draft spec for unit {number}, revised", ct);
        if (refusal is not null) return Refuse("refused", refusal);
        return unchanged ? new StewardReply("unchanged")
            : new StewardReply(there is null ? "drafted" : "revised", null, new { file = path, outcomes = unit.Outcomes.Count, commit = sha });
    }

    // ---- what goes to the owner, and the record ---------------------------------------

    /// <summary>Put one question to the owner, once: recorded where the board shows it, and announced.</summary>
    public async Task<StewardReply> AskAsync(string projectKey, StewardAsk a, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        var key = a.Key?.Trim() ?? "";
        if (!Regex.IsMatch(key, "^[a-z0-9-]{4,50}$"))
            return Refuse("bad-request", "The key is a short name for this question, lowercase with hyphens, like \"sms-provider\".");
        foreach (var (name, value) in new[] { ("question", a.Question), ("why", a.Why), ("heldUp", a.HeldUp) })
            if (string.IsNullOrWhiteSpace(value)) return Refuse("bad-request", $"\"{name}\" is missing.");
        var options = (a.Options ?? Array.Empty<BlockOption>()).Where(o => !string.IsNullOrWhiteSpace(o.Label)).ToList();
        if (options.Count < 2) return Refuse("bad-request", "Give at least two options, each with what it costs or rules out.");
        if (options.Count(o => o.Recommended) != 1)
            return Refuse("bad-request", "Mark exactly one option as the one you would pick.");

        if (await _db.StewardQuestions.AnyAsync(q => q.ProjectKey == projectKey && q.Key == key, ct))
            return Refuse("already-asked", $"\"{key}\" has been asked already. A question is asked once.");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"ask the owner: {a.Question.Trim()}");

        _db.StewardQuestions.Add(new StewardQuestion
        {
            ProjectKey = projectKey,
            Key = key,
            Question = a.Question.Trim(),
            Why = a.Why.Trim(),
            OptionsJson = JsonSerializer.Serialize(options, Json),
            HeldUp = a.HeldUp.Trim(),
            TicketId = a.TicketId,
        });
        await _db.SaveChangesAsync(ct);

        var pick = options.First(o => o.Recommended).Label;
        var notice = _notify is null ? null : await _notify.SendAsync("steward_question",
            $"{cfg.DisplayName} needs a decision",
            $"{a.Question.Trim()}\n\nI would pick: {pick}\n\nHeld up: {a.HeldUp.Trim()}\n\nAnswer it on the board, under Steward.", ct);
        return new StewardReply("asked", null, new { notice = notice is null ? "no notifier" : notice.Sent ? "sent" : notice.Message });
    }

    /// <summary>
    /// Add a line to the record the owner reads back: a call made for them about one ticket, or
    /// something to read before the base branch is released. Written once and never read again by
    /// the steward - which is what keeps it out of every later run.
    /// </summary>
    public async Task<StewardReply> RecordAsync(string projectKey, StewardEntryKind kind, int ticketId, string text, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        if (kind == StewardEntryKind.OwnerSaid) return Refuse("refused", "What the owner said is the owner's to write.");
        if (string.IsNullOrWhiteSpace(text)) return Refuse("bad-request", "Say what was decided, and the place that decided it.");
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"note for ticket {ticketId}: {Head(text, 200)}");

        _db.StewardEntries.Add(new StewardEntry { ProjectKey = projectKey, Kind = kind, TicketId = ticketId, Text = text.Trim() });
        await _db.SaveChangesAsync(ct);
        return new StewardReply("noted");
    }

    /// <summary>
    /// Replace the steward's memo to its next run. Saved even when it is too long - a run that
    /// could not save would leave the next one nothing - and the answer says so, so it is cut.
    /// </summary>
    public async Task<StewardReply> SaveMemoAsync(string projectKey, string memo, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        if (cfg.StewardMode == StewardMode.Propose)
            return new StewardReply("proposed", "Nothing happened in this run, so the memo must not say it did: it was left as it was.");

        var s = await StewardDue.StateAsync(_db, projectKey);
        s.Memo = (memo ?? "").Replace("\r\n", "\n").Trim();
        s.MemoUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var over = s.Memo.Length > cfg.Steward.MemoMaxChars;
        return new StewardReply(over ? "saved-too-long" : "saved", over
            ? $"The memo is {s.Memo.Length} characters and its limit is {cfg.Steward.MemoMaxChars}: every run reads all of it. Cut it and save again. Where an outcome stands is set on the outcome; a call about one ticket goes on the record; this is only what the next run needs and cannot read anywhere else."
            : null);
    }

    // ---- the owner's side: no steward verb reaches these --------------------------------

    /// <summary>The owner's answer to one waiting question. A decision made, and a reason to run.</summary>
    public async Task<StewardReply> OwnerAnswersAsync(string projectKey, string key, string answer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(answer)) return Refuse("bad-request", "The answer is empty.");
        var q = await _db.StewardQuestions.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.Key == key, ct);
        if (q is null) return Refuse("not-waiting", $"No question named \"{key}\".");
        if (q.Answer is not null) return Refuse("not-waiting", $"\"{key}\" was answered on {q.AnsweredAt:yyyy-MM-dd}.");

        q.Answer = Regex.Replace(answer.Trim(), @"\s*\n\s*", " ");
        q.AnsweredAt = DateTime.UtcNow;
        await StewardDue.MarkAsync(_db, projectKey, $"the owner answered {key}");
        await _db.SaveChangesAsync(ct);
        var exported = Config(projectKey) is { } cfg ? await ExportDecisionsAsync(cfg, ct) : null;
        _heartbeat?.Wake();
        return new StewardReply("recorded", exported);
    }

    /// <summary>A standing instruction from the owner that answers no question.</summary>
    public async Task<StewardReply> OwnerSaysAsync(string projectKey, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Refuse("bad-request", "There is nothing to record.");
        if (Config(projectKey) is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        _db.StewardEntries.Add(new StewardEntry { ProjectKey = projectKey, Kind = StewardEntryKind.OwnerSaid, Text = text.Trim() });
        await StewardDue.MarkAsync(_db, projectKey, "the owner left an instruction");
        await _db.SaveChangesAsync(ct);
        var exported = await ExportDecisionsAsync(Config(projectKey)!, ct);
        _heartbeat?.Wake();
        return new StewardReply("recorded", exported);
    }

    /// <summary>
    /// Take in a steward's record that was kept somewhere else - a ledger file, another machine.
    /// <para>
    /// The owner's to run, once, when a project's stand-in moves into the dashboard from wherever
    /// it lived before. Safe to run again: a question already here by its key is left alone, an
    /// identical line is not added twice, an outcome is set to what the import says, and the memo
    /// is only filled when there is none. Everything it brings in is history, so none of it is
    /// handed to the next run as news - that run finds it where every later one will.
    /// </para>
    /// </summary>
    public async Task<StewardReply> ImportAsync(string projectKey, StewardImport import, CancellationToken ct = default)
    {
        if (Config(projectKey) is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var now = DateTime.UtcNow;
        int questions = 0, said = 0, entries = 0, outcomes = 0, tickets = 0;

        var keys = (await _db.StewardQuestions.Where(q => q.ProjectKey == projectKey).Select(q => q.Key).ToListAsync(ct)).ToHashSet();
        foreach (var q in import.Questions ?? Array.Empty<StewardImportQuestion>())
        {
            if (string.IsNullOrWhiteSpace(q.Key) || string.IsNullOrWhiteSpace(q.Question) || !keys.Add(q.Key.Trim())) continue;
            var answered = !string.IsNullOrWhiteSpace(q.Answer);
            _db.StewardQuestions.Add(new StewardQuestion
            {
                ProjectKey = projectKey,
                Key = q.Key.Trim(),
                Question = q.Question.Trim(),
                Why = q.Why?.Trim() ?? "",
                OptionsJson = JsonSerializer.Serialize(q.Options ?? Array.Empty<BlockOption>(), Json),
                HeldUp = q.HeldUp?.Trim() ?? "",
                AskedAt = q.AskedAt ?? q.AnsweredAt ?? now,
                Answer = answered ? q.Answer!.Trim() : null,
                AnsweredAt = answered ? q.AnsweredAt ?? now : null,
                AnswerDeliveredAt = answered ? now : null,
            });
            questions++;
        }

        var have = (await _db.StewardEntries.Where(e => e.ProjectKey == projectKey).Select(e => e.Text).ToListAsync(ct)).ToHashSet();
        foreach (var e in import.Entries ?? Array.Empty<StewardImportEntry>())
        {
            var text = e.Text?.Trim() ?? "";
            if (text.Length == 0 || !Enum.TryParse<StewardEntryKind>(e.Kind, true, out var kind) || !have.Add(text)) continue;
            _db.StewardEntries.Add(new StewardEntry
            {
                ProjectKey = projectKey, Kind = kind, TicketId = e.TicketId, Text = text, At = e.At ?? now,
                DeliveredAt = kind == StewardEntryKind.OwnerSaid ? now : null,
            });
            if (kind == StewardEntryKind.OwnerSaid) said++; else entries++;
        }

        foreach (var o in import.Outcomes ?? Array.Empty<StewardImportOutcome>())
        {
            if (string.IsNullOrWhiteSpace(o.Outcome) || !Enum.TryParse<StewardOutcomeState>(o.State, true, out var st)) continue;
            var row = await _db.StewardOutcomes.FirstOrDefaultAsync(
                x => x.ProjectKey == projectKey && x.Unit == o.Unit && x.Outcome == o.Outcome, ct);
            if (row is null) _db.StewardOutcomes.Add(row = new StewardOutcome { ProjectKey = projectKey, Unit = o.Unit, Outcome = o.Outcome.Trim() });
            row.State = st;
            row.MoreToFile = o.MoreToFile && st != StewardOutcomeState.Done;
            row.Note = string.IsNullOrWhiteSpace(o.Note) ? null : o.Note.Trim();
            row.UpdatedAt = now;
            outcomes++;
        }

        foreach (var t in import.Tickets ?? Array.Empty<StewardImportTicket>())
        {
            var row = await _db.Tickets.FirstOrDefaultAsync(x => x.Id == t.Id && x.ProjectKey == projectKey, ct);
            if (row is null || t.Outcomes is not { Count: > 0 }) continue;
            row.PlanUnit = t.Unit;
            row.PlanOutcomes = string.Join(", ", t.Outcomes.Select(o => o.Trim()).Where(o => o.Length > 0));
            if (t.FiledBySteward) row.CreatedBy = TransitionActor.Steward;
            tickets++;
        }

        var s = await StewardDue.StateAsync(_db, projectKey);
        var memo = false;
        if (!string.IsNullOrWhiteSpace(import.Memo) && string.IsNullOrWhiteSpace(s.Memo))
        {
            s.Memo = import.Memo.Replace("\r\n", "\n").Trim();
            s.MemoUpdatedAt = now;
            memo = true;
        }

        await _db.SaveChangesAsync(ct);
        return new StewardReply("imported", null, new { questions, ownerSaid = said, entries, outcomes, tickets, memo });
    }

    private static string Head(string s, int max)
    {
        var line = s.Trim().Split('\n')[0].Trim();
        return line.Length > max ? line[..(max - 3)] + "..." : line;
    }
}
