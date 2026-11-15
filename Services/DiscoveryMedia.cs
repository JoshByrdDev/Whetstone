using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// A discovery's video and what is kept of it (docs/discovery-track-plan.md, step 3: "Video", and
/// "What is kept, and for how long").
/// <para>
/// <b>Recording.</b> A Scout or Recheck run's browser records itself (<c>--caps devtools</c>,
/// <c>browser_start_video</c>) into the run's folder, <c>discovery/&lt;ticket&gt;/runs/&lt;stamp&gt;/</c>.
/// Nothing tells the board when that file appears - the agent may forget to say, or be cut short -
/// so the board looks for it: whenever it reads a discovery, finishes a verdict or runs retention,
/// a finished video under <c>runs/</c> is <i>adopted</i>, moved into the discovery's own folder,
/// and everything logged during its span is stamped against it. Second 0 of the video is read from
/// the name the browser gives it, so an observation's place in it comes from the board's clock and
/// the screenshot's, never from what the agent says.
/// </para>
/// <para>
/// <b>Clips.</b> A proposal's clip is cut from its recording around the observations it cites,
/// bounded by the chapters the run marked, the first time anyone plays it - and, for a proposal
/// whose clip is kept, before its recording is pruned.
/// </para>
/// <para>
/// <b>Keeping.</b> At Complete a rejected or snoozed proposal loses its clip and keeps one still,
/// and a shot no kept proposal uses goes. Later the retention pass (<see cref="CaptureRetention"/>)
/// removes a recording 7 days after its verdict, a kept clip 90 days after the discovery is Done,
/// and behind both the project's <c>discovery.storageBudgetMb</c>. Every removal keeps its row,
/// marked with when, so the drawer says "removed on".
/// </para>
/// </summary>
public static partial class DiscoveryMedia
{
    public const string RunsFolder = "runs";

    // ---- the clip's span --------------------------------------------------------------------

    /// <summary>At most this long before the moment, when no chapter starts nearer.</summary>
    public const double Lead = 20;
    /// <summary>At least this long before the moment, whatever the chapters say: enough to see what led to it.</summary>
    public const double MinLead = 5;
    /// <summary>After the moment - the shot is taken as the scout sees it, and after that it is thinking.</summary>
    public const double Tail = 4;
    /// <summary>Two spans closer than this are one.</summary>
    public const double JoinGap = 2;
    /// <summary>A clip is evidence for one verdict, not a second recording.</summary>
    public const double MaxClip = 90;

    /// <summary>
    /// The spans of a recording a proposal's clip is made of: around each moment it cites, from the
    /// chapter it falls in (but no more than <see cref="Lead"/> before it, and no less than
    /// <see cref="MinLead"/>) to a few seconds after it or the next chapter, whichever comes first.
    /// Overlapping spans are joined; past <see cref="MaxClip"/> seconds the rest is left out.
    /// </summary>
    public static IReadOnlyList<(double From, double To)> ClipSpans(double length, IEnumerable<double> chapters, IEnumerable<double> moments)
    {
        if (length <= 0) return Array.Empty<(double, double)>();
        var marks = chapters.Where(c => c >= 0 && c <= length).OrderBy(c => c).ToList();
        var spans = new List<(double From, double To)>();
        foreach (var raw in moments.OrderBy(m => m))
        {
            var t = Math.Clamp(raw, 0, length);
            var chapter = marks.LastOrDefault(c => c <= t, 0);
            var from = Math.Max(chapter, t - Lead);
            if (t - from < MinLead) from = Math.Max(0, t - MinLead);
            var next = marks.FirstOrDefault(c => c > t, length);
            var to = Math.Min(length, Math.Max(Math.Min(next, t + Tail), t + 1.5));
            if (spans.Count > 0 && from <= spans[^1].To + JoinGap)
                spans[^1] = (spans[^1].From, Math.Max(spans[^1].To, to));
            else
                spans.Add((from, to));
        }
        var kept = new List<(double From, double To)>();
        var total = 0.0;
        foreach (var s in spans)
        {
            var room = MaxClip - total;
            if (room < 1) break;
            var len = s.To - s.From;
            kept.Add(len <= room ? s : (s.From, s.From + room));
            total += Math.Min(len, room);
        }
        return kept.Select(s => (Math.Round(s.From, 2), Math.Round(s.To, 2))).ToList();
    }

    // ---- adopting a run's recording ---------------------------------------------------------

