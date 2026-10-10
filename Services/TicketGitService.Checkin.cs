using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>What TFVC says about one of a ticket's files. See <see cref="CheckinFile.Tfvc"/>.</summary>
public static class TfvcState
{
    /// <summary>A pending change - it rides on the next check-in, and has not been checked in yet.</summary>
    public const string Pending = "pending";
    /// <summary>Only under Detected Changes. A check-in will NOT carry it until it is promoted.</summary>
    public const string Detected = "detected";
    /// <summary>Nothing pending, nothing detected: what the server holds matches the disk.</summary>
    public const string Clean = "clean";
    /// <summary>Not asked (nothing is staged yet), or tf's answer could not be read in full.</summary>
    public const string Unknown = "unknown";
    /// <summary>Outside every mapped folder - a git-only file, never a check-in item.</summary>
    public const string NotInTfvc = "not-in-tfvc";
}

/// <summary>
/// One of the ticket's files, as a check-in has to treat it: what git says happened to it, whether
/// TFVC holds it at all, and - once the merge is staged - what TFVC currently has pending for it.
/// A rename carries both halves, because TFVC sees it as two changes and both have to be promoted.
/// </summary>
public record CheckinFile(
    string Path,
    string Status,
    string? RenamedFrom,
    bool InTfvc,
    bool RenamedFromInTfvc,
    string Tfvc,
    string? TfvcChange,
    string? RenamedFromTfvc,
    string? RenamedFromTfvcChange,
    /// <summary>
    /// Set once the merge is staged, when the disk disagrees with it: <c>missing</c> - the merge
    /// has this file and the disk does not; <c>present</c> - the merge deletes it and it is still
    /// there. TFVC checks in the disk, so either way it cannot hold what git is about to record -
    /// and it says nothing about a file it never saw, which is why silence alone is not "clean".
    /// </summary>
    string? Disk = null,
    string? RenamedFromDisk = null);

/// <summary>
/// Where a TFVC check-in stands: the hub's state, the ticket's files, and TFVC's view of them.
/// </summary>
public record CheckinStatus(
    bool Ok,
    string? Error,
    string Branch,
    string BaseBranch,
    string RepoPath,
    /// <summary>What the hub has checked out. Empty on a detached HEAD.</summary>
    string HubBranch,
    bool OnBase,
    /// <summary>"none"; "this" - this ticket's merge is staged; "other" - some other merge is.</summary>
    string Merge,
    string? MergeHead,
    /// <summary>Commits the ticket's branch has gained since its merge was staged. They are not in it.</summary>
    int StagedBehind,
    /// <summary>
    /// Tracked files that differ from HEAD and are not the staged merge: with nothing staged, every
    /// modified tracked file (these block staging); with the merge staged, the unstaged ones - a Get
    /// Latest's landings, or local edits TFVC will also offer as pending changes.
    /// </summary>
    IReadOnlyList<GitChange> Dirty,
    IReadOnlyList<string> Unmerged,
    IReadOnlyList<CheckinFile> Files,
    /// <summary>Whether tf was asked at all. It is asked only once the merge is staged.</summary>
    bool TfvcChecked,
    string? TfvcError,
    IReadOnlyList<string> TfvcProblems,
    string? TfPath,
    IReadOnlyList<string> TfvcFolders,
    /// <summary>The project key, so the dialog can offer the project-level sync from here.</summary>
    string Project = "");

/// <summary>Outcome of staging or aborting. <paramref name="Blocking"/> is the refusal's file list.</summary>
public record CheckinStepResult(
    bool Ok,
    string? Error,
    bool Conflicted,
    IReadOnlyList<string> ConflictedFiles,
    IReadOnlyList<GitChange> Blocking,
    IReadOnlyList<string> Output);

/// <summary>
/// Outcome of recording a check-in. <paramref name="NotCheckedIn"/> is the refusal: one sentence per
/// file TFVC still has something pending or detected for.
/// </summary>
public record CheckinRecordResult(
    bool Ok,
    string? Error,
    IReadOnlyList<string> NotCheckedIn,
    string? CommitSha,
    string? CommitSubject,
    /// <summary>
    /// Tracked files still differing from the new HEAD. Normally a Get Latest's landings: TFVC has
    /// them, git main does not, and they want a "Sync with TFVC" commit of their own.
    /// </summary>
    IReadOnlyList<GitChange> LeftUnstaged,
    /// <summary>
    /// The ticket's own files among <paramref name="LeftUnstaged"/>. TFVC checks in the disk, not
    /// git's index, so an unstaged edit to one of these went to the server and is not in this commit.
    /// </summary>
    IReadOnlyList<string> TicketFilesDiffer,
    IReadOnlyList<string> Output,
    bool MovedToDone = false,
    string? PhaseNote = null);

