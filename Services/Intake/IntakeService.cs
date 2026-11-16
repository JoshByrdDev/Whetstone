using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services.Intake;

/// <summary>
/// The board's half of intake (docs/intake-plan.md): a message off a project's queue becomes a
/// ticket, a note on one, or nothing but a count. Board code only - no MCP tool reaches here, and
/// nothing an agent is handed carries a source's credential.
/// <para>
/// Each message is handled in one transaction, and the caller deletes it from the queue only after
/// that commits. Its id is kept for 14 days, the queue's own retention, so a redelivery is a no-op.
/// </para>
/// </summary>
public sealed class IntakeService
{
    /// <summary>How long a handled message's id (and an unrecognised one's body) is kept.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(14);

    /// <summary>A repeat adds at most one note an hour to the open ticket.</summary>
    public static readonly TimeSpan NoteEvery = TimeSpan.FromHours(1);

    /// <summary>The phase an intake ticket lands in: where a bug filed with no phase lands - straight into the fleet.</summary>
    public const TicketPhase Landing = TicketPhase.NeedsSpeculation;

    private const int MaxRawBody = 64 * 1024;

    private readonly DashboardDbContext _db;
    private readonly TicketService _tickets;
    private readonly IIntakeSettings _settings;
    private readonly IIntakeRunbook? _runbook;
    private readonly Func<DateTime> _now;
    private readonly ProjectRegistry? _registry;

    public IntakeService(DashboardDbContext db, TicketService tickets, IIntakeSettings settings,
        IIntakeRunbook? runbook = null, ProjectRegistry? registry = null, Func<DateTime>? clock = null)
        => (_db, _tickets, _settings, _runbook, _registry, _now) = (db, tickets, settings, runbook, registry, clock ?? (() => DateTime.UtcNow));

    // ---- a pass over one source ----------------------------------------------------------

    public sealed record PassResult(int Received, int Handled, int Redelivered, int Failed, string? Error);

    /// <summary>
    /// Drains up to <paramref name="maxMessages"/> from one source: each message handled and
    /// committed, then deleted. A message that fails to handle is left on the queue for the next
    /// pass - SQS gives it back once its visibility runs out.
    /// </summary>
    public async Task<PassResult> PollAsync(string project, IntakeSource source, IIntakeTransport transport,
        int maxMessages = 50, CancellationToken ct = default)
    {
        await PruneAsync(project, ct);
        int received = 0, handled = 0, redelivered = 0, failed = 0;
        string? error = null;
        while (received < maxMessages)
        {
            var batch = await transport.ReceiveAsync(Math.Min(10, maxMessages - received), ct);
            if (batch.Count == 0) break;
            received += batch.Count;
            foreach (var m in batch)
            {
                try
                {
                    var (_, again) = await HandleAsync(project, source.Name, m, ct);
                    if (again) redelivered++; else handled++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failed++;
                    error = $"Message {m.MessageId}: {e.Message}";
                    _db.ChangeTracker.Clear();
                    continue;
                }
                await transport.DeleteAsync(m, ct);
            }
        }
        return new PassResult(received, handled, redelivered, failed, error);
    }