    [GeneratedRegex(@"video-(\d{4})-(\d{2})-(\d{2})T(\d{2})-(\d{2})-(\d{2})-(\d{3})Z", RegexOptions.IgnoreCase)]
    private static partial Regex StampedName();

    /// <summary>
    /// The moment a recording began, from the name the browser server gives a video it was not
    /// asked to name (<c>video-2026-11-15T05-32-17-887Z.webm</c>) - stamped as it starts. Null for
    /// any other name.
    /// </summary>
    public static DateTime? StartFromName(string fileName)
    {
        var m = StampedName().Match(fileName);
        if (!m.Success) return null;
        int G(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        try { return new DateTime(G(1), G(2), G(3), G(4), G(5), G(6), G(7), DateTimeKind.Utc); }
        catch { return null; }
    }

    /// <summary>A file the browser is still writing is left for the next look.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Adopt every finished video under the discovery's <c>runs/</c> that is not yet adopted: move it
    /// into the discovery's folder, work out its span, and stamp the observations and chapters
    /// logged during it. Without ffmpeg a video is still adopted and stamped; its length is then
    /// read as the time from its start to its last write.
    /// </summary>
    public static async Task<int> AdoptAsync(DashboardDbContext db, DashboardPaths paths, int ticketId, string? ffmpeg,
        DateTime now, CancellationToken ct = default)
    {
        var dir = paths.DiscoveryTicketDir(ticketId);
        var runs = Path.Combine(dir, RunsFolder);
        if (!Directory.Exists(runs)) return 0;
        List<string> files;
        try { files = Directory.EnumerateFiles(runs, "*.webm", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList(); }
        catch { return 0; }
        if (files.Count == 0) return 0;

        var adopted = 0;
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0 || now - info.LastWriteTimeUtc < Settled) continue;
            var source = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (await db.DiscoveryRecordings.AnyAsync(r => r.TicketId == ticketId && r.Source == source, ct)) continue;

            var seconds = ffmpeg is null ? null : await Ffmpeg.SecondsAsync(ffmpeg, file, ct);
            var start = StartFromName(info.Name)
                ?? (seconds is { } s ? info.LastWriteTimeUtc.AddSeconds(-s) : info.CreationTimeUtc);
            var length = seconds ?? Math.Max(0, (info.LastWriteTimeUtc - start).TotalSeconds);
            var end = start.AddSeconds(length);

            var rec = new DiscoveryRecording
            {
                TicketId = ticketId, Source = source, StartedAt = start, Seconds = Math.Round(length, 2), Bytes = info.Length, CreatedAt = now,
            };
            db.DiscoveryRecordings.Add(rec);
            await db.SaveChangesAsync(ct);
            rec.File = $"rec{rec.Id}{info.Extension.ToLowerInvariant()}";
            try { File.Move(file, Path.Combine(dir, rec.File), overwrite: true); }
            catch
            {
                // Held open after all: leave it for the next look, as if it had not been seen.
                db.DiscoveryRecordings.Remove(rec);
                await db.SaveChangesAsync(ct);
                continue;
            }

            // What was logged during it. An observation's moment is its screenshot's; a second of
            // slack either side covers the two clocks' rounding.
            var from = start.AddSeconds(-1);
            var to = end.AddSeconds(1);
            var obs = await db.DiscoveryObservations
                .Where(o => o.TicketId == ticketId && o.RecordingId == null).ToListAsync(ct);
            obs = obs.Where(o => (o.ObservedAt ?? o.CreatedAt) is var at && at >= from && at <= to).ToList();
            foreach (var o in obs)
            {
                o.RecordingId = rec.Id;
                o.VideoAt = Math.Round(Math.Clamp(((o.ObservedAt ?? o.CreatedAt) - start).TotalSeconds, 0, length), 2);
            }
            var chapters = (await db.DiscoveryChapters.Where(c => c.TicketId == ticketId && c.RecordingId == null).ToListAsync(ct))
                .Where(c => c.At >= from && c.At <= to).ToList();
            foreach (var c in chapters)
            {
                c.RecordingId = rec.Id;
                c.VideoAt = Math.Round(Math.Clamp((c.At - start).TotalSeconds, 0, length), 2);
            }
            // Which run and round it was: whatever was logged during it says.
            var rounds = obs.Select(o => (o.Round, o.RunId)).Concat(chapters.Select(c => (c.Round, c.RunId))).ToList();
            if (rounds.Count > 0)
            {
                var top = rounds.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
                (rec.Round, rec.RunId) = (top.Round, top.RunId);
            }
            await db.SaveChangesAsync(ct);
            adopted++;
        }
        return adopted;
    }

