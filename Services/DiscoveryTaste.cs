using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// A project's taste file and its fold (docs/discovery-track-plan.md, "The taste file", step 4).
/// <para>
/// Rejections are not fed back to Sift as a growing log. The file is short - what this owner does
/// and does not want proposed - and it is rewritten from the verdicts every
/// <c>discovery.tasteFoldEvery</c> of them, by a model run that rides on the back of a worker run
/// the way the notes store's maintenance does (<c>Invoke-DiscoveryTasteFold</c> in
/// data/worker-discovery.ps1). The run only writes prose: the board composes what it is told
/// (<see cref="BeginFoldAsync"/>), checks what it answers, writes the file, commits it in the
/// project folder's own repo and records the version (<see cref="FinishFoldAsync"/>), so a bad
/// lesson is one revert away and every Sift and Recheck run can say which version it judged by.
/// </para>
/// <para>
/// The file has two sections. <b>The owner's</b> is the owner's alone: a fold keeps it word for word
/// and may only suggest a change to it, which the panel shows beside the file. <b>Learned</b> is the
/// fold's. Not every verdict teaches: an approval, a rejection as generic or wrong, and a line restored
/// from the dropped list do; "not now" and a snooze are timing, and "already decided" is a decision the
/// register is missing - the panel lists those for the owner rather than the fold learning from them.
/// </para>
/// <para>
/// Every write here is the owner's or the fold's. The owner's are REST routes with no tool behind
/// them (edit, revert, ask for a fold); the fold's are the begin and finish a worker's script calls,
/// not an agent's tool. A Sift or Recheck run only reads, through get_discovery.
/// </para>
/// </summary>
public class DiscoveryTaste
{
    /// <summary>The file's path inside its project folder.</summary>
    public const string RelativePath = "discovery/taste.md";

    public const string OwnerHeading = "## The owner's";
    public const string LearnedHeading = "## Learned";
    public const string SuggestionsMarker = "SUGGESTIONS FOR THE OWNER:";

    /// <summary>The fold's section is kept short: about forty lines is what a sift can hold in mind.</summary>
    public const int MaxLearnedLines = 40;
    public const int MaxLearnedChars = 6000;
    /// <summary>What the owner may write, the whole file: generous, but a file, not a log.</summary>
    public const int MaxFileChars = 20000;

    /// <summary>A fold's claim lapses after this long, so a run that died mid-fold does not hold the next one off.</summary>
    public static readonly TimeSpan ClaimFor = TimeSpan.FromMinutes(15);

    // One paragraph, no hard breaks: the panel wraps it, and so does any editor.
    private const string Preamble =
        "# Taste\n\n" +
        "What the owner's verdicts on discoveries have taught about what they want proposed. Sift and Recheck " +
        "read this before they judge. Every few verdicts a fold rewrites \"Learned\" and commits it here, so a " +
        "lesson can be found and reverted. \"The owner's\" is the owner's own: no fold changes it.\n";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly DashboardDbContext _db;
    private readonly ProjectRegistry _registry;
    private readonly DashboardPaths _paths;

    public DiscoveryTaste(DashboardDbContext db, ProjectRegistry registry, DashboardPaths paths)
        => (_db, _registry, _paths) = (db, registry, paths);

    /// <summary>A refusal the caller hands straight back: the owner or the worker's log reads it.</summary>
    public sealed class Refused(string message) : Exception(message);

    // ---- the file ---------------------------------------------------------------------------

    /// <summary>LF line ends, no trailing whitespace at the end: the same file is the same version.</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() + (text.Trim().Length == 0 ? "" : "\n");