    /// <summary>
    /// One message, in one transaction. Returns what was recorded for it, and whether it had been
    /// handled before (a redelivery, which changes nothing).
    /// </summary>
    public async Task<(IntakeMessageLog Log, bool Redelivered)> HandleAsync(string project, string source, IntakeEnvelope m,
        CancellationToken ct = default)
    {
        var seen = await _db.IntakeMessages.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectKey == project && x.Source == source && x.MessageId == m.MessageId, ct);
        if (seen is not null) return (seen, true);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var log = new IntakeMessageLog { ProjectKey = project, Source = source, MessageId = m.MessageId, HandledAt = _now() };
        var (report, problem) = IntakeAdapters.Read(m);
        if (report is null)
        {
            log.Outcome = IntakeOutcomes.Unrecognised;
            log.Note = problem ?? "No adapter recognised it.";
            log.RawBody = m.Body.Length > MaxRawBody ? m.Body[..MaxRawBody] : m.Body;
            log.RawAttributes = JsonSerializer.Serialize(m.Attributes);
        }
        else
        {
            log.Adapter = report.Adapter;
            await ApplyAsync(project, report, log, ct);
        }
        _db.IntakeMessages.Add(log);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (log, false);
    }

    // ---- section 4: one problem, one ticket ----------------------------------------------

    private async Task ApplyAsync(string project, IntakeReport r, IntakeMessageLog log, CancellationToken ct)
    {
        var now = _now();
        var cfg = _settings.For(project);
        if (r.IsTest) { await TestAsync(project, r, log, ct); return; }

        var issue = await _db.IntakeIssues.FirstOrDefaultAsync(
            i => i.ProjectKey == project && i.Channel == r.Channel && i.Fingerprint == r.Fingerprint, ct);

        if (issue is null)
        {
            if (r.State is IntakeStates.Cleared or IntakeStates.Unknown)
            {
                (log.Outcome, log.Note) = (IntakeOutcomes.Ignored, r.State == IntakeStates.Cleared
                    ? "Cleared, with nothing ever filed for it." : "No data, with nothing ever filed for it.");
                return;
            }
            issue = new IntakeIssue
            {
                ProjectKey = project, Channel = r.Channel, Fingerprint = r.Fingerprint,
                FirstSeen = r.FirstSeen ?? r.OccurredAt ?? now,
            };
            _db.IntakeIssues.Add(issue);
            Count(issue, r, now);
            await _db.SaveChangesAsync(ct);
            await FileOrHoldAsync(project, cfg, issue, r, regressionOf: null, log, ct);
            return;
        }

        Count(issue, r, now);
        log.IssueId = issue.Id;
        if (issue.Muted) { (log.Outcome, log.Note) = (IntakeOutcomes.Muted, "Muted by the owner: counted, nothing filed."); return; }
        if (r.State == IntakeStates.Unknown) { (log.Outcome, log.Note) = (IntakeOutcomes.Ignored, "No data: noted, never filed."); return; }

        var ticket = issue.TicketId is { } tid ? await _db.Tickets.FirstOrDefaultAsync(t => t.Id == tid, ct) : null;
        if (ticket is null)
        {
            if (r.State == IntakeStates.Cleared) { (log.Outcome, log.Note) = (IntakeOutcomes.Ignored, "Cleared, with no ticket open for it."); return; }
            // Held back before, or its ticket was deleted: filed now, if the limits allow.
            await FileOrHoldAsync(project, cfg, issue, r, regressionOf: null, log, ct);
            return;
        }

        if (ticket.Phase == TicketPhase.Done)
        {
            if (r.State == IntakeStates.Cleared) { (log.Outcome, log.Note) = (IntakeOutcomes.Ignored, $"Cleared; #{ticket.Id} is Done."); return; }
            await FileOrHoldAsync(project, cfg, issue, r, regressionOf: ticket.Id, log, ct);
            return;
        }

        log.TicketId = ticket.Id;
        log.Outcome = IntakeOutcomes.Noted;
        if (r.State == IntakeStates.Cleared)
        {
            await _tickets.AddCommentAsync(ticket.Id, IntakeTicketText.Cleared(r.OccurredAt ?? now), AuthorKind.System);
            log.Note = $"Cleared: noted on #{ticket.Id}.";
            return;
        }
        if (issue.LastNotedAt is null || now - issue.LastNotedAt >= NoteEvery)
        {
            await _tickets.AddCommentAsync(ticket.Id,
                IntakeTicketText.Repeat(issue.UnnotedOccurrences, issue.LastSeen, issue.ReportedCount, r.State), AuthorKind.System);
            (issue.UnnotedOccurrences, issue.LastNotedAt) = (0, now);
            log.Note = $"Seen again: noted on #{ticket.Id}.";
        }
        else log.Note = $"Seen again: counted on #{ticket.Id}, noted within the hour.";
    }

    private static void Count(IntakeIssue issue, IntakeReport r, DateTime now)
    {
        issue.Occurrences++;
        issue.UnnotedOccurrences++;
        issue.LastSeen = r.OccurredAt is { } at && at > issue.LastSeen ? at : now > issue.LastSeen ? now : issue.LastSeen;
        issue.LastState = r.State;
        if (r.Count is { } c) issue.ReportedCount = c;
        if (r.State is not (IntakeStates.Cleared or IntakeStates.Unknown)) issue.Title = r.Title.Length > 300 ? r.Title[..300] : r.Title;
    }

    // ---- section 5: filing, the limits, the storm ----------------------------------------

    private async Task FileOrHoldAsync(string project, IntakeConfig cfg, IntakeIssue issue, IntakeReport r, int? regressionOf,
        IntakeMessageLog log, CancellationToken ct)
    {
        var now = _now();
        log.IssueId = issue.Id;
        if (await OverLimitAsync(project, cfg.Limits, now, ct) is { } window)
        {
            issue.HeldIn = window;
            await _db.SaveChangesAsync(ct);
            var storm = await StormAsync(project, cfg.Limits, window, ct);
            (log.Outcome, log.TicketId, log.Note) = (IntakeOutcomes.Held, storm, $"Over the limit for {IntakeTicketText.WindowName(window)}: listed on storm #{storm}.");
            return;
        }

        string? section = null;
        if (r.Channel == IntakeChannels.CloudWatchAlarm && cfg.Runbook is { } path && _runbook is not null)
        {
            try { section = IntakeTicketText.RunbookSection(await _runbook.ReadAsync(project, path, ct), r.Fingerprint); }
            catch (Exception e) when (e is not OperationCanceledException) { section = null; }
        }
        var made = await _tickets.CreateAsync(project, IntakeTicketText.Title(r),
            IntakeTicketText.Description(r, regressionOf, section, cfg.Runbook), null, null, Landing,
            priority: r.Severity,
            skipClassroomAnalysis: _registry?.GetProjectConfig(project).SkipClassroomAnalysisByDefault ?? false,
            filing: new TicketFiling(RegressionOf: regressionOf, IntakeChannel: r.Channel, IntakeIssueId: issue.Id));
        if (r.IsBug) await _tickets.SetBugAsync(made.Id, true);

        issue.TicketId = made.Id;
        issue.HeldIn = null;
        issue.TicketHistory = string.IsNullOrEmpty(issue.TicketHistory) ? $"{made.Id}" : $"{issue.TicketHistory},{made.Id}";
        (issue.UnnotedOccurrences, issue.LastNotedAt) = (0, now);
        await _db.SaveChangesAsync(ct);
        (log.Outcome, log.TicketId, log.Note) = (IntakeOutcomes.Filed, made.Id,
            regressionOf is { } old ? $"Came back after #{old} was Done: filed #{made.Id}." : $"Filed #{made.Id}.");
    }

    /// <summary>The window that is full, when one is: the day's before the hour's, so a day that is spent holds for the rest of the day.</summary>
    private async Task<string?> OverLimitAsync(string project, IntakeLimits limits, DateTime now, CancellationToken ct)
    {
        var day = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        var hour = day.AddHours(now.Hour);
        var filed = await _db.IntakeMessages.AsNoTracking()
            .Where(m => m.ProjectKey == project && m.Outcome == IntakeOutcomes.Filed && m.HandledAt >= day)
            .Select(m => m.HandledAt).ToListAsync(ct);
        if (filed.Count >= limits.NewPerDay) return "d:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (filed.Count(t => t >= hour) >= limits.NewPerHour) return "h:" + hour.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>The window's one storm ticket, filed on its first overflow and rewritten on each one after.</summary>
    private async Task<int> StormAsync(string project, IntakeLimits limits, string window, CancellationToken ct)
    {
        var held = await _db.IntakeIssues.Where(i => i.ProjectKey == project && i.HeldIn == window).ToListAsync(ct);
        var title = IntakeTicketText.StormTitle(window, held.Count);
        var text = IntakeTicketText.StormDescription(window, limits, held);
        var storm = await _db.IntakeStorms.FirstOrDefaultAsync(s => s.ProjectKey == project && s.Window == window, ct);
        if (storm is not null && await _db.Tickets.FirstOrDefaultAsync(t => t.Id == storm.TicketId, ct) is { } t)
        {
            (t.Title, t.Description, t.UpdatedAt) = (title, text, DateTime.UtcNow);
            await _db.SaveChangesAsync(ct);
            return t.Id;
        }
        var made = await _tickets.CreateAsync(project, title, text, null, null, Landing,
            skipClassroomAnalysis: _registry?.GetProjectConfig(project).SkipClassroomAnalysisByDefault ?? false,
            filing: new TicketFiling(IntakeChannel: IntakeChannels.Storm));
        if (storm is null) _db.IntakeStorms.Add(new IntakeStorm { ProjectKey = project, Window = window, TicketId = made.Id, CreatedAt = _now() });
        else storm.TicketId = made.Id;
        await _db.SaveChangesAsync(ct);
        return made.Id;
    }

    /// <summary>
    /// A project's end-to-end test report: one ticket that says it is a test, straight to Done, so
    /// the proof costs no fleet run. At most one an hour per project; the rest are counted in the log.
    /// Not a bug and not an issue: the next test is never a "regression" of the last.
    /// </summary>
    private async Task TestAsync(string project, IntakeReport r, IntakeMessageLog log, CancellationToken ct)
    {
        var since = _now() - TimeSpan.FromHours(1);
        var recent = await _db.IntakeMessages.AsNoTracking()
            .Where(m => m.ProjectKey == project && m.Outcome == IntakeOutcomes.Test && m.HandledAt >= since && m.TicketId != null)
            .Select(m => m.TicketId).FirstOrDefaultAsync(ct);
        log.Outcome = IntakeOutcomes.Test;
        if (recent is { } already)
        {
            log.Note = $"A test report; #{already} already proved the path this hour.";
            return;
        }
        var made = await _tickets.CreateAsync(project, IntakeTicketText.Title(r, test: true),
            IntakeTicketText.Description(r, null, null, null, test: true), null, null, TicketPhase.Backlog,
            skipClassroomAnalysis: true, filing: new TicketFiling(IntakeChannel: r.Channel));
        await _tickets.CloseUnworkedAsync(made.Id, "a test report from the project's own end-to-end check: the path works, and there is nothing to fix",
            TransitionActor.System);
        (log.TicketId, log.Note) = (made.Id, $"A test report: filed #{made.Id} and closed it.");
    }

    // ---- mute ------------------------------------------------------------------------------

    public sealed record MuteResult(int IssueId, bool Muted, bool TicketClosed, string Message);

    /// <summary>
    /// The owner's "never file this again", from an intake ticket's drawer: the issue is muted and
    /// its ticket closed. A REST route with no tool behind it. A ticket a run is working stays open
    /// (the run is not pulled out from under), and says so.
    /// </summary>
    public async Task<MuteResult?> MuteFromTicketAsync(int ticketId, CancellationToken ct = default)
    {
        var t = await _db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t?.IntakeIssueId is not { } issueId) return null;
        var issue = await _db.IntakeIssues.FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null) return null;
        (issue.Muted, issue.MutedAt, issue.HeldIn) = (true, _now(), null);
        await _db.SaveChangesAsync(ct);
        var closed = await _tickets.CloseUnworkedAsync(ticketId,
            "the owner muted this intake issue: its reports are counted and never filed again", TransitionActor.Human);
        return new MuteResult(issue.Id, true, closed == true, closed == true
            ? $"Muted, and #{ticketId} is closed."
            : $"Muted. #{ticketId} is being worked by a run, so it stays open; close it when the run ends.");
    }

    public async Task<IntakeIssue?> SetMutedAsync(int issueId, bool muted, CancellationToken ct = default)
    {
        var issue = await _db.IntakeIssues.FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null) return null;
        (issue.Muted, issue.MutedAt) = (muted, muted ? _now() : null);
        await _db.SaveChangesAsync(ct);
        return issue;
    }

    // ---- upkeep ------------------------------------------------------------------------------

    private async Task PruneAsync(string project, CancellationToken ct)
    {
        var before = _now() - Keep;
        await _db.IntakeMessages.Where(m => m.ProjectKey == project && m.HandledAt < before).ExecuteDeleteAsync(ct);
    }
}