    // ---- cutting a clip ---------------------------------------------------------------------

    /// <summary>Why a proposal has, or has not, a clip to play - what the drawer says in its place.</summary>
    public enum ClipState
    {
        /// <summary>Cut and on disk.</summary>
        Ready,
        /// <summary>Not cut yet; playing it cuts it.</summary>
        Cuttable,
        /// <summary>Removed, by Complete or retention; <see cref="DiscoveryProposal.ClipPrunedAt"/> says when.</summary>
        Removed,
        /// <summary>Its recording is gone and no clip was cut before it went.</summary>
        RecordingRemoved,
        /// <summary>The run was not recorded, or nothing it cites was seen on camera.</summary>
        None,
        /// <summary>There is a recording, but no ffmpeg on this machine to cut it with.</summary>
        NoFfmpeg,
    }

    /// <summary>
    /// The recording a proposal's clip comes from, and the moments in it: the one that holds most of
    /// the observations it cites (a run that stopped and started its video again has two).
    /// </summary>
    public static (DiscoveryRecording? Recording, List<double> Moments) SourceOf(DiscoveryProposal p,
        IEnumerable<DiscoveryObservation> observations, IEnumerable<DiscoveryRecording> recordings)
    {
        var cited = observations.Where(o => p.ObservationIds.Contains(o.Id) && o.RecordingId is not null && o.VideoAt is not null).ToList();
        var best = cited.GroupBy(o => o.RecordingId!.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
        if (best is null) return (null, new());
        return (recordings.FirstOrDefault(r => r.Id == best.Key), best.Select(o => o.VideoAt!.Value).ToList());
    }

    public static ClipState StateOf(DiscoveryProposal p, IEnumerable<DiscoveryObservation> observations,
        IEnumerable<DiscoveryRecording> recordings, string ticketDir, bool ffmpeg)
    {
        if (p.ClipPrunedAt is not null) return ClipState.Removed;
        if (p.ClipFile is not null && File.Exists(Path.Combine(ticketDir, p.ClipFile))) return ClipState.Ready;
        var (rec, _) = SourceOf(p, observations, recordings);
        if (rec is null) return ClipState.None;
        if (rec.PrunedAt is not null || !File.Exists(Path.Combine(ticketDir, rec.File))) return ClipState.RecordingRemoved;
        return ffmpeg ? ClipState.Cuttable : ClipState.NoFfmpeg;
    }

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ClipLocks = new();
    /// <summary>Clips are cut a few at a time: opening a drawer asks for every one of them at once.</summary>
    private static readonly SemaphoreSlim Cutters = new(2);

    /// <summary>
    /// The clip for a proposal, cut now if it can be and has not been. Returns its path on disk, or
    /// null with the reason there is none.
    /// </summary>
    public static async Task<(string? Path, ClipState State, string? Error)> EnsureClipAsync(DashboardDbContext db, DashboardPaths paths,
        int proposalId, string? ffmpeg, CancellationToken ct = default)
    {
        var gate = ClipLocks.GetOrAdd(proposalId, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            var p = await db.DiscoveryProposals.FirstOrDefaultAsync(x => x.Id == proposalId, ct);
            if (p is null) return (null, ClipState.None, "no such proposal");
            var dir = paths.DiscoveryTicketDir(p.TicketId);
            var obs = await db.DiscoveryObservations.AsNoTracking().Where(o => o.TicketId == p.TicketId).ToListAsync(ct);
            var recs = await db.DiscoveryRecordings.AsNoTracking().Where(r => r.TicketId == p.TicketId).ToListAsync(ct);
            var state = StateOf(p, obs, recs, dir, ffmpeg is not null);
            if (state == ClipState.Ready) return (Path.Combine(dir, p.ClipFile!), state, null);
            if (state != ClipState.Cuttable) return (null, state, null);

            var (rec, moments) = SourceOf(p, obs, recs);
            var chapters = await db.DiscoveryChapters.AsNoTracking()
                .Where(c => c.RecordingId == rec!.Id && c.VideoAt != null).Select(c => c.VideoAt!.Value).ToListAsync(ct);
            var length = rec!.Seconds > 0 ? rec.Seconds : moments.Max() + Tail;
            var spans = ClipSpans(length, chapters, moments);
            if (spans.Count == 0) return (null, ClipState.None, null);

            await Cutters.WaitAsync(ct);
            try
            {
                var (file, error) = await Ffmpeg.CutAsync(ffmpeg!, Path.Combine(dir, rec.File), spans, Path.Combine(dir, $"p{p.Id}"), ct);
                if (file is null) return (null, ClipState.Cuttable, error ?? "ffmpeg failed");
                p.ClipFile = Path.GetFileName(file);
                await db.SaveChangesAsync(ct);
                return (file, ClipState.Ready, null);
            }
            finally { Cutters.Release(); }
        }
        finally { gate.Release(); }
    }

    // ---- what Complete removes --------------------------------------------------------------

    /// <summary>The proposals whose evidence is kept whole: approved, filed, undecided, or pinned.</summary>
    private static bool KeptWhole(DiscoveryProposal p) =>
        p.Pinned || p.Status is ProposalStatus.Approved or ProposalStatus.Filed or ProposalStatus.Proposed;

    /// <summary>
    /// What the owner's Complete removes (the plan's table): a rejected or snoozed proposal's clip,
    /// keeping one still - the first screenshot it cites - and every shot no kept proposal, still or
    /// goal check uses. The recordings it fed start their 7 days now. A run's own folder is the
    /// browser's scratch, everything in it already copied out, so it goes too.
    /// </summary>
    public static async Task<List<CaptureRetention.Pruned>> PruneAtCompleteAsync(DashboardDbContext db, DashboardPaths paths,
        int ticketId, string? ffmpeg, DateTime now, CancellationToken ct = default)
    {
        await AdoptAsync(db, paths, ticketId, ffmpeg, now.AddSeconds(Settled.TotalSeconds), ct);
        var dir = paths.DiscoveryTicketDir(ticketId);
        long bytes = 0;

        var props = await db.DiscoveryProposals.Where(p => p.TicketId == ticketId).ToListAsync(ct);
        foreach (var p in props.Where(p => !p.Pinned && p.ClipPrunedAt is null && p.Status is ProposalStatus.Rejected or ProposalStatus.Snoozed))
        {
            if (p.ClipFile is not null) bytes += Delete(Path.Combine(dir, p.ClipFile));
            p.ClipFile = null;
            p.ClipPrunedAt = now;
        }

        var obs = await db.DiscoveryObservations.Where(o => o.TicketId == ticketId).ToListAsync(ct);
        var keep = await ShotsKeptAsync(db, ticketId, props, obs, wholeFor: KeptWhole, goalChecks: true, ct);
        bytes += PruneShots(obs, keep, dir, now);

        foreach (var r in await db.DiscoveryRecordings.Where(r => r.TicketId == ticketId && r.VerdictAt == null).ToListAsync(ct))
            r.VerdictAt = now;
        await db.SaveChangesAsync(ct);

        bytes += DeleteDir(Path.Combine(dir, RunsFolder));
        return bytes > 0 ? new() { new CaptureRetention.Pruned(CaptureRetention.Store.Discovery, ticketId, "complete", bytes, now) } : new();
    }

    /// <summary>
    /// The observations whose screenshot stays: every one a proposal kept whole cites, the first one
    /// (with a shot) of every other proposal - its still - and, while the discovery is open, every one a goal check names.
    /// </summary>
    private static async Task<HashSet<int>> ShotsKeptAsync(DashboardDbContext db, int ticketId, List<DiscoveryProposal> props,
        List<DiscoveryObservation> obs, Func<DiscoveryProposal, bool> wholeFor, bool goalChecks, CancellationToken ct)
    {
        var withShot = obs.Where(o => o.ShotPath is not null && o.ShotPrunedAt is null).Select(o => o.Id).ToHashSet();
        var keep = new HashSet<int>();
        foreach (var p in props.Where(p => p.Status != ProposalStatus.Dropped))
        {
            if (wholeFor(p)) keep.UnionWith(p.ObservationIds);
            else
            {
                var still = p.ObservationIds.FirstOrDefault(withShot.Contains);
                if (still > 0) keep.Add(still);
            }
        }
        if (goalChecks)
            foreach (var ids in await db.DiscoveryGoalChecks.Where(g => g.TicketId == ticketId).Select(g => g.ObservationIds).ToListAsync(ct))
                keep.UnionWith(ids);
        return keep;
    }

    private static long PruneShots(List<DiscoveryObservation> obs, HashSet<int> keep, string dir, DateTime now)
    {
        long bytes = 0;
        foreach (var o in obs.Where(o => o.ShotPath is not null && o.ShotPrunedAt is null && !keep.Contains(o.Id)))
        {
            bytes += Delete(Path.Combine(dir, o.ShotPath!));
            o.ShotPrunedAt = now;
        }
        return bytes;
    }

    // ---- the retention pass -----------------------------------------------------------------

    /// <summary>
    /// One pass over <c>discovery/</c>: orphans, recordings past their days, kept clips past theirs,
    /// then each project's budget. Called from <see cref="CaptureRetention.PruneAsync"/>, so it runs
    /// on the same clock and writes the same ledger.
    /// </summary>
    public static async Task<List<CaptureRetention.Pruned>> PruneAsync(DashboardDbContext db, DashboardPaths paths,
        CaptureRetention.Policy policy, Func<string, long> budgetMbFor, string? ffmpeg, DateTime now, ILogger log, CancellationToken ct = default)
    {
        var result = new List<CaptureRetention.Pruned>();
        var root = paths.DiscoveryDir;
        if (!Directory.Exists(root)) return result;
        var dirs = Directory.EnumerateDirectories(root)
            .Select(d => (dir: d, id: int.TryParse(Path.GetFileName(d), out var i) ? i : (int?)null))
            .Where(x => x.id is not null).ToDictionary(x => x.id!.Value, x => x.dir);
        if (dirs.Count == 0) return result;

        var ids = dirs.Keys.ToList();
        var tickets = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Phase, t.ProjectKey, t.UpdatedAt }).ToDictionaryAsync(t => t.Id, ct);
        var arrivals = (await db.PhaseTransitions.AsNoTracking()
                .Where(p => p.To == TicketPhase.Done && ids.Contains(p.TicketId))
                .Select(p => new { p.TicketId, p.At }).ToListAsync(ct))
            .GroupBy(p => p.TicketId).ToDictionary(g => g.Key, g => g.Max(p => p.At));
        DateTime? DoneAt(int id) => tickets.TryGetValue(id, out var t) && t.Phase == TicketPhase.Done
            ? (arrivals.TryGetValue(id, out var at) ? at : t.UpdatedAt) : null;

