using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>What an agent or the owner wants on the list.</summary>
public record OwnerTaskDraft(
    string? Project, string Title, string Body, string? Kind = null, string? Key = null,
    int? TicketId = null, int? Unit = null, bool HoldsRelease = false, IReadOnlyList<int>? NotDuplicateOf = null);

/// <summary>The slice an item holds the release of, as the release gate places it: its number and its name.</summary>
public record OwnerTaskSlice(int Unit, string Title)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? $"slice {Unit:00}" : $"slice {Unit:00} ({Title})";
}

/// <summary>An answer from the list: a word, why when it is a refusal, and what it is about.</summary>
public record OwnerTaskReply(string Answer, string? Why = null, object? Data = null)
{
    public bool Ok => Answer is "added" or "merged" or "noted" or "recorded" or "found" or "proposed";
}

/// <summary>
/// The owner's to-do list (docs/owner-todo-plan.md): one list across every project of what only the
/// owner can do.
/// <para>
/// Two halves, and the split is the point. What an agent may do - find, read, add, append, suggest -
/// is reached from the MCP tools. What is the owner's - done, drop, wait, reopen, edit - is reached
/// only from REST routes with no tool behind them, the same separation by absence the steward's
/// limits use. A Claude Code session the owner is driving calls those routes for them.
/// </para>
/// </summary>
public class OwnerTaskService
{
    private readonly DashboardDbContext _db;
    private readonly ProjectRegistry? _registry;
    private readonly NotificationSender? _notify;
    private readonly HeartbeatManager? _heartbeat;

    public OwnerTaskService(DashboardDbContext db, ProjectRegistry? registry = null,
        NotificationSender? notify = null, HeartbeatManager? heartbeat = null)
        => (_db, _registry, _notify, _heartbeat) = (db, registry, notify, heartbeat);

    public const int TitleMax = 200;
    public const int BodyMax = 8000;
    public const int NoteMax = 4000;

    /// <summary>How far back a done item still counts as a likely duplicate: long enough to catch "already did that".</summary>
    public static readonly TimeSpan RecentlyDone = TimeSpan.FromDays(30);

    private static OwnerTaskReply Refuse(string answer, string why, object? data = null) => new(answer, why, data);

    // ---- reading ------------------------------------------------------------------

    /// <summary>Parse <c>14</c>, <c>OT-14</c> or <c>ot14</c>.</summary>
    public static int? ParseRef(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s.Trim(), @"^(?:OT-?)?(\d+)$", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    public Task<OwnerTask?> GetAsync(int id, CancellationToken ct = default) =>
        _db.OwnerTasks.AsNoTracking().Include(t => t.Notes.OrderBy(n => n.At)).FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>
    /// The list, open first. <paramref name="project"/> narrows to one project and the owner's own
    /// items; "none" is the owner's own alone. <paramref name="status"/> is "open" (open and
    /// waiting, the default), "done", "dropped" or "all".
    /// </summary>
    public async Task<List<OwnerTask>> ListAsync(string? project = null, string? status = null, CancellationToken ct = default)
    {
        var q = _db.OwnerTasks.AsNoTracking().Include(t => t.Notes.OrderBy(n => n.At)).AsQueryable();
        if (project == "none") q = q.Where(t => t.ProjectKey == null);
        else if (!string.IsNullOrWhiteSpace(project)) q = q.Where(t => t.ProjectKey == project || t.ProjectKey == null);
        q = (status ?? "open").ToLowerInvariant() switch
        {
            "all" => q,
            "done" => q.Where(t => t.Status == OwnerTaskStatus.Done),
            "dropped" => q.Where(t => t.Status == OwnerTaskStatus.Dropped),
            _ => q.Where(t => t.Status == OwnerTaskStatus.Open || t.Status == OwnerTaskStatus.Waiting),
        };
        var list = await q.ToListAsync(ct);
        return Order(list).ToList();
    }

    /// <summary>
    /// The order the owner reads the list in: what holds a release, then what a ticket waits on, then
    /// the rest; waiting items after the open ones; oldest first within each.
    /// </summary>
    public static IEnumerable<OwnerTask> Order(IEnumerable<OwnerTask> list) => list
        .OrderBy(t => t.IsOpen ? 0 : 1)
        .ThenBy(t => t.Status == OwnerTaskStatus.Waiting ? 1 : 0)
        .ThenBy(t => t.HoldsRelease ? 0 : t.TicketId is not null ? 1 : 2)
        .ThenBy(t => t.IsOpen ? t.CreatedAt.Ticks : -(t.DoneAt ?? t.DroppedAt ?? t.UpdatedAt).Ticks)
        .ThenBy(t => t.Id);

    public Task<int> OpenCountAsync(CancellationToken ct = default) =>
        _db.OwnerTasks.CountAsync(t => t.Status == OwnerTaskStatus.Open || (t.Status == OwnerTaskStatus.Waiting && t.WaitingUntil != null && t.WaitingUntil <= DateTime.UtcNow), ct);

    /// <summary>
    /// Search the list for something like <paramref name="query"/>: open and waiting items, and those
    /// done in the last month, best match first. What an agent calls before it adds.
    /// </summary>
    public async Task<List<(OwnerTask Task, double Score)>> FindAsync(string? project, string query, CancellationToken ct = default)
    {
        var pool = await PoolAsync(project, ct);
        var q = Words(query);
        var ids = Identifiers(query);
        return pool.Select(t => (t, Score: Likeness(q, ids, t, query)))
            .Where(x => x.Score > 0.15).OrderByDescending(x => x.Score).Take(10).ToList();
    }

    private async Task<List<OwnerTask>> PoolAsync(string? project, CancellationToken ct)
    {
        var since = DateTime.UtcNow - RecentlyDone;
        return await _db.OwnerTasks.AsNoTracking()
            .Where(t => (project == null || t.ProjectKey == project || t.ProjectKey == null)
                && (t.Status == OwnerTaskStatus.Open || t.Status == OwnerTaskStatus.Waiting
                    || (t.Status == OwnerTaskStatus.Done && t.DoneAt >= since)))
            .ToListAsync(ct);
    }

    // ---- likeness -----------------------------------------------------------------

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "this", "that", "from", "into", "onto", "set", "get", "add", "new", "our",
        "need", "needs", "needed", "make", "sure", "before", "after", "release", "owner", "have", "has", "are",
        "will", "can", "you", "your", "its", "not", "all", "any", "one", "use", "using", "run", "via", "per",
    };

