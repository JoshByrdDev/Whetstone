using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// Bug tracing (docs/bugs-and-experiments-plan.md, section 1): which commits each ticket delivered,
/// where a bug ticket's fix came from, and the owner's verdict on each suggestion.
/// <para>
/// The board suggests; only a person decides. Every method that changes a link's status is called
/// from a REST route and from nothing else - no MCP tool reaches this service, and
/// <c>HumanOnlyLimitTests</c> holds that true.
/// </para>
/// </summary>
public class BugTraceService
{
    /// <summary>The stages a bug can be said to have slipped past, by skill name.</summary>
    public static readonly string[] Stages = { "speculate", "implement", "qa-test", "refine", "review" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DashboardDbContext _db;
    private readonly TicketGitService _git;

    public BugTraceService(DashboardDbContext db, TicketGitService git) => (_db, _git) = (db, git);

    /// <summary>One introducing commit of a link, as stored in <see cref="BugLink.CommitsJson"/>.</summary>
    public record LinkCommit(string Repo, string Sha, string Subject, double Lines, IReadOnlyList<string> Files);

    public static IReadOnlyList<LinkCommit> CommitsOf(BugLink link)
    {
        if (string.IsNullOrEmpty(link.CommitsJson)) return [];
        try { return JsonSerializer.Deserialize<List<LinkCommit>>(link.CommitsJson, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    // ---- Marking ----------------------------------------------------------------------

    public async Task<Ticket?> SetBugAsync(int ticketId, bool isBug)
    {
        var t = await _db.Tickets.FindAsync(ticketId);
        if (t is null) return null;
        t.IsBug = isBug;
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return t;
    }

    // ---- What each ticket delivered ---------------------------------------------------

    /// <summary>
    /// Record the commits a ticket delivered: its board merge's when it has one, else its branch's
    /// against the base. Idempotent - a commit already recorded for the ticket is left as it is.
    /// </summary>
    public async Task<int> RecordDeliveredAsync(int ticketId, CancellationToken ct = default)
    {
        var t = await _db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null || t.Track != TicketTrack.Delivery || t.Walkthrough) return 0;

        var delivered = await _git.DeliveredAsync(t.ProjectKey, t.MergeSha, t.Branch, ct);
        if (delivered.Count == 0) return 0;

        var known = (await _db.TicketCommits.Where(c => c.TicketId == ticketId).Select(c => new { c.Repo, c.Sha }).ToListAsync(ct))
            .Select(c => (c.Repo, c.Sha)).ToHashSet();
        var added = 0;
        foreach (var (repo, source, c) in delivered)
        {
            if (!known.Add((repo, c.Sha))) continue;
            _db.TicketCommits.Add(new TicketCommit
            {
                TicketId = ticketId, ProjectKey = t.ProjectKey, Repo = repo, Sha = c.Sha,
                PatchId = c.PatchId, Subject = Clip(c.Subject, 300), Source = source,
            });
            added++;
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// The first trace in a project records what every ticket the board merged before this table
    /// existed delivered, so their commits map back to them.
    /// </summary>
    public async Task BackfillAsync(string projectKey, CancellationToken ct = default)
    {
        var withRows = _db.TicketCommits.Where(c => c.ProjectKey == projectKey).Select(c => c.TicketId);
        var missing = await _db.Tickets
            .Where(t => t.ProjectKey == projectKey && t.Phase == TicketPhase.Done
                        && (t.MergeSha != null || t.Branch != null) && !withRows.Contains(t.Id))
            .Select(t => t.Id).ToListAsync(ct);
        foreach (var id in missing) await RecordDeliveredAsync(id, ct);
    }

    // ---- Tracing ------------------------------------------------------------------------

    /// <summary>
    /// Blame a bug ticket's fix and write what it found as suggestions. Replaces the suggestions an
    /// earlier trace wrote; never touches a link the owner confirmed or rejected.
    /// </summary>
    public async Task<BugTrace?> TraceAsync(int bugTicketId, CancellationToken ct = default)
    {
        var bug = await _db.Tickets.FirstOrDefaultAsync(t => t.Id == bugTicketId, ct);
        if (bug is null || !bug.IsBug) return null;

        await RecordDeliveredAsync(bug.Id, ct);
        await BackfillAsync(bug.ProjectKey, ct);

        var recorded = (await _db.TicketCommits.Where(c => c.TicketId == bug.Id).ToListAsync(ct))
            .GroupBy(c => c.Repo)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.Sha).ToList());
        var ranges = await _git.FixRangesAsync(bug.ProjectKey, bug.MergeSha, bug.Branch, recorded, ct);

        var blamed = new List<BlamedCommit>();
        foreach (var r in ranges) blamed.AddRange(await _git.BlameFixAsync(r, ct));

        // Commits to tickets: by sha, then by patch id, then by the subject the squash rule writes.
        var index = await _db.TicketCommits.Where(c => c.ProjectKey == bug.ProjectKey).ToListAsync(ct);
        var bySha = index.GroupBy(c => (c.Repo, c.Sha)).ToDictionary(g => g.Key, g => g.First().TicketId);
        var byPatch = index.Where(c => c.PatchId != null).GroupBy(c => (c.Repo, c.PatchId!))
            .ToDictionary(g => g.Key, g => g.Select(c => c.TicketId).Distinct().ToList());
        var keyed = await _db.Tickets
            .Where(t => t.ProjectKey == bug.ProjectKey && t.IssueKey != null && t.Id != bug.Id)
            .Select(t => new { t.Id, t.IssueKey }).ToListAsync(ct);

        int? TicketOf(BlamedCommit c, string? patchId)
        {
            if (bySha.TryGetValue((c.Repo, c.Sha), out var id)) return id;
            if (patchId is not null && byPatch.TryGetValue((c.Repo, patchId), out var ids) && ids.Count == 1) return ids[0];
            var subject = c.Subject.Trim();
            var hit = keyed.Where(k => subject.StartsWith(k.IssueKey + ":", StringComparison.OrdinalIgnoreCase)
                                       || subject.Contains("AB#" + k.IssueKey, StringComparison.OrdinalIgnoreCase)).ToList();
            return hit.Count == 1 ? hit[0].Id : null;
        }

        var cfg = ranges.ToDictionary(r => r.Repo, r => r.Path);
        var groups = new Dictionary<int, List<(BlamedCommit Commit, double Weight)>>();   // ticket id, or 0 for unmapped
        foreach (var c in blamed)
        {
            string? patch = null;
            if (!bySha.ContainsKey((c.Repo, c.Sha)) && cfg.TryGetValue(c.Repo, out var path))
                patch = await _git.PatchIdAsync(path, c.Sha, ct);
            var ticket = TicketOf(c, patch) ?? 0;
            if (ticket == bug.Id) continue;
            (groups.TryGetValue(ticket, out var g) ? g : groups[ticket] = new()).Add((c, c.Weight));
        }

        var totalLines = groups.Values.SelectMany(g => g).Sum(x => x.Weight);
        var existing = await _db.BugLinks.Where(l => l.BugTicketId == bug.Id).ToListAsync(ct);
        _db.BugLinks.RemoveRange(existing.Where(l => l.Status == BugLinkStatus.Suggested && l.Origin == BugLinkOrigin.Blame));

        foreach (var (ticketId, commits) in groups)
        {
            int? source = ticketId == 0 ? null : ticketId;
            var lines = commits.Sum(x => x.Weight);
            var json = JsonSerializer.Serialize(commits
                .Select(x => new LinkCommit(x.Commit.Repo, x.Commit.Sha, x.Commit.Subject, x.Weight, x.Commit.Files))
                .OrderByDescending(x => x.Lines).ToList(), Json);
            var decided = existing.FirstOrDefault(l => l.SourceTicketId == source && l.Status != BugLinkStatus.Suggested);
            if (decided is not null)
            {
                // The owner's verdict stands; the evidence under it is refreshed.
                decided.Lines = lines; decided.Share = totalLines > 0 ? lines / totalLines : 0;
                decided.CommitsJson = json; decided.Neighbours = commits.All(x => x.Commit.Neighbours);
                continue;
            }
            _db.BugLinks.Add(new BugLink
            {
                BugTicketId = bug.Id, SourceTicketId = source, ProjectKey = bug.ProjectKey,
                Status = BugLinkStatus.Suggested, Origin = BugLinkOrigin.Blame,
                Lines = lines, Share = totalLines > 0 ? lines / totalLines : 0,
                CommitsJson = json, Neighbours = commits.All(x => x.Commit.Neighbours),
                Escaped = source is int s && await IsEscapedAsync(s, bug.CreatedAt, ct),
                TracedAt = DateTime.UtcNow,
            });
        }

        var trace = await _db.BugTraces.FindAsync(new object[] { bug.Id }, ct);
        if (trace is null) { trace = new BugTrace { TicketId = bug.Id }; _db.BugTraces.Add(trace); }
        trace.TracedAt = DateTime.UtcNow;
        trace.LinesBlamed = totalLines;
        trace.RangesJson = JsonSerializer.Serialize(ranges.Select(r => new { repo = r.Repo, parent = r.Parent, tip = r.Tip }), Json);
        trace.Note = ranges.Count == 0
            ? "Nothing to blame: the fix has no merge here, its branch is gone, and no delivered commits were recorded for it."
            : blamed.Count == 0
                ? "The fix changed only files that are generated or vendored, or lines git could not blame."
                : groups.Keys.All(k => k == 0)
                    ? "Every blamed line came from commits the board did not deliver (written by hand, or before the board)."
                    : null;
        await _db.SaveChangesAsync(ct);
        return trace;
    }

    /// <summary>
    /// An escaped bug: one traced to a ticket that was already merged or Done when the bug was filed.
    /// The one definition the board, the experiments' guardrail and the analytics all use.
    /// </summary>
    public static bool IsEscaped(DateTime? sourceMergedAt, DateTime? sourceDoneAt, DateTime bugFiledAt)
    {
        var landed = sourceMergedAt ?? sourceDoneAt;
        return landed is not null && landed <= bugFiledAt;
    }

    private async Task<bool> IsEscapedAsync(int sourceTicketId, DateTime bugFiledAt, CancellationToken ct)
    {
        var src = await _db.Tickets.Where(t => t.Id == sourceTicketId).Select(t => new { t.MergedAt }).FirstOrDefaultAsync(ct);
        if (src is null) return false;
        var doneAt = await _db.PhaseTransitions.Where(p => p.TicketId == sourceTicketId && p.To == TicketPhase.Done)
            .OrderBy(p => p.At).Select(p => (DateTime?)p.At).FirstOrDefaultAsync(ct);
        return IsEscaped(src.MergedAt, doneAt, bugFiledAt);
    }

    // ---- The owner's verdicts ------------------------------------------------------------

    /// <summary>Thrown for a verdict that cannot be recorded as asked; the route answers 400 with it.</summary>
    public class LinkRefused(string message) : Exception(message);

    public async Task<BugLink?> ConfirmAsync(int linkId, BugFoundBy foundBy, string missedBy, string? note)
    {
        var link = await _db.BugLinks.FindAsync(linkId);
        if (link is null) return null;
        var stage = Stage(missedBy);
        if (link.SourceTicketId is null)
            throw new LinkRefused("These lines came from commits the board did not deliver, so there is no ticket to confirm as the cause. Name the ticket instead, or reject this.");
        var bug = await _db.Tickets.FindAsync(link.BugTicketId);
        link.Status = BugLinkStatus.Confirmed;
        link.FoundBy = foundBy;
        link.MissedBy = stage;
        link.Note = Clip(note, 2000);
        link.DecidedAt = DateTime.UtcNow;
        link.Escaped = bug is not null && await IsEscapedAsync(link.SourceTicketId.Value, bug.CreatedAt, default);
        await _db.SaveChangesAsync();
        return link;
    }

    public async Task<BugLink?> RejectAsync(int linkId, string? note)
    {
        var link = await _db.BugLinks.FindAsync(linkId);
        if (link is null) return null;
        link.Status = BugLinkStatus.Rejected;
        link.Note = Clip(note, 2000);
        link.DecidedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return link;
    }

    public async Task<BugLink?> ReopenAsync(int linkId)
    {
        var link = await _db.BugLinks.FindAsync(linkId);
        if (link is null) return null;
        link.Status = BugLinkStatus.Suggested;
        link.FoundBy = null; link.MissedBy = null; link.DecidedAt = null;
        await _db.SaveChangesAsync();
        return link;
    }

    /// <summary>"The cause is ticket N", confirmed in one step - the owner naming what the trace did not.</summary>
    public async Task<BugLink?> AddManualAsync(int bugTicketId, int sourceTicketId, BugFoundBy foundBy, string missedBy, string? note)
    {
        var bug = await _db.Tickets.FindAsync(bugTicketId);
        if (bug is null) return null;
        if (sourceTicketId == bugTicketId) throw new LinkRefused("A bug cannot be its own cause.");
        var src = await _db.Tickets.FindAsync(sourceTicketId) ?? throw new LinkRefused($"There is no ticket #{sourceTicketId}.");
        if (src.ProjectKey != bug.ProjectKey) throw new LinkRefused($"#{sourceTicketId} is in another project.");
        var stage = Stage(missedBy);

        var link = await _db.BugLinks.FirstOrDefaultAsync(l => l.BugTicketId == bugTicketId && l.SourceTicketId == sourceTicketId);
        if (link is null)
        {
            link = new BugLink { BugTicketId = bugTicketId, SourceTicketId = sourceTicketId, ProjectKey = bug.ProjectKey, Origin = BugLinkOrigin.Manual };
            _db.BugLinks.Add(link);
        }
        link.Status = BugLinkStatus.Confirmed;
        link.FoundBy = foundBy;
        link.MissedBy = stage;
        link.Note = Clip(note, 2000);
        link.DecidedAt = DateTime.UtcNow;
        link.Escaped = await IsEscapedAsync(sourceTicketId, bug.CreatedAt, default);
        await _db.SaveChangesAsync();
        return link;
    }

    private static string Stage(string? missedBy)
    {
        var s = (missedBy ?? "").Trim().ToLowerInvariant();
        if (s == "qa") s = "qa-test";
        if (!Stages.Contains(s))
            throw new LinkRefused($"Say which stage should have caught it: one of {string.Join(", ", Stages)}.");
        return s;
    }

    private static string? Clip(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];

    // ---- Reading -------------------------------------------------------------------------

    /// <summary>A run that may have written a link's lines, and how sure the board is.</summary>
    /// <param name="Tier"><c>exact</c> (the commit is inside the run's range), <c>likely</c> (the run's own
    /// commits touched a blamed file), or <c>phase</c> (only that it was a run of the source ticket that made commits).</param>
    public record RunGuess(int RunId, string Skill, int Pass, DateTime StartedAt, string? Model, string? SkillHash, string Tier);

    public record LinkView(
        int Id, int? SourceTicketId, string? SourceTitle, string Status, string Origin, double Lines, double Share,
        bool Neighbours, bool Escaped, string? FoundBy, string? MissedBy, string? Note, DateTime TracedAt, DateTime? DecidedAt,
        IReadOnlyList<LinkCommit> Commits, IReadOnlyList<RunGuess> Runs);

    public record BugView(int TicketId, bool IsBug, DateTime? TracedAt, double LinesBlamed, string? Note,
        IReadOnlyList<LinkView> Links);

    public record TracedHere(int LinkId, int BugTicketId, string? BugTitle, string Status, bool Escaped,
        string? FoundBy, string? MissedBy, double Share, DateTime TracedAt);

    public async Task<BugView?> ForBugAsync(int ticketId, CancellationToken ct = default)
    {
        var t = await _db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId, ct);
        if (t is null) return null;
        var trace = await _db.BugTraces.FindAsync(new object[] { ticketId }, ct);
        var links = await _db.BugLinks.Where(l => l.BugTicketId == ticketId).ToListAsync(ct);
        var sources = links.Where(l => l.SourceTicketId != null).Select(l => l.SourceTicketId!.Value).Distinct().ToList();
        var titles = await _db.Tickets.Where(x => sources.Contains(x.Id))
            .Select(x => new { x.Id, Title = x.Title ?? x.IssueKey }).ToDictionaryAsync(x => x.Id, x => x.Title, ct);

        var views = new List<LinkView>();
        foreach (var l in links.OrderBy(l => l.Status == BugLinkStatus.Rejected).ThenByDescending(l => l.Share))
        {
            var commits = CommitsOf(l);
            var runs = l.SourceTicketId is int src ? await RunsForAsync(t.ProjectKey, src, commits, ct) : [];
            views.Add(new LinkView(l.Id, l.SourceTicketId, l.SourceTicketId is int s ? titles.GetValueOrDefault(s) : null,
                l.Status.ToString(), l.Origin.ToString(), l.Lines, l.Share, l.Neighbours, l.Escaped,
                l.FoundBy?.ToString(), l.MissedBy, l.Note, l.TracedAt, l.DecidedAt, commits, runs));
        }
        return new BugView(t.Id, t.IsBug, trace?.TracedAt, trace?.LinesBlamed ?? 0, trace?.Note, views);
    }

    public async Task<IReadOnlyList<TracedHere>> TracedHereAsync(int sourceTicketId, CancellationToken ct = default)
    {
        var links = await _db.BugLinks.Where(l => l.SourceTicketId == sourceTicketId && l.Status != BugLinkStatus.Rejected)
            .ToListAsync(ct);
        var bugIds = links.Select(l => l.BugTicketId).ToList();
        var titles = await _db.Tickets.Where(x => bugIds.Contains(x.Id))
            .Select(x => new { x.Id, Title = x.Title ?? x.IssueKey }).ToDictionaryAsync(x => x.Id, x => x.Title, ct);
        return links.OrderBy(l => l.Status != BugLinkStatus.Confirmed).ThenByDescending(l => l.TracedAt)
            .Select(l => new TracedHere(l.Id, l.BugTicketId, titles.GetValueOrDefault(l.BugTicketId), l.Status.ToString(),
                l.Escaped, l.FoundBy?.ToString(), l.MissedBy, l.Share, l.TracedAt))
            .ToList();
    }

    /// <summary>A recorded commit range of one run, as stored in <see cref="CycleRun.CommitRangeJson"/>.</summary>
    public record RunRange(string? Repo, string From, string To);

    /// <summary>
    /// The runs of the source ticket that wrote a link's commits, derived rather than stored
    /// (docs/bugs-and-experiments-plan.md, 1.8): exact where a run's recorded range still contains the
    /// commit, likely where the run's own commits touched one of its blamed files.
    /// </summary>
    private async Task<IReadOnlyList<RunGuess>> RunsForAsync(string projectKey, int sourceTicketId,
        IReadOnlyList<LinkCommit> commits, CancellationToken ct)
    {
        var runs = await _db.CycleRuns.Where(r => r.TicketId == sourceTicketId && (r.RunCommits > 0 || r.CommitRangeJson != null))
            .OrderBy(r => r.StartedAt).ToListAsync(ct);
        if (runs.Count == 0) return [];

        var exact = new HashSet<int>();
        var likely = new HashSet<int>();
        foreach (var run in runs)
        {
            foreach (var range in ParseRanges(run.CommitRangeJson))
                foreach (var c in commits.Where(c => (range.Repo ?? "") == c.Repo && c.Sha != range.From))
                    if (await _git.IsAncestorAsync(projectKey, c.Repo, range.From, c.Sha, ct)
                        && await _git.IsAncestorAsync(projectKey, c.Repo, c.Sha, range.To, ct))
                        exact.Add(run.Id);

            var files = ParseFiles(run.RunFilesJson);
            if (files.Count > 0 && commits.Any(c => c.Files.Any(files.Contains))) likely.Add(run.Id);
        }

        RunGuess G(CycleRun r, string tier) => new(r.Id, r.Skill, r.Pass ?? 0, r.StartedAt, r.Model, r.SkillHash, tier);
        if (exact.Count > 0) return runs.Where(r => exact.Contains(r.Id)).Select(r => G(r, "exact")).ToList();
        if (likely.Count > 0) return runs.Where(r => likely.Contains(r.Id)).Reverse().Select(r => G(r, "likely")).ToList();
        return runs.Where(r => (r.RunCommits ?? 0) > 0).Reverse().Select(r => G(r, "phase")).ToList();
    }

    public static IReadOnlyList<RunRange> ParseRanges(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<RunRange>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static HashSet<string> ParseFiles(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return (JsonSerializer.Deserialize<List<string>>(json, Json) ?? []).ToHashSet(); }
        catch (JsonException) { return []; }
    }
}