        foreach (var (id, dir) in dirs)
        {
            if (ct.IsCancellationRequested) break;
            if (!tickets.TryGetValue(id, out var t))
            {
                var b = DeleteDir(dir);
                if (!Directory.Exists(dir)) result.Add(new(CaptureRetention.Store.Discovery, id, "orphan", b, now));
                continue;
            }
            long bytes;
            var driving = t.Phase is TicketPhase.Scouting or TicketPhase.Rechecking;
            if (!driving)
            {
                await AdoptAsync(db, paths, id, ffmpeg, now, ct);
                // A run's own folder, once nothing is running: everything in it is copied out.
                bytes = 0;
                var runs = Path.Combine(dir, RunsFolder);
                if (Directory.Exists(runs))
                    foreach (var run in Directory.EnumerateDirectories(runs))
                        if (now - LastWrite(run) > TimeSpan.FromDays(1)) bytes += DeleteDir(run);
                if (bytes > 0) result.Add(new(CaptureRetention.Store.Discovery, id, "runs", bytes, now));
            }

            var done = DoneAt(id);
            if (policy.DiscoveryRecordingDays > 0)
            {
                bytes = await PruneRecordingsAsync(db, paths, id, r => (r.VerdictAt ?? done) is { } at && now - at > TimeSpan.FromDays(policy.DiscoveryRecordingDays),
                    ffmpeg, now, log, ct);
                if (bytes > 0) result.Add(new(CaptureRetention.Store.Discovery, id, "recording-age", bytes, now));
            }
            if (policy.DiscoveryClipDays > 0 && done is { } d && now - d > TimeSpan.FromDays(policy.DiscoveryClipDays))
            {
                bytes = await PruneKeptAsync(db, paths, id, alsoStills: false, now, ct);
                if (bytes > 0) result.Add(new(CaptureRetention.Store.Discovery, id, "clip-age", bytes, now));
            }
        }

