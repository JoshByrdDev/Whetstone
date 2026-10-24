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
/// <param name="Touches">The register's decisions the question bears on; empty when none does. Required.</param>
/// <param name="Changes">For each option, by its label, which of <paramref name="Touches"/> picking it would change.</param>
public record StewardAsk(
    string Key, string Question, string Why, IReadOnlyList<BlockOption>? Options, string HeldUp, int? TicketId = null,
    int? Unit = null, IReadOnlyList<int>? Decisions = null, IReadOnlyList<int>? Touches = null,
    IReadOnlyDictionary<string, IReadOnlyList<int>>? Changes = null, IReadOnlyList<int>? FollowUps = null,
    StewardIdeaProposal? Idea = null, StewardStoryChange? Story = null);

/// <summary>The sentence of a slice's story an answer would change, and its rewording under each option, by label.</summary>
public record StewardStoryChange(string Sentence, IReadOnlyDictionary<string, string>? Rewordings);

/// <summary>A steward's record kept elsewhere, to be taken in once. See <see cref="StewardService.ImportAsync"/>.</summary>
public record StewardImport(
    IReadOnlyList<StewardImportQuestion>? Questions = null, IReadOnlyList<StewardImportEntry>? Entries = null,
    IReadOnlyList<StewardImportOutcome>? Outcomes = null, IReadOnlyList<StewardImportTicket>? Tickets = null);

/// <summary>A question as it was asked; with <paramref name="Answer"/> when the owner has answered it.</summary>
public record StewardImportQuestion(string Key, string Question, string? Why = null,
    IReadOnlyList<BlockOption>? Options = null, string? HeldUp = null, DateTime? AskedAt = null,
    string? Answer = null, DateTime? AnsweredAt = null);

/// <param name="Kind">Any record kind: Call, BeforeRelease, OwnerSaid, TicketNote, Ruling or Learned.</param>
/// <param name="Basis">A ruling's: what it rests on.</param>
/// <param name="Key">A learned fact's suggested note key.</param>
public record StewardImportEntry(string Kind, string Text, int? TicketId = null, DateTime? At = null,
    string? Basis = null, string? Key = null);

public record StewardImportOutcome(int Unit, string Outcome, string State, bool MoreToFile = false, string? Note = null);

public record StewardImportTicket(int Id, int Unit, IReadOnlyList<string> Outcomes, bool FiledBySteward = true);