/// <summary>
/// The TFVC half: delivering a ticket on a project whose real source control is TFVC.
///
/// <para>
/// The fleet works in a git mirror that shares its working tree with a TFVC local workspace, and
/// the human is the only writer to TFVC. So delivery is three steps with a person in the middle:
/// merge the ticket's branch into the hub WITHOUT committing, so the workspace sees the ticket's
/// files as changed; the human checks those in from Team Explorer; then the merge is committed to
/// git with the changeset number, so git main and TFVC say the same thing.
/// </para>
///
/// <para>
/// The middle step is where tickets get lost. TFVC notices an edited file by itself, but a new file,
/// a deleted one and both halves of a rename appear only under Detected Changes and have to be
/// promoted by hand - and a missed add in an SDK-style project builds fine on the server with the
/// code silently missing. So recording is gated on TFVC itself: <c>tf status</c> on exactly this
/// ticket's files must come back with nothing pending and nothing detected, or it refuses and names
/// each file. The checklist in the dialog is a reminder; this is the check.
/// </para>
///
/// <para>
/// Nothing here writes to TFVC. <c>tf status</c> is a read of the local workspace; every tf verb
/// that changes anything belongs to the human, and <c>data/tfvc-guard.ps1</c> says so to every
/// agent.
/// </para>
/// </summary>
public partial class TicketGitService
{
    /// <summary>Where Visual Studio 18 puts tf.exe. The fallback when <c>tfPath</c> is not configured.</summary>
    public const string DefaultTfPath =
        @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\TF.exe";

    private const string TfRelativeToVs =
        @"Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\TF.exe";

    /// <summary>
    /// Paths per <c>tf status</c>. Named files only - never <c>/recursive</c>, which over this tree
    /// walks the hub's node_modules into some 25,000 lines - and batched so a large ticket cannot
    /// run past the command-line limit.
    /// </summary>
    private const int TfPathsPerCall = 40;

    private static string? _vswhereTf;

    // ---- status -------------------------------------------------------------

    /// <summary>
    /// Where the check-in stands. Read-only: git reads in the hub, and - once this ticket's merge is
    /// staged - one <c>tf status</c> over its files.
    /// <para>
    /// Before staging, the file list is the branch against the base (what staging would bring in).
    /// After, it is what is actually staged, <c>git diff --cached</c>, because that is what TFVC is
    /// looking at and what the record step will commit.
    /// </para>
    /// </summary>
    public async Task<CheckinStatus> CheckinStatusAsync(string projectKey, string branch, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var repo = cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var baseBranch = ResolveBaseBranch(cfg);

        CheckinStatus Fail(string error) => new(
            false, error, branch, baseBranch, repo, "", false, "none", null, 0,
            Array.Empty<GitChange>(), Array.Empty<string>(), Array.Empty<CheckinFile>(),
            false, null, Array.Empty<string>(), null, cfg.TfvcFolders, projectKey);

        if (CheckinGuard(cfg, repo, branch, baseBranch) is { } refusal) return Fail(refusal);
        if (!(await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).Ok)
            return Fail($"Branch '{branch}' does not exist in {repo}.");

        var hub = await ReadHubAsync(repo, branch, ct);
        var staged = hub.Merge == "this";
        var files = await ReadCheckinFilesAsync(repo, cfg, staged ? "--cached" : $"{baseBranch}...{branch}", staged, ct);

        TfvcView? view = staged ? await QueryTfvcAsync(repo, files, ct) : null;
        var tfPath = view?.TfPath ?? (await ResolveTfPathAsync(ct)).Path;

        return new CheckinStatus(
            true, null, branch, baseBranch, repo, hub.Branch, hub.Branch == baseBranch,
            hub.Merge, hub.MergeHead, hub.StagedBehind, hub.Dirty(staged), hub.Unmerged,
            view?.Files ?? files, view is not null, view?.Error, view?.Problems ?? new List<string>(),
            tfPath, cfg.TfvcFolders, projectKey);
    }