        // The budget, per project: whole discoveries' media, the one Done longest first, never an open one.
        foreach (var project in tickets.Values.GroupBy(t => t.ProjectKey))
        {
            var budgetMb = budgetMbFor(project.Key);
            if (budgetMb <= 0) continue;
            var budget = budgetMb * 1024 * 1024;
            var sizes = project.Where(t => dirs.ContainsKey(t.Id)).ToDictionary(t => t.Id, t => SizeOf(dirs[t.Id]));
            var total = sizes.Values.Sum();
            if (total <= budget) continue;
            foreach (var t in project.Where(t => t.Phase == TicketPhase.Done).OrderBy(t => DoneAt(t.Id) ?? DateTime.MinValue).ThenBy(t => t.Id))
            {
                if (total <= budget || ct.IsCancellationRequested) break;
                var b = await PruneRecordingsAsync(db, paths, t.Id, _ => true, ffmpeg: null, now, log, ct)
                    + await PruneKeptAsync(db, paths, t.Id, alsoStills: true, now, ct);
                if (b > 0) { result.Add(new(CaptureRetention.Store.Discovery, t.Id, "budget", b, now)); total -= b; }
            }
            if (total > budget)
                log.LogWarning("Discovery media for {Project} is {Mb}MB, over its {Budget}MB budget, and what is left is open or pinned",
                    project.Key, total / 1048576, budgetMb);
        }
        return result;
    }

    /// <summary>
    /// Remove the recordings <paramref name="due"/> picks, after cutting the clips that outlive them:
    /// every proposal kept whole whose clip has not been cut yet. With no ffmpeg (or none passed, as
    /// the budget does) the recording goes regardless - the shots still stand.
    /// </summary>
    private static async Task<long> PruneRecordingsAsync(DashboardDbContext db, DashboardPaths paths, int ticketId,
        Func<DiscoveryRecording, bool> due, string? ffmpeg, DateTime now, ILogger log, CancellationToken ct)
    {
        var dir = paths.DiscoveryTicketDir(ticketId);
        var recs = (await db.DiscoveryRecordings.Where(r => r.TicketId == ticketId && r.PrunedAt == null).ToListAsync(ct)).Where(due).ToList();
        if (recs.Count == 0) return 0;
        if (ffmpeg is not null)
        {
            var keptIds = await db.DiscoveryProposals.AsNoTracking()
                .Where(p => p.TicketId == ticketId && p.ClipPrunedAt == null && p.ClipFile == null
                    && (p.Pinned || p.Status == ProposalStatus.Approved || p.Status == ProposalStatus.Filed || p.Status == ProposalStatus.Proposed))
                .Select(p => p.Id).ToListAsync(ct);
            foreach (var pid in keptIds)
            {
                var (_, state, error) = await EnsureClipAsync(db, paths, pid, ffmpeg, ct);
                if (error is not null) log.LogInformation("Could not cut proposal {Id}'s clip before its recording went: {Error}", pid, error);
            }
        }
        long bytes = 0;
        foreach (var r in recs)
        {
            bytes += Delete(Path.Combine(dir, r.File));
            r.PrunedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return bytes;
    }

    /// <summary>
    /// Remove a finished discovery's kept clips and every shot but each proposal's still - or, for
    /// the budget, the stills too. A pinned proposal keeps all of its own.
    /// </summary>
    private static async Task<long> PruneKeptAsync(DashboardDbContext db, DashboardPaths paths, int ticketId, bool alsoStills,
        DateTime now, CancellationToken ct)
    {
        var dir = paths.DiscoveryTicketDir(ticketId);
        long bytes = 0;
        var props = await db.DiscoveryProposals.Where(p => p.TicketId == ticketId).ToListAsync(ct);
        foreach (var p in props.Where(p => !p.Pinned && p.ClipPrunedAt is null))
        {
            if (p.ClipFile is not null) bytes += Delete(Path.Combine(dir, p.ClipFile));
            p.ClipFile = null;
            p.ClipPrunedAt = now;
        }
        var obs = await db.DiscoveryObservations.Where(o => o.TicketId == ticketId).ToListAsync(ct);
        var keep = alsoStills
            ? props.Where(p => p.Pinned).SelectMany(p => p.ObservationIds).ToHashSet()
            : await ShotsKeptAsync(db, ticketId, props, obs, wholeFor: p => p.Pinned, goalChecks: false, ct);
        bytes += PruneShots(obs, keep, dir, now);
        await db.SaveChangesAsync(ct);
        return bytes;
    }

    // ---- small helpers ----------------------------------------------------------------------

    private static long Delete(string path)
    {
        try
        {
            var f = new FileInfo(path);
            if (!f.Exists) return 0;
            var len = f.Length;
            f.Delete();
            return len;
        }
        catch { return 0; }
    }

    private static long DeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        var size = SizeOf(dir);
        try { Directory.Delete(dir, recursive: true); return size; }
        catch { return 0; }
    }

    public static long SizeOf(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }); }
        catch { return 0; }
    }

    private static DateTime LastWrite(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc).Append(Directory.GetLastWriteTimeUtc(dir)).Max();
        }
        catch { return DateTime.UtcNow; }
    }
}