/// <summary>One idea a new plan starts with: its title, and its paragraph.</summary>
public record StewardNewIdea(string Title, string? Paragraph);

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
public partial class StewardService
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
        // Handed over, not yet dealt with: the reasons, answers and instructions below are only
        // marked done by steward_end. A run that dies part-way leaves them for the next one.
        s.RunDueReason = why;
        s.LastRunStartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var fresh = await _db.StewardQuestions.AsNoTracking()
            .Where(q => q.ProjectKey == projectKey && q.Answer != null && q.AnswerDeliveredAt == null).ToListAsync(ct);
        var said = await _db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.OwnerSaid && e.DeliveredAt == null)
            .OrderBy(e => e.At).ToListAsync(ct);
        var reversed = await _db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.Ruling && e.ReversedAt != null && e.DeliveredAt == null)
            .OrderBy(e => e.ReversedAt).ToListAsync(ct);
        await EnsureRegisterAsync(cfg!, ct);
        var changed = await UndeliveredAsync(projectKey, ct);
        var instructed = await _db.StewardInstructions.AsNoTracking()
            .Where(i => i.ProjectKey == projectKey && i.ClosedAt == null && i.DeliveredAt == null).OrderBy(i => i.At).ToListAsync(ct);

        SyncFromResult? sync = null;
        if (cfg!.Steward.SyncFrom.Length > 0)
            sync = await _git.SyncFromAsync(projectKey, cfg.Steward.SyncFrom, cfg.StewardMode == StewardMode.Propose, ct);

        var (still, gone) = await FreshReasonsAsync(projectKey, why, ct);
        var walkthroughs = await WalkthroughsAsync(cfg, ct);

        return new StewardReply("started", null, new
        {
            mode = cfg.StewardMode.ToString().ToLowerInvariant(),
            // Why this run was owed, checked against the board as it is now: a ticket that was
            // blocked when the reason was written may have been answered since.
            because = still.Count > 0 ? string.Join("; ", still) : "nothing in particular: a look at whether anything is left to file",
            noLongerSo = gone.Count > 0 ? gone : null,
            newAnswers = fresh.Select(q => new { q.Key, q.Question, q.Answer }),
            newInstructions = said.Select(e => e.Text)
                .Concat(instructed.Select(i => $"[instruction {i.Id}] {i.Text} - until {i.Until}. Close it with steward_close_instruction when that comes.")),
            // Rulings of yours the owner has reversed since the last run that ended properly: stop
            // applying each, and apply what the owner said instead. Each is a decision made.
            reversedRulings = reversed.Select(e => new { e.Id, ruling = e.Text, ownerSaid = e.ReversedBecause }),
            // The owner's own changes to the register since the last run: a decision recorded,
            // withdrawn, or now replacing another. Apply what is in force; stop applying what is not.
            changedDecisions = changed.Select(d => new
            {
                number = d.Number, rule = d.Rule,
                withdrawn = d.WithdrawnAt is null ? null : d.WithdrawnBecause,
                replaces = d.Replaces.Count == 0 ? null : d.Replaces.Select(r => new { number = r.Number, why = r.Why }),
            }),
            sync,
            // The slice's decisions in force, provisional ones coming due, rulings reading a decision
            // that no longer holds, open follow-ups and instructions.
            briefing = await BriefingAsync(cfg, ct),
            // Walk-throughs the board closed (passed: Z1 done) or filed (every other outcome done).
            walkthroughs,
            status = (await StatusAsync(projectKey, ct)).Data,
        });
    }

    private static readonly Regex TicketReason = new(@"^#(\d+) is (ready for review|blocked)$");

    /// <summary>
    /// The reasons a run is owed, split into those still true and those the board has moved past.
    /// Only a reason about where a ticket stands can go stale; every other reason (an answer, an
    /// instruction, a run that did not finish) is news until a run has dealt with it.
    /// </summary>
    private async Task<(List<string> Still, List<string> Gone)> FreshReasonsAsync(string projectKey, string? why, CancellationToken ct)
    {
        var still = new List<string>();
        var gone = new List<string>();
        var reasons = (why ?? "").Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var r in reasons)
        {
            var m = TicketReason.Match(r);
            if (!m.Success) { still.Add(r); continue; }
            var id = int.Parse(m.Groups[1].Value);
            var phase = await _db.Tickets.AsNoTracking().Where(t => t.Id == id && t.ProjectKey == projectKey)
                .Select(t => (TicketPhase?)t.Phase).FirstOrDefaultAsync(ct);
            var want = m.Groups[2].Value == "blocked" ? TicketPhase.Blocked : TicketPhase.NeedsReview;
            if (phase == want) still.Add(r);
            else gone.Add($"{r} (now {phase?.ToString() ?? "gone"})");
        }
        return (still, gone);
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
        // What this run was handed at its start is dealt with now, and only now. Anything that
        // arrived while it ran (an answer, a ticket at review) is still news for the next.
        if (s.LastRunStartedAt is { } began)
        {
            var now = DateTime.UtcNow;
            foreach (var q in await _db.StewardQuestions.Where(q => q.ProjectKey == projectKey && q.Answer != null
                         && q.AnswerDeliveredAt == null && q.AnsweredAt <= began).ToListAsync(ct))
                q.AnswerDeliveredAt = now;
            foreach (var e in await _db.StewardEntries.Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.OwnerSaid
                         && e.DeliveredAt == null && e.At <= began).ToListAsync(ct))
                e.DeliveredAt = now;
            foreach (var e in await _db.StewardEntries.Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.Ruling
                         && e.ReversedAt != null && e.DeliveredAt == null && e.ReversedAt <= began).ToListAsync(ct))
                e.DeliveredAt = now;
            await DeliveredAsync(projectKey, began, ct);
            foreach (var i in await _db.StewardInstructions.Where(i => i.ProjectKey == projectKey && i.DeliveredAt == null && i.At <= began).ToListAsync(ct))
                i.DeliveredAt = now;
        }
        StewardDue.Settle(s);
        s.LastRunEndedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(summary)) s.LastRunSummary = summary.Trim();
        _db.StewardEntries.Add(new StewardEntry
        {
            ProjectKey = projectKey, Kind = StewardEntryKind.Run,
            Text = string.IsNullOrWhiteSpace(summary) ? "Ended without saying what it did." : summary.Trim(),
        });
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
            // What it was handed is still due (begin never cleared it), and the next run is told
            // this one stopped part-way, so it checks what was left half done. The gap between runs
            // still holds, so a run that dies on every start is not restarted in a loop.
            s.RunDueReason = null;
            await StewardDue.MarkAsync(_db, projectKey, "the last run did not finish");
            _db.StewardEntries.Add(new StewardEntry
            {
                ProjectKey = projectKey, Kind = StewardEntryKind.Run, Text = $"Did not finish: {reason}.",
            });
            await _db.SaveChangesAsync();
        }
        return released;
    }

    /// <summary>
    /// Write the register of decisions out to the project's repo, when it names a file for them.
    /// Best-effort by design: the hub is a person's checkout, and when it is busy or dirty this
    /// says so and leaves it - the next change to the register, or the end of the next run, writes
    /// the whole file again, so nothing is lost by skipping once.
    /// </summary>
    private async Task<string?> ExportDecisionsAsync(ProjectConfig cfg, CancellationToken ct)
    {
        var file = cfg.Steward.DecisionsFile;
        if (file.Length == 0 || cfg.StewardMode is StewardMode.Off or StewardMode.Propose) return null;
        await EnsureRegisterAsync(cfg, ct);
        if (await RegisterTextAsync(cfg, ct) is not { } text) return null;

        var (sha, unchanged, refusal) = await _git.CommitFileAsync(cfg.Name, file, text, "The owner's decisions, written out from the board's register", ct);
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
    /// steward's own notes, rulings and recent runs.
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
        var key = cfg!.Steward.Plan.Key;
        var states = await StatesAsync(projectKey, key, ct);
        // Only this plan's tickets count toward it: the last plan's unit 1 is not this one's.
        var mine = all.Where(t => (t.PlanKey ?? "") == key).ToList();
        var current = StewardProgress.Current(plan, states);
        var recentRuns = await _db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.Run)
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(3).ToListAsync(ct);
        var byId = all.ToDictionary(t => t.Id);
        // Every open ticket note, by ticket - Done tickets included, since what is left to do after
        // a merge is most of what these say. A note whose ticket is gone has nothing to act on.
        var ticketNotes = (await OpenNotesAsync(projectKey, null, ct))
            .Where(n => n.TicketId is { } id && byId.ContainsKey(id))
            .GroupBy(n => n.TicketId!.Value)
            .Select(g => new
            {
                ticket = g.Key,
                phase = byId[g.Key].Phase.ToString(),
                title = byId[g.Key].Title,
                notes = g.Select(n => new { n.Id, n.Text, n.At }),
            }).ToList();
        var waiting = await _db.StewardQuestions.AsNoTracking()
            .Where(q => q.ProjectKey == projectKey && q.Answer == null).OrderBy(q => q.AskedAt).ToListAsync(ct);
        var rulings = await _db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.Ruling && e.ReversedAt == null)
            .OrderBy(e => e.At).ToListAsync(ct);
        var learned = await UnplacedAsync(projectKey, ct);
        var flags = await RulingFlagsAsync(projectKey, ct);
        var instructions = await OpenInstructionsAsync(projectKey, ct);

        return new StewardReply("status", null, new
        {
            mode = cfg.StewardMode.ToString().ToLowerInvariant(),
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
            progress = current is null ? null : StewardProgress.For(current, states, mine),
            // Every outcome not yet Done, in every agreed unit - not only the earliest unit with one
            // left. One outcome waiting on the owner for weeks would otherwise hide every later
            // unit's notes from the run, and the notes are where what is left to file now lives.
            outcomes = plan.Where(u => u.Agreed).SelectMany(u => u.Outcomes.Select(o => (u, o)))
                .Select(x => (x.u, x.o, st: states.FirstOrDefault(s => s.Unit == x.u.Number && s.Outcome == x.o)))
                .Where(x => x.st?.State != StewardOutcomeState.Done)
                .Select(x => new
                {
                    unit = x.u.Number,
                    outcome = x.o,
                    state = (x.st?.State ?? StewardOutcomeState.NotStarted).ToString(),
                    moreToFile = x.st?.MoreToFile ?? false,
                    note = x.st?.Note,
                    tickets = mine.Where(t => t.PlanUnit == x.u.Number && StewardProgress.OutcomesOf(t).Contains(x.o))
                        .Select(t => $"#{t.Id} {t.Phase}"),
                }),
            waitingOnOwner = waiting.Select(q => new { q.Key, q.Question, q.HeldUp, asked = q.AskedAt }),
            done = all.Count - open.Count,
            tickets = rows,
            // What earlier runs meant to do, by ticket. Each stays until closed with steward_done_with.
            ticketNotes,
            // Your standing rulings, in force until the owner reverses one. Apply them as you would
            // a decision already made - they are readings of decisions, never new ones.
            rulings = rulings.Select(e => new
            {
                e.Id, ruling = e.Text, restsOn = e.Basis, reads = Numbers(e.Reads), ticket = e.TicketId, keptByOwner = e.HandledAt != null,
                // A ruling reading a decision that no longer holds: apply it no further until the owner says.
                flagged = flags.TryGetValue(e.Id, out var f) ? f : null,
            }),
            // What the owner told you to do for a while, each with its end. Close one with
            // steward_close_instruction when its end has come.
            instructions = instructions.Select(i => new { i.Id, i.Text, i.Until }),
            // Facts you said with steward_learned that are not in the notes store yet: they go in
            // after this run. Until then the store's index does not list them, so they are here.
            learnedPending = learned.Select(e => e.Text),
            // The last three runs, newest first: what each did, or why it stopped.
            recentRuns = recentRuns.Select(e => new { e.At, e.Text }),
        });
    }

    /// <summary>One option a question offered, as <see cref="DecisionsAsync"/> hands it back.</summary>
    private sealed record Offered(string Label, string? Detail, bool Picked, bool StewardsPick);

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

    /// <summary>
    /// Flags that say nothing was checked, so there is nothing for the owner to have looked at and
    /// waved through. A clearance of one would clear everything the check it stands for would raise.
    /// </summary>
    private static readonly HashSet<string> NeverCleared = new(StringComparer.OrdinalIgnoreCase) { "project-flags-did-not-run" };

    /// <summary>The flags the owner has cleared for this exact change of the ticket's.</summary>
    private async Task<HashSet<string>> ClearedAsync(string projectKey, int ticketId, BranchChange change, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(change.ChangeId)) return new();
        var names = await _db.StewardClearances.AsNoTracking()
            .Where(c => c.ProjectKey == projectKey && c.TicketId == ticketId && c.ChangeId == change.ChangeId)
            .Select(c => c.Flag).ToListAsync(ct);
        return names.Where(n => !NeverCleared.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A finished ticket's record, what it changed, and what about it a merge has to weigh.</summary>
    /// <summary>
    /// The most of a review sent in one answer. Past it, an MCP client saves the answer to a file the
    /// steward has no way to read whole - so a long review is sent as its head and its flags, and the
    /// rest by part.
    /// </summary>
    public const int ReviewMaxChars = 24000;

    /// <summary>The parts a long review is read in.</summary>
    public static readonly string[] ReviewParts = { "record", "checks", "findings", "flags" };

    public async Task<StewardReply> ReviewAsync(string projectKey, int ticketId, CancellationToken ct = default) =>
        await ReviewAsync(projectKey, ticketId, null, ct);

    public async Task<StewardReply> ReviewAsync(string projectKey, int ticketId, string? part, CancellationToken ct = default)
    {
        part = part?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(part) && !ReviewParts.Contains(part))
            return Refuse("bad-request", $"A review's parts are {string.Join(", ", ReviewParts)}.");
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;

        var change = string.IsNullOrWhiteSpace(t.Branch) ? null : await _git.BranchChangeAsync(projectKey, t.Branch, ct);
        var lastBackward = await _tickets.LastBackwardAtAsync(t.Id);
        var said = t.Comments.Where(c => c.Author != AuthorKind.System).OrderBy(c => c.CreatedAt).ToList();

        var notes = await OpenNotesAsync(projectKey, t.Id, ct);
        var qa = t.QaItems.OrderBy(q => q.Order).ToList();
        var findings = t.GateFindings.OrderBy(f => f.Order).ToList();

        // Every answer opens with this: which ticket, and what an earlier run meant to do about it -
        // first, because it is often why this review matters. Close each with steward_done_with.
        var reply = new Dictionary<string, object?>
        {
            ["id"] = t.Id,
            ["phase"] = t.Phase.ToString(),
            ["title"] = t.Title,
            ["yourNotes"] = notes.Select(n => new { n.Id, n.Text, n.At }),
        };
        var parts = new Dictionary<string, Dictionary<string, object?>>
        {
            ["record"] = new()
            {
                ["description"] = t.Description,
                ["summary"] = t.ImplementationSummary,
                ["workItems"] = t.WorkItems.OrderBy(w => w.Order).Select(w => new { w.Done, scope = w.Scope.ToString(), w.Text }),
                // A worker never edits the owner's own documents: the line it would have written is in
                // its comment under this heading, wherever in the record that comment now sits.
                ["forTheOwnersDocs"] = said.Where(c => Regex.IsMatch(c.Body, @"for the owner['’]s docs", RegexOptions.IgnoreCase))
                    .Select(c => new { by = c.Author.ToString(), @in = c.Phase?.ToString(), c.Body }),
                // The last things the phases said, newest last: the refinement hand-off is where open questions sit.
                ["comments"] = said.TakeLast(4).Select(c => new { by = c.Author.ToString(), @in = c.Phase?.ToString(), c.Body }),
            },
            ["checks"] = new()
            {
                ["codeReviewOnly"] = t.SkipQaTesting,
                ["qaShots"] = t.QaShots.Count,
                ["qaItems"] = qa.Select(q => new
                {
                    status = q.Status.ToString(),
                    verified = HandoffRequirements.IsVerified(q, lastBackward),
                    byInspection = q.PassedByInspection,
                    q.Text,
                    q.Note,
                }),
            },
            ["findings"] = new()
            {
                ["independentReview"] = findings.Select(f => new { f.Title, ruling = f.Ruling?.ToString(), why = f.RulingNote }),
            },
            ["flags"] = new()
            {
                ["sentBackBefore"] = await SendBacksAsync(t.Id),
                ["changed"] = change is null ? "this ticket has no branch" : change,
                ["flags"] = change is null ? new List<StewardFlag>() : await FlagsAsync(cfg!, t, change, ct),
                // Owner flags the owner has cleared for this change as it stands: merge does not stop on these.
                ["clearedByOwner"] = change is null ? new List<string>() : (await ClearedAsync(projectKey, t.Id, change, ct)).ToList(),
            },
        };

        if (!string.IsNullOrEmpty(part))
        {
            foreach (var kv in parts[part]) reply[kv.Key] = kv.Value;
            return new StewardReply("review", null, reply);
        }

        var whole = new Dictionary<string, object?>(reply);
        foreach (var p in parts.Values) foreach (var kv in p) whole[kv.Key] = kv.Value;
        if (System.Text.Json.JsonSerializer.Serialize(whole).Length <= ReviewMaxChars)
            return new StewardReply("review", null, whole);

        // Too long to send whole. The flags are what a merge stops on, so they always come; the rest
        // is counted, so the steward knows what it is asking for.
        foreach (var kv in parts["flags"]) reply[kv.Key] = kv.Value;
        reply["checksAtAGlance"] = new
        {
            items = qa.Count,
            passedAndCurrent = qa.Count(q => q.Status == QaStatus.Passed && HandoffRequirements.IsVerified(q, lastBackward)),
            byInspection = qa.Count(q => q.PassedByInspection),
        };
        reply["findingsAtAGlance"] = new { findings = findings.Count, unruled = findings.Count(f => f.Ruling is null) };
        reply["parts"] = ReviewParts.ToDictionary(p => p, p => System.Text.Json.JsonSerializer.Serialize(parts[p]).Length);
        return new StewardReply("review",
            "Too long to send whole, so this is its head and its flags. Ask again with part = record, checks or findings for the "
            + "rest; 'parts' gives each one's size in characters.", reply);
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
            return Refuse("not-ready", $"Ticket {ticketId} is in {t.Phase}. Only a ticket at NeedsReview is merged.{await LeftReviewAsync(t)}");

        var change = string.IsNullOrWhiteSpace(t.Branch) ? null : await _git.BranchChangeAsync(projectKey, t.Branch, ct);
        if (change is null || change.Ahead == 0)
            return Refuse("nothing-to-merge", $"Ticket {ticketId} has no commits that the base lacks: there is nothing to merge. "
                + "If its review holds because what it asked for is already true on the base, close it with steward_close_unchanged, "
                + "saying why, then set its outcomes and note anything for release as after a merge. If the work should be "
                + "there and is not, send it back to NeedsImplementation saying so.");

        var flags = await FlagsAsync(cfg, t, change, ct);
        var cleared = await ClearedAsync(projectKey, t.Id, change, ct);
        if (flags.Where(f => f.Owner && !cleared.Contains(f.Flag)).ToList() is { Count: > 0 } owners)
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
                                 : $"'{c.Name}' fails on the merged result. Send it back to NeedsImplementation with this: "
                                   + "'failing' is every line in the whole output that names a failure, 'output' is how it ended.",
                    new { check = c.Name, failing = run.Failures ?? Array.Empty<string>(), output = run.Output });
            }
        }

        if (cfg.StewardMode == StewardMode.Propose)
            return Proposed($"merge ticket {ticketId} into the base ({change.Stat}) and close it");

        // The checks can outlast the call that started them (see StewardMerges), and so can outlast
        // the run. A change needs a run: one that has ended since gets no merge.
        if (!await RunOpenAsync(projectKey))
            return Refuse("no-run", $"The run ended while ticket {ticketId} was being checked, so it was not merged. Begin a run and merge it again.");

        // The merge commit is named for the ticket, as it is when a person merges one from the board.
        var merged = await _git.MergeAsync(projectKey, t.Branch!, $"{t.Title} (#{t.Id})", null, ct);
        if (!merged.Ok)
            return merged.Conflicted
                ? Refuse("conflict", "The merge conflicts and was rolled back. Send it back to NeedsImplementation naming these files.", merged.ConflictedFiles)
                : Refuse("refused", merged.Error ?? "The merge was refused.");

        var sha = merged.MergeSha?[..Math.Min(7, merged.MergeSha.Length)];
        await _tickets.SetMergedAsync(ticketId, merged.MergeSha);
        await _tickets.TransitionAsync(ticketId, TicketPhase.Done, $"Merged into {merged.BaseBranch} at {sha}", TransitionActor.Steward);
        return new StewardReply("merged", null, new { commit = sha, closed = true });
    }

    /// <summary>
    /// Why a ticket is no longer at NeedsReview, from the transition that took it away - so a merge
    /// or a send-back refused as not-ready says where it went and who moved it, not only that it moved.
    /// Empty when it never was at review.
    /// </summary>
    private async Task<string> LeftReviewAsync(Ticket t)
    {
        var left = await _db.PhaseTransitions.AsNoTracking()
            .Where(p => p.TicketId == t.Id && p.From == TicketPhase.NeedsReview)
            .OrderByDescending(p => p.At).ThenByDescending(p => p.Id).FirstOrDefaultAsync();
        if (left is null) return "";
        var mins = (int)(DateTime.UtcNow - left.At).TotalMinutes;
        var when = mins < 2 ? "just now" : mins < 120 ? $"{mins} minutes ago" : $"{mins / 60} hours ago";
        var why = string.IsNullOrWhiteSpace(left.Reason) ? "" : $": {Head(left.Reason.Trim(), 300)}";
        return $" It left NeedsReview for {left.To} {when}, moved by {left.Actor.ToString().ToLowerInvariant()}{why}."
            + " Nothing for you to do about it now: it comes back to review by itself.";
    }

    /// <summary>
    /// Close a ticket at review that has nothing to merge, because what it asked for is already true
    /// on the base. Only in full mode, which is the mode that closes tickets; only at NeedsReview;
    /// only when the branch really has no commit the base lacks. The reason goes on the record.
    /// </summary>
    public async Task<StewardReply> CloseUnchangedAsync(string projectKey, int ticketId, string why, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        if (cfg.StewardMode == StewardMode.Manage)
            return Refuse("not-in-this-mode", "Closing a ticket is the owner's in this mode. Ask them, with the ticket's id.");
        why = why?.Trim() ?? "";
        if (why.Length == 0) return Refuse("bad-request", "Say why there is nothing to merge: what on the base already does what it asked.");
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (t.Track != TicketTrack.Delivery) return Refuse("not-ready", "Only a delivery ticket is closed this way.");
        if (t.Phase != TicketPhase.NeedsReview)
            return Refuse("not-ready", $"Ticket {ticketId} is in {t.Phase}. Only a ticket at NeedsReview is closed.{await LeftReviewAsync(t)}");
        var change = string.IsNullOrWhiteSpace(t.Branch) ? null : await _git.BranchChangeAsync(projectKey, t.Branch, ct);
        if (change is not null && change.Ahead > 0)
            return Refuse("has-commits", $"Ticket {ticketId} has {change.Ahead} commit(s) the base lacks. It is merged, not closed.");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"close ticket {ticketId} unchanged: {Head(why, 200)}");

        _db.StewardEntries.Add(new StewardEntry
        {
            ProjectKey = projectKey, Kind = StewardEntryKind.Call, TicketId = ticketId,
            Text = $"Closed with nothing to merge. {why}",
        });
        await _db.SaveChangesAsync(ct);
        await _tickets.TransitionAsync(ticketId, TicketPhase.Done, $"Closed with nothing to merge: {Head(why, 300)}", TransitionActor.Steward);
        return new StewardReply("closed", null, new { closed = true });
    }

    /// <summary>
    /// What a ticket is right now, as far as a merge's answer depends on it: its phase, its branch's
    /// tip, and how far it is from the base either way. See <see cref="StewardMerges"/>.
    /// </summary>
    public async Task<string?> MergeStampAsync(string projectKey, int ticketId, CancellationToken ct = default)
    {
        var t = await _db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId && x.ProjectKey == projectKey, ct);
        if (t is null) return null;
        var change = string.IsNullOrWhiteSpace(t.Branch) ? null : await _git.BranchChangeAsync(projectKey, t.Branch, ct);
        return $"{t.Phase}|{change?.Tip}|{change?.Behind}|{change?.Ahead}";
    }

    public async Task<StewardReply> RebaseAsync(string projectKey, int ticketId, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (string.IsNullOrWhiteSpace(t.Branch)) return Refuse("no-branch", $"Ticket {ticketId} has no branch.");
        if (t.Phase != TicketPhase.NeedsReview)
            return Refuse("not-ready", $"Ticket {ticketId} is in {t.Phase}. A branch is only rebased from here while it waits at NeedsReview: at any other time a worker may be in it.{await LeftReviewAsync(t)}");
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
            return Refuse("not-ready", $"Ticket {ticketId} is in {t.Phase}. Only a ticket at NeedsReview is sent back from here.{await LeftReviewAsync(t)}");
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
        t.PlanUnit is null ? null
            : $"**Plan**\n\nUnit {t.PlanUnit}{(string.IsNullOrWhiteSpace(t.PlanOutcomes) ? "" : $": {t.PlanOutcomes}")}.";

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
        if (WalkthroughOf(cfg.Steward.Plan, unit) is { } walk && outcomes.Contains(walk))
            return Refuse("board", $"{walk} is the walk-through, and the board files it once every other outcome of the unit is done.");
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
        row.PlanKey = cfg.Steward.Plan.Key;
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
        if (outcome == WalkthroughOf(cfg.Steward.Plan, u) && st == StewardOutcomeState.Done)
            return Refuse("board", $"{outcome} is the walk-through: the board calls it done when its ticket passes QA, and nothing else does.");
        if (note?.Length > 400)
            return Refuse("bad-request", "The note is a line for the next run: what is left to file and what it waits on. 400 characters at most. What a merged ticket built is in the code.");
        if (st == StewardOutcomeState.Done && moreToFile)
            return Refuse("bad-request", "An outcome with a ticket still to be written is not done.");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"mark {unit} {outcome} as {st}");

        var key = cfg.Steward.Plan.Key;
        var row = await _db.StewardOutcomes.FirstOrDefaultAsync(
            o => o.ProjectKey == projectKey && o.Plan == key && o.Unit == unit && o.Outcome == outcome, ct);
        if (row is null) _db.StewardOutcomes.Add(row = new StewardOutcome { ProjectKey = projectKey, Plan = key, Unit = unit, Outcome = outcome });
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
            return Refuse("exists", $"Unit {number} is already {(there.Idea ? "an idea" : "drafted")} as {there.File}. {(there.Idea ? "Expand" : "Revise")} that file, under its own name.");
        // A plan of ideas is drafted only from its ideas: the steward writes none, it expands the
        // next one in place. A plan from before ideas has none, and drafts as it always has.
        if (there is null && plan.Any(u => u.Idea))
            return Refuse("no-idea", $"There is no idea numbered {number} to expand. Ideas are the owner's: propose one with steward_ask_owner (idea).");

        var unit = TicketGitService.ReadUnit(p, number, path, text ?? "");
        if (unit.Agreed || !unit.Draft)
            return Refuse("bad-request", "Its status line must say it is a draft. Only the owner agrees a unit.");
        if (unit.Outcomes.Count == 0) return Refuse("bad-request", "The draft has no outcomes written the way the plan's other units write them.");
        // The story is the promise: every step cites the outcomes that make it true, and a story
        // too long to read is a slice too big to build (docs/plans-as-slices.md, section 4).
        var story = SliceStory.Check(text ?? "", unit.Outcomes);
        if (story.Errors.Count > 0)
            return Refuse("story", string.Join(" ", story.Errors), new { words = story.Words, notes = story.Notes });
        // A decision keeps its number when the owner settles it, and numbers are the project's,
        // once: one the register already holds would be two decisions under one name.
        var asks = UnitDecisions.Open(text ?? "").Select(d => d.Number).ToList();
        if (asks.Count > 0)
        {
            await EnsureRegisterAsync(cfg, ct);
            if (await _db.StewardDecisions.Where(d => d.ProjectKey == projectKey && asks.Contains(d.Number))
                    .Select(d => (int?)d.Number).FirstOrDefaultAsync(ct) is { } taken)
                return Refuse("number-taken", $"#{taken} is already decision #{taken} in the register. Number what the draft leaves open from steward_decisions' 'next'.");
        }
        if (cfg.StewardMode == StewardMode.Propose)
            return Proposed($"{(there is null ? "write" : "revise")} the draft spec {path} ({unit.Outcomes.Count} outcomes)");

        var (sha, unchanged, refusal) = await _git.CommitFileAsync(projectKey, path, text!,
            there is null ? $"A draft spec for unit {number}" : $"The draft spec for unit {number}, revised", ct);
        if (refusal is not null) return Refuse("refused", refusal);
        if (!unchanged && (there is null || there.Idea)) await WriteReadableAsync(cfg, ct);
        return unchanged ? new StewardReply("unchanged")
            : new StewardReply(there is null || there.Idea ? "drafted" : "revised", story.Notes.Count > 0 ? string.Join(" ", story.Notes) : null,
                new { file = path, outcomes = unit.Outcomes.Count, commit = sha, storyWords = story.Words, notCited = story.Uncited });
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
        // A question about a unit's open decisions is answered on those decisions themselves, so it
        // needs no options of its own - and has to name decisions the spec really leaves open, or
        // the owner is handed an empty sheet.
        var asks = (a.Decisions ?? Array.Empty<int>()).Distinct().ToList();
        var story = a.Story;
        if (asks.Count > 0 && a.Unit is null) return Refuse("bad-request", "Name the unit whose open decisions these are.");
        if (story is not null && a.Unit is null) return Refuse("bad-request", "A story is a slice's: name the slice (unit) it belongs to.");
        if (a.Unit is not null && asks.Count == 0 && story is null)
            return Refuse("bad-request", "Name a unit with its open decisions, or with the sentence of its story an answer would change - or neither.");
        if (a.Idea is not null && (a.Unit is not null || asks.Count > 0))
            return Refuse("bad-request", "A proposed idea is its own question: ask it without a unit or decisions.");
        if (a.Unit is { } unitNo)
        {
            var spec = (await _git.PlanAsync(projectKey, ct)).FirstOrDefault(u => u.Number == unitNo);
            if (spec is null) return Refuse("bad-request", $"The plan has no unit numbered {unitNo}.");
            var open = UnitDecisions.Open(spec.Text).Select(d => d.Number).ToHashSet();
            if (asks.FirstOrDefault(n => !open.Contains(n)) is var missing and > 0)
                return Refuse("bad-request", $"#{missing} is not an open decision in {spec.File}. Only a decision written under \"To decide\" in the template's shape can be asked this way.");
            if (story is not null)
            {
                // Only a slice being built has a story that changes with the owner's answers: a
                // draft is the steward's to revise, and a built slice's story is never rewritten.
                if (!spec.Agreed) return Refuse("bad-request", $"{spec.File} is not agreed: revise the draft's story with steward_draft_unit instead.");
                if ((await BuiltUnitsAsync(cfg, ct)).Contains(unitNo))
                    return Refuse("bad-request", $"Slice {unitNo} is built: its story is history and is never rewritten. A later slice says what it changes.");
                if (string.IsNullOrWhiteSpace(story.Sentence) || !SliceStory.Has(spec.Text, story.Sentence))
                    return Refuse("bad-request", $"Quote the sentence exactly as {spec.File}'s story has it, once.");
                if ((story.Rewordings ?? new Dictionary<string, string>()).Count == 0)
                    return Refuse("bad-request", "Give the sentence's rewording under each option that changes it.");
                foreach (var label in story.Rewordings!.Keys)
                    if (options.All(o => !string.Equals(o.Label.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase)))
                        return Refuse("bad-request", $"\"{label}\" is not one of the options.");
            }
        }
        if (a.Idea is not null)
        {
            if (!cfg.Steward.Plan.Configured) return Refuse("no-plan", "This project has no plan folder for an idea.");
            if (IdeaRefusal(cfg.Steward.Plan, await _git.PlanAsync(projectKey, ct), a.Idea) is { } no) return Refuse("bad-request", no);
            // The owner says yes or no to the idea as written: the board's two answers, not the steward's.
            options = new List<BlockOption> { new(CommitIdea, $"Commit {a.Idea.File.Trim()} as written", true), new(LeaveIdeas) };
        }
        if (asks.Count == 0 || options.Count > 0)
        {
            if (options.Count < 2) return Refuse("bad-request", "Give at least two options, each with what it costs or rules out.");
            if (options.Count(o => o.Recommended) != 1)
                return Refuse("bad-request", "Mark exactly one option as the one you would pick.");
        }

        // What it touches is the steward's claim, made where the owner can see it: an override is
        // declared when it is asked about, never found afterwards.
        if (a.Touches is null)
            return Refuse("bad-request", "Say which decisions this bears on (touches), by number, from steward_decisions - an empty list when none does.");
        var touches = a.Touches.Distinct().ToList();
        if (touches.Count > 0)
        {
            await EnsureRegisterAsync(cfg, ct);
            var all = await RegisterAsync(projectKey, track: false, ct);
            var map = ReplacedIndex(all);
            foreach (var n in touches)
            {
                var d = all.FirstOrDefault(x => x.Number == n);
                if (d is null) return Refuse("bad-request", $"There is no decision #{n}.");
                var st = StatusOf(d, map);
                if (st.Name == Withdrawn) return Refuse("bad-request", $"#{n} is withdrawn: there is nothing in force to touch.");
                if (st.Name == Replaced)
                    return Refuse("bad-request", $"#{n} no longer holds; touch what replaced it ({string.Join(", ", st.ReplacedBy.Select(r => $"#{r.By}"))}).");
            }
        }
        var changes = new Dictionary<string, List<int>>();
        foreach (var (label, nums) in a.Changes ?? new Dictionary<string, IReadOnlyList<int>>())
        {
            if (options.All(o => !string.Equals(o.Label.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase)))
                return Refuse("bad-request", $"\"{label}\" is not one of the options.");
            if (nums.FirstOrDefault(n => !touches.Contains(n)) is var stray and > 0)
                return Refuse("bad-request", $"An option can only change a decision the question touches; #{stray} is not in touches.");
            if (nums.Count > 0) changes[label.Trim()] = nums.Distinct().ToList();
        }

        var (citing, notWaiting) = await FollowUpsToCiteAsync(projectKey, a.FollowUps, ct);
        if (notWaiting is not null) return notWaiting;

        if (await _db.StewardQuestions.AnyAsync(q => q.ProjectKey == projectKey && q.Key == key, ct))
            return Refuse("already-asked", $"\"{key}\" has been asked already. A question is asked once.");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"ask the owner: {a.Question.Trim()}");
        Cite(citing, $"question:{key}");

        _db.StewardQuestions.Add(new StewardQuestion
        {
            ProjectKey = projectKey,
            Key = key,
            Question = a.Question.Trim(),
            Why = a.Why.Trim(),
            OptionsJson = JsonSerializer.Serialize(options, Json),
            HeldUp = a.HeldUp.Trim(),
            TicketId = a.TicketId,
            PlanUnit = a.Unit,
            PlanKey = a.Unit is null ? null : cfg.Steward.Plan.Key,
            Decisions = asks.Count == 0 ? null : string.Join(",", asks),
            Touches = string.Join(",", touches),
            OptionChangesJson = changes.Count == 0 ? null : JsonSerializer.Serialize(changes, Json),
            ProposedFile = a.Idea?.File.Trim(),
            ProposedText = a.Idea?.Text,
            StorySentence = story?.Sentence.Trim(),
            StoryRewordingsJson = story is null ? null : JsonSerializer.Serialize(story.Rewordings, Json),
        });
        await _db.SaveChangesAsync(ct);

        var pick = options.FirstOrDefault(o => o.Recommended)?.Label
            ?? $"the spec's picks for {string.Join(", ", asks.Select(n => $"#{n}"))}";
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
    public async Task<StewardReply> RecordAsync(string projectKey, StewardEntryKind kind, int ticketId, string text,
        string? release = null, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        if (kind == StewardEntryKind.OwnerSaid) return Refuse("refused", "What the owner said is the owner's to write.");
        if (string.IsNullOrWhiteSpace(text)) return Refuse("bad-request", "Say what was decided, and the place that decided it.");
        // A note for a release says what kind of thing it is, because that is how it is read back:
        // hundreds of them in the order tickets merged is a list nobody reads before a release.
        StewardReleaseKind? releaseKind = null;
        if (kind == StewardEntryKind.BeforeRelease)
        {
            if (ParseRelease(release) is not { } rk)
                return Refuse("bad-request", "Say what kind of note it is: migration, config, action or note. One note, one kind - "
                    + "a ticket that leaves a migration and wording to check gets two notes.");
            releaseKind = rk;
        }
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"note for ticket {ticketId}: {Head(text, 200)}");

        _db.StewardEntries.Add(new StewardEntry { ProjectKey = projectKey, Kind = kind, TicketId = ticketId, Text = text.Trim(), Release = releaseKind });
        await _db.SaveChangesAsync(ct);
        return new StewardReply("noted");
    }

    /// <summary>The longest a ticket note may be: one thing to do, not an essay.</summary>
    public const int TicketNoteMaxChars = 600;

    /// <summary>
    /// Write down something to do about one ticket, for later runs: it is shown by
    /// <see cref="ReviewAsync"/> and in every run's status until <see cref="DoneWithAsync"/> closes it.
    /// </summary>
    public async Task<StewardReply> NoteTicketAsync(string projectKey, int ticketId, string text, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        text = text?.Trim() ?? "";
        if (text.Length == 0) return Refuse("bad-request", "Say what is to be done, and when.");
        if (text.Length > TicketNoteMaxChars)
            return Refuse("bad-request", $"A ticket note is one thing to do, at most {TicketNoteMaxChars} characters. "
                + "A fact about the repo is not a ticket note; a call for the owner goes on the record.");
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"note on ticket {ticketId}: {Head(text, 200)}");

        var e = new StewardEntry { ProjectKey = projectKey, Kind = StewardEntryKind.TicketNote, TicketId = ticketId, Text = text };
        _db.StewardEntries.Add(e);
        await _db.SaveChangesAsync(ct);
        return new StewardReply("noted", null, new { id = e.Id });
    }

    /// <summary>Close a ticket note: what it said is done, or no longer to be done.</summary>
    public async Task<StewardReply> DoneWithAsync(string projectKey, int noteId, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        var e = await _db.StewardEntries.FirstOrDefaultAsync(
            x => x.Id == noteId && x.ProjectKey == projectKey && x.Kind == StewardEntryKind.TicketNote, ct);
        if (e is null) return Refuse("not-found", $"There is no ticket note {noteId} here.");
        if (e.HandledAt is not null) return new StewardReply("already-done");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"close note {noteId} on ticket {e.TicketId}");
        e.HandledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new StewardReply("done");
    }

    /// <summary>The longest a ruling, or what it rests on, may be.</summary>
    public const int RulingMaxChars = 600;

    /// <summary>
    /// Record a standing call: something the steward has taken as settled and will apply again,
    /// with the decision or document it rests on. It applies at once, is handed to every run, and
    /// is the owner's to keep or reverse.
    /// </summary>
    public async Task<StewardReply> RuleAsync(string projectKey, string text, string basis, int? ticketId = null, CancellationToken ct = default) =>
        await RuleAsync(projectKey, text, basis, ticketId, null, ct);

    /// <summary>
    /// As above, with the register's decisions the ruling reads. When one of them is replaced or
    /// withdrawn the ruling is flagged on the record and in the next briefing.
    /// </summary>
    public async Task<StewardReply> RuleAsync(string projectKey, string text, string basis, int? ticketId, IReadOnlyList<int>? reads,
        CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        text = text?.Trim() ?? "";
        basis = basis?.Trim() ?? "";
        if (text.Length == 0) return Refuse("bad-request", "Say what you will take as settled from now on.");
        if (basis.Length == 0)
            return Refuse("bad-request", "Say what it rests on: the spec and section, the plan's decision, or the owner's answer by its key. "
                + "A ruling that rests on nothing the owner decided is a question for the owner.");
        if (text.Length > RulingMaxChars || basis.Length > RulingMaxChars)
            return Refuse("bad-request", $"A ruling and what it rests on are each at most {RulingMaxChars} characters: one call, not a policy.");
        if (ticketId is { } tid && (await TicketOfAsync(projectKey, tid)).Item1 is null)
            return Refuse("not-found", $"There is no ticket {tid} here.");
        var live = await _db.StewardEntries.AsNoTracking().Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.Ruling
            && e.ReversedAt == null).ToListAsync(ct);
        if (live.FirstOrDefault(e => string.Equals(e.Text.Trim(), text, StringComparison.OrdinalIgnoreCase)) is { } had)
            return new StewardReply("already-ruled", "That ruling is already in force.", new { id = had.Id });
        var read = (reads ?? Array.Empty<int>()).Distinct().ToList();
        if (read.Count > 0)
        {
            await EnsureRegisterAsync(cfg, ct);
            var all = await RegisterAsync(projectKey, track: false, ct);
            var map = ReplacedIndex(all);
            foreach (var n in read)
            {
                var d = all.FirstOrDefault(x => x.Number == n);
                if (d is null) return Refuse("bad-request", $"There is no decision #{n}.");
                if (StatusOf(d, map).Name is Replaced or Withdrawn) return Refuse("bad-request", $"#{n} no longer holds: a ruling cannot read it.");
            }
        }
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"rule: {Head(text, 200)}");

        var r = new StewardEntry
        {
            ProjectKey = projectKey, Kind = StewardEntryKind.Ruling, TicketId = ticketId, Text = text, Basis = basis,
            Reads = reads is null ? null : string.Join(",", read),
        };
        _db.StewardEntries.Add(r);
        await _db.SaveChangesAsync(ct);
        return new StewardReply("ruled", null, new { id = r.Id });
    }

    /// <summary>The owner has read a ruling and lets it stand. Not a reason to run. No tool reaches this.</summary>
    public async Task<StewardReply> OwnerKeepsRulingAsync(string projectKey, int id, CancellationToken ct = default)
    {
        var e = await RulingAsync(projectKey, id, ct);
        if (e is null) return Refuse("not-found", $"There is no ruling {id} here.");
        if (e.ReversedAt is not null) return Refuse("bad-request", "That ruling was reversed.");
        e.HandledAt ??= DateTime.UtcNow;
        // Kept while it reads a decision that no longer holds: it stands on what replaced it now,
        // and a withdrawn one it read drops out of what it reads.
        if (e.Reads is not null && Config(projectKey) is { } cfg)
        {
            var all = await RegisterAsync(projectKey, track: false, ct);
            var map = ReplacedIndex(all);
            var now = new List<int>();
            foreach (var n in Numbers(e.Reads))
            {
                var d = all.FirstOrDefault(x => x.Number == n);
                var st = d is null ? null : StatusOf(d, map);
                if (st?.Name == Replaced) now.AddRange(st.ReplacedBy.Select(r => r.By));
                else if (st?.Name != Withdrawn) now.Add(n);
            }
            e.Reads = string.Join(",", now.Distinct());
        }
        await _db.SaveChangesAsync(ct);
        return new StewardReply("kept");
    }

    /// <summary>
    /// The owner reverses a ruling, saying what should hold instead. It stops being handed to runs,
    /// and the reversal reaches the next run as a decision made. No tool reaches this.
    /// </summary>
    public async Task<StewardReply> OwnerReversesRulingAsync(string projectKey, int id, string because, CancellationToken ct = default)
    {
        because = because?.Trim() ?? "";
        if (because.Length == 0) return Refuse("bad-request", "Say what should hold instead: the steward reads it as a decision made.");
        var e = await RulingAsync(projectKey, id, ct);
        if (e is null) return Refuse("not-found", $"There is no ruling {id} here.");
        if (e.ReversedAt is not null) return Refuse("bad-request", "That ruling is already reversed.");
        e.ReversedAt = DateTime.UtcNow;
        e.ReversedBecause = because;
        e.HandledAt ??= e.ReversedAt;
        e.DeliveredAt = null;
        if (Config(projectKey)?.StewardMode is { } m && m != StewardMode.Off)
            await StewardDue.MarkAsync(_db, projectKey, $"the owner reversed ruling {id}");
        await _db.SaveChangesAsync(ct);
        _heartbeat?.Wake();
        return new StewardReply("reversed");
    }

    private Task<StewardEntry?> RulingAsync(string projectKey, int id, CancellationToken ct) =>
        _db.StewardEntries.FirstOrDefaultAsync(x => x.Id == id && x.ProjectKey == projectKey && x.Kind == StewardEntryKind.Ruling, ct);

    /// <summary>The longest a learned fact may be.</summary>
    public const int LearnedMaxChars = 1200;

    /// <summary>
    /// Queue a fact about the repo for the project's notes store. Saved at once; placed by the
    /// launcher after the run, beside the notes already there.
    /// </summary>
    public async Task<StewardReply> LearnedAsync(string projectKey, string fact, string? key = null, CancellationToken ct = default)
    {
        var (cfg, off) = await WritableAsync(projectKey);
        if (cfg is null) return off!;
        fact = System.Text.RegularExpressions.Regex.Replace(fact ?? "", @"\s+", " ").Trim();
        if (fact.Length == 0) return Refuse("bad-request", "Say the fact.");
        if (fact.Length > LearnedMaxChars)
            return Refuse("bad-request", $"A fact is at most {LearnedMaxChars} characters. Two facts are two calls.");
        key = NoteKey(key);
        if ((await UnplacedAsync(projectKey, ct)).Any(e => string.Equals(e.Text, fact, StringComparison.OrdinalIgnoreCase)))
            return new StewardReply("already-queued");
        if (cfg.StewardMode == StewardMode.Propose) return Proposed($"learn: {Head(fact, 200)}");

        _db.StewardEntries.Add(new StewardEntry { ProjectKey = projectKey, Kind = StewardEntryKind.Learned, Text = fact, Placed = key });
        await _db.SaveChangesAsync(ct);
        return new StewardReply("queued", cfg.ResolvedMemoryDir.Length == 0
            ? "This project keeps no notes store, so it waits on the board for its owner."
            : "It goes into the notes store after this run.");
    }

    /// <summary>A note key as the store spells them: lowercase words and hyphens, at most 60 characters. Null when there is none.</summary>
    public static string? NoteKey(string? key)
    {
        var k = System.Text.RegularExpressions.Regex.Replace((key ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (k.Length > 60) k = k[..60].TrimEnd('-');
        return k.Length == 0 ? null : k;
    }

    /// <summary>The facts not yet placed in the notes store, oldest first.</summary>
    public Task<List<StewardEntry>> UnplacedAsync(string projectKey, CancellationToken ct = default) =>
        _db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.Learned && e.HandledAt == null)
            .OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(ct);

    /// <summary>
    /// The launcher placed a learned fact: say where it went. Called by <c>steward.ps1</c> after a
    /// run, never by the steward. A fact placed already keeps its first answer.
    /// </summary>
    public async Task<StewardReply> PlacedAsync(string projectKey, int id, string where, CancellationToken ct = default)
    {
        var e = await _db.StewardEntries.FirstOrDefaultAsync(
            x => x.Id == id && x.ProjectKey == projectKey && x.Kind == StewardEntryKind.Learned, ct);
        if (e is null) return Refuse("not-found", $"There is no learned fact {id} here.");
        if (e.HandledAt is not null) return new StewardReply("already-placed");
        e.HandledAt = DateTime.UtcNow;
        e.Placed = string.IsNullOrWhiteSpace(where) ? "placed" : where.Trim();
        await _db.SaveChangesAsync(ct);
        return new StewardReply("placed");
    }

    /// <summary>The open ticket notes, oldest first; one ticket's with <paramref name="ticketId"/>.</summary>
    private async Task<List<StewardEntry>> OpenNotesAsync(string projectKey, int? ticketId, CancellationToken ct)
    {
        var q = _db.StewardEntries.AsNoTracking().Where(e => e.ProjectKey == projectKey
            && e.Kind == StewardEntryKind.TicketNote && e.HandledAt == null);
        if (ticketId is { } id) q = q.Where(e => e.TicketId == id);
        return await q.OrderBy(e => e.At).ThenBy(e => e.Id).ToListAsync(ct);
    }

    /// <summary>A release note's kind by name. Null for anything that is not one: a number is not a kind.</summary>
    public static StewardReleaseKind? ParseRelease(string? name)
    {
        var n = name?.Trim() ?? "";
        foreach (var k in Enum.GetValues<StewardReleaseKind>())
            if (string.Equals(k.ToString(), n, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    // ---- the owner's side: no steward verb reaches these --------------------------------

    /// <summary>The owner's answer to one waiting question. A decision made, and a reason to run.</summary>
    public Task<StewardReply> OwnerAnswersAsync(string projectKey, string key, string answer, CancellationToken ct = default) =>
        OwnerAnswersAsync(projectKey, key, answer, null, ct);

    /// <summary>
    /// The owner's answer, and which of the decisions the question touched it replaces. Null
    /// <paramref name="replaces"/> takes what the picked option said it would change; an empty list
    /// replaces nothing. Only a decision the question touched can be replaced this way: the override
    /// is the one the owner was shown.
    /// </summary>
    public async Task<StewardReply> OwnerAnswersAsync(string projectKey, string key, string answer, IReadOnlyList<int>? replaces,
        CancellationToken ct = default) => await OwnerAnswersAsync(projectKey, key, answer, replaces, null, ct);

    /// <summary>
    /// As above; for a question about a slice being built, <paramref name="story"/> is the owner's
    /// own wording of the sentence the answer changes (null takes the picked option's).
    /// </summary>
    public async Task<StewardReply> OwnerAnswersAsync(string projectKey, string key, string answer, IReadOnlyList<int>? replaces,
        string? story, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(answer)) return Refuse("bad-request", "The answer is empty.");
        var q = await _db.StewardQuestions.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.Key == key, ct);
        if (q is null) return Refuse("not-waiting", $"No question named \"{key}\".");
        if (q.Answer is not null) return Refuse("not-waiting", $"\"{key}\" was answered on {q.AnsweredAt:yyyy-MM-dd}.");
        var touched = Numbers(q.Touches);
        var picked = replaces?.Distinct().ToList() ?? ChangesOf(q, answer);
        if (picked.FirstOrDefault(n => !touched.Contains(n)) is var stray and > 0)
            return Refuse("bad-request", $"\"{key}\" does not touch #{stray}, so its answer cannot replace it. Record a change to #{stray} on the register instead.");
        var cfg = Config(projectKey);
        var text = Regex.Replace(answer.Trim(), @"\s*\n\s*", " ");

        // A proposed idea is committed on a yes, and is no decision: ideas are the owner's text.
        var idea = q.ProposedFile is not null;
        string? ideaDone = null;
        if (idea && text.StartsWith(CommitIdea, StringComparison.OrdinalIgnoreCase))
        {
            if (cfg is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
            if (await CommitIdeaAsync(cfg, q, ct) is { } no) return Refuse("refused", no);
            ideaDone = $"{q.ProposedFile} committed";
        }

        q.Answer = text;
        q.AnsweredAt = DateTime.UtcNow;
        StewardDecision? made = null;
        if (!idea)
        {
            // The answer is a decision, and the register is where its status lives from now on.
            if (cfg is not null) await EnsureRegisterAsync(cfg, ct);
            made = await AnswerDecisionAsync(q, picked, ct);
        }

        // An answer about a slice being built rewords the sentence of its story it changes, and
        // writes itself under "Decided", in one commit: the story stays true, and only answers change it.
        string? storyDone = null;
        if (made is not null && cfg is not null && q.StorySentence is not null && q.PlanUnit is { } unit
            && (story ?? RewordingOf(q, text)) is { Length: > 0 } rewording)
        {
            var spec = (await _git.PlanAsync(projectKey, ct)).FirstOrDefault(u => u.Number == unit);
            if (spec is null || !spec.Agreed) return Refuse("refused", $"Slice {unit} is not an agreed slice now, so its story was not changed and nothing was recorded.");
            var reworded = SliceStory.Reword(spec.Text, q.StorySentence, rewording);
            if (reworded is null) return Refuse("refused", $"The sentence is no longer in {spec.File}'s story as it was asked about. Nothing was recorded; answer it without the story, or have it asked again.");
            reworded = UnitDecisions.AppendDecided(reworded,
                $"- **(#{made.Number}) {Head(q.Question, 160).TrimEnd('?', '.')}.** Decided by the owner {DateTime.Now:yyyy-MM-dd}: {text.TrimEnd('.')}.");
            var (sha, _, no) = await _git.CommitFileAsync(projectKey, spec.File, reworded,
                $"Slice {unit}: #{made.Number}, and the story sentence it changes, by its owner", ct);
            if (no is not null) return Refuse("refused", no);
            made.SourceRef = spec.File;
            storyDone = $"{spec.File} reworded at {sha}";
        }

        await StewardDue.MarkAsync(_db, projectKey, $"the owner answered {key}");
        await _db.SaveChangesAsync(ct);
        var exported = cfg is not null ? await ExportDecisionsAsync(cfg, ct) : null;
        if (ideaDone is not null && cfg is not null) await WriteReadableAsync(cfg, ct);
        _heartbeat?.Wake();
        return new StewardReply("recorded", string.Join("; ", new[] { exported, ideaDone, storyDone }.Where(x => x is not null)) is { Length: > 0 } w ? w : null);
    }

    /// <summary>The rewording of a story sentence under the option the answer starts with.</summary>
    private static string? RewordingOf(StewardQuestion q, string answer)
    {
        if (string.IsNullOrEmpty(q.StoryRewordingsJson)) return null;
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(q.StoryRewordingsJson, Json) ?? new();
        return map.Where(kv => answer.StartsWith(kv.Key.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
    }

    /// <summary>
    /// The flags on a ticket's change that are its owner's to decide, and which of them the owner
    /// has already cleared for the change as it stands.
    /// </summary>
    public async Task<StewardReply> OwnerFlagsAsync(string projectKey, int ticketId, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (Off(cfg, projectKey) is { } off) return off;
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        var change = string.IsNullOrWhiteSpace(t.Branch) ? null : await _git.BranchChangeAsync(projectKey, t.Branch, ct);
        if (change is null || change.Ahead == 0) return new StewardReply("flags", null, new { tip = (string?)null, flags = Array.Empty<object>() });

        var cleared = await ClearedAsync(projectKey, t.Id, change, ct);
        var flags = (await FlagsAsync(cfg!, t, change, ct)).Where(f => f.Owner).Select(f => new
        {
            f.Flag, f.Say, f.Detail,
            cleared = cleared.Contains(f.Flag),
            clearable = !NeverCleared.Contains(f.Flag) && !string.IsNullOrEmpty(change.ChangeId),
        });
        return new StewardReply("flags", null, new { tip = change.Tip, phase = t.Phase.ToString(), flags });
    }

    /// <summary>
    /// The owner clears one flag on a ticket's change: the steward may merge past it. Only a flag
    /// that is raised now and is the owner's, and only for the change as it stands - rework of the
    /// ticket lets it lapse, and the flag is asked about again.
    /// </summary>
    public async Task<StewardReply> OwnerClearsFlagAsync(string projectKey, int ticketId, string flag, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (cfg is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var (t, no) = await TicketOfAsync(projectKey, ticketId);
        if (t is null) return no!;
        if (NeverCleared.Contains(flag))
            return Refuse("refused", $"'{flag}' means nothing was checked, so there is nothing to clear. Fix what stopped the check, or merge it yourself.");
        var change = string.IsNullOrWhiteSpace(t.Branch) ? null : await _git.BranchChangeAsync(projectKey, t.Branch, ct);
        if (change is null || change.Ahead == 0) return Refuse("nothing-to-merge", $"Ticket {ticketId} has no commits that the base lacks.");
        if (string.IsNullOrEmpty(change.ChangeId)) return Refuse("no-change", $"Ticket {ticketId}'s change could not be read, so there is nothing to tie a clearance to.");
        var raised = (await FlagsAsync(cfg, t, change, ct)).FirstOrDefault(f => f.Owner && string.Equals(f.Flag, flag, StringComparison.OrdinalIgnoreCase));
        if (raised is null) return Refuse("not-raised", $"'{flag}' is not raised as the owner's on ticket {ticketId} now.");

        if (!await _db.StewardClearances.AnyAsync(c => c.ProjectKey == projectKey && c.TicketId == ticketId
                && c.Flag == raised.Flag && c.ChangeId == change.ChangeId, ct))
        {
            _db.StewardClearances.Add(new StewardClearance
            {
                ProjectKey = projectKey, TicketId = ticketId, Flag = raised.Flag, ChangeId = change.ChangeId, Sha = change.Tip,
            });
            // The record says so, where the owner reads back what was decided about this ticket.
            _db.StewardEntries.Add(new StewardEntry
            {
                ProjectKey = projectKey, Kind = StewardEntryKind.OwnerSaid, TicketId = ticketId,
                Text = $"Ticket {ticketId}: '{raised.Flag}' is cleared for its change as it stands"
                    + (change.Tip is { Length: >= 7 } tip ? $" (at {tip[..7]})" : "")
                    + ". It may be merged past this flag; rework lets the clearance lapse.",
            });
            await StewardDue.MarkAsync(_db, projectKey, $"the owner cleared {raised.Flag} on #{ticketId}");
            await _db.SaveChangesAsync(ct);
            _heartbeat?.Wake();
        }
        return new StewardReply("cleared", null, new { flag = raised.Flag, tip = change.Tip });
    }

    /// <summary>
    /// The owner links a ticket to a unit of one of the project's plans, and to which of its
    /// outcomes it carries, or unlinks it (<paramref name="unit"/> null). The link is what every
    /// count of a unit's progress is taken from, so it is checked against the plan as committed:
    /// the unit and each outcome must be in it. Outcomes are optional - a ticket can belong to a
    /// unit without carrying any one of its outcomes. Not the steward's: no tool reaches this.
    /// </summary>
    public async Task<StewardReply> LinkTicketAsync(int ticketId, string? planKey, int? unit, IReadOnlyList<string>? outcomes,
        CancellationToken ct = default)
    {
        var t = await _db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return Refuse("not-found", $"There is no ticket {ticketId}.");
        var cfg = Config(t.ProjectKey);
        if (cfg is null) return Refuse("no-project", $"There is no enabled project '{t.ProjectKey}'.");

        string? key = null;
        string? title = null;
        var picked = new List<string>();
        if (unit is not null)
        {
            var plan = planKey is null ? cfg.Steward.Plan : cfg.Steward.PlanByKey(planKey);
            if (plan is null || !plan.Configured) return Refuse("no-plan", $"This project has no plan '{planKey}'.");
            var units = await _git.PlanOfAsync(t.ProjectKey, plan, ct);
            var u = units.FirstOrDefault(x => x.Number == unit);
            if (u is null) return Refuse("bad-request", $"{plan.DisplayName} has no unit {unit}.");
            picked = (outcomes ?? Array.Empty<string>()).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();
            if (picked.Where(o => !u.Outcomes.Contains(o)).ToList() is { Count: > 0 } unknown)
                return Refuse("bad-request", $"{u.File} has no outcome {string.Join(", ", unknown)}.");
            // In the spec's own order, whatever order they were ticked in.
            picked = u.Outcomes.Where(picked.Contains).ToList();
            key = plan.Key;
            title = $"{plan.DisplayName}, unit {u.Number}";
        }

        var joined = unit is null ? null : string.Join(", ", picked);
        if ((t.PlanKey ?? "") == (key ?? "") && t.PlanUnit == unit && (t.PlanOutcomes ?? "") == (joined ?? ""))
            return new StewardReply("unchanged");

        var was = t.PlanUnit;
        t.PlanKey = key;
        t.PlanUnit = unit;
        t.PlanOutcomes = joined;
        // A ticket the steward filed carries its link as a line in its description too, for the
        // agents that read it. Kept in step, never added to a description that had none.
        if (t.Description is { } d && PlanLineAt.IsMatch(d))
        {
            var rest = PlanLineAt.Replace(d, "").TrimEnd();
            t.Description = PlanLine(t) is { } line ? $"{rest}\n\n{line}" : rest;
        }
        t.UpdatedAt = DateTime.UtcNow;
        // Out of the plan, the steward has nothing more to do about it.
        if (unit is null)
            foreach (var n in await _db.StewardEntries.Where(e => e.TicketId == ticketId
                         && e.Kind == StewardEntryKind.TicketNote && e.HandledAt == null).ToListAsync(ct))
                n.HandledAt = DateTime.UtcNow;
        if (cfg.StewardMode != StewardMode.Off)
            await StewardDue.MarkAsync(_db, t.ProjectKey, unit is null
                ? $"the owner took #{ticketId} out of unit {was}"
                : $"the owner linked #{ticketId} to unit {unit}");
        await _db.SaveChangesAsync(ct);

        await _tickets.AddCommentAsync(ticketId, unit is null
            ? "Taken out of the plan by the owner."
            : $"Linked to {title}{(picked.Count > 0 ? $" ({string.Join(", ", picked)})" : "")} by the owner.", AuthorKind.System);
        if (cfg.StewardMode != StewardMode.Off) _heartbeat?.Wake();
        return new StewardReply("linked", null, new { plan = key, unit, outcomes = picked });
    }

    private static readonly System.Text.RegularExpressions.Regex PlanLineAt = new(
        @"(\r?\n)*\*\*Plan\*\*\r?\n\r?\nUnit \d+[^\r\n]*\s*$");

    /// <summary>A standing instruction from the owner that answers no question.</summary>
    public async Task<StewardReply> OwnerSaysAsync(string projectKey, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Refuse("bad-request", "There is nothing to record.");
        if (Config(projectKey) is not { } cfg) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        await EnsureRegisterAsync(cfg, ct);
        var said = new StewardEntry { ProjectKey = projectKey, Kind = StewardEntryKind.OwnerSaid, Text = text.Trim() };
        _db.StewardEntries.Add(said);
        await _db.SaveChangesAsync(ct);
        // Instructions with an end are a kind of their own later (docs/plans-as-slices.md, section 3);
        // until then the owner's word counts as decided, as it always has.
        _db.StewardDecisions.Add(new StewardDecision
        {
            ProjectKey = projectKey, Number = await NextNumberAsync(projectKey, ct), Rule = said.Text,
            Source = StewardDecisionSource.OwnerSaid, SourceRef = $"record line {said.Id}", ImportKey = $"said:{said.Id}",
            MadeAt = said.At, DeliveredAt = DateTime.UtcNow,
        });
        await StewardDue.MarkAsync(_db, projectKey, "the owner left an instruction");
        await _db.SaveChangesAsync(ct);
        var exported = await ExportDecisionsAsync(cfg, ct);
        _heartbeat?.Wake();
        return new StewardReply("recorded", exported);
    }

    /// <summary>
    /// The owner ticks a line of the record off, or says what kind of release note it is. Either
    /// may be left out. A kind is only for a release note; a call or an instruction has none.
    /// </summary>
    public async Task<StewardReply> OwnerMarksEntryAsync(string projectKey, int id, bool? handled, string? release, CancellationToken ct = default)
    {
        var e = await _db.StewardEntries.FirstOrDefaultAsync(x => x.Id == id && x.ProjectKey == projectKey, ct);
        if (e is null) return Refuse("no-entry", $"No line {id} in this project's record.");
        if (e.Kind == StewardEntryKind.TicketNote) return Refuse("bad-request", "A ticket note is the steward's own, and it closes it.");
        if (e.Kind == StewardEntryKind.Ruling) return Refuse("bad-request", "A ruling is kept or reversed, not ticked off.");
        if (e.Kind == StewardEntryKind.Learned) return Refuse("bad-request", "A learned fact is placed in the notes store, not ticked off.");
        if (release is not null)
        {
            if (e.Kind != StewardEntryKind.BeforeRelease) return Refuse("bad-request", "Only a note for a release has a kind.");
            if (ParseRelease(release) is not { } rk) return Refuse("bad-request", "The kind is migration, config, action or note.");
            e.Release = rk;
        }
        if (handled is { } h) e.HandledAt = h ? e.HandledAt ?? DateTime.UtcNow : null;
        await _db.SaveChangesAsync(ct);
        return new StewardReply("recorded");
    }

    /// <summary>
    /// Say what kind each of many release notes is, in one go: for a record written before notes
    /// had kinds. A line that is not this project's release note, or a kind that is not one, is
    /// passed over and counted.
    /// </summary>
    public async Task<StewardReply> OwnerSortsReleaseNotesAsync(string projectKey, IReadOnlyDictionary<int, string> kinds, CancellationToken ct = default)
    {
        if (Config(projectKey) is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var rows = await _db.StewardEntries
            .Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.BeforeRelease).ToListAsync(ct);
        int sorted = 0;
        foreach (var e in rows)
        {
            if (!kinds.TryGetValue(e.Id, out var name) || ParseRelease(name) is not { } rk) continue;
            e.Release = rk;
            sorted++;
        }
        await _db.SaveChangesAsync(ct);
        return new StewardReply("recorded", null, new { sorted, passedOver = kinds.Count - sorted });
    }

    private Task<List<StewardOutcome>> StatesAsync(string projectKey, string planKey, CancellationToken ct) =>
        _db.StewardOutcomes.AsNoTracking().Where(o => o.ProjectKey == projectKey && o.Plan == planKey).ToListAsync(ct);

    /// <summary>One plan's notes for a release, each under the unit it will go out with.</summary>
    private sealed record UnitNotes(Dictionary<int, List<StewardEntry>> ByUnit)
    {
        public IReadOnlyList<StewardEntry> Of(int unit) => ByUnit.TryGetValue(unit, out var l) ? l : new List<StewardEntry>();
    }

    /// <summary>
    /// Which unit each note for a release belongs to. A note goes with its ticket's unit. One with
    /// no unit to go with - written about a ticket filed outside the plan, or brought in from an
    /// older record - goes out with whatever is released next, so it is put with the earliest unit
    /// not yet released: nothing noted for a release may be left where no release asks about it.
    /// </summary>
    private async Task<UnitNotes> UnitNotesAsync(string projectKey, StewardPlan plan, bool working,
        IReadOnlyList<PlanUnit> units, IReadOnlyCollection<StewardUnit> rows, IReadOnlyCollection<Ticket> all, bool track, CancellationToken ct)
    {
        var q = _db.StewardEntries.Where(e => e.ProjectKey == projectKey && e.Kind == StewardEntryKind.BeforeRelease);
        var notes = await (track ? q : q.AsNoTracking()).ToListAsync(ct);
        var unitOf = all.Where(t => t.PlanUnit is not null).ToDictionary(t => t.Id, t => (Key: t.PlanKey ?? "", Unit: t.PlanUnit!.Value));
        var next = working
            ? units.Where(u => u.Agreed && rows.All(r => r.Unit != u.Number || r.ReleasedAt is null)).Select(u => (int?)u.Number).FirstOrDefault()
            : null;

        var by = new Dictionary<int, List<StewardEntry>>();
        foreach (var n in notes)
        {
            int? unit = n.TicketId is { } id && unitOf.TryGetValue(id, out var at)
                ? at.Key == plan.Key ? at.Unit : null
                : next;
            // A ticket of another plan's is that plan's note, not a loose one.
            if (n.TicketId is { } tid && unitOf.TryGetValue(tid, out var other) && other.Key != plan.Key) continue;
            if (unit is not { } u) continue;
            if (!by.TryGetValue(u, out var list)) by[u] = list = new();
            list.Add(n);
        }
        return new UnitNotes(by);
    }

    /// <summary>
    /// For the plan being worked, the unit each note for a release will go out with, by the note's
    /// id - so the record can show a unit's notes as the same set its release is held for.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>> ReleaseUnitOfNotesAsync(string projectKey, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (cfg is null || !cfg.Steward.Plan.Configured) return new Dictionary<int, int>();
        var p = cfg.Steward.Plan;
        var plan = await _git.PlanAsync(projectKey, ct);
        var rows = await _db.StewardUnits.AsNoTracking().Where(u => u.ProjectKey == projectKey && u.Plan == p.Key).ToListAsync(ct);
        var notes = await UnitNotesAsync(projectKey, p, working: true, plan, rows, await _tickets.ListAsync(projectKey, null), track: false, ct);
        return notes.ByUnit.SelectMany(kv => kv.Value.Select(n => (n.Id, Unit: kv.Key))).ToDictionary(x => x.Id, x => x.Unit);
    }

    private static bool Outstanding(StewardEntry e) =>
        e.HandledAt is null && (e.Release ?? StewardReleaseKind.Note) != StewardReleaseKind.Note;

    /// <summary>
    /// The plan as its owner reads it: every unit with its own name, its spec, and its outcomes
    /// under the headings the spec puts them beneath - each with its sentence, where it stands and
    /// the tickets that carry it - and where the unit is on its way from a draft to released. Read
    /// from the base branch as committed, like everything else about the plan. It is the owner's
    /// view and no run is shown it: a run reads the specs. With <paramref name="planKey"/>, a plan
    /// the project has finished.
    /// </summary>
    public async Task<StewardReply> PlanViewAsync(string projectKey, string? planKey = null, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (cfg is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var working = cfg.Steward.Plan;
        var which = planKey is null ? working : cfg.Steward.PlanByKey(planKey);
        var plans = new[] { working }.Where(p => p.Configured).Concat(cfg.Steward.PastPlans)
            .Select(p => new { p.Key, name = p.DisplayName, working = ReferenceEquals(p, working) }).ToList();
        if (which is null || !which.Configured)
            return Refuse("no-plan", "This project has no plan yet, so there is nothing to file tickets from.", new { plans });

        var isWorking = ReferenceEquals(which, working);
        var plan = await _git.PlanOfAsync(projectKey, which, ct);
        var all = await _tickets.ListAsync(projectKey, null);
        var mine = all.Where(t => (t.PlanKey ?? "") == which.Key).ToList();
        var states = await StatesAsync(projectKey, which.Key, ct);
        var rows = await _db.StewardUnits.AsNoTracking().Where(u => u.ProjectKey == projectKey && u.Plan == which.Key).ToListAsync(ct);
        var notes = await UnitNotesAsync(projectKey, which, isWorking, plan, rows, all, track: false, ct);
        var current = isWorking ? StewardProgress.Current(plan, states) : null;
        var moved = await MovedSlicesAsync(projectKey, which, plan, ct);

        return new StewardReply("plan", null, new
        {
            plans,
            // Agreed slices whose number or file changed since: tickets and outcomes carry those.
            moved = moved.Values,
            plan = new { which.Key, name = which.DisplayName, working = isWorking, which.UnitsDir },
            current = current?.Number,
            units = plan.Select(u =>
            {
                var p = StewardProgress.For(u, states, mine);
                var its = mine.Where(t => t.PlanUnit == u.Number).ToList();
                var row = rows.FirstOrDefault(r => r.Unit == u.Number);
                var built = u.Agreed && p.Outcomes > 0 && p.Done == p.Outcomes;
                var left = notes.Of(u.Number).Where(n => n.HandledAt is null).ToList();
                int Left(StewardReleaseKind k) => left.Count(n => (n.Release ?? StewardReleaseKind.Note) == k);
                var outstanding = left.Count(Outstanding);
                var earlier = plan.Where(x => x.Number < u.Number && !x.Agreed).Select(x => x.Number).ToList();
                return new
                {
                    unit = u.Number,
                    u.File,
                    title = TicketGitService.UnitTitle(u),
                    u.Agreed,
                    u.Draft,
                    // Built is every outcome called done, and a unit with none has built nothing.
                    done = built,
                    // Where it is on the way from a draft to released, in one word.
                    stage = row?.ReleasedAt is not null ? "released" : built ? "built"
                        : u.Agreed ? u.Number == current?.Number ? "building" : "agreed"
                        : u.Draft ? "draft" : u.Idea ? "idea" : "notAgreed",
                    movedFrom = moved.TryGetValue(u.Number, out var mv) ? mv : null,
                    // An idea's paragraph, which is what the plan shows of it.
                    idea = u.Idea ? SliceStory.IdeaParagraph(u.Text) : null,
                    story = SliceStory.Find(u.Text)?.Text,
                    agreedAt = row?.AgreedAt,
                    releasedAt = row?.ReleasedAt,
                    // Why a draft cannot be agreed from here, or null when it can (or is not a draft).
                    cannotAgree = u.Idea ? "An idea is drafted before it is agreed: the steward expands it when its turn comes."
                        : !u.Draft ? null
                        : !isWorking ? "This plan is finished: nothing is filed against it."
                        : earlier.Count > 0 ? $"Unit {earlier[0]} comes first and is not agreed."
                        : TicketGitService.MarkAgreed(which, u.Text, DateTime.Now) is null
                            ? "Its status line is not written the way the board can rewrite. Mark it agreed in the file and commit that."
                        : null,
                    release = !u.Agreed ? null : new
                    {
                        migrations = Left(StewardReleaseKind.Migration),
                        config = Left(StewardReleaseKind.Config),
                        actions = Left(StewardReleaseKind.Action),
                        toKnow = Left(StewardReleaseKind.Note),
                        outstanding,
                    },
                    progress = new { p.Percent, p.Done, p.InProgress, p.NotStarted, p.Outcomes },
                    // What its spec still leaves for the owner to decide, by number.
                    openDecisions = UnitDecisions.Open(u.Text).Select(d => d.Number),
                    u.Text,
                    groups = TicketGitService.UnitGroups(which, u).Select(g => new
                    {
                        g.Heading,
                        outcomes = g.Outcomes.Select(o =>
                        {
                            var st = states.FirstOrDefault(x => x.Unit == u.Number && x.Outcome == o.Outcome);
                            return new
                            {
                                o.Outcome,
                                o.Text,
                                state = (st?.State ?? StewardOutcomeState.NotStarted).ToString(),
                                moreToFile = st?.MoreToFile ?? false,
                                note = st?.Note,
                                tickets = its.Where(t => StewardProgress.OutcomesOf(t).Contains(o.Outcome)).OrderBy(t => t.Id)
                                    .Select(t => new { t.Id, phase = t.Phase.ToString(), t.Title }),
                            };
                        }),
                    }),
                };
            }),
        });
    }

    private async Task<StewardUnit> UnitRowAsync(string projectKey, string planKey, int unit, CancellationToken ct)
    {
        var row = await _db.StewardUnits.FirstOrDefaultAsync(u => u.ProjectKey == projectKey && u.Plan == planKey && u.Unit == unit, ct);
        if (row is null) _db.StewardUnits.Add(row = new StewardUnit { ProjectKey = projectKey, Plan = planKey, Unit = unit });
        return row;
    }

    /// <summary>
    /// The owner agrees a draft unit: its status line is rewritten and committed to the base
    /// branch, which is the only thing that has ever made a unit agreed. This is that same commit,
    /// made from the board instead of an editor - and it has no tool, so it is still the owner's
    /// alone. Units are agreed in order, and only a spec that says it is a draft can be agreed by
    /// rule: anything else is left for a person to edit.
    /// </summary>
    public Task<StewardReply> OwnerAgreesUnitAsync(string projectKey, int unit, CancellationToken ct = default) =>
        OwnerDecidesAsync(projectKey, unit, Array.Empty<UnitDecisionChoice>(), agree: true, answering: null, ct);

    /// <summary>The decisions a unit of the plan being worked still leaves open, read from its spec as committed.</summary>
    public async Task<StewardReply> UnitDecisionsAsync(string projectKey, int unit, CancellationToken ct = default)
    {
        if (Config(projectKey) is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var u = (await _git.PlanAsync(projectKey, ct)).FirstOrDefault(x => x.Number == unit);
        if (u is null) return Refuse("bad-request", $"The plan has no unit numbered {unit}.");
        return new StewardReply("decisions", null, new
        {
            unit, file = u.File, title = TicketGitService.UnitTitle(u), u.Agreed, u.Draft,
            open = UnitDecisions.Open(u.Text),
        });
    }

    /// <summary>
    /// The owner settles some of a unit's open decisions, and may agree the unit in the same commit.
    /// <para>
    /// The answers are written into the spec, under "Decided" - the document every run files from -
    /// rather than only into the record beside it: a decision a steward has to find in two places
    /// is one it will one day find in neither. Like agreeing, this has no tool behind it. Given the
    /// key of a question that asked for these decisions, it answers that question too, so the
    /// owner says it once.
    /// </para>
    /// </summary>
    public async Task<StewardReply> OwnerDecidesAsync(string projectKey, int unit, IReadOnlyList<UnitDecisionChoice> choices,
        bool agree, string? answering, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (cfg is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var p = cfg.Steward.Plan;
        var plan = await _git.PlanAsync(projectKey, ct);
        var u = plan.FirstOrDefault(x => x.Number == unit);
        if (u is null) return Refuse("bad-request", $"The plan has no unit numbered {unit}.");
        if (choices.Count == 0 && !agree) return Refuse("bad-request", "There is nothing to settle.");

        StewardQuestion? q = null;
        if (!string.IsNullOrWhiteSpace(answering))
        {
            q = await _db.StewardQuestions.FirstOrDefaultAsync(x => x.ProjectKey == projectKey && x.Key == answering, ct);
            if (q is null) return Refuse("not-waiting", $"No question named \"{answering}\".");
            if (q.Answer is not null) return Refuse("not-waiting", $"\"{answering}\" was answered on {q.AnsweredAt:yyyy-MM-dd}.");
        }

        var text = u.Text;
        IReadOnlyList<string> said = Array.Empty<string>();
        IReadOnlyList<(int Number, string Title, string Rule)> each = Array.Empty<(int, string, string)>();
        if (choices.Count > 0)
        {
            var (settled, s, e, refusal) = UnitDecisions.SettleEach(text, choices, DateTime.Now);
            if (settled is null) return Refuse("bad-request", refusal!);
            (text, said, each) = (settled, s, e);
            // A settled decision keeps the number its spec gave it, so that number must be free.
            await EnsureRegisterAsync(cfg, ct);
            var numbers = each.Select(x => x.Number).ToList();
            if (await _db.StewardDecisions.Where(d => d.ProjectKey == projectKey && numbers.Contains(d.Number))
                    .Select(d => (int?)d.Number).FirstOrDefaultAsync(ct) is { } taken)
                return Refuse("number-taken", $"#{taken} is already a decision in the register, so the spec's #{taken} cannot keep its number. Renumber it in {u.File} from steward_decisions' next number, and commit that.");
        }
        if (agree)
        {
            if (u.Agreed) return Refuse("already-agreed", $"{u.File} is already agreed.");
            if (!u.Draft) return Refuse("not-a-draft", $"{u.File} does not say it is a draft, so there is nothing here to agree by rule. Mark it agreed in the file and commit that.");
            if (plan.FirstOrDefault(x => x.Number < unit && !x.Agreed) is { } earlier)
                return Refuse("not-next", $"Unit {earlier.Number} comes first and is not agreed. Units are worked in order.");
            if (TicketGitService.MarkAgreed(p, text, DateTime.Now) is not { } marked)
                return Refuse("not-by-rule", $"The status line of {u.File} is not written the way the board can rewrite. Mark it agreed in the file and commit that.");
            text = marked;
        }

        var n = said.Count;
        var message = agree
            ? n == 0 ? $"Unit {unit} of the plan, agreed by its owner" : $"Unit {unit} of the plan, agreed by its owner with {n} decision{(n == 1 ? "" : "s")}"
            : $"Unit {unit}: {n} decision{(n == 1 ? "" : "s")}, made by its owner";
        var (sha, _, no) = await _git.CommitFileAsync(projectKey, u.File, text, message, ct);
        if (no is not null) return Refuse("refused", no);

        var summary = $"Unit {unit}: {(n == 0 ? "agreed" : $"{(agree ? "agreed, and " : "")}decided {string.Join("; ", said)}")}. Written into {u.File} at {sha}.";
        if (agree)
        {
            var row = await UnitRowAsync(projectKey, p.Key, unit, ct);
            (row.AgreedAt, row.AgreedFile) = (DateTime.UtcNow, u.File);
        }
        // The register holds each decision's status; the spec keeps the words, written once, above.
        foreach (var (number, title, rule) in each)
            _db.StewardDecisions.Add(new StewardDecision
            {
                ProjectKey = projectKey, Number = number, Title = title, Rule = rule, Source = StewardDecisionSource.Slice,
                PlanKey = p.Key, PlanUnit = unit, SourceRef = u.File, ImportKey = $"slice:{p.Key}:{number}",
                QuestionKey = q?.Key, Question = q?.Question, DeliveredAt = DateTime.UtcNow,
            });
        if (q is not null)
        {
            (q.Answer, q.AnsweredAt) = (summary, DateTime.UtcNow);
            await StewardDue.MarkAsync(_db, projectKey, $"the owner answered {q.Key}");
        }
        else
        {
            if (n > 0) _db.StewardEntries.Add(new StewardEntry { ProjectKey = projectKey, Kind = StewardEntryKind.OwnerSaid, Text = summary });
            await StewardDue.MarkAsync(_db, projectKey, agree ? $"the owner agreed unit {unit}" : $"the owner decided {n} of unit {unit}'s decisions");
        }
        await _db.SaveChangesAsync(ct);
        var exported = n > 0 || q is not null ? await ExportDecisionsAsync(cfg, ct) : null;
        if (agree) await WriteReadableAsync(cfg, ct);
        _heartbeat?.Wake();
        return new StewardReply(agree ? "agreed" : "decided", exported, new
        {
            file = u.File, commit = sha, decided = said,
            open = UnitDecisions.Open(text).Select(d => d.Number),
        });
    }

    /// <summary>
    /// The owner says a unit's work has been released, or takes that back.
    /// <para>
    /// Releasing is theirs in every sense - the merge that carries it out, and the push after, are
    /// made by hand and never by the board - so this only records that it happened. What it asks
    /// first is the reason notes for a release exist at all: every outcome built, and every note
    /// that is something to do (a migration, something to set up, something theirs) ticked off.
    /// What was only to know goes out with it and is ticked off here.
    /// </para>
    /// </summary>
    public async Task<StewardReply> OwnerReleasesUnitAsync(string projectKey, int unit, bool released, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (cfg is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var p = cfg.Steward.Plan;
        var plan = await _git.PlanAsync(projectKey, ct);
        var u = plan.FirstOrDefault(x => x.Number == unit);
        if (u is null || !u.Agreed) return Refuse("unit-not-agreed", $"Unit {unit} is not an agreed unit of the plan.");

        var row = await UnitRowAsync(projectKey, p.Key, unit, ct);
        if (!released)
        {
            (row.ReleasedAt, row.ReleasedSha) = (null, null);
            await _db.SaveChangesAsync(ct);
            return new StewardReply("recorded");
        }
        if (row.ReleasedAt is not null) return Refuse("already-released", $"Unit {unit} was released on {row.ReleasedAt.Value.ToLocalTime():yyyy-MM-dd}.");

        var all = await _tickets.ListAsync(projectKey, null);
        var mine = all.Where(t => (t.PlanKey ?? "") == p.Key).ToList();
        var progress = StewardProgress.For(u, await StatesAsync(projectKey, p.Key, ct), mine);
        if (progress.Outcomes == 0 || progress.Done < progress.Outcomes)
            return Refuse("not-built", $"{progress.Outcomes - progress.Done} of unit {unit}'s {progress.Outcomes} outcomes are not done.");

        var rows = await _db.StewardUnits.Where(x => x.ProjectKey == projectKey && x.Plan == p.Key).ToListAsync(ct);
        var notes = (await UnitNotesAsync(projectKey, p, working: true, plan, rows, all, track: true, ct)).Of(unit);
        var outstanding = notes.Count(Outstanding);
        if (outstanding > 0)
            return Refuse("notes-outstanding", $"{outstanding} note{(outstanding == 1 ? "" : "s")} for this release - a migration, something to set up, something yours to do - {(outstanding == 1 ? "is" : "are")} not ticked off.");

        var now = DateTime.UtcNow;
        foreach (var n in notes.Where(n => n.HandledAt is null)) n.HandledAt = now;
        (row.ReleasedAt, row.ReleasedSha) = (now, await _git.BaseHeadAsync(projectKey, ct));
        await _db.SaveChangesAsync(ct);
        await WriteReadableAsync(cfg, ct);
        return new StewardReply("released", null, new { at = row.ReleasedSha });
    }

    /// <summary>
    /// The owner starts a new plan: its charter and its first ideas are committed to the base
    /// branch, each from a template (docs/plans-as-slices.md, "Starting a plan"). The steward then
    /// expands the first idea into a draft. Refused while a run is open, or while the plan being
    /// worked has a ticket still open: a steward in the middle of one plan is not handed another.
    /// <para>
    /// It does not write the project's config; the caller does, once this has answered "started",
    /// because where a project's settings live is not this service's to know.
    /// </para>
    /// </summary>
    public async Task<StewardReply> OwnerStartsPlanAsync(string projectKey, string? key, string? name, string? folder,
        string charterTemplate, string ideaTemplate, IReadOnlyList<StewardNewIdea>? ideas, CancellationToken ct = default)
    {
        var cfg = Config(projectKey);
        if (cfg is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        key = key?.Trim().ToLowerInvariant() ?? "";
        name = name?.Trim() ?? "";
        if (!StewardPlan.IsKey(key)) return Refuse("bad-request", "The key is the plan's short name: lowercase letters, digits and hyphens, 40 at most.");
        if (name.Length is < 3 or > 80) return Refuse("bad-request", "Give the plan a name a person would call it, 3 to 80 characters.");
        if (cfg.Steward.PlanByKey(key) is not null) return Refuse("exists", $"This project already has a plan with the key \"{key}\".");
        var wanted = (ideas ?? Array.Empty<StewardNewIdea>()).Where(i => !string.IsNullOrWhiteSpace(i.Title)).ToList();
        if (wanted.Count == 0) return Refuse("bad-request", "Give at least one idea: a title, and a paragraph on who can do what at the end of it.");
        if (wanted.Any(i => string.IsNullOrWhiteSpace(i.Paragraph)))
            return Refuse("bad-request", "Each idea needs its paragraph: who, what they can do at the end, what it builds on, what it is not.");

        folder = string.IsNullOrWhiteSpace(folder) ? $"docs/plans/{key}" : folder.Replace('\\', '/').Trim().Trim('/');
        if (folder.Split('/').Any(part => part is "" or "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return Refuse("bad-request", "The folder is a path inside the repo, like docs/plans/its-key.");

        if (await RunOpenAsync(projectKey)) return Refuse("run-open", "A steward run is in progress. Start the plan once it has ended.");
        var working = cfg.Steward.Plan;
        if (working.Configured)
        {
            var open = (await _tickets.ListAsync(projectKey, null))
                .Where(t => t.PlanUnit is not null && (t.PlanKey ?? "") == working.Key && t.Phase != TicketPhase.Done).Select(t => t.Id).ToList();
            if (open.Count > 0)
                return Refuse("plan-in-flight", $"{working.DisplayName} still has {open.Count} ticket{(open.Count == 1 ? "" : "s")} open ({string.Join(", ", open.Take(8).Select(i => $"#{i}"))}). Finish or close them first: the plan being worked is the one a steward files against.");
        }

        var charter = $"{folder}/charter.md";
        var units = $"{folder}/slices";
        if (await _git.FileAtBaseAsync(projectKey, charter, ct) is not null) return Refuse("exists", $"{charter} is already in the repo.");
        var probe = new StewardPlan(units, StewardPlan.DefaultUnitFile, StewardPlan.DefaultAgreed, StewardPlan.DefaultDraft, StewardPlan.DefaultOutcome);
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        var files = new List<(string Path, string Text)>();
        for (var i = 0; i < wanted.Count; i++)
        {
            var title = Regex.Replace(wanted[i].Title.Trim(), @"\s+", " ");
            var slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 50) slug = slug[..50].Trim('-');
            var path = $"{units}/{i + 1:00}-{(slug.Length > 0 ? slug : "idea")}.md";
            var text = ideaTemplate.Replace("{{TITLE}}", title).Replace("{{DATE}}", day)
                .Replace("{{PARAGRAPH}}", Regex.Replace(wanted[i].Paragraph!.Trim(), @"\s*\n\s*", " "));
            if (!TicketGitService.ReadUnit(probe, i + 1, path, text).Idea)
                return Refuse("bad-template", "The idea template does not read as an idea, so nothing was written.");
            if (await _git.FileAtBaseAsync(projectKey, path, ct) is not null) return Refuse("exists", $"{path} is already in the repo.");
            files.Add((path, text));
        }

        string Fill(string t) => t.Replace("{{NAME}}", name).Replace("{{KEY}}", key).Replace("{{DATE}}", day).Replace("{{UNITS_DIR}}", "slices");
        var (_, _, no1) = await _git.CommitFileAsync(projectKey, charter, Fill(charterTemplate), $"{name}: the charter, to be written", ct);
        if (no1 is not null) return Refuse("refused", no1);
        string? sha = null;
        foreach (var (path, text) in files)
        {
            var (s1, _, no2) = await _git.CommitFileAsync(projectKey, path, text, $"{name}: an idea, {Path.GetFileName(path)}", ct);
            if (no2 is not null) return Refuse("refused", $"{charter} was committed, but {path} was not: {no2}");
            sha = s1;
        }

        return new StewardReply("started", null, new { key, name, charter, units, ideas = files.Select(f => f.Path), commit = sha });
    }

    /// <summary>
    /// Take in a steward's record that was kept somewhere else - a ledger file, another machine.
    /// <para>
    /// The owner's to run, once, when a project's stand-in moves into the dashboard from wherever
    /// it lived before. Safe to run again: a question already here by its key is left alone, an
    /// identical line is not added twice, and an outcome is set to what the import says. Everything it brings in is history, so none of it is
    /// handed to the next run as news - that run finds it where every later one will.
    /// </para>
    /// </summary>
    public async Task<StewardReply> ImportAsync(string projectKey, StewardImport import, CancellationToken ct = default)
    {
        if (Config(projectKey) is null) return Refuse("no-project", $"There is no enabled project '{projectKey}'.");
        var now = DateTime.UtcNow;
        var planKey = Config(projectKey)!.Steward.Plan.Key;
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
                Basis = kind == StewardEntryKind.Ruling ? e.Basis?.Trim() : null,
                Placed = kind == StewardEntryKind.Learned ? NoteKey(e.Key) : null,
            });
            if (kind == StewardEntryKind.OwnerSaid) said++; else entries++;
        }

        foreach (var o in import.Outcomes ?? Array.Empty<StewardImportOutcome>())
        {
            if (string.IsNullOrWhiteSpace(o.Outcome) || !Enum.TryParse<StewardOutcomeState>(o.State, true, out var st)) continue;
            var row = await _db.StewardOutcomes.FirstOrDefaultAsync(
                x => x.ProjectKey == projectKey && x.Plan == planKey && x.Unit == o.Unit && x.Outcome == o.Outcome, ct);
            if (row is null) _db.StewardOutcomes.Add(row = new StewardOutcome { ProjectKey = projectKey, Plan = planKey, Unit = o.Unit, Outcome = o.Outcome.Trim() });
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
            row.PlanKey = planKey;
            row.PlanOutcomes = string.Join(", ", t.Outcomes.Select(o => o.Trim()).Where(o => o.Length > 0));
            if (t.FiledBySteward) row.CreatedBy = TransitionActor.Steward;
            tickets++;
        }

        await _db.SaveChangesAsync(ct);
        // What came in is folded into the register too: it is where decisions are read from.
        var register = await BuildRegisterAsync(Config(projectKey)!, ct);
        return new StewardReply("imported", null, new { questions, ownerSaid = said, entries, outcomes, tickets, register });
    }

    private static string Head(string s, int max)
    {
        var line = s.Trim().Split('\n')[0].Trim();
        return line.Length > max ? line[..(max - 3)] + "..." : line;
    }
}