    public static string HashOf(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text)))).ToLowerInvariant();

    /// <summary>
    /// The owner's section and the fold's. A file without the headings - one the owner wrote by
    /// hand - is all the owner's: a fold adds its section after it rather than guessing what to keep.
    /// </summary>
    public static (string Owner, string Learned) Split(string text)
    {
        var lines = Normalize(text).Split('\n');
        int owner = -1, learned = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimEnd();
            if (owner < 0 && l.Equals(OwnerHeading, StringComparison.OrdinalIgnoreCase)) owner = i;
            else if (learned < 0 && l.Equals(LearnedHeading, StringComparison.OrdinalIgnoreCase)) learned = i;
        }
        string Join(int from, int to) => string.Join('\n', lines[from..to]).Trim();
        if (owner < 0 && learned < 0) return (StripPreamble(Normalize(text)).Trim(), "");
        if (owner >= 0 && learned > owner) return (Join(owner + 1, learned), Join(learned + 1, lines.Length));
        if (owner >= 0 && learned < 0) return (Join(owner + 1, lines.Length), "");
        if (owner < 0) return (StripPreamble(Join(0, learned)), Join(learned + 1, lines.Length));
        // Learned before the owner's: each section runs to the other heading.
        return (Join(owner + 1, lines.Length), Join(learned + 1, owner));
    }

    private static string StripPreamble(string s)
    {
        var p = Normalize(Preamble).Trim();
        return s.Trim().StartsWith(p, StringComparison.Ordinal) ? s.Trim()[p.Length..].Trim() : s.Trim();
    }

    /// <summary>The file from its two sections, with the preamble that says how it works.</summary>
    public static string Compose(string owner, string learned) =>
        Normalize(Preamble + "\n" + OwnerHeading + "\n\n" + (owner.Trim().Length > 0 ? owner.Trim() + "\n\n" : "")
            + LearnedHeading + "\n\n" + learned.Trim() + "\n");

    private string FilePath(string key) => _paths.DiscoveryTasteFile(key);
    private string Folder(string key) => _paths.ProjectDir(key);

    private static readonly Regex KeyShape = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    /// <summary>A project this board has, or a refusal - the key becomes a path, so it is checked before it is used.</summary>
    public void RequireProject(string key)
    {
        if (!KeyShape.IsMatch(key ?? "") || !_paths.DiscoverAllProjects().Contains(key!, StringComparer.Ordinal))
            throw new Refused($"There is no project '{key}'.");
    }

    // ---- versions -----------------------------------------------------------------------------

    private Task<DiscoveryTasteVersion?> LatestAsync(string key) =>
        _db.DiscoveryTasteVersions.Where(v => v.ProjectKey == key).OrderByDescending(v => v.Number).FirstOrDefaultAsync();

    /// <summary>
    /// The version the file on disk is, recording it first when it has changed outside the board - an
    /// edit in the folder, a <c>git revert</c> of a fold - so what a run reads always has a number.
    /// Null when there is no file and never was one.
    /// </summary>
    public async Task<DiscoveryTasteVersion?> CurrentAsync(string key)
    {
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try { return await CurrentLockedAsync(key); }
        finally { gate.Release(); }
    }

    private async Task<DiscoveryTasteVersion?> CurrentLockedAsync(string key)
    {
        var latest = await LatestAsync(key);
        var path = FilePath(key);
        var text = File.Exists(path) ? Normalize(await File.ReadAllTextAsync(path)) : "";
        if (text.Length == 0 && (latest is null || latest.Text.Length == 0)) return latest;
        var hash = HashOf(text);
        if (latest is not null && latest.Hash == hash) return latest;
        return await AddVersionAsync(key, text, TasteSource.Outside,
            new ProjectRepo.FileCommit(ProjectRepo.CommittedAs(Folder(key), RelativePath),
                text.Length == 0 ? "Found deleted." : "Found changed in the project folder."));
    }

    private async Task<DiscoveryTasteVersion> AddVersionAsync(string key, string text, TasteSource source, ProjectRepo.FileCommit commit,
        IReadOnlyList<int>? events = null, int verdicts = 0, string? suggestions = null, int? revertOf = null, int? rodeOn = null)
    {
        var number = (await _db.DiscoveryTasteVersions.Where(v => v.ProjectKey == key).MaxAsync(v => (int?)v.Number) ?? 0) + 1;
        var v = new DiscoveryTasteVersion
        {
            ProjectKey = key, Number = number, Text = text, Hash = HashOf(text), Source = source,
            Commit = commit.Sha, CommitNote = commit.Note, EventIds = events?.ToList() ?? new(), Verdicts = verdicts,
            Suggestions = string.IsNullOrWhiteSpace(suggestions) ? null : suggestions.Trim(), RevertOf = revertOf,
            RodeOnTicketId = rodeOn, At = DateTime.UtcNow,
        };
        _db.DiscoveryTasteVersions.Add(v);
        await _db.SaveChangesAsync();
        return v;
    }

    /// <summary>Write the file, commit it in the project folder, record the version. Under the project's gate.</summary>
    private async Task<DiscoveryTasteVersion> WriteLockedAsync(string key, string text, TasteSource source, string message,
        IReadOnlyList<int>? events = null, int verdicts = 0, string? suggestions = null, int? revertOf = null, int? rodeOn = null)
    {
        text = Normalize(text);
        var path = FilePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        var commit = ProjectRepo.CommitFile(Folder(key), RelativePath, message);
        return await AddVersionAsync(key, text, source, commit, events, verdicts, suggestions, revertOf, rodeOn);
    }

    // ---- what a run reads ------------------------------------------------------------------------

    /// <summary>What get_discovery hands a Sift or Recheck run of the taste file.</summary>
    public sealed record ForRun(int? Version, string? Hash, string Text, string Note);

    /// <summary>
    /// The taste file for a run, and the record that it read it: one row per run and round, so the
    /// version a proposal was judged by is a join through its run id.
    /// </summary>
    public async Task<ForRun> ReadForRunAsync(int ticketId, string key, TicketPhase phase, int round, int? runId)
    {
        var v = await CurrentAsync(key);
        var have = v is not null && v.Text.Length > 0;
        int? versionId = have ? v!.Id : null;
        var seen = await _db.DiscoveryTasteReads.AnyAsync(r => r.TicketId == ticketId && r.RunId == runId && r.Round == round
            && r.VersionId == versionId);
        if (!seen)
        {
            _db.DiscoveryTasteReads.Add(new DiscoveryTasteRead
            {
                TicketId = ticketId, RunId = runId, Round = round, Phase = phase.ToString(),
                VersionId = have ? v!.Id : null, VersionNumber = have ? v!.Number : null, Hash = have ? v!.Hash : null,
            });
            await _db.SaveChangesAsync();
        }
        if (!have)
            return new ForRun(null, null, "",
                "This project has no taste file yet: no verdicts have been folded into one. Judge by your skill's rules alone; " +
                "the owner's verdicts on what you propose are what will start it.");
        return new ForRun(v!.Number, v.Hash, v.Text,
            "What this owner's verdicts have taught about what they want proposed. Apply it as you cut and rank: what it says is " +
            "unwanted needs a reason this case is different before it is proposed. \"The owner's\" section is the owner's own words and " +
            "outranks \"Learned\". It is guidance on taste, never a reason to skip checking against the specs and decisions.");
    }

    // ---- what is waiting to be folded --------------------------------------------------------------

    /// <summary>Whether a proposal's verdict, as it stands, teaches anything about taste.</summary>
    public static bool Teaches(DiscoveryProposal p, IEnumerable<DiscoveryVerdictEvent> events) =>
        p.Status is ProposalStatus.Approved or ProposalStatus.Filed
        || (p.Status == ProposalStatus.Rejected && p.RejectReason is RejectReason.Generic or RejectReason.Wrong)
        || events.Any(e => e.Kind == VerdictEventKind.Restored);

    /// <summary>One proposal with verdict events not yet folded: what the owner did, as it now stands.</summary>
    public sealed record Pending(DiscoveryProposal Proposal, string Discovery, IReadOnlyList<DiscoveryVerdictEvent> Events, bool Teaches);

    /// <summary>
    /// Proposals whose verdict events no fold has taken in yet, among those whose verdict is closed:
    /// decided (not undecided again) and not on a discovery still waiting for its verdict, where a
    /// change of mind is a click away.
    /// </summary>
    public async Task<IReadOnlyList<Pending>> PendingAsync(string key)
    {
        var folded = (await _db.DiscoveryTasteVersions.AsNoTracking().Where(v => v.ProjectKey == key && v.Source == TasteSource.Fold)
            .Select(v => v.EventIds).ToListAsync()).SelectMany(x => x).ToHashSet();
        var events = (await _db.DiscoveryVerdictEvents.AsNoTracking().Where(e => e.ProjectKey == key).OrderBy(e => e.Id).ToListAsync())
            .Where(e => !folded.Contains(e.Id)).ToList();
        if (events.Count == 0) return Array.Empty<Pending>();
        var ids = events.Select(e => e.ProposalId).Distinct().ToList();
        var props = await _db.DiscoveryProposals.AsNoTracking().Include(p => p.Ticket).Where(p => ids.Contains(p.Id)).ToListAsync();
        var list = new List<Pending>();
        foreach (var p in props.OrderBy(p => p.Id))
        {
            if (p.Status is ProposalStatus.Proposed or ProposalStatus.Dropped) continue;
            if (p.Ticket!.Phase == TicketPhase.NeedsVerdict) continue;
            var mine = events.Where(e => e.ProposalId == p.Id).ToList();
            list.Add(new Pending(p, (p.Ticket.Title ?? "").Trim(), mine, Teaches(p, mine)));
        }
        return list;
    }

    // ---- the panel -----------------------------------------------------------------------------

    public sealed record VersionLine(int Id, int Number, string Source, string Hash, string? Commit, string? CommitNote,
        int Verdicts, int Events, bool HasSuggestions, int? RevertOf, int? RodeOnTicketId, DateTime At, int Reads);

    public sealed record FoldState(int Every, int Verdicts, bool Due, bool Requested, string? LastNote, DateTime? LastAt,
        DateTime? ClaimedAt, int TeachNothing);

    public sealed record MissingDecision(int ProposalId, int Discovery, string Title, string? Note);

    public sealed record Status(string Path, bool Exists, string Text, string Owner, string Learned, VersionLine? Current,
        string? Suggestions, IReadOnlyList<VersionLine> Versions, FoldState Fold, IReadOnlyList<MissingDecision> AlreadyDecided,
        int VerdictsEver, bool Versioned);

    /// <summary>Everything the owner's panel shows: the file, its versions, and how near the next fold is.</summary>
    public async Task<Status> StatusAsync(string key)
    {
        RequireProject(key);
        var current = await CurrentAsync(key);
        var versions = await _db.DiscoveryTasteVersions.AsNoTracking().Where(v => v.ProjectKey == key)
            .OrderByDescending(v => v.Number).Take(40).ToListAsync();
        var vids = versions.Select(v => (int?)v.Id).ToList();
        var reads = await _db.DiscoveryTasteReads.AsNoTracking().Where(r => vids.Contains(r.VersionId))
            .GroupBy(r => r.VersionId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.N);
        VersionLine Line(DiscoveryTasteVersion v) => new(v.Id, v.Number, v.Source.ToString(), v.Hash, v.Commit, v.CommitNote,
            v.Verdicts, v.EventIds.Count, v.Suggestions is not null, v.RevertOf, v.RodeOnTicketId, v.At, reads.GetValueOrDefault(v.Id));
        var pending = await PendingAsync(key);
        var state = await _db.DiscoveryTasteStates.AsNoTracking().FirstOrDefaultAsync(s => s.ProjectKey == key);
        var every = _registry.GetProjectConfig(key).Discovery.TasteFoldEvery;
        var teaching = pending.Count(p => p.Teaches);
        var requested = state?.FoldRequestedAt is not null;
        var lastFold = versions.FirstOrDefault(v => v.Source == TasteSource.Fold);
        var text = current?.Text ?? "";
        var (owner, learned) = Split(text);
        var already = pending.Where(p => p.Proposal.Status == ProposalStatus.Rejected && p.Proposal.RejectReason == RejectReason.AlreadyDecided)
            .Select(p => new MissingDecision(p.Proposal.Id, p.Proposal.TicketId, p.Proposal.Title, p.Proposal.RejectNote)).ToList();
        return new Status(FilePath(key), File.Exists(FilePath(key)), text, owner, learned, current is null ? null : Line(current),
            lastFold?.Suggestions, versions.Select(Line).ToList(),
            new FoldState(every, teaching, IsDue(teaching, every, requested), requested, state?.LastFoldNote, state?.LastFoldAt,
                state?.ClaimedAt is { } c && DateTime.UtcNow - c < ClaimFor ? c : null, pending.Count - teaching),
            already, await _db.DiscoveryVerdictEvents.CountAsync(e => e.ProjectKey == key),
            ProjectRepo.Read(Folder(key)).Versioned);
    }

    /// <summary>A fold is due at the count, or sooner when the owner asked - but never with nothing to learn from.</summary>
    public static bool IsDue(int verdicts, int every, bool requested) => verdicts > 0 && (verdicts >= every || requested);

    /// <summary>A version's whole text, for the panel's diff and revert.</summary>
    public async Task<DiscoveryTasteVersion> VersionAsync(string key, int id)
    {
        RequireProject(key);
        return await _db.DiscoveryTasteVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && v.ProjectKey == key)
            ?? throw new Refused($"Project {key} has no taste version {id}.");
    }

    // ---- the owner -----------------------------------------------------------------------------

    /// <summary>The owner writes the file. Either section: it is theirs. A REST route with no tool behind it.</summary>
    public async Task<DiscoveryTasteVersion> OwnerEditAsync(string key, string text)
    {
        RequireProject(key);
        text = Normalize(text ?? "");
        if (text.Length > MaxFileChars) throw new Refused($"The taste file is kept short: {text.Length} characters is past {MaxFileChars}.");
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var current = await CurrentLockedAsync(key);
            if (current is not null && current.Hash == HashOf(text)) return current;
            if (current is null && text.Length == 0) throw new Refused("There is no taste file to clear.");
            return await WriteLockedAsync(key, text, TasteSource.Owner, "Taste: edited by the owner");
        }
        finally { gate.Release(); }
    }

    /// <summary>The owner puts an earlier version back, as a new version - nothing is rewritten. A REST route with no tool behind it.</summary>
    public async Task<DiscoveryTasteVersion> RevertAsync(string key, int versionId)
    {
        var old = await VersionAsync(key, versionId);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var current = await CurrentLockedAsync(key);
            if (current is not null && current.Hash == old.Hash) throw new Refused($"Version {old.Number} is what the file already says.");
            return await WriteLockedAsync(key, old.Text, TasteSource.Revert, $"Taste: back to version {old.Number}, by the owner", revertOf: old.Id);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The owner asks for a fold at the next worker run, below the count (or takes the ask back).
    /// The fold itself is still a run's: the board starts no model. A REST route with no tool behind it.
    /// </summary>
    public async Task<bool> RequestFoldAsync(string key, bool requested)
    {
        RequireProject(key);
        var s = await StateAsync(key);
        s.FoldRequestedAt = requested ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync();
        return requested;
    }

    private async Task<DiscoveryTasteState> StateAsync(string key)
    {
        var s = await _db.DiscoveryTasteStates.FirstOrDefaultAsync(x => x.ProjectKey == key);
        if (s is null) { s = new DiscoveryTasteState { ProjectKey = key }; _db.DiscoveryTasteStates.Add(s); }
        return s;
    }

    // ---- the fold ------------------------------------------------------------------------------

    public sealed record FoldBegin(bool Due, string Reason, string? Claim = null, string? Prompt = null, int Verdicts = 0, int Events = 0);

    /// <summary>
    /// A worker run, finished, asks whether a fold is due here; when one is, it gets a claim and the
    /// prompt to run, and the next run to ask is told one is under way. Called by the worker's script,
    /// not by an agent: there is no tool behind it.
    /// </summary>
    public async Task<FoldBegin> BeginFoldAsync(string key, int? rodeOn = null)
    {
        RequireProject(key);
        var cfg = _registry.GetProjectConfig(key);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var state = await StateAsync(key);
            if (state.Claim is not null && state.ClaimedAt is { } at && DateTime.UtcNow - at < ClaimFor)
                return new FoldBegin(false, $"A fold has been under way since {at:u}.");
            var pending = await PendingAsync(key);
            var teaching = pending.Count(p => p.Teaches);
            var requested = state.FoldRequestedAt is not null;
            if (!IsDue(teaching, cfg.Discovery.TasteFoldEvery, requested))
            {
                await _db.SaveChangesAsync();
                return new FoldBegin(false, teaching == 0
                    ? "No verdict waits to be folded."
                    : $"{teaching} of {cfg.Discovery.TasteFoldEvery} verdicts toward the next fold.", Verdicts: teaching);
            }
            var current = await CurrentLockedAsync(key);
            var text = current?.Text ?? "";
            state.Claim = Guid.NewGuid().ToString("N");
            state.ClaimedAt = DateTime.UtcNow;
            state.ClaimBasedOn = HashOf(text);
            state.ClaimEventIds = string.Join(',', pending.SelectMany(p => p.Events).Select(e => e.Id).Order());
            await _db.SaveChangesAsync();
            var (owner, learned) = Split(text);
            return new FoldBegin(true, $"{teaching} verdict{(teaching == 1 ? "" : "s")} to fold.", state.Claim,
                FoldPrompt(owner, learned, pending), teaching, pending.Sum(p => p.Events.Count));
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The fold's answer, from the run that held the claim: checked, written into the Learned section
    /// (the owner's section kept as it is on disk), committed, recorded. A refused answer, or an error
    /// the run reports instead of one, folds nothing and lets go of the claim - the verdicts wait for
    /// the next run.
    /// </summary>
    public async Task<DiscoveryTasteVersion> FinishFoldAsync(string key, string claim, string? reply, string? error, int? rodeOn = null)
    {
        RequireProject(key);
        var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var state = await StateAsync(key);
            if (state.Claim is null || state.Claim != claim)
                throw new Refused("That fold's claim is not the one this project holds: it lapsed, or another run's fold replaced it.");
            var eventIds = (state.ClaimEventIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
            var basedOn = state.ClaimBasedOn;
            void Release(string note)
            {
                state.Claim = null; state.ClaimedAt = null; state.ClaimBasedOn = null; state.ClaimEventIds = null;
                state.LastFoldNote = note; state.LastFoldAt = DateTime.UtcNow;
            }

            string? refusal = null;
            (string Learned, string? Suggestions) parsed = default;
            if (!string.IsNullOrWhiteSpace(error)) refusal = $"The fold's run failed: {Cap(error.Trim(), 300)}";
            else
            {
                try { parsed = ParseReply(reply ?? ""); }
                catch (Refused r) { refusal = r.Message; }
            }
            var current = await CurrentLockedAsync(key);
            if (refusal is null && (current is null ? HashOf("") : current.Hash) != basedOn)
                refusal = "The taste file changed while the fold ran, so the fold was not written over it. It runs again next time.";
            if (refusal is not null)
            {
                Release($"Not folded: {refusal}");
                await _db.SaveChangesAsync();
                throw new Refused(refusal);
            }

            var pendingNow = (await PendingAsync(key)).Where(p => p.Events.Any(e => eventIds.Contains(e.Id))).ToList();
            var verdicts = pendingNow.Count(p => p.Teaches);
            var (owner, _) = Split(current?.Text ?? "");
            var discoveries = pendingNow.Select(p => p.Proposal.TicketId).Distinct().Order().ToList();
            var message = $"Taste fold: {verdicts} verdict{(verdicts == 1 ? "" : "s")}"
                + (discoveries.Count > 0 ? $" from discover{(discoveries.Count == 1 ? "y" : "ies")} {string.Join(", ", discoveries.Select(d => $"#{d}"))}" : "");
            var v = await WriteLockedAsync(key, Compose(owner, parsed.Learned), TasteSource.Fold, message,
                eventIds, verdicts, parsed.Suggestions, rodeOn: rodeOn);
            Release($"Folded {verdicts} verdict{(verdicts == 1 ? "" : "s")} into version {v.Number}.");
            state.FoldRequestedAt = null;
            await _db.SaveChangesAsync();
            return v;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// What a fold answered, checked before it lands: the Learned section and, after the marker, its
    /// suggestions for the owner's section. Refused when it is empty, too long, has no rule in it, or
    /// reaches for the owner's section - a fold that would overwrite the owner's words folds nothing.
    /// </summary>
    public static (string Learned, string? Suggestions) ParseReply(string reply)
    {
        var text = Normalize(reply).Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = text.IndexOf('\n');
            text = nl < 0 ? "" : text[(nl + 1)..];
            if (text.TrimEnd().EndsWith("```", StringComparison.Ordinal)) text = text.TrimEnd()[..^3];
            text = text.Trim();
        }
        string? suggestions = null;
        var m = Regex.Match(text, @"^\s*" + Regex.Escape(SuggestionsMarker.TrimEnd(':')) + @":?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (m.Success)
        {
            suggestions = text[(m.Index + m.Length)..].Trim();
            text = text[..m.Index].Trim();
            if (suggestions.Length == 0 || Regex.IsMatch(suggestions, @"^(-\s*)?(none|nothing)\.?$", RegexOptions.IgnoreCase)) suggestions = null;
        }
        if (text.StartsWith(LearnedHeading, StringComparison.OrdinalIgnoreCase)) text = text[LearnedHeading.Length..].Trim();
        if (text.Length == 0) throw new Refused("The fold answered nothing to put in Learned.");
        if (Regex.IsMatch(text, @"^#{1,2}\s", RegexOptions.Multiline))
            throw new Refused("The fold answered with headings of its own. It writes the Learned section's lines and nothing else - the owner's section is not its to write.");
        var lines = text.Split('\n').Count(l => l.Trim().Length > 0);
        if (lines > MaxLearnedLines) throw new Refused($"The fold wrote {lines} lines; Learned is kept to {MaxLearnedLines}.");
        if (text.Length > MaxLearnedChars) throw new Refused($"The fold wrote {text.Length} characters; Learned is kept to {MaxLearnedChars}.");
        if (!Regex.IsMatch(text, @"^\s*[-*]\s+\S", RegexOptions.Multiline))
            throw new Refused("The fold's answer has no rule in it (no '- ' line), so it is not a Learned section.");
        if (suggestions is { Length: > 2000 }) suggestions = suggestions[..2000];
        return (text, suggestions);
    }

    /// <summary>
    /// What the fold is told: the file as it is, the verdicts since the last fold, what each kind of
    /// verdict means, and the shape of the answer. Pure, so a test can read it whole.
    /// </summary>
    public static string FoldPrompt(string owner, string learned, IReadOnlyList<Pending> pending)
    {
        var b = new StringBuilder();
        b.AppendLine("You keep a short taste file for a product's owner. A discovery agent uses the product, proposes changes, and the");
        b.AppendLine("owner approves or rejects each proposal. The taste file is what those verdicts have taught about what this owner");
        b.AppendLine("wants proposed, and the agent that writes the next proposals reads it first. Rewrite its Learned section from the");
        b.AppendLine("verdicts below, keeping what still holds.");
        b.AppendLine();
        b.AppendLine("Rules:");
        b.AppendLine("- Each line is a rule a reviewer can apply to a new proposal: what kind of proposal this owner wants or does not, and why.");
        b.AppendLine("  Generalise - say what kind of thing, not this one proposal - and keep a short count of the evidence in brackets,");
        b.AppendLine("  e.g. \"(3 rejected as generic, 1 approved)\". Merge a new verdict into the line it supports rather than adding one.");
        b.AppendLine("- Keep a lesson from the current Learned section unless these verdicts contradict it. Drop or soften one they contradict.");
        b.AppendLine($"- At most {MaxLearnedLines} lines, each starting \"- \". Fewer is better. No headings, no preamble, no ticket numbers.");
        b.AppendLine("- An approval teaches what is wanted. An edit before approval teaches how the owner wants it written or scoped - say so");
        b.AppendLine("  when the edit changed the substance (a different kind, a narrower scope), not for a reworded sentence.");
        b.AppendLine("- A rejection as GENERIC means it could be said of any product: learn what made it generic. A rejection as WRONG means the");
        b.AppendLine("  agent misjudged the product: learn what it should have checked. The owner's note, when there is one, is the best evidence.");
        b.AppendLine("- RESTORED means the agent dropped it and the owner promoted it back: the agent is cutting that kind of thing too hard.");
        b.AppendLine("- NEW TO ME means the owner had not thought of it: the most valuable kind of proposal. Learn what such ones have in common.");
        b.AppendLine("- Verdicts marked \"teaches nothing\" (not now, snoozed, already decided) are timing or a missing decision, not taste. Do");
        b.AppendLine("  not learn from them.");
        b.AppendLine("- The owner's section is the owner's own words and you may not change it. If these verdicts argue against something");
        b.AppendLine("  in it, say so as a suggestion for the owner after the marker below. Never contradict it in Learned.");
        b.AppendLine();
        b.AppendLine("Answer with the new Learned section's lines only - no fences, no heading. Then, only if you have any, a line");
        b.AppendLine($"\"{SuggestionsMarker}\" followed by \"- \" lines, each a change to the owner's section and the verdicts that argue for it.");
        b.AppendLine();
        b.AppendLine("# The owner's section (read only)");
        b.AppendLine();
        b.AppendLine(owner.Trim().Length > 0 ? owner.Trim() : "(empty)");
        b.AppendLine();
        b.AppendLine("# The current Learned section");
        b.AppendLine();
        b.AppendLine(learned.Trim().Length > 0 ? learned.Trim() : "(empty - this is the first fold)");
        b.AppendLine();
        b.AppendLine("# Verdicts since the last fold");
        foreach (var p in pending)
        {
            var x = p.Proposal;
            b.AppendLine();
            b.AppendLine($"## {x.Kind}{(x.Area is { Length: > 0 } a ? $", area {a}" : "")}{(x.Round > 0 ? $", from a recheck ({x.RecheckKind})" : "")} - on the direction \"{Cap(p.Discovery, 160)}\"");
            b.AppendLine($"Title: {x.Title}");
            if (x.Why is { Length: > 0 }) b.AppendLine($"Why: {Cap(x.Why, 300)}");
            if (x.Body.Length > 0) b.AppendLine($"Body: {Cap(x.Body.Replace('\n', ' '), 500)}");
            b.AppendLine($"Verdict: {VerdictLine(x, p.Events)}");
        }
        return b.ToString();
    }

    /// <summary>The verdict as it stands, in words, with what came before it when the owner changed their mind.</summary>
    public static string VerdictLine(DiscoveryProposal p, IReadOnlyList<DiscoveryVerdictEvent> events)
    {
        var parts = new List<string>();
        var restored = events.Any(e => e.Kind == VerdictEventKind.Restored);
        if (restored) parts.Add($"RESTORED from the dropped list{(p.DropReason is { Length: > 0 } dr ? $" (the agent had dropped it as: {Cap(dr, 160)})" : "")}");
        switch (p.Status)
        {
            case ProposalStatus.Approved:
            case ProposalStatus.Filed:
                var last = events.LastOrDefault(e => e.Kind is VerdictEventKind.Approved or VerdictEventKind.Filed);
                var edit = last is { Edited: true }
                    ? $", EDITED before approval ({last.EditDistance} of {Math.Max(last.DraftLength, 1)} characters changed"
                      + (last.KindChanged ? $"; kind changed from {p.DraftKind} to {p.Kind}" : "")
                      + (p.DraftTitle != p.Title ? $"; the draft's title was \"{Cap(p.DraftTitle, 160)}\"" : "") + ")"
                    : "";
                parts.Add("APPROVED" + edit);
                break;
            case ProposalStatus.Rejected:
                var teaches = p.RejectReason is RejectReason.Generic or RejectReason.Wrong;
                parts.Add($"REJECTED as {Reason(p.RejectReason)}{(p.RejectNote is { Length: > 0 } n ? $" - the owner's note: \"{Cap(n, 300)}\"" : "")}"
                    + (teaches || restored ? "" : " (teaches nothing)"));
                break;
            case ProposalStatus.Snoozed:
                parts.Add("SNOOZED (teaches nothing)");
                break;
        }
        if (p.NewToMe == true) parts.Add("NEW TO ME");
        var minds = events.Count(e => e.Kind is VerdictEventKind.Approved or VerdictEventKind.Rejected or VerdictEventKind.Snoozed);
        if (minds > 1) parts.Add($"after {minds - 1} change{(minds == 2 ? "" : "s")} of mind");
        return string.Join("; ", parts);
    }

    private static string Reason(RejectReason? r) => r switch
    {
        RejectReason.Generic => "GENERIC",
        RejectReason.Wrong => "WRONG",
        RejectReason.AlreadyDecided => "already decided",
        RejectReason.NotNow => "not now",
        _ => "no reason",
    };

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