    public static HashSet<string> Words(string? s) =>
        Regex.Matches((s ?? "").ToLowerInvariant(), @"[a-z0-9][a-z0-9_\-]{2,}")
            .Select(m => m.Value.TrimEnd('s'))
            .Where(w => w.Length >= 3 && !Stop.Contains(w)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The names that make two items the same thing: an env var, a secret's name, a key slug.</summary>
    public static HashSet<string> Identifiers(string? s) =>
        Regex.Matches(s ?? "", @"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b|\b[a-z]+:[a-z0-9_.\-/]+\b")
            .Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// How alike a new item is to an existing one, 0 to 1. A shared identifier (an env var name, a
    /// key) is as good as the same item; otherwise the overlap of the titles' words, with the body's
    /// words counting for a little.
    /// </summary>
    public static double Likeness(HashSet<string> words, HashSet<string> ids, OwnerTask t, string? raw = null)
    {
        var theirIds = Identifiers(t.Title + "\n" + t.Body);
        if (t.Key is { Length: > 0 } k) theirIds.Add(k.ToLowerInvariant());
        if (ids.Overlaps(theirIds)) return 1.0;
        var title = Words(t.Title);
        if (words.Count == 0 || title.Count == 0) return 0;
        double inter = words.Intersect(title).Count();
        var jaccard = inter / words.Union(title).Count();
        // Containment: a short title wholly inside a longer one is still the same thing.
        var contain = inter / Math.Min(words.Count, title.Count);
        var body = Words(t.Body);
        var bodyHit = body.Count == 0 ? 0 : (double)words.Intersect(body).Count() / words.Count;
        return Math.Max(jaccard, contain * 0.8) * 0.85 + bodyHit * 0.15;
    }

    /// <summary>The longest title an agent may write: one plain line, read at a glance.</summary>
    public const int AgentTitleMax = 110;

    private static readonly Regex WordingTitle = new(
        @"^\W*(read|check|review|look at|confirm|update|reword|change)\b.{0,80}\b(word|words|wording|copy|sentence|sentences|hint|label|text|phrasing)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DocsTitle = new(
        @"\b(line|lines|wording|heading|entry|entries|sentence)\b.{0,60}\b(for|to|in|into)\b.{0,40}(docs?\b|claude\.md|plan\.md|phase-b|\bspec\b|runbook|conventions)"
        + @"|^\W*(add|update|reword|change|paste)\b.{0,80}(claude\.md|plan\.md|phase-b|\.md\b|\bslice \d+\b.{0,20}\b[A-Z]\d\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PathLike = new(@"`|\b[\w.\-]+/[\w.\-/]+\.\w{1,5}\b", RegexOptions.Compiled);

    /// <summary>
    /// Why an agent's item is not a job for the owner's hands, or null when it may be one. The list is
    /// for what needs the owner outside the code - an account, a secret, infrastructure, a deploy, a
    /// test with their own keys - and the two things that used to crowd it out are sent where they go:
    /// wording to the release's reading, and a line for a document to the branch or the release's docs.
    /// The title is the first thing read, so it is plain words: what to do and why, no paths.
    /// </summary>
    public static string? NotAJob(string title)
    {
        if (WordingTitle.IsMatch(title))
            return "Wording to read is not a to-do. A customer-facing word goes in the release's reading as a 'words' note "
                + "(steward_note_for_release), or 'terms' when it is about money, consent or legal terms; words only an admin sees are not noted at all.";
        if (DocsTitle.IsMatch(title))
            return "A line for a document is not a to-do. A worker writes it into a document it may edit, on its branch; a line for "
                + "one it may not (the conventions file, CLAUDE.md) is a 'docs' note for release (steward_note_for_release).";
        if (title.Length > AgentTitleMax)
            return $"The title is one plain line of at most {AgentTitleMax} characters: what to do, and why it matters, in everyday words. The detail goes in the body.";
        if (PathLike.IsMatch(title))
            return "The title is plain words: no file paths and no code. Say what to do and why it matters; paths and commands go in the body.";
        return null;
    }

    /// <summary>At or above this an add is refused as a possible duplicate until the agent rules each candidate out.</summary>
    public const double DuplicateAt = 0.5;

    // ---- an agent's half ----------------------------------------------------------

    /// <summary>
    /// Put something on the list. A matching key lands on the existing item; anything that looks
    /// like an existing one is refused with the candidates until <see cref="OwnerTaskDraft.NotDuplicateOf"/>
    /// names each of them. <paramref name="by"/> is who is asking: "worker", "steward", "owner", "session".
    /// </summary>
    public async Task<OwnerTaskReply> AddAsync(OwnerTaskDraft d, string by, CancellationToken ct = default, bool requireBody = true)
    {
        var title = (d.Title ?? "").Trim();
        var body = (d.Body ?? "").Trim();
        if (title.Length == 0) return Refuse("bad-request", "Say what is to be done, in one line.");
        if (title.Length > TitleMax) return Refuse("bad-request", $"The title is one line, at most {TitleMax} characters. The detail goes in the body.");
        if (body.Length > BodyMax) return Refuse("bad-request", $"The body is at most {BodyMax} characters.");
        var agent = by is "worker" or "steward";
        if (agent && requireBody && body.Length < 40)
            return Refuse("bad-request", "Write the body so a fresh session can carry it out with nothing else: why (what it unblocks), "
                + "where (the console, the account), the steps, how to tell it is done, and where the result goes - the file and the key, never a secret's value.");
        if (agent && NotAJob(title) is { } notAJob) return Refuse("not-a-job", notAJob);
        if (ParseKind(d.Kind) is not { } kind)
            return Refuse("bad-request", "The kind is secret, config, infra, account, deploy or action.");

        var project = string.IsNullOrWhiteSpace(d.Project) || d.Project == "none" ? null : d.Project.Trim();
        if (project is not null && _registry is not null && !_registry.IsEnabled(project))
            return Refuse("no-project", $"There is no enabled project '{project}'. Leave the project out for one of the owner's own.");
        if (d.TicketId is { } tid)
        {
            var t = await _db.Tickets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tid, ct);
            if (t is null) return Refuse("no-ticket", $"There is no ticket {tid}.");
            if (project is null) project = t.ProjectKey;
            else if (t.ProjectKey != project) return Refuse("bad-request", $"Ticket {tid} is {t.ProjectKey}'s, not {project}'s.");
        }
        var key = string.IsNullOrWhiteSpace(d.Key) ? null : d.Key.Trim().ToLowerInvariant();
        if (key is { Length: > 160 }) return Refuse("bad-request", "A key is a short slug, at most 160 characters: kind:name.");

        // The same key on the same project, still open: the same item. This ticket is one more asker.
        if (key is not null)
        {
            var same = await _db.OwnerTasks.FirstOrDefaultAsync(t => t.ProjectKey == project && t.Key == key
                && (t.Status == OwnerTaskStatus.Open || t.Status == OwnerTaskStatus.Waiting), ct);
            if (same is not null)
            {
                await AddNoteAsync(same, d.TicketId is not null && d.TicketId != same.TicketId ? OwnerTaskNoteKind.AlsoAsked : OwnerTaskNoteKind.Context,
                    by, d.TicketId, $"Asked again{(d.TicketId is { } a ? $" by ticket {a}" : "")}: {title}" + (body.Length > 0 && body != same.Body ? $"\n\n{body}" : ""), ct);
                if (d.HoldsRelease && !same.HoldsRelease) { same.HoldsRelease = true; same.Unit ??= d.Unit; }
                await _db.SaveChangesAsync(ct);
                return new OwnerTaskReply("merged", $"{same.Ref} is already on the list with this key; this ask is logged on it.", Summary(same));
            }
        }

        // Anything that looks like it: refused until each candidate is ruled out by name.
        var words = Words(title + " " + key);
        var ids = Identifiers(title + "\n" + body + "\n" + key);
        var ruledOut = (d.NotDuplicateOf ?? Array.Empty<int>()).ToHashSet();
        var like = (await PoolAsync(project, ct))
            .Select(t => (t, s: Likeness(words, ids, t)))
            .Where(x => x.s >= DuplicateAt && !ruledOut.Contains(x.t.Id))
            .OrderByDescending(x => x.s).Take(5).ToList();
        if (like.Count > 0)
            return Refuse("possible-duplicate",
                "This looks like something already on the list. Read each candidate (owner_todo_get). If it is the same thing, add to it with "
                + "owner_todo_append instead. If it is not, add again with notDuplicateOf naming every candidate below.",
                new { candidates = like.Select(x => new { @ref = x.t.Ref, x.t.Id, x.t.Title, status = x.t.Status.ToString(), project = x.t.ProjectKey, likeness = Math.Round(x.s, 2) }) });

        var task = new OwnerTask
        {
            ProjectKey = project, Kind = kind, Title = title, Body = body, Key = key,
            TicketId = d.TicketId, Unit = d.Unit, HoldsRelease = d.HoldsRelease, CreatedBy = by,
        };
        _db.OwnerTasks.Add(task);
        task.Notes.Add(new OwnerTaskNote { Kind = OwnerTaskNoteKind.Added, By = by, TicketId = d.TicketId,
            Text = d.TicketId is { } from ? $"Added from ticket {from}." : "Added." });
        await _db.SaveChangesAsync(ct);

        // A new thing that holds something up is worth a push; one the owner wrote themself is not news to them.
        if (agent && (task.HoldsRelease || task.TicketId is not null) && _notify is not null)
        {
            var name = project is null ? "" : (_registry?.IsEnabled(project) == true ? _registry.GetProjectConfig(project).DisplayName : project) + ": ";
            var holds = task.HoldsRelease ? "Holds a release." : $"Asked for by ticket {task.TicketId}.";
            var o = await _notify.SendAsync("owner_todo", $"{name}{task.Ref} is yours to do",
                $"{task.Title}\n\n{holds}\n\nIn Claude Code: /todo {task.Ref}", ct);
            task.Notice = o.Sent ? "sent" : Head(o.Message, 280);
            await _db.SaveChangesAsync(ct);
        }
        return new OwnerTaskReply("added", null, Summary(task));
    }

    /// <summary>More on an item: a finding, a link, a correction - or another ticket asking for the same thing.</summary>
    public async Task<OwnerTaskReply> AppendAsync(int id, string text, int? ticketId, string by, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return Refuse("bad-request", "Say what to add.");
        if (text.Length > NoteMax) return Refuse("bad-request", $"A note is at most {NoteMax} characters.");
        var t = await _db.OwnerTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Refuse("not-found", $"There is no OT-{id}.");
        if (ticketId is { } tid && !await _db.Tickets.AnyAsync(x => x.Id == tid, ct)) return Refuse("no-ticket", $"There is no ticket {tid}.");
        var kind = by is "owner" or "session" ? OwnerTaskNoteKind.Owner
            : ticketId is not null && ticketId != t.TicketId ? OwnerTaskNoteKind.AlsoAsked : OwnerTaskNoteKind.Context;
        await AddNoteAsync(t, kind, by, ticketId, text, ct);
        await _db.SaveChangesAsync(ct);
        return new OwnerTaskReply("noted", t.IsOpen ? null : $"{t.Ref} is {t.Status.ToString().ToLowerInvariant()}; the note is logged, and the owner decides whether it needs reopening.", Summary(t));
    }

    /// <summary>An agent's word that an item looks done ("done") or no longer needed ("obsolete"), with its evidence.</summary>
    public async Task<OwnerTaskReply> SuggestAsync(int id, string suggestion, string because, string by, CancellationToken ct = default)
    {
        var word = (suggestion ?? "").Trim().ToLowerInvariant();
        if (word is not ("done" or "obsolete")) return Refuse("bad-request", "Suggest 'done' or 'obsolete'.");
        because = (because ?? "").Trim();
        if (because.Length < 10) return Refuse("bad-request", "Say what you saw that makes it so: the file, the command and what it printed.");
        var t = await _db.OwnerTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Refuse("not-found", $"There is no OT-{id}.");
        if (!t.IsOpen) return new OwnerTaskReply("recorded", $"{t.Ref} is already {t.Status.ToString().ToLowerInvariant()}.");
        (t.Suggestion, t.SuggestionBecause, t.SuggestedAt) = (word, Head(because, 1000), DateTime.UtcNow);
        await AddNoteAsync(t, OwnerTaskNoteKind.Suggested, by, null, $"Looks {word}: {because}", ct);
        await _db.SaveChangesAsync(ct);
        return new OwnerTaskReply("recorded", "The owner decides; it stays on the list until they do.", Summary(t));
    }

    // ---- the owner's half: REST routes only -----------------------------------------

    /// <summary>
    /// The owner did it. The note is required - what was done, and where the result lives - because
    /// it is what the ticket that asked is told, and what a later session reads to find the thing again.
    /// </summary>
    public async Task<OwnerTaskReply> DoneAsync(int id, string? note, string by = "owner", CancellationToken ct = default)
    {
        // The note is the owner's choice: what they did and where the result lives helps the ticket that
        // asked, but a job that speaks for itself is marked done with a click.
        note = (note ?? "").Trim();
        if (note.Length > NoteMax) return Refuse("bad-request", $"At most {NoteMax} characters.");
        var t = await _db.OwnerTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Refuse("not-found", $"There is no OT-{id}.");
        if (t.Status == OwnerTaskStatus.Done) return new OwnerTaskReply("recorded", $"{t.Ref} was already done.", Summary(t));
        var now = DateTime.UtcNow;
        (t.Status, t.DoneAt, t.DoneNote, t.DroppedAt, t.DroppedBecause) = (OwnerTaskStatus.Done, now, note.Length == 0 ? null : note, null, null);
        ClearSuggestion(t);
        await AddNoteAsync(t, OwnerTaskNoteKind.Owner, by, null, note.Length == 0 ? "Done." : $"Done: {note}", ct);
        await _db.SaveChangesAsync(ct);
        await TellAsync(t, note.Length == 0 ? $"The owner did {t.Ref} ({t.Title})." : $"The owner did {t.Ref} ({t.Title}): {note}", $"{t.Ref} done", ct);
        return new OwnerTaskReply("recorded", null, Summary(t));
    }

    public async Task<OwnerTaskReply> DropAsync(int id, string? because, string by = "owner", CancellationToken ct = default)
    {
        because = (because ?? "").Trim();
        if (because.Length == 0) return Refuse("bad-request", "Say why it is no longer needed.");
        var t = await _db.OwnerTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Refuse("not-found", $"There is no OT-{id}.");
        (t.Status, t.DroppedAt, t.DroppedBecause) = (OwnerTaskStatus.Dropped, DateTime.UtcNow, Head(because, 1000));
        ClearSuggestion(t);
        await AddNoteAsync(t, OwnerTaskNoteKind.Owner, by, null, $"Dropped: {because}", ct);
        await _db.SaveChangesAsync(ct);
        await TellAsync(t, $"The owner dropped {t.Ref} ({t.Title}) as no longer needed: {because}", $"{t.Ref} dropped", ct);
        return new OwnerTaskReply("recorded", null, Summary(t));
    }

    public async Task<OwnerTaskReply> WaitAsync(int id, string? on, DateTime? until, string by = "owner", CancellationToken ct = default)
    {
        on = (on ?? "").Trim();
        if (on.Length == 0) return Refuse("bad-request", "Say who or what it is waiting on.");
        var t = await _db.OwnerTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Refuse("not-found", $"There is no OT-{id}.");
        if (!t.IsOpen) return Refuse("closed", $"{t.Ref} is {t.Status.ToString().ToLowerInvariant()}. Reopen it first.");
        (t.Status, t.WaitingOn, t.WaitingUntil) = (OwnerTaskStatus.Waiting, Head(on, 300), until?.ToUniversalTime());
        await AddNoteAsync(t, OwnerTaskNoteKind.Owner, by, null,
            $"Waiting on {on}{(until is { } u ? $", look again {u:yyyy-MM-dd}" : "")}.", ct);
        await _db.SaveChangesAsync(ct);
        return new OwnerTaskReply("recorded", null, Summary(t));
    }

    public async Task<OwnerTaskReply> ReopenAsync(int id, string? why, string by = "owner", CancellationToken ct = default)
    {
        var t = await _db.OwnerTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Refuse("not-found", $"There is no OT-{id}.");
        if (t.Status == OwnerTaskStatus.Open) return new OwnerTaskReply("recorded", $"{t.Ref} is already open.", Summary(t));
        (t.Status, t.WaitingOn, t.WaitingUntil, t.DoneAt, t.DoneNote, t.DroppedAt, t.DroppedBecause) =
            (OwnerTaskStatus.Open, null, null, null, null, null, null);
        await AddNoteAsync(t, OwnerTaskNoteKind.Owner, by, null, string.IsNullOrWhiteSpace(why) ? "Reopened." : $"Reopened: {why.Trim()}", ct);
        await _db.SaveChangesAsync(ct);
        return new OwnerTaskReply("recorded", null, Summary(t));
    }

    /// <summary>The owner rewrites an item. Any field left null is kept; an empty project makes it the owner's own.</summary>
    public async Task<OwnerTaskReply> EditAsync(int id, string? title, string? body, string? kind, string? project,
        bool? holdsRelease, string by = "owner", CancellationToken ct = default)
    {
        var t = await _db.OwnerTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Refuse("not-found", $"There is no OT-{id}.");
        var changed = new List<string>();
        if (title is not null)
        {
            title = title.Trim();
            if (title.Length is 0 or > TitleMax) return Refuse("bad-request", $"The title is one line of 1 to {TitleMax} characters.");
            if (title != t.Title) { t.Title = title; changed.Add("title"); }
        }
        if (body is not null)
        {
            body = body.Trim();
            if (body.Length > BodyMax) return Refuse("bad-request", $"The body is at most {BodyMax} characters.");
            if (body != t.Body) { t.Body = body; changed.Add("body"); }
        }
        if (kind is not null)
        {
            if (ParseKind(kind) is not { } k) return Refuse("bad-request", "The kind is secret, config, infra, account, deploy or action.");
            if (k != t.Kind) { t.Kind = k; changed.Add("kind"); }
        }
        if (project is not null)
        {
            var p = project.Trim() is "" or "none" ? null : project.Trim();
            if (p is not null && _registry is not null && !_registry.IsEnabled(p)) return Refuse("no-project", $"There is no enabled project '{p}'.");
            if (p != t.ProjectKey) { t.ProjectKey = p; changed.Add("project"); }
        }
        if (holdsRelease is { } h && h != t.HoldsRelease) { t.HoldsRelease = h; changed.Add(h ? "now holds its release" : "no longer holds its release"); }
        if (changed.Count == 0) return new OwnerTaskReply("recorded", "Nothing changed.", Summary(t));
        t.UpdatedAt = DateTime.UtcNow;
        await AddNoteAsync(t, OwnerTaskNoteKind.Owner, by, null, $"Edited: {string.Join(", ", changed)}.", ct);
        await _db.SaveChangesAsync(ct);
        return new OwnerTaskReply("recorded", null, Summary(t));
    }

    /// <summary>
    /// The owner says an item is not a job for their hands but reading for a release: it becomes a
    /// release note of <paramref name="kind"/> on its ticket (words, terms, docs, note or migration),
    /// and the item is dropped with where it went. Quiet: the tickets that asked are not told, since
    /// nothing was done - it only moved.
    /// </summary>
    public async Task<OwnerTaskReply> ToReleaseNoteAsync(int id, string? kind, string by = "owner", CancellationToken ct = default)
    {
        var t = await _db.OwnerTasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Refuse("not-found", $"There is no OT-{id}.");
        if (!t.IsOpen) return Refuse("closed", $"{t.Ref} is {t.Status.ToString().ToLowerInvariant()}.");
        if (t.ProjectKey is null) return Refuse("bad-request", "One of your own items has no project, so no release to go out with.");
        if (StewardService.ParseRelease(kind) is not { } rk || rk is StewardReleaseKind.Config or StewardReleaseKind.Action)
            return Refuse("bad-request", "Move it to words, terms, docs, note or migration. Config and action are jobs, which is what the list is for.");
        var body = t.Body.Trim();
        var text = body.StartsWith(t.Title, StringComparison.Ordinal) || body.Length == 0 ? (body.Length == 0 ? t.Title : body) : $"{t.Title}\n\n{body}";
        _db.StewardEntries.Add(new StewardEntry { ProjectKey = t.ProjectKey, Kind = StewardEntryKind.BeforeRelease, TicketId = t.TicketId, Text = text, Release = rk });
        var word = rk.ToString().ToLowerInvariant();
        (t.Status, t.DroppedAt, t.DroppedBecause) = (OwnerTaskStatus.Dropped, DateTime.UtcNow, $"Not a job for your hands: moved to the release notes as {word}.");
        ClearSuggestion(t);
        await AddNoteAsync(t, OwnerTaskNoteKind.Owner, by, null, $"Moved to the release notes as {word}.", ct);
        await _db.SaveChangesAsync(ct);
        return new OwnerTaskReply("recorded", null, Summary(t));
    }

    // ---- the release gate ---------------------------------------------------------

    /// <summary>
    /// The items that hold a release on one project, open or not, for the steward's release view to
    /// place against its slices the way it places release notes.
    /// </summary>
    public Task<List<OwnerTask>> HoldingReleaseAsync(string projectKey, CancellationToken ct = default) =>
        _db.OwnerTasks.AsNoTracking().Where(t => t.ProjectKey == projectKey && t.HoldsRelease).ToListAsync(ct);

    /// <summary>
    /// Move a config or action release note onto the list: the note row goes, and an item that holds
    /// its release takes its place - open if the note was not ticked off, done if it was.
    /// </summary>
    public async Task<OwnerTask> FromReleaseNoteAsync(StewardEntry e, string by, CancellationToken ct = default)
    {
        var text = e.Text.Trim();
        var task = new OwnerTask
        {
            ProjectKey = e.ProjectKey,
            Kind = e.Release == StewardReleaseKind.Config ? OwnerTaskKind.Config : OwnerTaskKind.Action,
            Title = Head(text, TitleMax),
            Body = text,
            TicketId = e.TicketId,
            HoldsRelease = true,
            CreatedBy = by,
            CreatedAt = e.At,
            UpdatedAt = DateTime.UtcNow,
        };
        if (e.HandledAt is { } h) (task.Status, task.DoneAt, task.DoneNote) = (OwnerTaskStatus.Done, h, "Ticked off as a release note.");
        task.Notes.Add(new OwnerTaskNote { Kind = OwnerTaskNoteKind.Added, By = by, TicketId = e.TicketId, At = e.At,
            Text = $"Moved from a {e.Release?.ToString().ToLowerInvariant()} release note{(e.TicketId is { } t ? $" on ticket {t}" : "")}." });
        _db.OwnerTasks.Add(task);
        _db.StewardEntries.Remove(e);
        await _db.SaveChangesAsync(ct);
        return task;
    }

    // ---- what follows from the owner's word ------------------------------------------

    /// <summary>
    /// Tell the tickets that asked, and the project's steward. An owner note on each ticket that is
    /// not finished is what the next run there reads; a steward wake-up is what picks up the release
    /// or the blocked ticket it was waiting on.
    /// </summary>
    private async Task TellAsync(OwnerTask t, string note, string reason, CancellationToken ct)
    {
        var askers = t.Notes.Count > 0 ? t.Notes : await _db.OwnerTaskNotes.Where(n => n.OwnerTaskId == t.Id).ToListAsync(ct);
        var ids = askers.Select(n => n.TicketId).Append(t.TicketId).OfType<int>().Distinct().ToList();
        if (ids.Count > 0)
        {
            var open = await _db.Tickets.Where(x => ids.Contains(x.Id) && x.Phase != TicketPhase.Done).ToListAsync(ct);
            foreach (var ticket in open)
            {
                _db.Comments.Add(new Comment { TicketId = ticket.Id, Body = note, Author = AuthorKind.Human, Phase = ticket.Phase });
                ticket.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
        }
        if (t.ProjectKey is { } p)
        {
            await StewardDue.MarkAsync(_db, p, reason);
            await _db.SaveChangesAsync(ct);
            _heartbeat?.Wake();
        }
    }

    private async Task AddNoteAsync(OwnerTask t, OwnerTaskNoteKind kind, string by, int? ticketId, string text, CancellationToken ct)
    {
        var n = new OwnerTaskNote { OwnerTaskId = t.Id, Kind = kind, By = by, TicketId = ticketId, Text = text.Trim() };
        if (_db.Entry(t).Collection(x => x.Notes).IsLoaded) t.Notes.Add(n); else _db.OwnerTaskNotes.Add(n);
        t.UpdatedAt = DateTime.UtcNow;
        await Task.CompletedTask;
    }

    private static void ClearSuggestion(OwnerTask t) => (t.Suggestion, t.SuggestionBecause, t.SuggestedAt) = (null, null, null);

    public static OwnerTaskKind? ParseKind(string? s) => string.IsNullOrWhiteSpace(s) ? OwnerTaskKind.Action
        : Enum.TryParse<OwnerTaskKind>(s.Trim(), ignoreCase: true, out var k) && Enum.IsDefined(k) ? k : null;

    private static string Head(string s, int max)
    {
        var line = s.Trim().Split('\n')[0].Trim().TrimStart('#', '-', '*', ' ');
        return line.Length > max ? line[..(max - 3)] + "..." : line;
    }

    // ---- what is shown ------------------------------------------------------------

    /// <summary>An item in a line or two, for a tool's answer.</summary>
    public static object Summary(OwnerTask t) => new
    {
        @ref = t.Ref, t.Id, t.Title, kind = t.Kind.ToString().ToLowerInvariant(), status = t.Status.ToString().ToLowerInvariant(),
        project = t.ProjectKey, t.TicketId, t.HoldsRelease,
    };

    /// <summary>An item in full, for the page and for a tool's get.</summary>
    public static object Detail(OwnerTask t, OwnerTaskSlice? slice = null) => new
    {
        // Which slice's release it holds, worked out the way the gate does; null when it holds none or none can be placed.
        slice = slice is null ? null : new { unit = slice.Unit, title = slice.Title },
        @ref = t.Ref, t.Id, project = t.ProjectKey, kind = t.Kind.ToString().ToLowerInvariant(),
        status = t.Status.ToString().ToLowerInvariant(), t.Title, t.Body, t.Key,
        t.WaitingOn, t.WaitingUntil, t.TicketId, t.Unit, t.HoldsRelease, t.CreatedBy, t.CreatedAt, t.UpdatedAt,
        t.DoneAt, t.DoneNote, t.DroppedAt, t.DroppedBecause, t.Suggestion, t.SuggestionBecause, t.SuggestedAt, t.Notice,
        askers = t.Notes.Select(n => n.TicketId).Append(t.TicketId).OfType<int>().Distinct(),
        notes = t.Notes.OrderBy(n => n.At).Select(n => new { n.Id, kind = n.Kind.ToString(), n.By, n.TicketId, n.Text, n.At }),
    };

    /// <summary>
    /// One item as a Claude Code session reads it: everything needed to do it, and how to say it is done.
    /// </summary>
    public static string Markdown(OwnerTask t, string boardUrl, OwnerTaskSlice? slice = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {t.Ref}: {t.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Status:** {t.Status.ToString().ToLowerInvariant()}{(t.Status == OwnerTaskStatus.Waiting ? $" on {t.WaitingOn}{(t.WaitingUntil is { } u ? $", look again {u:yyyy-MM-dd}" : "")}" : "")}");
        sb.AppendLine($"- **Kind:** {t.Kind.ToString().ToLowerInvariant()}");
        sb.AppendLine($"- **Project:** {t.ProjectKey ?? "(the owner's own)"}");
        if (t.TicketId is { } tid) sb.AppendLine($"- **Asked for by:** ticket {tid}{Also(t)}");
        if (t.HoldsRelease) sb.AppendLine($"- **Holds:** the release of {(slice is not null ? slice.ToString() : t.Unit is { } u2 ? $"slice {u2:00}" : "the next slice released")}");
        if (t.Key is { } k) sb.AppendLine($"- **Key:** `{k}`");
        sb.AppendLine($"- **Added:** {t.CreatedAt:yyyy-MM-dd} by {t.CreatedBy}");
        if (t.Suggestion is { } s) sb.AppendLine($"- **An agent says it looks {s}:** {t.SuggestionBecause}");
        if (t.DoneNote is { } dn) sb.AppendLine($"- **Done {t.DoneAt:yyyy-MM-dd}:** {dn}");
        if (t.DroppedBecause is { } db) sb.AppendLine($"- **Dropped {t.DroppedAt:yyyy-MM-dd}:** {db}");
        sb.AppendLine();
        sb.AppendLine(t.Body.Length > 0 ? t.Body : "_(no detail written)_");
        var log = t.Notes.Where(n => n.Kind != OwnerTaskNoteKind.Added).OrderBy(n => n.At).ToList();
        if (log.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Log");
            foreach (var n in log)
                sb.AppendLine($"- {n.At:yyyy-MM-dd} {n.By}{(n.TicketId is { } nt ? $" (ticket {nt})" : "")}: {n.Text.Replace("\n", "\n  ")}");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("Secrets: never paste a value into this item or a chat. The owner pastes it where Hand back says.");
        sb.AppendLine($"When it is done: `curl -s -X POST {boardUrl}/api/owner-todos/{t.Id}/done -H 'Content-Type: application/json' -d '{{\"note\":\"what was done and where the result lives\"}}'`");
        sb.AppendLine($"Waiting on someone: `POST {boardUrl}/api/owner-todos/{t.Id}/wait` with `{{\"on\":\"...\",\"until\":\"YYYY-MM-DD\"}}`. No longer needed: `POST .../{t.Id}/drop` with `{{\"because\":\"...\"}}`.");
        return sb.ToString();
    }

    private static string Also(OwnerTask t)
    {
        var also = t.Notes.Where(n => n.Kind == OwnerTaskNoteKind.AlsoAsked).Select(n => n.TicketId).OfType<int>().Distinct().Where(x => x != t.TicketId).ToList();
        return also.Count == 0 ? "" : $", and {string.Join(", ", also.Select(a => $"ticket {a}"))}";
    }

    /// <summary>The open list as a session reads it: grouped by what each item holds up.</summary>
    public static string ListMarkdown(IReadOnlyList<OwnerTask> list, string boardUrl,
        IReadOnlyDictionary<int, OwnerTaskSlice>? slices = null)
    {
        var sb = new StringBuilder();
        var open = list.Where(t => t.IsOpen).ToList();
        sb.AppendLine($"# The owner's to-do list: {open.Count} open");
        sb.AppendLine();
        void Group(string heading, IEnumerable<OwnerTask> items)
        {
            var l = items.ToList();
            if (l.Count == 0) return;
            sb.AppendLine($"## {heading}");
            foreach (var t in l)
                sb.AppendLine($"- **{t.Ref}** [{t.Kind.ToString().ToLowerInvariant()}] {t.Title} - {t.ProjectKey ?? "own"}"
                    + (t.TicketId is { } x ? $", ticket {x}" : "")
                    + (t.HoldsRelease && slices?.GetValueOrDefault(t.Id) is { } sl ? $", holds {sl}" : "")
                    + (t.Suggestion is { } s ? $" _(an agent says it looks {s})_" : ""));
            sb.AppendLine();
        }
        Group("Holds a release", open.Where(t => t.Status == OwnerTaskStatus.Open && t.HoldsRelease));
        Group("A ticket asked for it", open.Where(t => t.Status == OwnerTaskStatus.Open && !t.HoldsRelease && t.TicketId is not null));
        Group("The rest", open.Where(t => t.Status == OwnerTaskStatus.Open && !t.HoldsRelease && t.TicketId is null));
        Group("Waiting on someone", open.Where(t => t.Status == OwnerTaskStatus.Waiting));
        if (open.Count == 0) sb.AppendLine("Nothing open.");
        sb.AppendLine($"Read one in full: `curl -s {boardUrl}/api/owner-todos/<id>/text`");
        return sb.ToString();
    }
}