    // ---- stage --------------------------------------------------------------

    /// <summary>
    /// <c>git merge --no-ff --no-commit &lt;branch&gt;</c> in the hub, onto the base: the ticket's
    /// files land on disk and in the index, and TFVC's local workspace sees them as changed.
    /// <para>
    /// Refuses unless the hub is on the base, nothing else is mid-merge or mid-rebase, and no tracked
    /// file is modified - the merge has to go onto a clean base so that what TFVC offers for check-in
    /// is this ticket and nothing else. Untracked files do not block: the merge refuses by itself to
    /// overwrite one. A conflict is rolled back on the spot, the way the rebase button does it -
    /// resolving it is a judgement about the code, and this has none.
    /// </para>
    /// </summary>
    public async Task<CheckinStepResult> StageCheckinAsync(string projectKey, string branch, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var repo = cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var baseBranch = ResolveBaseBranch(cfg);
        var output = new List<string>();

        CheckinStepResult Fail(string error, IReadOnlyList<GitChange>? blocking = null) =>
            new(false, error, false, Array.Empty<string>(), blocking ?? Array.Empty<GitChange>(), output);

        if (CheckinGuard(cfg, repo, branch, baseBranch) is { } refusal) return Fail(refusal);
        if (!(await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).Ok)
            return Fail($"Branch '{branch}' does not exist in {repo}.");

        var hub = await ReadHubAsync(repo, branch, ct);
        if (hub.Branch != baseBranch)
            return Fail(hub.Branch.Length == 0
                ? $"The hub at {repo} is on a detached HEAD. Check out {baseBranch} there first - a check-in merges onto the base."
                : $"The hub at {repo} is on '{hub.Branch}', not '{baseBranch}'. Check out {baseBranch} there first - a check-in merges onto the base.");
        if (hub.Merge == "this")
            return Fail($"This ticket's merge is already staged in {repo}. Check it in from Team Explorer, then record the changeset.");
        if (hub.Merge == "other")
            return Fail($"A merge is already in progress in {repo} (MERGE_HEAD {Short(hub.MergeHead!)}), and it is not this ticket's. " +
                        "Finish or abort that one first.");
        if (await RebaseInProgressAsync(repo, ct))
            return Fail($"A rebase is in progress in {repo}. Finish or abort it first.");

        var dirty = hub.Dirty(staged: false);
        if (dirty.Count > 0)
        {
            var word = dirty.Count == 1 ? "file is" : "files are";
            return Fail(
                $"{dirty.Count} tracked {word} modified in {repo}. The merge has to go onto a clean {baseBranch} so the check-in " +
                "carries this ticket and nothing else. Sync with TFVC commits what a Get Latest brought in; your own local " +
                "edits can be stashed and re-applied after the check-in.",
                dirty);
        }

        var (_, ahead) = await CountAsync(repo, baseBranch, branch, ct);
        if (ahead == 0) return Fail($"'{branch}' has nothing that {baseBranch} does not already have.");

        output.Add($"git -C \"{repo}\" merge --no-ff --no-commit {branch}");
        var merge = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "-c", "core.editor=true", "merge", "--no-ff", "--no-commit", branch);
        AddLines(output, merge);

        var mergeHead = (await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", "MERGE_HEAD")).Stdout.Trim();
        if (!merge.Ok)
        {
            var conflicts = await ConflictedPathsAsync(repo, ct);
            if (mergeHead.Length > 0)
            {
                var abort = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "merge", "--abort");
                AddLines(output, abort);
                if (!abort.Ok)
                    return new CheckinStepResult(false,
                        $"The merge stopped and could not be rolled back - {repo} is still mid-merge. Sort it out by hand: git -C \"{repo}\" merge --abort",
                        conflicts.Count > 0, conflicts, Array.Empty<GitChange>(), output);
            }

            if (conflicts.Count > 0)
            {
                var word = conflicts.Count == 1 ? "file" : "files";
                return new CheckinStepResult(false,
                    $"Merging '{branch}' into {baseBranch} conflicts in {conflicts.Count} {word}. Rolled back - nothing was changed. " +
                    "Rebase the branch onto the current base, then stage again.",
                    true, conflicts, Array.Empty<GitChange>(), output);
            }
            return Fail(Explain(merge, $"could not merge '{branch}'"));
        }

        if (mergeHead.Length == 0)
            return Fail($"git merged nothing - '{branch}' is already in {baseBranch}.");

        _log.LogInformation("Staged {Branch} for TFVC check-in in {Repo}", branch, repo);
        return new CheckinStepResult(true, null, false, Array.Empty<string>(), Array.Empty<GitChange>(), output);
    }

    // ---- abort --------------------------------------------------------------

    /// <summary>
    /// <c>git merge --abort</c>: the hub back to the base as it was before staging.
    /// <para>
    /// Only before the check-in. Once TFVC holds the ticket, putting the old versions back on disk
    /// reads to the workspace as a fresh set of edits that undo it - the next check-in would push
    /// them. So when tf can be asked and says every one of the ticket's TFVC files is already clean,
    /// this refuses and points at the record step instead.
    /// </para>
    /// </summary>
    public async Task<CheckinStepResult> AbortCheckinAsync(string projectKey, string branch, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var repo = cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var baseBranch = ResolveBaseBranch(cfg);
        var output = new List<string>();

        CheckinStepResult Fail(string error) =>
            new(false, error, false, Array.Empty<string>(), Array.Empty<GitChange>(), output);

        if (CheckinGuard(cfg, repo, branch, baseBranch) is { } refusal) return Fail(refusal);

        var hub = await ReadHubAsync(repo, branch, ct);
        if (hub.Merge == "none") return Fail($"Nothing is staged in {repo} - there is no merge to abort.");
        if (hub.Merge == "other")
            return Fail($"The merge in progress in {repo} (MERGE_HEAD {Short(hub.MergeHead!)}) is not this ticket's, so this will not abort it.");

        var files = await ReadCheckinFilesAsync(repo, cfg, "--cached", checkDisk: true, ct);
        var view = await QueryTfvcAsync(repo, files, ct);
        var tfvcFiles = view.Files.Where(f => f.InTfvc).ToList();
        if (view.Error is null && view.Problems.Count == 0 && tfvcFiles.Count > 0
            && tfvcFiles.All(f => f.Tfvc == TfvcState.Clean && (f.RenamedFromTfvc is null or TfvcState.Clean or TfvcState.NotInTfvc)))
            return Fail("TFVC has nothing pending for any of this ticket's files, so it looks checked in already. Aborting now would put " +
                        "the old versions back on disk, and TFVC would see that as edits undoing the check-in. Record the changeset instead.");

        output.Add($"git -C \"{repo}\" merge --abort");
        var abort = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "merge", "--abort");
        AddLines(output, abort);
        if (!abort.Ok) return Fail(Explain(abort, "could not abort the merge"));

        _log.LogInformation("Aborted the staged check-in of {Branch} in {Repo}", branch, repo);
        return new CheckinStepResult(true, null, false, Array.Empty<string>(), Array.Empty<GitChange>(), output);
    }

    // ---- record -------------------------------------------------------------

    /// <summary>
    /// The human has checked in; make git say so. Verifies first, then commits the staged merge with
    /// the changeset number in its message.
    /// <para>
    /// The verification is the point. <c>tf status</c> on exactly the ticket's TFVC files - both
    /// halves of every rename - must report nothing pending and nothing detected; anything else is
    /// refused with a sentence per file, because a file still under Detected Changes is a file the
    /// server does not have. An answer tf gave that this could not read in full refuses too: a row
    /// lost in parsing looks exactly like a clean file.
    /// </para>
    /// <para>
    /// Then a plain <c>git commit</c> - the index only, never <c>-a</c>. The index holds exactly the
    /// staged merge, so whatever else a Get Latest has since put in the working tree stays out of
    /// it, and is reported back as wanting a sync commit of its own.
    /// </para>
    /// </summary>
    public async Task<CheckinRecordResult> RecordCheckinAsync(
        string projectKey, string branch, int changeset, string title, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var repo = cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var baseBranch = ResolveBaseBranch(cfg);
        var output = new List<string>();
        var none = Array.Empty<string>();

        CheckinRecordResult Fail(string error, IReadOnlyList<string>? notCheckedIn = null) =>
            new(false, error, notCheckedIn ?? none, null, null, Array.Empty<GitChange>(), none, output);

        if (CheckinGuard(cfg, repo, branch, baseBranch) is { } refusal) return Fail(refusal);
        if (changeset <= 0) return Fail("A changeset number is required - the one Team Explorer reported for this check-in.");

        var hub = await ReadHubAsync(repo, branch, ct);
        if (hub.Merge == "none")
            return Fail($"Nothing of this ticket's is staged in {repo}. Stage it, check it in from Team Explorer, then record the changeset.");
        if (hub.Merge == "other")
            return Fail($"The merge in progress in {repo} (MERGE_HEAD {Short(hub.MergeHead!)}) is not this ticket's.");
        if (hub.Unmerged.Count > 0)
            return Fail($"{hub.Unmerged.Count} file(s) in {repo} are still unmerged: {string.Join(", ", hub.Unmerged)}.");

        var files = await ReadCheckinFilesAsync(repo, cfg, "--cached", checkDisk: true, ct);
        var view = await QueryTfvcAsync(repo, files, ct);
        if (view.Error is not null)
            return Fail($"Could not ask TFVC whether the check-in is complete: {view.Error} Nothing was recorded.");

        var missing = view.Problems.Select(p => $"tf said something this could not read, so the check-in cannot be confirmed: {p}").ToList();
        missing.AddRange(NotCheckedIn(view.Files));
        if (missing.Count > 0)
        {
            var word = missing.Count == 1 ? "thing" : "things";
            return Fail($"Not recorded - TFVC still has {missing.Count} {word} this check-in did not carry. " +
                        "Nothing in git was changed.", missing);
        }

        var cleanTitle = Regex.Replace(title ?? "", @"\s+", " ").Trim();
        if (cleanTitle.Length == 0) cleanTitle = branch;
        var subject = $"Changeset {changeset}: {cleanTitle}";
        var body = $"Merges {branch} into {baseBranch}. Checked in to TFVC by hand as changeset {changeset}; " +
                   "this commit brings the git mirror level with it.";

        output.Add($"git -C \"{repo}\" commit -m \"{subject}\"");
        var commit = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "-c", "core.editor=true", "commit", "-m", subject, "-m", body);
        AddLines(output, commit);
        if (!commit.Ok) return Fail(Explain(commit, "could not commit the merge"));

        var sha = (await GitAsync(repo, ct, "rev-parse", "HEAD")).Stdout.Trim();
        var after = await ReadHubAsync(repo, branch, ct);
        var left = after.Dirty(staged: false);
        var ticketPaths = new HashSet<string>(
            files.SelectMany(f => f.RenamedFrom is null ? new[] { f.Path } : new[] { f.Path, f.RenamedFrom }),
            StringComparer.OrdinalIgnoreCase);

        _log.LogInformation("Recorded TFVC changeset {Changeset} for {Branch} in {Repo} as {Sha}", changeset, branch, repo, Short(sha));
        return new CheckinRecordResult(true, null, none, sha, subject, left,
            left.Where(c => ticketPaths.Contains(c.Path)).Select(c => c.Path).ToList(), output);
    }

    /// <summary>
    /// One sentence per file TFVC is still holding something for - the reason a record is refused,
    /// written so the person reading it knows what to go and click.
    /// </summary>
    public static List<string> NotCheckedIn(IEnumerable<CheckinFile> files)
    {
        var said = new List<string>();
        foreach (var f in files)
        {
            if (f.InTfvc && f.Disk is { } d) said.Add(DiskSentence(f.Path, d));
            if (f.InTfvc && Sentence(f.Path, f.Tfvc, f.TfvcChange) is { } s) said.Add(s);
            if (f.RenamedFrom is not null && f.RenamedFromInTfvc && f.RenamedFromDisk is { } rd) said.Add(DiskSentence(f.RenamedFrom, rd));
            if (f.RenamedFrom is not null && f.RenamedFromInTfvc
                && Sentence(f.RenamedFrom, f.RenamedFromTfvc, f.RenamedFromTfvcChange, oldHalfOf: f.Path) is { } o)
                said.Add(o);
        }
        return said;

        // TFVC reports nothing about a file it has never seen, so a new file that has left the disk
        // reads exactly like one that was checked in. The disk is the tiebreak: it is what TFVC
        // checks in, so if it disagrees with the merge, the check-in cannot have matched it.
        static string DiskSentence(string path, string disk) => disk == "missing"
            ? $"{path} is in this merge but is not on disk, so TFVC cannot have checked it in. Put it back with " +
              $"git checkout -- \"{path}\" and check it in - or, if it was dropped on purpose, take it out of the merge first."
            : $"{path} is deleted by this merge but is still on disk, so TFVC will not have checked the delete in.";

        static string? Sentence(string path, string? state, string? change, string? oldHalfOf = null)
        {
            var what = change ?? "change";
            if (state == TfvcState.Pending)
                return $"{path} is still a pending {what} - it has not been checked in.";
            if (state != TfvcState.Detected) return null;

            if (what.Contains("add", StringComparison.OrdinalIgnoreCase))
                return $"{path} was never promoted - the server does not have it. Promote it from Detected Changes in Team Explorer and check it in.";
            if (what.Contains("delete", StringComparison.OrdinalIgnoreCase))
                return oldHalfOf is null
                    ? $"{path} was deleted here but the delete was never promoted - the server still has it."
                    : $"{path} was renamed to {oldHalfOf}, but the delete of the old name was never promoted - the server still has it.";
            return $"{path} is only a detected {what} - promote it in Team Explorer and check it in.";
        }
    }

    // ---- reading the hub ----------------------------------------------------

    private record HubEntry(char Index, char Worktree, string Path)
    {
        public bool Unmerged =>
            Index == 'U' || Worktree == 'U' || (Index == 'A' && Worktree == 'A') || (Index == 'D' && Worktree == 'D');

        public GitChange ToChange() => new($"{Index}{Worktree}".Trim(), Path);
    }

    private record HubState(string Branch, string? MergeHead, string Merge, int StagedBehind, List<HubEntry> Entries)
    {
        public IReadOnlyList<string> Unmerged => Entries.Where(e => e.Unmerged).Select(e => e.Path).ToList();

        /// <summary>
        /// Tracked changes outside the staged merge. With a merge staged the index IS the merge, so
        /// only the working-tree column counts; without one, anything at all.
        /// </summary>
        public IReadOnlyList<GitChange> Dirty(bool staged) => Entries
            .Where(e => !e.Unmerged && (!staged || e.Worktree != ' '))
            .Select(e => e.ToChange())
            .ToList();
    }

    /// <summary>The hub's branch, whether a merge is staged and whose, and its tracked changes.</summary>
    private async Task<HubState> ReadHubAsync(string repo, string branch, CancellationToken ct)
    {
        var headRead = GitAsync(repo, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
        var mergeRead = GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", "MERGE_HEAD");
        var tipRead = GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        // Tracked only. An untracked file is not this merge's business, and the hub of a TFVC mirror
        // carries plenty that git was never told about.
        var statusRead = GitAsync(repo, ct, "status", "--porcelain=v1", "-z", "--untracked-files=no");
        await Task.WhenAll(headRead, mergeRead, tipRead, statusRead);

        var mergeHead = (await mergeRead).Ok ? (await mergeRead).Stdout.Trim() : "";
        var tip = (await tipRead).Ok ? (await tipRead).Stdout.Trim() : "";

        var merge = "none";
        var behind = 0;
        if (mergeHead.Length > 0)
        {
            if (mergeHead == tip) merge = "this";
            // Staged, and then the agent committed again. Still this ticket's merge - but of an older
            // tip, and the difference is worth saying out loud.
            else if (tip.Length > 0 && (await GitAsync(repo, ct, "merge-base", "--is-ancestor", mergeHead, tip)).Ok)
            {
                merge = "this";
                behind = (await CountAsync(repo, mergeHead, tip, ct)).Right;
            }
            else merge = "other";
        }

        var head = await headRead;
        return new HubState(head.Ok ? head.Stdout.Trim() : "", mergeHead.Length > 0 ? mergeHead : null,
            merge, behind, ParsePorcelainZ((await statusRead).Stdout));
    }

    /// <summary><c>git status --porcelain=v1 -z</c>: <c>XY path</c>, and a rename's old path as its own record after it.</summary>
    private static List<HubEntry> ParsePorcelainZ(string z)
    {
        var tokens = z.Split('\0');
        var list = new List<HubEntry>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t.Length < 4) continue;
            list.Add(new HubEntry(t[0], t[1], t[3..]));
            if (t[0] == 'R' || t[0] == 'C') i++;
        }
        return list;
    }

    /// <summary>
    /// The ticket's files over <paramref name="range"/> (<c>--cached</c>, or <c>base...branch</c>), not
    /// yet asked about. <paramref name="checkDisk"/> once the merge is staged: before that the disk
    /// is the base, and a new file not existing yet is the expected state rather than a fault.
    /// </summary>
    private async Task<List<CheckinFile>> ReadCheckinFilesAsync(
        string repo, ProjectConfig cfg, string range, bool checkDisk, CancellationToken ct)
    {
        string? DiskOf(string rel, bool shouldExist)
        {
            var exists = File.Exists(Path.Combine(repo, rel.Replace('/', Path.DirectorySeparatorChar)));
            return exists == shouldExist ? null : shouldExist ? "missing" : "present";
        }

        var diff = await GitAsync(repo, ct, "diff", "--name-status", "-M", "-z", range);
        return ParseNameStatus(diff.Stdout)
            .Select(s =>
            {
                var inTfvc = cfg.InTfvc(s.Path);
                var fromInTfvc = s.From is not null && cfg.InTfvc(s.From);
                return new CheckinFile(
                    s.Path, s.Status, s.From, inTfvc, fromInTfvc,
                    inTfvc ? TfvcState.Unknown : TfvcState.NotInTfvc, null,
                    s.From is null ? null : fromInTfvc ? TfvcState.Unknown : TfvcState.NotInTfvc, null,
                    checkDisk && inTfvc ? DiskOf(s.Path, shouldExist: s.Status != FileStatus.Deleted) : null,
                    checkDisk && fromInTfvc ? DiskOf(s.From!, shouldExist: false) : null);
            })
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- asking TFVC --------------------------------------------------------

    private record TfvcView(List<CheckinFile> Files, string? Error, List<string> Problems, string? TfPath);

    /// <summary>
    /// <c>tf status &lt;paths&gt; /format:brief</c> over exactly these files, and each file's answer.
    /// <para>
    /// A file tf says nothing about is clean - but only when the whole answer was read. If any line
    /// was not understood, a file with no row is <see cref="TfvcState.Unknown"/> rather than clean,
    /// because the row that was not understood may have been that file's.
    /// </para>
    /// </summary>
    private async Task<TfvcView> QueryTfvcAsync(string repo, List<CheckinFile> files, CancellationToken ct)
    {
        // Every path tf is asked about, keyed as tf will print it, with whether it is still on disk.
        var asks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (f.InTfvc) asks[LocalPathOf(repo, f.Path)] = f.Status == FileStatus.Deleted;
            if (f.RenamedFrom is not null && f.RenamedFromInTfvc) asks[LocalPathOf(repo, f.RenamedFrom)] = true;
        }

        var (tf, tfError) = await ResolveTfPathAsync(ct);
        if (asks.Count == 0) return new TfvcView(files, null, new List<string>(), tf);
        if (tf is null) return new TfvcView(files, tfError, new List<string>(), null);

        var rows = new Dictionary<string, List<TfStatusEntry>>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (var chunk in asks.Keys.Chunk(TfPathsPerCall))
        {
            var args = new List<string> { "status" };
            args.AddRange(chunk);
            args.Add("/format:brief");
            var run = await RunAsync(tf, repo, TimeSpan.FromSeconds(90), ct, args.ToArray());
            if (run.ExitCode == -1 && run.Stdout.Length == 0)
                return new TfvcView(files, $"tf status did not run: {run.Stderr.Trim()}", problems, tf);

            var report = TfStatusParser.Parse(run.Stdout);
            var said = report.Problems.Concat(run.Stderr.Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0));
            foreach (var line in said)
            {
                // A file no longer on disk that TFVC has also stopped holding - a delete already checked
                // in - has nothing pending, and tf may say "not found" about it instead of saying nothing.
                if (IsGoneAndUnknown(line, chunk, asks)) continue;
                problems.Add(line);
            }

            if (!report.CountsAgree)
                problems.Add($"tf's summary counts {report.PendingCount ?? 0} pending and {report.DetectedCount ?? 0} detected " +
                             $"change(s), but {report.Entries.Count} row(s) could be read.");
            if (!report.NoPendingChanges && report.Entries.Count == 0 && report.Problems.Count == 0 && run.Stderr.Trim().Length == 0)
                problems.Add($"tf status printed nothing this could read (exit code {run.ExitCode}).");

            foreach (var e in report.Entries)
            {
                var key = NormalizeLocal(e.LocalPath);
                if (!asks.ContainsKey(key))
                {
                    problems.Add($"tf reported {e.LocalPath} ({e.Change}), which is not one of this ticket's files.");
                    continue;
                }
                if (!rows.TryGetValue(key, out var list)) rows[key] = list = new List<TfStatusEntry>();
                list.Add(e);
            }
        }

        (string State, string? Change) Answer(string rel)
        {
            if (!rows.TryGetValue(LocalPathOf(repo, rel), out var mine) || mine.Count == 0)
                return (problems.Count == 0 ? TfvcState.Clean : TfvcState.Unknown, null);
            var pending = mine.Where(e => !e.Detected).ToList();
            var chosen = pending.Count > 0 ? pending : mine;
            return (pending.Count > 0 ? TfvcState.Pending : TfvcState.Detected,
                string.Join(", ", chosen.Select(e => e.Change).Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        var answered = files.Select(f =>
        {
            var now = f.InTfvc ? Answer(f.Path) : (State: TfvcState.NotInTfvc, Change: (string?)null);
            var old = f.RenamedFrom is null ? (State: (string?)null, Change: (string?)null)
                : f.RenamedFromInTfvc ? Answer(f.RenamedFrom) : (TfvcState.NotInTfvc, null);
            return f with
            {
                Tfvc = now.State, TfvcChange = now.Change,
                RenamedFromTfvc = old.State, RenamedFromTfvcChange = old.Change,
            };
        }).ToList();

        return new TfvcView(answered, null, problems, tf);
    }

    private static readonly Regex NotFound = new(@"could not be found|does not exist|not found", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsGoneAndUnknown(string line, IEnumerable<string> chunk, Dictionary<string, bool> asks) =>
        NotFound.IsMatch(line)
        && chunk.Any(p => asks[p] && line.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static string LocalPathOf(string repo, string repoRelative) =>
        NormalizeLocal(Path.Combine(repo, repoRelative.Replace('/', Path.DirectorySeparatorChar)));

    private static string NormalizeLocal(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// Where tf.exe is. It is not on PATH - Visual Studio keeps it deep inside its own install - so:
    /// the global <c>tfPath</c> when one is configured, else Visual Studio 18's usual location, else
    /// whatever <c>vswhere</c> finds in the newest install. A configured path that does not exist is
    /// an error rather than a cue to go looking, because it is a setting somebody got wrong.
    /// </summary>
    public async Task<(string? Path, string? Error)> ResolveTfPathAsync(CancellationToken ct = default)
    {
        var global = _registry.GetGlobalConfig();
        if (global.TryGetValue("tfPath", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            && v.GetString()?.Trim() is { Length: > 0 } configured)
            return File.Exists(configured)
                ? (configured, null)
                : (null, $"tfPath in data/config.json points at '{configured}', which does not exist.");

        if (File.Exists(DefaultTfPath)) return (DefaultTfPath, null);
        if (_vswhereTf is not null && File.Exists(_vswhereTf)) return (_vswhereTf, null);

        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            var found = await RunAsync(vswhere, Path.GetDirectoryName(vswhere)!, TimeSpan.FromSeconds(20), ct,
                "-latest", "-products", "*", "-find", TfRelativeToVs);
            var hit = found.Stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && File.Exists(l));
            if (hit is not null) return (_vswhereTf = hit, null);
        }

        return (null, "TF.exe was not found - not at Visual Studio 18's usual path, and vswhere found no Team Explorer. " +
                      "Set \"tfPath\" in data/config.json.");
    }

    /// <summary>The refusals every check-in step shares: a git project, then the general ones.</summary>
    private string? CheckinGuard(ProjectConfig cfg, string repo, string branch, string baseBranch)
    {
        if (!cfg.IsTfvc)
            return $"{cfg.DisplayName} is a git project - its branches go out as pull requests, not TFVC check-ins.";
        if (string.IsNullOrWhiteSpace(branch)) return "This ticket has no branch yet.";
        if (!IsGitRepo(repo)) return $"{cfg.DisplayName} has no git repo at '{repo}'.";
        if (branch == baseBranch) return $"'{branch}' is the base branch - there is nothing to check in.";
        return null;
    }
}
