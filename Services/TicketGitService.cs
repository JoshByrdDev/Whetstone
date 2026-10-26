using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>One entry from <c>git status --porcelain</c>: the two-letter code and the path.</summary>
public record GitChange(string Status, string Path);

/// <summary>
/// What happened when the human asked to put their own checkout on a ticket's branch.
/// <paramref name="Pending"/> is populated only on the refusal that names it as the reason.
/// </summary>
public record CheckoutResult(
    bool Ok,
    string? Error,
    string Branch,
    string RepoPath,
    /// <summary>True when the branch was taken with <c>--ignore-other-worktrees</c>.</summary>
    bool Forced,
    /// <summary>Agent worktree that also holds this branch, when one does.</summary>
    string? SharedWith,
    /// <summary>Created a local branch tracking <c>origin/&lt;branch&gt;</c> because none existed here.</summary>
    bool CreatedFromRemote,
    IReadOnlyList<GitChange> Pending);

public record PrCommit(string Sha, string Subject, string Author, string Date);

/// <summary>
/// One changed file, ranked. <paramref name="Patch"/> is that file's hunks alone; it is empty with
/// <paramref name="PatchOmitted"/> set when the diff was too large to send in full — and the budget
/// is spent in <paramref name="Tier"/> order, so the files worth reading are never the ones dropped.
/// </summary>
public record PrFile(
    string Path,
    int Added,
    int Removed,
    bool Binary,
    string Tier,
    int Score,
    IReadOnlyList<string> Reasons,
    string Patch,
    bool PatchOmitted,
    /// <summary>Set when git resolved this as a rename, so the UI can show where it came from.</summary>
    string? RenamedFrom = null,
    /// <summary>
    /// <c>added</c>, <c>deleted</c>, <c>renamed</c> or <c>modified</c>, from <c>git diff --name-status</c>.
    /// A reviewer reads it as context; a TFVC check-in reads it as instructions, because TFVC picks
    /// up an edit by itself and has to be told about every other kind.
    /// </summary>
    string Status = FileStatus.Modified);

/// <summary>The four answers <see cref="PrFile.Status"/> gives.</summary>
public static class FileStatus
{
    public const string Added = "added";
    public const string Deleted = "deleted";
    public const string Renamed = "renamed";
    public const string Modified = "modified";
}

/// <summary>One record of <c>git diff --name-status -z</c>. <paramref name="From"/> is a rename's old path.</summary>
public record NameStatus(string Path, string Status, string? From);

/// <summary>
/// Everything the PR dialog needs to decide whether this branch is fit to raise: how it sits
/// against the latest base, what is on it, and the diff itself.
/// </summary>
public record PrPreflight(
    bool Ok,
    string? Error,
    string Branch,
    string BaseBranch,
    string RepoPath,
    /// <summary>
    /// The project's editor URL template - <c>{path}</c> and <c>{line}</c> - so a file in the diff
    /// can be opened where it actually lives. Set per project with <c>ideUrl</c>; VS Code by default.
    /// </summary>
    string IdeUrl,
    /// <summary>"github", "azure-devops", "other", or "none" when there is no origin.</summary>
    string Provider,
    string? RemoteUrl,
    /// <summary>
    /// Whether the base comparison has been made at all. False on the local-only read the dialog
    /// opens with: the counts below are then last-known values, carried so the dialog has something
    /// to draw, and NOT an answer to "is this branch fit to raise". The caller must treat them as
    /// provisional until <see cref="TicketGitService.BaseCheckAsync"/> replaces them.
    /// </summary>
    bool BaseChecked,
    /// <summary>Whether <c>git fetch</c> reached the remote. False means every number below is local-only.</summary>
    bool Fetched,
    string? FetchError,
    /// <summary>The ref the comparison actually used — <c>origin/main</c>, or plain <c>main</c> when unfetched.</summary>
    string BaseRef,
    /// <summary>Commits on the base that this branch does not have. Zero is "based off the latest".</summary>
    int Behind,
    /// <summary>Commits on this branch that the base does not have.</summary>
    int Ahead,
    bool RemoteBranchExists,
    /// <summary>True when <c>origin/&lt;branch&gt;</c> is already at the local tip — nothing left to push.</summary>
    bool RemoteUpToDate,
    /// <summary>
    /// Commits on <c>origin/&lt;branch&gt;</c> that the local branch does not have. Non-zero means the
    /// two have diverged rather than the remote simply trailing — which is what a rebase leaves
    /// behind, and the one case where an ordinary push is rejected and only a force will move it.
    /// </summary>
    int RemoteAhead,
    /// <summary>Uncommitted files in the agent's worktree. They will NOT be in the PR.</summary>
    int WorktreeDirtyFiles,
    string? WorktreePath,
    IReadOnlyList<PrCommit> Commits,
    /// <summary>
    /// The body of the branch's single commit — everything under the subject line — or null when
    /// the branch is not exactly one commit, or that commit has no body.
    /// <para>
    /// This is the best description of the change that will ever exist for this branch. The rule
    /// for this pipeline is one ticket, one commit, and refinement writes that message to a spec:
    /// a lead paragraph saying what the change delivers, then bullets for the specifics. A branch
    /// carrying more than one commit has not been through refinement yet, so its messages are
    /// still the provisional ones implementation and QA left behind — worth nothing to a reviewer,
    /// which is why this is null rather than a concatenation.
    /// </para>
    /// </summary>
    string? CommitBody,
    /// <summary>Ranked: Major first, then by score. See <see cref="DiffSignificance"/> for why.</summary>
    IReadOnlyList<PrFile> Files,
    /// <summary>True when at least one file's hunks were dropped to keep the response a sane size.</summary>
    bool PatchTruncated)
{
    /// <summary>Properly based off the latest base: nothing on the base is missing from this branch.</summary>
    public bool UpToDateWithBase => Behind == 0;
}

/// <summary>
/// The half of the preflight that has to go to the network: the fetch, and the four counts whose
/// answers are only worth having fresh.
///
/// <para>
/// Split out because it is also the slow half — one round trip to the remote against a handful of
/// local reads — and the dialog has plenty to show while it runs. What it must NOT do is let the
/// dialog act early: every gate on raising a PR lives in these numbers, so until this lands the
/// answer is "not known yet" rather than the last one we happened to have.
/// </para>
/// </summary>
public record PrBaseCheck(
    bool Ok,
    string? Error,
    bool Fetched,
    string? FetchError,
    string BaseRef,
    int Behind,
    int Ahead,
    bool RemoteBranchExists,
    bool RemoteUpToDate,
    int RemoteAhead);

/// <summary>
/// Outcome of raising the PR. <paramref name="Mode"/> is "gh" when the PR now exists, "web" when
/// the branch is pushed and <paramref name="Url"/> is the prefilled create-PR page to finish on.
/// </summary>
public record PrCreateResult(
    bool Ok,
    string? Error,
    bool Pushed,
    string? Url,
    string Mode,
    IReadOnlyList<string> Output);

/// <summary>
/// Outcome of replaying a branch onto the latest base.
///
/// <para>
/// <paramref name="Conflicted"/> is the expected refusal rather than a fault: the rebase was rolled
/// back and the branch is exactly where it was, with <paramref name="ConflictedFiles"/> naming what
/// a human now has to decide. Any other <c>Ok=false</c> is a refusal that happened before git was
/// asked to move anything.
/// </para>
/// </summary>
public record RebaseResult(
    bool Ok,
    string? Error,
    /// <summary>The rebase hit conflicts and was aborted. Nothing changed.</summary>
    bool Conflicted,
    IReadOnlyList<string> ConflictedFiles,
    string Branch,
    string BaseRef,
    /// <summary>Where the branch pointed before. The one command that undoes this is a reset to it.</summary>
    string? PreviousSha,
    string? NewSha,
    /// <summary>The working tree the rebase ran in — the hub, or the worktree that holds the branch.</summary>
    string RanIn,
    IReadOnlyList<string> Output);

/// <summary>
/// Outcome of merging a ticket's branch into the base in the hub, on a project that does not raise
/// PRs. <paramref name="Conflicted"/> is the expected refusal: the merge was aborted and the base is
/// where it was. <paramref name="AheadOfOrigin"/> is what a push of the base would now publish.
/// </summary>
public record MergeResult(
    bool Ok,
    string? Error,
    bool Conflicted,
    IReadOnlyList<string> ConflictedFiles,
    string Branch,
    string BaseBranch,
    string RepoPath,
    string? MergeSha,
    /// <summary>The local base trailed origin and was fast-forwarded before the merge.</summary>
    bool FastForwardedBase,
    int AheadOfOrigin,
    IReadOnlyList<string> Output)
{
    /// <summary>There is an <c>origin/&lt;base&gt;</c> to be ahead of at all.</summary>
    public bool HasOrigin { get; init; }
    public bool MovedToDone { get; init; }
    public string? PhaseNote { get; init; }
}

/// <summary>
/// Outcome of a force push. <paramref name="Stale"/> marks the one rejection that means someone
/// else moved <c>origin/&lt;branch&gt;</c> since this dialog last fetched — the lease doing its job.
/// </summary>
public record ForcePushResult(
    bool Ok,
    string? Error,
    bool Stale,
    string Branch,
    string? Sha,
    IReadOnlyList<string> Output);

/// <summary>
/// The git actions a human takes on a ticket from the board: put my checkout on this ticket's
/// branch, get it based off the latest main, raise the PR for it, and force the remote to match
/// afterwards.
///
/// <para>
/// Distinct from <see cref="GitWorktreeService"/> on purpose. That one serves the pipeline — it
/// reads and reaps the worktrees the heartbeat creates. This one serves the person: every operation
/// here targets the HUB checkout (<see cref="ProjectConfig.ProjectPath"/>), the one directory the
/// user actually has open in their editor, and every one of them is something they would otherwise
/// type by hand.
/// </para>
///
/// <para>
/// Nothing here is allowed to lose work, and the two operations that DO rewrite are each fenced by
/// the same rule: they run only where they cannot cost anything, and they are their own button
/// rather than a step inside another one. Checkout refuses while a tree has pending changes.
/// Rebasing refuses the same way, and rolls itself back the moment a conflict appears — a rebase
/// that needs a human decision is not one this is allowed to make. Force pushing goes out with a
/// lease, so it replaces only the remote this dialog actually looked at. Raising a PR is unchanged
/// and still never rewrites: no force, no rebase, no commit.
/// </para>
/// </summary>
public partial class TicketGitService
{
    /// <summary>
    /// How much patch text is sent to the browser. A ticket's branch is normally a few hundred
    /// lines; a lockfile or a generated migration turns it into megabytes, and neither the wire nor
    /// a human reading the dialog benefits from the rest of it.
    /// <para>
    /// Spent in significance order, so what gets dropped is the generated tail rather than whatever
    /// happened to sort last alphabetically.
    /// </para>
    /// </summary>
    private const int MaxPatchBytes = 400_000;

    /// <summary>
    /// How much patch text is read for RANKING, which is far more than is ever sent. Scoring a file
    /// needs its hunks, and a file scored blind would be ranked on line count — the exact failure
    /// the ranking exists to fix — so this is deliberately generous.
    /// </summary>
    private const int MaxScanBytes = 4_000_000;

    /// <summary>Paths handed to one <c>git grep</c> looking for generated-file headers.</summary>
    private const int MaxGrepPaths = 300;

    /// <summary>Enough commits to see the shape of a branch. Longer than any ticket has produced.</summary>
    private const int MaxCommits = 60;

    private readonly ProjectRegistry _registry;
    private readonly GitWorktreeService _worktrees;
    private readonly ILogger<TicketGitService> _log;

    public TicketGitService(ProjectRegistry registry, GitWorktreeService worktrees, ILogger<TicketGitService> log)
    {
        _registry = registry;
        _worktrees = worktrees;
        _log = log;
    }

    // ---- checkout -----------------------------------------------------------

    /// <summary>
    /// Move the hub checkout onto <paramref name="branch"/> so the user can read the ticket's code
    /// in their own editor.
    ///
    /// <para>
    /// Refuses while anything is uncommitted. A checkout across a dirty tree either fails or carries
    /// the changes over, and the second of those is how work quietly ends up committed to the wrong
    /// branch — so this asks first and does nothing until the tree is clean.
    /// </para>
    ///
    /// <para>
    /// The branch is normally live in an agent worktree, which git refuses to check out twice. That
    /// refusal is about the danger of two trees sharing one branch ref, not about corruption, and
    /// looking at the code is the entire point of this button — so it is overridden with
    /// <c>--ignore-other-worktrees</c> and reported as forced, loudly, because the agent may commit
    /// underneath the user while they sit on it.
    /// </para>
    /// </summary>
    public async Task<CheckoutResult> CheckoutAsync(string projectKey, string branch, CancellationToken ct = default,
        string? member = null)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var none = Array.Empty<GitChange>();
        if (WriteTargetOf(cfg, member, out var notHere) is not { } target)
            return new CheckoutResult(false, notHere, branch ?? "", cfg.ProjectPath ?? "", false, null, false, none);
        var repo = target.Path;

        if (string.IsNullOrWhiteSpace(branch))
            return new CheckoutResult(false, "This ticket has no branch yet.", "", repo, false, null, false, none);
        if (!IsGitRepo(repo))
            return new CheckoutResult(false, $"{cfg.DisplayName} has no git repo at '{repo}'.", branch, repo, false, null, false, none);

        var current = (await GitAsync(repo, ct, "rev-parse", "--abbrev-ref", "HEAD")).Stdout.Trim();
        if (string.Equals(current, branch, StringComparison.Ordinal))
            return new CheckoutResult(true, null, branch, repo, false, SharedWorktree(repo, branch), false, none);

        var pending = await PendingChangesAsync(repo, ct);
        if (pending.Count > 0)
        {
            var word = pending.Count == 1 ? "change" : "changes";
            return new CheckoutResult(false,
                $"You have {pending.Count} uncommitted {word} in {repo}. Commit or stash them first.",
                branch, repo, false, null, false, pending);
        }

        // A branch that exists only on the remote is the case where the worktree has already been
        // reaped. Recreating it as a tracking branch is exactly what the user would type.
        var local = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        if (!local.Ok)
        {
            var remote = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}");
            if (!remote.Ok)
                return new CheckoutResult(false, $"Branch '{branch}' does not exist locally or on origin.",
                    branch, repo, false, null, false, none);

            var track = await GitAsync(repo, ct, "checkout", "-b", branch, "--track", $"origin/{branch}");
            if (!track.Ok)
                return new CheckoutResult(false, Explain(track, $"could not create '{branch}' from origin"),
                    branch, repo, false, null, false, none);
            return new CheckoutResult(true, null, branch, repo, false, null, true, none);
        }

        var shared = SharedWorktree(repo, branch);
        var args = shared is null
            ? new[] { "checkout", branch }
            : new[] { "checkout", "--ignore-other-worktrees", branch };

        var result = await GitAsync(repo, ct, args);
        if (!result.Ok)
            return new CheckoutResult(false, Explain(result, $"could not check out '{branch}'"),
                branch, repo, false, shared, false, none);

        _log.LogInformation("Checked out {Branch} in {Repo}{Forced}", branch, repo, shared is null ? "" : " (forced)");
        return new CheckoutResult(true, null, branch, repo, shared is not null, shared, false, none);
    }

    /// <summary>The non-main worktree holding <paramref name="branch"/>, or null when the branch is free.</summary>
    private string? SharedWorktree(string repo, string branch) =>
        _worktrees.List(repo).FirstOrDefault(w => !w.IsMain && w.Branch == branch)?.Path;

    private async Task<List<GitChange>> PendingChangesAsync(string dir, CancellationToken ct)
    {
        // Untracked files count. They are what an editor leaves behind and what a half-done
        // experiment looks like, and "no pending changes" would be a lie that excluded them.
        var status = await GitAsync(dir, ct, "status", "--porcelain", "--untracked-files=normal");
        var list = new List<GitChange>();
        foreach (var raw in status.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4) continue;
            list.Add(new GitChange(line[..2].Trim(), line[3..].Trim()));
        }
        return list;
    }

    // ---- PR preflight -------------------------------------------------------

    /// <summary>
    /// Everything needed to judge the branch before raising a PR for it, in one pass: how it sits
    /// against the latest base, what is on it, and what it changes.
    ///
    /// <para>
    /// Fetches first, so every number is a comparison against the base as it is on the server. The
    /// dialog does not use this — it runs the two halves side by side, see
    /// <see cref="LocalPreflightAsync"/> — but a caller that wants one answer and can wait for it
    /// should ask here rather than stitching the halves back together itself.
    /// </para>
    /// </summary>
    public Task<PrPreflight> PreflightAsync(string projectKey, string branch, CancellationToken ct = default,
        string? member = null)
        => PreflightCoreAsync(projectKey, branch, fetch: true, ct, member);

    /// <summary>
    /// The half of the preflight that never leaves this machine: the commits, the ranked diff, and
    /// the worktree's uncommitted files.
    ///
    /// <para>
    /// This is what the dialog opens on, because none of it needs the remote and all of it is what
    /// the reader came to look at. The base comparison it carries is last-known rather than checked
    /// — <see cref="PrPreflight.BaseChecked"/> is false — and <see cref="BaseCheckAsync"/> runs
    /// alongside to replace it.
    /// </para>
    ///
    /// <para>
    /// The diff is measured from the merge base, which a stale <c>origin/base</c> almost never
    /// moves: the base gaining commits does not change where this branch forked. The exception is a
    /// base ref so old it predates the fork point, and the fetch names that case for free — commits
    /// it pulls in stop counting as this branch's own, so <see cref="PrPreflight.Ahead"/> drops.
    /// A caller that sees it drop must read this again rather than trust the diff it has.
    /// </para>
    /// </summary>
    public Task<PrPreflight> LocalPreflightAsync(string projectKey, string branch, CancellationToken ct = default,
        string? member = null)
        => PreflightCoreAsync(projectKey, branch, fetch: false, ct, member);

    private async Task<PrPreflight> PreflightCoreAsync(string projectKey, string branch, bool fetch, CancellationToken ct,
        string? member)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var target = TargetOf(cfg, member, out var notHere);
        var repo = target?.Path ?? cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var baseBranch = target?.BaseBranch ?? ResolveBaseBranch(cfg);

        PrPreflight Fail(string error) => new(
            false, error, branch, baseBranch, repo, cfg.ResolvedIdeUrl, "none", null, fetch, false, null, baseBranch,
            0, 0, false, false, 0, 0, null,
            Array.Empty<PrCommit>(), null, Array.Empty<PrFile>(), false);

        if (notHere is not null) return Fail(notHere);
        if (Guard(repo, cfg, branch, baseBranch) is { } refusal) return Fail(refusal);

        var exists = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        if (!exists.Ok) return Fail($"Branch '{branch}' does not exist in {repo}.");

        // Started here and read at the end: it is one process spawn nothing else waits on, and on
        // Windows the spawn is most of what a git call costs.
        var remoteUrlRead = GitAsync(repo, ct, "remote", "get-url", "origin");

        var (fetched, fetchError) = fetch ? await FetchAsync(repo, baseBranch, branch, ct) : (false, null);

        var cmp = await CompareAsync(repo, cfg, baseBranch, branch, fetched, fetchError, ct);
        if (!cmp.Ok) return Fail(cmp.Error!);

        var remoteUrl = (await remoteUrlRead).Stdout.Trim();
        var provider = string.IsNullOrWhiteSpace(remoteUrl) ? "none" : ProviderOf(remoteUrl);

        // Four independent reads of one repository, none of which writes anything. In sequence they
        // are four process spawns end to end, and that is most of the wait on a dialog whose whole
        // point is to open — so they go together. The commit body is read without first asking
        // whether it is wanted: waiting for the log to be counted costs a round trip to save one.
        var dirtyRead = DirtyCountAsync(repo, branch, ct);
        var commitsRead = ReadCommitsAsync(repo, cmp.BaseRef, branch, ct);
        var bodyRead = ReadCommitBodyAsync(repo, branch, ct);
        var filesRead = ReadFilesAsync(repo, cfg, cmp.BaseRef, branch, ct);
        await Task.WhenAll(dirtyRead, commitsRead, bodyRead, filesRead);

        var (worktree, dirty) = await dirtyRead;
        var commits = await commitsRead;
        var (files, truncated) = await filesRead;

        return new PrPreflight(
            true, null, branch, baseBranch, repo, cfg.ResolvedIdeUrl, provider,
            string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl,
            fetch, cmp.Fetched, cmp.FetchError, cmp.BaseRef, cmp.Behind, cmp.Ahead,
            cmp.RemoteBranchExists, cmp.RemoteUpToDate, cmp.RemoteAhead, dirty, worktree,
            commits, commits.Count == 1 ? await bodyRead : null, files, truncated);
    }

    /// <summary>
    /// Fetch, then answer the questions only a fresh fetch can: how far behind the base this branch
    /// is, and whether origin's copy of it has parted company with this one.
    ///
    /// <para>
    /// A fetch that fails is reported rather than swallowed, and the comparison falls back to the
    /// local base ref with <see cref="PrBaseCheck.Fetched"/> false so the dialog can say the numbers
    /// are stale. Answering "yes, up to date" from a week-old <c>origin/main</c> would be worse than
    /// not answering at all.
    /// </para>
    /// </summary>
    public async Task<PrBaseCheck> BaseCheckAsync(string projectKey, string branch, CancellationToken ct = default,
        string? member = null)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var target = TargetOf(cfg, member, out var notHere);
        var repo = target?.Path ?? "";
        var baseBranch = target?.BaseBranch ?? ResolveBaseBranch(cfg);

        PrBaseCheck Fail(string error) => new(false, error, false, null, baseBranch, 0, 0, false, false, 0);

        if (notHere is not null) return Fail(notHere);
        if (Guard(repo, cfg, branch, baseBranch) is { } refusal) return Fail(refusal);

        var exists = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        if (!exists.Ok) return Fail($"Branch '{branch}' does not exist in {repo}.");

        var (fetched, fetchError) = await FetchAsync(repo, baseBranch, branch, ct);
        return await CompareAsync(repo, cfg, baseBranch, branch, fetched, fetchError, ct);
    }

    /// <summary>The refusals both halves share, in the order they stop being worth checking.</summary>
    private string? Guard(string repo, ProjectConfig cfg, string branch, string baseBranch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return "This ticket has no branch yet.";
        if (!IsGitRepo(repo)) return $"{cfg.DisplayName} has no git repo at '{repo}'.";
        if (branch == baseBranch) return $"'{branch}' is the base branch — there is nothing to raise a PR for.";
        return null;
    }

    /// <summary>
    /// The network. Generous timeout, and its failure is data rather than an exception: an offline
    /// laptop should still be able to read the diff, just not be told the base is current.
    /// </summary>
    private async Task<(bool Fetched, string? Error)> FetchAsync(
        string repo, string baseBranch, string branch, CancellationToken ct)
    {
        var fetch = await GitAsync(repo, ct, TimeSpan.FromSeconds(90), "fetch", "origin", baseBranch, branch, "--prune");
        if (fetch.Ok) return (true, null);

        // The combined fetch fails outright when the branch has never been pushed. Retry for the
        // base alone, because the base is what the up-to-date question is actually about.
        var baseOnly = await GitAsync(repo, ct, TimeSpan.FromSeconds(90), "fetch", "origin", baseBranch);
        return baseOnly.Ok ? (true, null) : (false, Explain(fetch, "could not fetch from origin"));
    }

    /// <summary>Where this branch sits against the base, and against origin's copy of itself.</summary>
    private async Task<PrBaseCheck> CompareAsync(
        string repo, ProjectConfig cfg, string baseBranch, string branch, bool fetched, string? fetchError, CancellationToken ct)
    {
        var baseRefRead = BaseRefAsync(repo, cfg, baseBranch, ct);
        var remoteBranchRead = GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}");
        var localTipRead = GitAsync(repo, ct, "rev-parse", branch);
        await Task.WhenAll(baseRefRead, remoteBranchRead, localTipRead);

        if (await baseRefRead is not { } baseRef)
            return new PrBaseCheck(
                false, $"Base branch '{baseBranch}' does not exist locally or on origin.",
                fetched, fetchError, baseBranch, 0, 0, false, false, 0);

        var remoteBranch = await remoteBranchRead;
        var remoteUpToDate = remoteBranch.Ok && remoteBranch.Stdout.Trim() == (await localTipRead).Stdout.Trim();

        // "Not up to date" covers two very different situations, and only one of them is fixed by
        // pushing. Counting what origin has that we do not separates the remote merely trailing from
        // the remote having been left on a history this branch abandoned, which is where a rebase
        // puts it — and that one needs a force.
        var countRead = CountAsync(repo, baseRef, branch, ct);
        var remoteAheadRead = remoteBranch.Ok && !remoteUpToDate
            ? CountAsync(repo, $"origin/{branch}", branch, ct)
            : Task.FromResult((Left: 0, Right: 0));
        await Task.WhenAll(countRead, remoteAheadRead);

        var (behind, ahead) = await countRead;
        return new PrBaseCheck(
            true, null, fetched, fetchError, baseRef, behind, ahead,
            remoteBranch.Ok, remoteUpToDate, (await remoteAheadRead).Left);
    }

    /// <summary>
    /// Uncommitted work in the agent's worktree, which will not be in the PR. Worth saying: the
    /// ticket reads as finished on the board either way.
    /// </summary>
    private async Task<(string? Path, int Dirty)> DirtyCountAsync(string repo, string branch, CancellationToken ct)
    {
        var worktree = SharedWorktree(repo, branch);
        var dirty = worktree is not null && Directory.Exists(worktree)
            ? (await PendingChangesAsync(worktree, ct)).Count
            : 0;
        return (worktree, dirty);
    }

    private async Task<List<PrCommit>> ReadCommitsAsync(string repo, string baseRef, string branch, CancellationToken ct)
    {
        // Unit separator between fields, record separator between commits: subjects contain every
        // punctuation mark a person can type, and these two are not among them.
        const string fmt = "%h\u001f%s\u001f%an\u001f%ad\u001e";
        var log = await GitAsync(repo, ct, "log", $"--max-count={MaxCommits}", "--date=short", $"--format={fmt}", $"{baseRef}..{branch}");
        var result = new List<PrCommit>();
        foreach (var record in log.Stdout.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.Trim('\n', '\r').Split('\u001f');
            if (f.Length < 4) continue;
            result.Add(new PrCommit(f[0], f[1], f[2], f[3]));
        }
        return result;
    }

    /// <summary>
    /// The body of the tip commit — <c>%b</c>, so the subject and the blank line after it are
    /// already gone. Null when there is nothing under the subject.
    /// </summary>
    private async Task<string?> ReadCommitBodyAsync(string repo, string rev, CancellationToken ct)
    {
        var log = await GitAsync(repo, ct, "log", "-1", "--format=%b", rev);
        if (!log.Ok) return null;
        var body = log.Stdout.Replace("\r\n", "\n").Trim();
        return body.Length == 0 ? null : body;
    }

    /// <summary>
    /// The changed files, ranked and carrying their own hunks.
    ///
    /// <para>
    /// Three dots throughout: everything this branch added since it forked, and nothing the base
    /// gained afterwards. The two-dot form would show unrelated base commits as if this branch had
    /// reverted them — exactly the wrong thing to put in front of someone approving a PR.
    /// </para>
    ///
    /// <para>
    /// Splitting the patch per file happens HERE rather than in the browser, because ranking needs
    /// the hunks and the ranking is what decides which hunks are worth sending. Doing it the other
    /// way round means the size budget is spent alphabetically, and the generated client at the
    /// bottom of the tree crowds out the migration.
    /// </para>
    /// </summary>
    private async Task<(List<PrFile> Files, bool Truncated)> ReadFilesAsync(
        string repo, ProjectConfig cfg, string baseRef, string branch, CancellationToken ct)
    {
        // Two readings of the same range, and neither needs the other: the counts and the text. Run
        // back to back they are two process spawns on the path the dialog waits for, so they start
        // together. Nothing changed means the patch is empty too, so the second one costs nothing on
        // the one path where its answer is thrown away.
        var statsRead = ReadNumstatAsync(repo, baseRef, branch, ct);
        var diffRead = GitAsync(repo, ct, TimeSpan.FromSeconds(60), "diff", "-M", $"{baseRef}...{branch}");
        // Numstat says how much changed and knows a rename when it sees one, but not an add from an
        // edit. A third reading of the same range, started with the other two.
        var statusRead = GitAsync(repo, ct, "diff", "--name-status", "-M", "-z", $"{baseRef}...{branch}");
        var stats = await statsRead;
        if (stats.Count == 0) return (new List<PrFile>(), false);
        var statuses = ParseNameStatus((await statusRead).Stdout)
            .GroupBy(s => s.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Status, StringComparer.Ordinal);

        var raw = (await diffRead).Stdout;
        var scanned = raw.Length <= MaxScanBytes ? raw : raw[..MaxScanBytes];
        var bodies = SplitPatch(scanned);

        var generated = await FindGeneratedAsync(repo, branch, stats.Select(s => s.Path).ToList(), ct);
        var noise = cfg.DiffNoisePaths;

        var judged = stats.Select(s =>
        {
            var body = bodies.TryGetValue(s.Path, out var hunks) ? hunks : "";
            var verdict = DiffSignificance.Judge(s.Path, s.Added, s.Removed, s.Binary, body, generated.Contains(s.Path), noise);
            return (Stat: s, Body: body, Verdict: verdict);
        }).ToList();

        // Rank, then spend the wire budget down that ranking. A dropped body is always the least
        // interesting one left, which is the entire point.
        var ordered = judged
            .OrderBy(j => j.Verdict.Tier)
            .ThenByDescending(j => j.Verdict.Score)
            .ThenBy(j => j.Stat.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = new List<PrFile>(ordered.Count);
        var budget = MaxPatchBytes;
        var truncated = false;
        foreach (var j in ordered)
        {
            var fits = j.Body.Length > 0 && j.Body.Length <= budget;
            if (fits) budget -= j.Body.Length;
            else if (j.Body.Length > 0) truncated = true;

            files.Add(new PrFile(
                j.Stat.Path, j.Stat.Added, j.Stat.Removed, j.Stat.Binary,
                j.Verdict.Tier.ToString(), j.Verdict.Score, j.Verdict.Reasons,
                fits ? j.Body : "", !fits && j.Body.Length > 0,
                j.Stat.RenamedFrom,
                statuses.TryGetValue(j.Stat.Path, out var status)
                    ? status
                    : j.Stat.RenamedFrom is null ? FileStatus.Modified : FileStatus.Renamed));
        }

        return (files, truncated);
    }

    /// <summary>
    /// Read <c>git diff --name-status -M -z</c>: a status token, then one path - or two for a rename,
    /// old then new. With <c>-z</c> the paths arrive verbatim, which is what lets them be matched
    /// against numstat's and against a path on disk.
    /// <para>
    /// A copy is reported as an add: the new file is what a check-in has to carry, and its source is
    /// untouched. A type change is an edit.
    /// </para>
    /// </summary>
    public static IReadOnlyList<NameStatus> ParseNameStatus(string z)
    {
        var tokens = (z ?? "").Split('\0');
        var result = new List<NameStatus>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var code = tokens[i].Trim();
            if (code.Length == 0) continue;

            var kind = char.ToUpperInvariant(code[0]);
            if ((kind == 'R' || kind == 'C') && i + 2 < tokens.Length)
            {
                var from = tokens[i + 1];
                var to = tokens[i + 2];
                i += 2;
                result.Add(kind == 'R'
                    ? new NameStatus(to, FileStatus.Renamed, from)
                    : new NameStatus(to, FileStatus.Added, null));
                continue;
            }

            if (i + 1 >= tokens.Length) break;
            var path = tokens[++i];
            if (path.Length == 0) continue;
            result.Add(new NameStatus(path, kind switch
            {
                'A' => FileStatus.Added,
                'D' => FileStatus.Deleted,
                _ => FileStatus.Modified,
            }, null));
        }
        return result;
    }

    private record NumStat(string Path, int Added, int Removed, bool Binary, string? RenamedFrom);

    /// <summary>
    /// Per-file line counts, read with <c>-z</c>.
    ///
    /// The NUL form is not a nicety: without it git collapses a rename into
    /// <c>dir/{old =&gt; new}/file</c>, one string that matches neither the path in the patch header
    /// nor anything on disk, so every renamed file would silently lose its diff. With <c>-z</c> a
    /// rename arrives as an empty path field followed by the two real names.
    /// </summary>
    private async Task<List<NumStat>> ReadNumstatAsync(string repo, string baseRef, string branch, CancellationToken ct)
    {
        var numstat = await GitAsync(repo, ct, "diff", "--numstat", "-M", "-z", $"{baseRef}...{branch}");
        var tokens = numstat.Stdout.Split('\0');
        var result = new List<NumStat>();

        for (var i = 0; i < tokens.Length; i++)
        {
            var head = tokens[i];
            if (head.Length == 0) continue;

            var f = head.Split('\t');
            if (f.Length < 3) continue;

            var binary = f[0] == "-" || f[1] == "-";
            var added = binary ? 0 : int.TryParse(f[0], out var a) ? a : 0;
            var removed = binary ? 0 : int.TryParse(f[1], out var d) ? d : 0;

            string path;
            string? renamedFrom = null;
            if (f[2].Length == 0 && i + 2 < tokens.Length)
            {
                renamedFrom = tokens[i + 1];
                path = tokens[i + 2];
                i += 2;
            }
            else
            {
                path = f[2];
            }

            if (path.Length > 0) result.Add(new NumStat(path, added, removed, binary, renamedFrom));
        }
        return result;
    }

    /// <summary>
    /// Which of these files a tool wrote, from the header conventions generators emit
    /// (<c>&lt;auto-generated</c>, <c>DO NOT EDIT</c>, <c>@generated</c>).
    ///
    /// One grep for all of them. This catches what a path list never can — an OpenAPI client or a
    /// protobuf stub living in an ordinary-looking source folder — and needs no per-project setup to
    /// do it. A failure here is not an error: it just means those files are ranked on their content
    /// like anything else.
    /// </summary>
    private async Task<HashSet<string>> FindGeneratedAsync(
        string repo, string branch, List<string> paths, CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (paths.Count == 0 || paths.Count > MaxGrepPaths) return found;

        var args = new List<string> { "grep", "--files-with-matches", "--fixed-strings", "-I" };
        foreach (var marker in DiffSignificance.GeneratedMarkers) { args.Add("-e"); args.Add(marker); }
        args.Add(branch);
        args.Add("--");
        args.AddRange(paths);

        var grep = await GitAsync(repo, ct, TimeSpan.FromSeconds(30), args.ToArray());
        // Exit 1 simply means nothing matched. Anything else is a malformed call, and silence is
        // still the right answer — ranking degrades, it does not break.
        foreach (var raw in grep.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon > 0 && colon + 1 < line.Length) found.Add(line[(colon + 1)..]);
        }
        return found;
    }

    /// <summary>
    /// Cut one <c>git diff</c> into per-file hunks, keyed by the path numstat reports.
    ///
    /// The <c>diff --git a/x b/y</c> header carries both sides, and for a rename they differ — the
    /// b-side is what numstat's rename record names last, so that is the side keyed on. Everything
    /// above the first hunk (index/mode/<c>---</c>/<c>+++</c> lines) is dropped: it is noise beside a
    /// file header the UI has already drawn, and counting <c>+++</c> as an added line would make
    /// every file look like it gained one.
    /// </summary>
    internal static Dictionary<string, string> SplitPatch(string patch)
    {
        var out_ = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(patch)) return out_;

        foreach (var chunk in Regex.Split(patch, @"^diff --git ", RegexOptions.Multiline))
        {
            if (chunk.Trim().Length == 0) continue;
            var nl = chunk.IndexOf('\n');
            var header = nl < 0 ? chunk : chunk[..nl];
            var body = nl < 0 ? "" : chunk[(nl + 1)..];

            var sep = header.IndexOf(" b/", StringComparison.Ordinal);
            var path = sep > 0 ? header[(sep + 3)..].Trim() : header.TrimStart().TrimStart('a', '/').Trim();

            var at = Regex.Match(body, @"^@@", RegexOptions.Multiline);
            out_[path] = at.Success ? body[at.Index..] : "";
        }
        return out_;
    }

    // ---- rebasing onto the base ---------------------------------------------

    /// <summary>
    /// Replay the branch onto the latest base, but only when that costs nothing.
    ///
    /// <para>
    /// The conflict-free test is the rebase itself. There is no way to ask git "would this conflict"
    /// that is actually the same question — a merge preview can come out clean while replaying the
    /// commits one at a time still stops halfway — so this runs the real thing and aborts the
    /// instant it stops. An aborted rebase restores the branch ref, the index and the tree exactly,
    /// which makes attempting it the cheapest honest answer available. A branch that does conflict
    /// is left untouched and handed back with the file list, because resolving it is a judgement
    /// about the code and this has none.
    /// </para>
    ///
    /// <para>
    /// It runs in whichever tree already holds the branch: the hub, or the agent worktree that
    /// checked it out. That tree must be clean — the same rule checkout follows, and here it doubles
    /// as the guard against rewriting history under an agent that is still mid-edit. A branch held
    /// in two trees at once (which only happens after a forced checkout) is refused outright: moving
    /// the ref would leave the other tree's HEAD pointing at a history its files no longer match.
    /// </para>
    /// </summary>
    public async Task<RebaseResult> RebaseAsync(string projectKey, string branch, CancellationToken ct = default,
        string? member = null)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var target = WriteTargetOf(cfg, member, out var notHere);
        var repo = target?.Path ?? "";
        var baseBranch = target?.BaseBranch ?? ResolveBaseBranch(cfg);
        var baseRef = baseBranch;
        var dir = repo;
        var output = new List<string>();
        var noFiles = Array.Empty<string>();

        RebaseResult Fail(string error) =>
            new(false, error, false, noFiles, branch, baseRef, null, null, dir, output);

        if (notHere is not null) return Fail(notHere);
        if (string.IsNullOrWhiteSpace(branch)) return Fail("This ticket has no branch yet.");
        if (!IsGitRepo(repo)) return Fail($"{cfg.DisplayName} has no git repo at '{repo}'.");
        if (branch == baseBranch) return Fail($"'{branch}' is the base branch — there is nothing to rebase.");

        var exists = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        if (!exists.Ok) return Fail($"Branch '{branch}' does not exist in {repo}.");

        // Rebasing onto a week-old origin/main is the failure this button exists to prevent, so the
        // base is refreshed first. A fetch that fails is not fatal — the local base is still a real
        // commit to sit on — but it is said out loud, because "rebased" would otherwise imply more
        // than happened.
        var fetch = await GitAsync(repo, ct, TimeSpan.FromSeconds(90), "fetch", "origin", baseBranch);
        if (!fetch.Ok) output.Add($"[dashboard] {Explain(fetch, "could not fetch from origin")}");

        if (await BaseRefAsync(repo, cfg, baseBranch, ct) is not { } resolved)
            return Fail($"Base branch '{baseBranch}' does not exist locally or on origin.");
        baseRef = resolved;

        var (behind, _) = await CountAsync(repo, baseRef, branch, ct);
        if (behind == 0) return Fail($"'{branch}' is already based off the latest {baseRef} — nothing to rebase.");

        var holders = _worktrees.List(repo).Where(w => w.Branch == branch).ToList();
        if (holders.Count > 1)
            return Fail($"'{branch}' is checked out in {holders.Count} worktrees at once. " +
                        "Rebasing would leave one of them holding files that no longer match its HEAD — " +
                        "close one of them first.");
        if (holders.Count == 1)
        {
            if (!Directory.Exists(holders[0].Path))
                return Fail($"'{branch}' is checked out at '{holders[0].Path}', which no longer exists. " +
                            $"Run: git -C \"{repo}\" worktree prune");
            dir = holders[0].Path;
        }

        var pending = await PendingChangesAsync(dir, ct);
        if (pending.Count > 0)
        {
            var word = pending.Count == 1 ? "change" : "changes";
            return Fail($"{pending.Count} uncommitted {word} in {dir}. Commit or stash them before rebasing.");
        }

        if (await RebaseInProgressAsync(dir, ct))
            return Fail($"A rebase is already in progress in {dir}. Finish or abort that one first.");

        var previous = (await GitAsync(repo, ct, "rev-parse", branch)).Stdout.Trim();

        // Where this tree was pointing, so a rebase that had to check the branch out can put it back.
        // Empty when detached, in which case the commit itself is the thing to return to.
        var headBranch = (await GitAsync(dir, ct, "symbolic-ref", "--quiet", "--short", "HEAD")).Stdout.Trim();
        var headSha = (await GitAsync(dir, ct, "rev-parse", "HEAD")).Stdout.Trim();
        var onBranchAlready = headBranch == branch;

        // A rebase given a branch to work on checks it out first, and an abort returns to it rather
        // than to whatever was here before — so putting the tree back is part of every ending, not
        // just the happy one. Safe at each of them: the tree was clean going in, and a failed rebase
        // is either aborted by this point or has already been handed over to a human.
        async Task RestoreHeadAsync()
        {
            if (onBranchAlready) return;
            var back = await GitAsync(dir, ct, "checkout", headBranch.Length > 0 ? headBranch : headSha);
            if (!back.Ok)
                output.Add($"[dashboard] {Explain(back, $"left this tree on '{branch}' — could not return to '{headBranch}'")}");
        }

        // --no-autostash because a stash here would be exactly the silent carrying-over of work this
        // service refuses everywhere else; the tree is already known clean. core.editor is pinned to
        // a no-op so that no configuration anywhere can turn this into a process waiting on a prompt
        // that nobody is there to answer.
        var args = new List<string> { "-c", "core.editor=true", "rebase", "--no-autostash", baseRef };
        if (!onBranchAlready) args.Add(branch);

        output.Add($"git -C \"{dir}\" rebase {baseRef}{(onBranchAlready ? "" : $" {branch}")}");
        var rebase = await GitAsync(dir, ct, TimeSpan.FromMinutes(5), args.ToArray());
        AddLines(output, rebase);

        if (!rebase.Ok)
        {
            // Read the conflicts before the abort, because after it there is nothing left to read.
            var conflicts = await ConflictedPathsAsync(dir, ct);

            if (await RebaseInProgressAsync(dir, ct))
            {
                var abort = await GitAsync(dir, ct, TimeSpan.FromMinutes(2), "rebase", "--abort");
                AddLines(output, abort);
                if (!abort.Ok)
                    return new RebaseResult(false,
                        $"The rebase stopped and could not be rolled back — {dir} is still mid-rebase. " +
                        $"Sort it out by hand: git -C \"{dir}\" rebase --abort",
                        conflicts.Count > 0, conflicts, branch, baseRef, previous, null, dir, output);
            }

            await RestoreHeadAsync();
            var now = (await GitAsync(repo, ct, "rev-parse", branch)).Stdout.Trim();
            if (now != previous)
                return new RebaseResult(false,
                    $"The rebase failed and '{branch}' did not come back to where it was. " +
                    $"It was at {Short(previous)}: git -C \"{repo}\" reset --hard {previous}",
                    false, conflicts, branch, baseRef, previous, now, dir, output);

            if (conflicts.Count > 0)
            {
                var word = conflicts.Count == 1 ? "file" : "files";
                return new RebaseResult(false,
                    $"Rebasing onto {baseRef} conflicts in {conflicts.Count} {word}. Nothing was changed — " +
                    "this one needs deciding by hand.",
                    true, conflicts, branch, baseRef, previous, previous, dir, output);
            }

            return Fail(Explain(rebase, $"could not rebase '{branch}' onto {baseRef}"));
        }

        await RestoreHeadAsync();

        var rebased = (await GitAsync(repo, ct, "rev-parse", branch)).Stdout.Trim();
        _log.LogInformation("Rebased {Branch} onto {BaseRef} in {Dir}: {Previous} -> {New}",
            branch, baseRef, dir, Short(previous), Short(rebased));

        return new RebaseResult(true, null, false, noFiles, branch, baseRef, previous, rebased, dir, output);
    }

    /// <summary>Whether this tree is sitting in the middle of a rebase, either backend.</summary>
    private async Task<bool> RebaseInProgressAsync(string dir, CancellationToken ct)
    {
        foreach (var name in new[] { "rebase-merge", "rebase-apply" })
        {
            var p = (await GitAsync(dir, ct, "rev-parse", "--git-path", name)).Stdout.Trim();
            if (p.Length == 0) continue;
            if (Directory.Exists(Path.IsPathRooted(p) ? p : Path.Combine(dir, p))) return true;
        }
        return false;
    }

    /// <summary>The paths git has left unmerged — valid only while the rebase is still stopped.</summary>
    private async Task<List<string>> ConflictedPathsAsync(string dir, CancellationToken ct)
    {
        var r = await GitAsync(dir, ct, "diff", "--name-only", "--diff-filter=U");
        return r.Stdout.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // ---- raising the PR -----------------------------------------------------

    /// <summary>
    /// Push the branch, then get the PR raised.
    ///
    /// <para>
    /// Push only — never <c>--force</c>, never a rebase, never a commit. If the remote branch has
    /// diverged, the right answer is a human looking at why, not this method resolving it.
    /// </para>
    ///
    /// <para>
    /// How the PR itself gets created depends on the host. GitHub has an authenticated CLI, so the
    /// PR is created outright. Azure DevOps has no CLI here, so this returns the prefilled
    /// create-PR page instead: the branch is pushed and the form is filled, and the user presses
    /// Create in the host's own UI. That is a deliberate stopping point rather than a shortfall —
    /// raising a PR against someone else's server is the one step worth confirming in their words.
    /// </para>
    /// </summary>
    public async Task<PrCreateResult> CreateAsync(
        string projectKey, string branch, string title, string? description, CancellationToken ct = default,
        string? member = null)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var target = WriteTargetOf(cfg, member, out var notHere);
        var repo = target?.Path ?? "";
        var baseBranch = target?.BaseBranch ?? ResolveBaseBranch(cfg);
        var output = new List<string>();

        PrCreateResult Fail(string error, bool pushed = false) =>
            new(false, error, pushed, null, "none", output);

        if (notHere is not null) return Fail(notHere);
        if (string.IsNullOrWhiteSpace(branch)) return Fail("This ticket has no branch yet.");
        if (!IsGitRepo(repo)) return Fail($"{cfg.DisplayName} has no git repo at '{repo}'.");
        if (branch == baseBranch) return Fail($"'{branch}' is the base branch.");

        var remoteUrl = (await GitAsync(repo, ct, "remote", "get-url", "origin")).Stdout.Trim();
        if (string.IsNullOrWhiteSpace(remoteUrl)) return Fail("This repo has no 'origin' remote to push to.");

        var push = await GitAsync(repo, ct, TimeSpan.FromMinutes(5), "push", "--set-upstream", "origin", branch);
        output.Add($"git push --set-upstream origin {branch}");
        AddLines(output, push);
        if (!push.Ok) return Fail(Explain(push, $"could not push '{branch}' to origin"));

        var provider = ProviderOf(remoteUrl);
        if (provider == "github")
        {
            var gh = await RunAsync("gh", repo, TimeSpan.FromMinutes(2), ct,
                "pr", "create", "--head", branch, "--base", baseBranch,
                "--title", title, "--body", description ?? "");
            AddLines(output, gh);

            if (gh.Ok)
            {
                var url = gh.Stdout.Split('\n')
                    .Select(l => l.Trim())
                    .LastOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase));
                return new PrCreateResult(true, null, true, url, "gh", output);
            }

            // gh failing is not a failure to deliver: the branch is pushed, and GitHub's own compare
            // page does the rest. Report why it fell back rather than hiding it.
            var web = GitHubCompareUrl(remoteUrl, baseBranch, branch);
            if (web is null) return Fail(Explain(gh, "gh could not create the PR"), pushed: true);
            output.Add("[dashboard] gh could not create the PR — falling back to the web form.");
            return new PrCreateResult(true, null, true, web, "web", output);
        }

        if (provider == "azure-devops")
        {
            var web = AzureDevOpsPrUrl(remoteUrl, baseBranch, branch, title, description);
            if (web is null) return Fail("Could not work out the Azure DevOps URL for this remote.", pushed: true);
            return new PrCreateResult(true, null, true, web, "web", output);
        }

        return new PrCreateResult(true, null, true, null, "none", output);
    }

    /// <summary>
    /// Make <c>origin/&lt;branch&gt;</c> match the local branch, discarding whatever it held.
    ///
    /// <para>
    /// The one operation here that destroys something on purpose, and it exists because a rebase
    /// makes it necessary: the commits already pushed are the pre-rebase copies of the same work,
    /// and nothing else can replace them. It is a separate button rather than a fallback inside the
    /// PR flow precisely so that it is never what happens when a plain push is refused.
    /// </para>
    ///
    /// <para>
    /// <c>--force-with-lease</c>, and deliberately no fetch first. The lease compares origin against
    /// the remote-tracking ref this dialog last read, so a push by someone else in the meantime is
    /// rejected rather than overwritten. Refreshing that ref immediately beforehand would adopt
    /// their commits as the expected value and quietly hand back the plain <c>--force</c> this is
    /// meant to avoid.
    /// </para>
    /// </summary>
    public async Task<ForcePushResult> ForcePushAsync(string projectKey, string branch, CancellationToken ct = default,
        string? member = null)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var target = WriteTargetOf(cfg, member, out var notHere);
        var repo = target?.Path ?? "";
        var baseBranch = target?.BaseBranch ?? ResolveBaseBranch(cfg);
        var output = new List<string>();

        ForcePushResult Fail(string error, bool stale = false) =>
            new(false, error, stale, branch, null, output);

        if (notHere is not null) return Fail(notHere);
        if (string.IsNullOrWhiteSpace(branch)) return Fail("This ticket has no branch yet.");
        if (!IsGitRepo(repo)) return Fail($"{cfg.DisplayName} has no git repo at '{repo}'.");
        // The base branch is the one ref where a force push is never a local matter.
        if (branch == baseBranch) return Fail($"'{branch}' is the base branch — this will not force push it.");

        var exists = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        if (!exists.Ok) return Fail($"Branch '{branch}' does not exist in {repo}.");

        var remoteUrl = (await GitAsync(repo, ct, "remote", "get-url", "origin")).Stdout.Trim();
        if (string.IsNullOrWhiteSpace(remoteUrl)) return Fail("This repo has no 'origin' remote to push to.");

        var push = await GitAsync(repo, ct, TimeSpan.FromMinutes(5),
            "push", "--force-with-lease", "--set-upstream", "origin", branch);
        output.Add($"git push --force-with-lease --set-upstream origin {branch}");
        AddLines(output, push);

        if (!push.Ok)
        {
            var said = (push.Stderr + push.Stdout);
            var stale = said.Contains("stale info", StringComparison.OrdinalIgnoreCase);
            return stale
                ? Fail($"origin/{branch} has moved since this dialog last fetched, so the force push was " +
                       "refused rather than overwriting whatever arrived. Re-check and look at what changed.", stale: true)
                : Fail(Explain(push, $"could not force push '{branch}' to origin"));
        }

        var sha = (await GitAsync(repo, ct, "rev-parse", branch)).Stdout.Trim();
        _log.LogWarning("Force pushed {Branch} to origin at {Sha}", branch, Short(sha));
        return new ForcePushResult(true, null, false, branch, sha, output);
    }

    // ---- merging instead of a PR ---------------------------------------------

    /// <summary>
    /// Merge the ticket's branch into the base branch in the hub — the whole of delivery on a project
    /// that does not use pull requests (<see cref="ProjectConfig.MergeInsteadOfPr"/>).
    ///
    /// <para>
    /// Local only: it commits a <c>--no-ff</c> merge on the hub's base branch and pushes nothing.
    /// Publishing main is left to the person, and the result says how far it now sits ahead of origin.
    /// </para>
    ///
    /// <para>
    /// Fenced the same way the rebase is. The hub must already be on the base branch with no tracked
    /// changes — this will not switch the user's checkout for them or merge across their edits. A
    /// local base that has fallen behind origin is fast-forwarded first, so the merge lands on top of
    /// what everyone else has; one that has diverged from origin is refused, because reconciling
    /// that is a decision. A conflicted merge is aborted and handed back with the files that clashed,
    /// leaving the base exactly where it was.
    /// </para>
    /// </summary>
    public async Task<MergeResult> MergeAsync(
        string projectKey, string branch, string? title, string? description, CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var repo = cfg.ProjectPath?.Trim().TrimEnd('\\', '/') ?? "";
        var baseBranch = ResolveBaseBranch(cfg);
        var output = new List<string>();
        var noFiles = Array.Empty<string>();
        var fastForwarded = false;

        MergeResult Fail(string error, bool conflicted = false, IReadOnlyList<string>? files = null) =>
            new(false, error, conflicted, files ?? noFiles, branch, baseBranch, repo, null, fastForwarded, 0, output);

        if (string.IsNullOrWhiteSpace(branch)) return Fail("This ticket has no branch yet.");
        if (!IsGitRepo(repo)) return Fail($"{cfg.DisplayName} has no git repo at '{repo}'.");
        if (branch == baseBranch) return Fail($"'{branch}' is the base branch — there is nothing to merge.");

        var exists = await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        if (!exists.Ok) return Fail($"Branch '{branch}' does not exist in {repo}.");

        var head = (await GitAsync(repo, ct, "symbolic-ref", "--quiet", "--short", "HEAD")).Stdout.Trim();
        if (head != baseBranch)
            return Fail($"{repo} is on '{(head.Length > 0 ? head : "a detached HEAD")}', not '{baseBranch}'. " +
                        $"Switch it back to {baseBranch} first — this merges there and will not move your checkout.");

        if ((await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", "MERGE_HEAD")).Ok)
            return Fail($"A merge is already in progress in {repo}. Finish or abort that one first.");

        // Untracked files are left alone: git refuses by itself if the merge would overwrite one.
        var tracked = await GitAsync(repo, ct, "status", "--porcelain", "--untracked-files=no");
        var dirty = tracked.Stdout.Split('\n').Count(l => l.Trim().Length > 0);
        if (dirty > 0)
            return Fail($"{dirty} uncommitted change{(dirty == 1 ? "" : "s")} in {repo}. Commit or stash them before merging.");

        // Land on top of what origin has, when there is an origin to ask.
        var hasOrigin = (await GitAsync(repo, ct, "remote", "get-url", "origin")).Ok;
        var originBase = $"origin/{baseBranch}";
        var hasOriginBase = false;
        if (hasOrigin)
        {
            var fetch = await GitAsync(repo, ct, TimeSpan.FromSeconds(90), "fetch", "origin", baseBranch);
            if (!fetch.Ok) output.Add($"[dashboard] {Explain(fetch, "could not fetch from origin")}");
            hasOriginBase = (await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/remotes/{originBase}")).Ok;
        }
        if (hasOriginBase)
        {
            var (localOnly, remoteOnly) = await CountAsync(repo, baseBranch, originBase, ct);
            if (remoteOnly > 0 && localOnly > 0)
                return Fail($"Your local {baseBranch} and {originBase} have diverged ({localOnly} and {remoteOnly} commits " +
                            "of their own). Reconcile them by hand before merging a ticket on top.");
            if (remoteOnly > 0)
            {
                if (await HeldRefusalAsync(repo, baseBranch, originBase, $"bringing {baseBranch} up to {originBase}", ct) is { } held)
                    return Fail(held);
                output.Add($"git merge --ff-only {originBase}");
                var ff = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "merge", "--ff-only", originBase);
                AddLines(output, ff);
                if (!ff.Ok) return Fail(Explain(ff, $"could not fast-forward {baseBranch} to {originBase}"));
                fastForwarded = true;
            }
        }

        var (_, ahead) = await CountAsync(repo, baseBranch, branch, ct);
        if (ahead == 0) return Fail($"'{branch}' has no commits that {baseBranch} does not already have.");

        // Work the merge out in memory first. A conflict is then refused before the checkout is
        // touched at all, rather than half-merged into it and rolled back - and a rollback is exactly
        // the step that has failed here (see RetryAnswerer). The tree it predicts is also what the
        // real merge must produce, which is checked below.
        var predicted = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "merge-tree", "--write-tree", "--name-only", baseBranch, branch);
        string? expectedTree = null;
        if (predicted.ExitCode is 0 or 1)
        {
            var lines = predicted.Stdout.Replace("\r", "").Split('\n');
            expectedTree = lines[0].Trim();
            if (predicted.ExitCode == 1)
            {
                var conflicted = lines.Skip(1).TakeWhile(l => l.Length > 0).Distinct().ToList();
                var word = conflicted.Count == 1 ? "file" : "files";
                return Fail($"Merging into {baseBranch} conflicts in {conflicted.Count} {word}. Nothing was changed - " +
                            "rebase the branch and resolve them, then merge again.", true, conflicted);
            }
        }
        else output.Add($"[dashboard] {Explain(predicted, "could not pre-check the merge; merging without it")}");

        // What the merge writes into the checkout is exactly what the branch changed. Any of it held
        // open elsewhere would stall git on a "try again?" for as long as it stays open - see HeldFiles.
        if (await HeldRefusalAsync(repo, $"{baseBranch}...{branch}", null, "the merge", ct) is { } heldByMerge)
            return Fail(heldByMerge);

        var previous = (await GitAsync(repo, ct, "rev-parse", baseBranch)).Stdout.Trim();
        var subject = string.IsNullOrWhiteSpace(title) ? $"Merge branch '{branch}'" : title.Trim();

        // core.editor pinned to a no-op for the same reason as the rebase: nothing may wait on a prompt.
        var args = new List<string> { "-c", "core.editor=true", "merge", "--no-ff", "--no-edit", "-m", subject };
        if (!string.IsNullOrWhiteSpace(description)) args.AddRange(new[] { "-m", description.Trim() });
        args.Add(branch);

        output.Add($"git merge --no-ff {branch}");
        var merge = await GitAsync(repo, ct, TimeSpan.FromMinutes(5), args.ToArray());
        AddLines(output, merge);

        if (!merge.Ok)
        {
            var conflicts = await ConflictedPathsAsync(repo, ct);
            if ((await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", "MERGE_HEAD")).Ok)
            {
                var abort = await GitAsync(repo, ct, TimeSpan.FromMinutes(2), "merge", "--abort");
                AddLines(output, abort);
                if (!abort.Ok)
                    return Fail($"The merge stopped and could not be rolled back — {repo} is still mid-merge. " +
                                $"Sort it out by hand: git -C \"{repo}\" merge --abort", conflicts.Count > 0, conflicts);
            }

            var now = (await GitAsync(repo, ct, "rev-parse", baseBranch)).Stdout.Trim();
            if (now != previous)
                return Fail($"The merge failed and '{baseBranch}' did not come back to where it was. " +
                            $"It was at {Short(previous)}: git -C \"{repo}\" reset --hard {previous}");

            if (conflicts.Count > 0)
            {
                var word = conflicts.Count == 1 ? "file" : "files";
                return Fail($"Merging into {baseBranch} conflicts in {conflicts.Count} {word}. Nothing was changed — " +
                            "rebase the branch and resolve them, then merge again.", true, conflicts);
            }
            return Fail(Explain(merge, $"could not merge '{branch}' into {baseBranch}"));
        }

        var merged = (await GitAsync(repo, ct, "rev-parse", baseBranch)).Stdout.Trim();

        // The merge said it worked; make it prove it. A merge commit whose tree is not the merge of
        // its parents is how 58fa1fb8 shipped a whole feature's message with none of its files.
        if (expectedTree is { Length: > 0 })
        {
            var tree = (await GitAsync(repo, ct, "rev-parse", $"{baseBranch}^{{tree}}")).Stdout.Trim();
            if (tree != expectedTree)
                return Fail($"The merge committed {Short(merged)}, but its contents are not the merge of {baseBranch} and " +
                            $"'{branch}' (tree {Short(tree)}, expected {Short(expectedTree)}). Do not push it. " +
                            $"To undo it: git -C \"{repo}\" reset --hard {previous}");
        }

        var aheadOfOrigin = hasOriginBase ? (await CountAsync(repo, originBase, baseBranch, ct)).Right : 0;
        _log.LogInformation("Merged {Branch} into {Base} in {Repo}: {Previous} -> {New}",
            branch, baseBranch, repo, Short(previous), Short(merged));

        return new MergeResult(true, null, false, noFiles, branch, baseBranch, repo, merged, fastForwarded, aheadOfOrigin, output)
        {
            HasOrigin = hasOriginBase,
        };
    }

    // ---- remote hosts -------------------------------------------------------

    /// <summary>Which host this origin points at, from the URL alone. No network, no config.</summary>
    public static string ProviderOf(string remoteUrl)
    {
        var u = remoteUrl.Trim();
        if (u.Contains("github.com", StringComparison.OrdinalIgnoreCase)) return "github";
        if (u.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains(".visualstudio.com", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("vs-ssh.visualstudio.com", StringComparison.OrdinalIgnoreCase)) return "azure-devops";
        return "other";
    }

    /// <summary>GitHub's compare page, prefilled — the fallback for when <c>gh</c> is missing or unauthenticated.</summary>
    public static string? GitHubCompareUrl(string remoteUrl, string baseBranch, string branch)
    {
        var (owner, repo) = GitHubSlug(remoteUrl);
        if (owner is null || repo is null) return null;
        return $"https://github.com/{owner}/{repo}/compare/{Uri.EscapeDataString(baseBranch)}...{Uri.EscapeDataString(branch)}?expand=1";
    }

    private static (string? Owner, string? Repo) GitHubSlug(string remoteUrl)
    {
        var u = remoteUrl.Trim();
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        // git@github.com:owner/repo  →  the path is everything after the colon.
        var at = u.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return (null, null);
        var rest = u[(at + "github.com".Length)..].TrimStart(':', '/');
        var seg = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return seg.Length >= 2 ? (seg[0], seg[1]) : (null, null);
    }

    /// <summary>
    /// Azure DevOps' create-PR form with the branches and text already filled in.
    ///
    /// Both URL shapes are handled because both are still issued:
    /// <c>https://org@dev.azure.com/org/project/_git/repo</c> and the older
    /// <c>https://org.visualstudio.com/project/_git/repo</c>. Everything is normalised onto
    /// dev.azure.com, which serves either.
    /// </summary>
    public static string? AzureDevOpsPrUrl(string remoteUrl, string baseBranch, string branch, string? title, string? description)
    {
        if (!Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri)) return null;

        var seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToList();

        // "DefaultCollection" and the SSH form's leading "v3" are routing noise, not path.
        seg.RemoveAll(s => s.Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) || s == "v3");

        string? org, project, repo;
        if (uri.Host.EndsWith("visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            org = uri.Host.Split('.')[0];
            // vs-ssh puts the org back in the path; dev.azure.com-style hosts do not.
            if (org.Equals("vs-ssh", StringComparison.OrdinalIgnoreCase) && seg.Count > 0)
            {
                org = seg[0];
                seg.RemoveAt(0);
            }
            var git = seg.IndexOf("_git");
            if (git < 1 || git + 1 >= seg.Count) return null;
            project = string.Join('/', seg.Take(git));
            repo = seg[git + 1];
        }
        else
        {
            var git = seg.IndexOf("_git");
            if (git < 2 || git + 1 >= seg.Count) return null;
            org = seg[0];
            project = string.Join('/', seg.Skip(1).Take(git - 1));
            repo = seg[git + 1];
        }

        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];

        var path = $"https://dev.azure.com/{Esc(org)}/{Esc(project)}/_git/{Esc(repo)}/pullrequestcreate";
        var query = new List<string>
        {
            $"sourceRef={Uri.EscapeDataString(branch)}",
            $"targetRef={Uri.EscapeDataString(baseBranch)}",
        };
        if (!string.IsNullOrWhiteSpace(title)) query.Add($"title={Uri.EscapeDataString(title)}");
        if (!string.IsNullOrWhiteSpace(description)) query.Add($"description={Uri.EscapeDataString(description)}");
        return $"{path}?{string.Join('&', query)}";

        // Path segments, so a project name with a space survives the round trip.
        static string Esc(string s) => Uri.EscapeDataString(s).Replace("%2F", "/");
    }

    // ---- plumbing -----------------------------------------------------------

    /// <summary>Project's <c>baseBranch</c>, else the global one, else main — the same order the worker uses.</summary>
    private string ResolveBaseBranch(ProjectConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.BaseBranch)) return cfg.BaseBranch.Trim();
        var global = _registry.GetGlobalConfig();
        if (global.TryGetValue("baseBranch", out var v) && v.GetString() is { Length: > 0 } s) return s.Trim();
        return "main";
    }

    /// <summary>
    /// The ref a ticket's branch is measured against and rebased onto, or null when the base exists
    /// nowhere. The same rule as <c>Resolve-BaseRef</c> in <c>data/worker.ps1</c>, and the two have
    /// to agree: the worker forks and squashes against one, and this draws the diff a person reviews.
    ///
    /// <para>
    /// A project that raises pull requests is measured against <c>origin/&lt;base&gt;</c> - the local
    /// ref is only as fresh as the last pull. One that merges instead
    /// (<see cref="ProjectConfig.MergeInsteadOfPr"/>) is measured against the local base, because
    /// that is where <see cref="MergeAsync"/> lands every ticket and nothing pushes it: measured
    /// against origin, a branch cut from local main would show every unpushed ticket before it as
    /// its own change. The exception is a local base strictly behind origin, which the merge
    /// fast-forwards first.
    /// </para>
    /// </summary>
    private async Task<string?> BaseRefAsync(string repo, ProjectConfig cfg, string baseBranch, CancellationToken ct)
    {
        var remoteRead = GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{baseBranch}");
        var localRead = GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{baseBranch}");
        await Task.WhenAll(remoteRead, localRead);
        var hasRemote = (await remoteRead).Ok;
        var hasLocal = (await localRead).Ok;

        var origin = $"origin/{baseBranch}";
        if (!hasRemote) return hasLocal ? baseBranch : null;
        if (!(cfg.MergeInsteadOfPr && !cfg.IsTfvc) || !hasLocal) return origin;

        var (localOnly, remoteOnly) = await CountAsync(repo, $"refs/heads/{baseBranch}", $"refs/remotes/{origin}", ct);
        return remoteOnly > 0 && localOnly == 0 ? origin : baseBranch;
    }

    private static bool IsGitRepo(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && Path.Exists(Path.Combine(dir, ".git"));

    /// <summary>
    /// Two counts from one command: commits on <paramref name="left"/> alone, then on
    /// <paramref name="right"/> alone. Both zero means the two refs are the same commit.
    /// </summary>
    private async Task<(int Left, int Right)> CountAsync(string repo, string left, string right, CancellationToken ct)
    {
        var counts = (await GitAsync(repo, ct, "rev-list", "--left-right", "--count", $"{left}...{right}")).Stdout;
        var parts = counts.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return (
            parts.Length > 0 && int.TryParse(parts[0], out var l) ? l : 0,
            parts.Length > 1 && int.TryParse(parts[1], out var r) ? r : 0);
    }

    /// <summary>An abbreviated sha for a message a human reads, without asking git for it again.</summary>
    /// <summary>
    /// A refusal naming the files between <paramref name="from"/> and <paramref name="to"/> (or the
    /// range in <paramref name="from"/> alone) that another program is holding open, or null when
    /// git is free to rewrite them all. Asked before, never during: git itself only ever waits.
    /// </summary>
    private async Task<string?> HeldRefusalAsync(string repo, string from, string? to, string what, CancellationToken ct)
    {
        var args = new List<string> { "diff", "--name-only", "--no-renames", from };
        if (to is not null) args.Add(to);
        var names = await GitAsync(repo, ct, args.ToArray());
        if (!names.Ok) return null;

        var held = await HeldFiles.FindAsync(repo, names.Stdout.Replace("\r", "").Split('\n'), ct);
        if (held.Count == 0) return null;

        var list = string.Join("; ", held.Take(5).Select(h =>
            h.Holders.Count > 0 ? $"{h.Path} (open in {string.Join(", ", h.Holders)})" : h.Path));
        if (held.Count > 5) list += $"; and {held.Count - 5} more";
        var files = held.Count == 1 ? "a file" : $"{held.Count} files";
        _log.LogWarning("Refused {What} in {Repo}: {List}", what, repo, list);
        return $"Nothing was changed: {what} has to rewrite {files} that another program has open, and git would " +
               $"wait on {(held.Count == 1 ? "it" : "them")} for as long as {(held.Count == 1 ? "it stays" : "they stay")} open. " +
               $"{list}. Close {(held.Count == 1 ? "it" : "them")} there, then try again.";
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>Everything a git command said, in the order it said it, for the transcript in the dialog.</summary>
    private static void AddLines(List<string> output, RunResult r)
    {
        foreach (var stream in new[] { r.Stdout, r.Stderr })
            output.AddRange(stream.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0));
    }

    /// <summary>git's own words when it has any, and a plain sentence when it does not.</summary>
    private static string Explain(RunResult r, string fallback)
    {
        var said = r.Stderr.Trim();
        if (said.Length == 0) said = r.Stdout.Trim();
        return said.Length == 0 ? $"{fallback}." : $"{fallback}: {said}";
    }

    /// <summary>
    /// One file's hunks against the base, on their own.
    /// <para>
    /// Deliberately not <see cref="PreflightAsync"/> with a filter. Preflight answers "is this
    /// branch fit to raise a PR" — it fetches from the remote, ranks every changed file and builds
    /// the whole diff. Asking it for one file would pay all of that to throw away nearly all of it,
    /// on a click that is meant to feel instant.
    /// </para>
    /// </summary>
    public record FileDiff(string Path, string Patch, int Added, int Removed, bool Truncated, string? Error);

    /// <summary>Enough for any single file worth citing; a generated blob is not worth the wire.</summary>
    private const int MaxFileDiffBytes = 120_000;

    /// <summary>
    /// The diff for one file between the base branch and <paramref name="headRef"/>.
    /// <para>
    /// <paramref name="headRef"/> is a commit rather than a branch wherever the caller has one: the
    /// Classroom pins it to the sha its analysis actually read, so the diff on screen cannot
    /// disagree with the diagram drawn beside it.
    /// </para>
    /// <para>
    /// With <paramref name="fromRef"/>, the diff from that commit to <paramref name="headRef"/>
    /// instead: a unit report's range, whose end is the base itself and so has nothing against it.
    /// </para>
    /// </summary>
    public async Task<FileDiff> FileDiffAsync(string projectKey, string headRef, string path,
        CancellationToken ct = default, string? fromRef = null)
    {
        FileDiff Fail(string error) => new(path, "", 0, 0, false, error);

        var cfg = _registry.GetProjectConfig(projectKey);
        // The path is model-written. `--` already stops git reading it as an option, and git will not
        // resolve outside the repo anyway; this refuses the shapes that would only ever be a mistake.
        // On a project of several repos its first segment says which one (see TicketGitService.Repos).
        if (CleanRepoPath(path) is not { } named) return Fail($"'{path}' is not a path inside the repo.");
        if (SplitRepoPath(cfg, named, out var notHere) is not { } split) return Fail(notHere!);
        var (target, clean) = split;
        var repo = target.Path;
        if (!IsGitRepo(repo)) return Fail($"{cfg.DisplayName} has no git repo at '{repo}'.");
        if (string.IsNullOrWhiteSpace(headRef)) return Fail("This analysis is not pinned to a commit.");

        var range = fromRef is null ? $"{target.BaseBranch}...{headRef}" : $"{fromRef}..{headRef}";

        var numstat = await GitAsync(repo, ct, "diff", "--numstat", "-M", range, "--", clean);
        if (!numstat.Ok) return Fail(Explain(numstat, $"Could not read the diff for {clean}"));

        var added = 0;
        var removed = 0;
        foreach (var line in numstat.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            if (int.TryParse(parts[0], out var a)) added += a;
            if (int.TryParse(parts[1], out var r)) removed += r;
        }

        var diff = await GitAsync(repo, ct, "diff", "-M", range, "--", clean);
        if (!diff.Ok) return Fail(Explain(diff, $"Could not read the diff for {clean}"));

        var patch = diff.Stdout;
        var truncated = patch.Length > MaxFileDiffBytes;
        if (truncated) patch = patch[..MaxFileDiffBytes];

        return new FileDiff(Prefixed(target, clean), patch, added, removed, truncated, null);
    }

    /// <summary>
    /// A model-written repo path, normalised, or null when it could only ever be a mistake. `--`
    /// already stops git reading it as an option and git will not resolve outside the repo anyway;
    /// this refuses the shapes that point elsewhere on purpose.
    /// </summary>
    private static string? CleanRepoPath(string path)
    {
        var clean = path.Replace('\\', '/').Trim();
        if (clean.Length == 0 || clean.Contains("..") || clean.StartsWith('/') || clean.StartsWith('~')
            || (clean.Length > 1 && clean[1] == ':'))
            return null;
        return clean;
    }

    /// <summary>
    /// One file's full text as it stood at <paramref name="commit"/>, read from the hub repo (which
    /// shares its objects with every worktree). A diff only carries three lines of context, and a
    /// slide showing new code usually wants the <c>if</c> it sits inside too.
    /// </summary>
    public async Task<(string? Text, string? Error)> FileAtAsync(string projectKey, string commit, string path,
        CancellationToken ct = default)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        if (CleanRepoPath(path) is not { } named) return (null, $"'{path}' is not a path inside the repo.");
        if (SplitRepoPath(cfg, named, out var notHere) is not { } split) return (null, notHere);
        var (target, clean) = split;
        var repo = target.Path;
        if (!IsGitRepo(repo)) return (null, $"{cfg.DisplayName} has no git repo at '{repo}'.");

        var r = await GitAsync(repo, ct, "show", $"{commit}:{clean}");
        return r.Ok ? (r.Stdout, null) : (null, Explain(r, $"Could not read {clean} at {commit[..Math.Min(7, commit.Length)]}"));
    }

    /// <summary>
    /// Every file the branch changed against its base, with line counts - what a script may cite. With
    /// <paramref name="fromRef"/>, every file changed from that commit to <paramref name="headRef"/>.
    /// <para>
    /// On a project of several repos, the files of every repo <paramref name="headRef"/> (and
    /// <paramref name="fromRef"/>) can be read in, each named with its repo first. A branch name is
    /// in every writable repo; a commit is in the one that made it.
    /// </para>
    /// </summary>
    public async Task<(IReadOnlyList<(string Path, int Added, int Removed)> Files, string? Error)> ChangedFilesAsync(
        string projectKey, string headRef, CancellationToken ct = default, string? fromRef = null)
    {
        var cfg = _registry.GetProjectConfig(projectKey);
        var none = Array.Empty<(string, int, int)>();
        var files = new List<(string, int, int)>();

        if (!cfg.IsMultiRepo)
        {
            var only = TargetOf(cfg, null, out _)!;
            if (!IsGitRepo(only.Path)) return (none, $"{cfg.DisplayName} has no git repo at '{only.Path}'.");
            var error = await ChangedFilesInAsync(only, headRef, fromRef, files, ct);
            return error is null ? (files, null) : (none, error);
        }

        var read = 0;
        foreach (var target in TargetsOf(cfg))
        {
            if (!IsGitRepo(target.Path)) continue;
            if (!await ResolvesAsync(target.Path, headRef, ct) || (fromRef is not null && !await ResolvesAsync(target.Path, fromRef, ct)))
                continue;
            if (await ChangedFilesInAsync(target, headRef, fromRef, files, ct) is { } error) return (none, $"{target.Dir}: {error}");
            read++;
        }
        return read > 0 ? (files, null) : (none, $"'{headRef}' is in none of {cfg.DisplayName}'s repos.");
    }

    private async Task<string?> ChangedFilesInAsync(RepoTarget target, string headRef, string? fromRef,
        List<(string, int, int)> into, CancellationToken ct)
    {
        var r = await GitAsync(target.Path, ct, "diff", "--numstat", "--no-renames",
            fromRef is null ? $"{target.BaseBranch}...{headRef}" : $"{fromRef}..{headRef}");
        if (!r.Ok) return Explain(r, "Could not list the branch's changes");

        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3) continue;
            into.Add((Prefixed(target, parts[2]), int.TryParse(parts[0], out var a) ? a : 0, int.TryParse(parts[1], out var d) ? d : 0));
        }
        return null;
    }

    private async Task<bool> ResolvesAsync(string repo, string rev, CancellationToken ct) =>
        (await GitAsync(repo, ct, "rev-parse", "--verify", "--quiet", $"{rev}^{{commit}}")).Ok;

    private record RunResult(bool Ok, int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// A program git runs, as <c>GIT_ASK_YESNO</c>, when Windows refuses it a rename or an unlink.
    /// <para>
    /// Git replaces the index by renaming <c>index.lock</c> over it, and Windows refuses that rename
    /// while any other process has <c>index</c> open - which the Claude desktop app's own
    /// <c>git status</c> polling does many times a minute. Git for Windows retries for a moment,
    /// then asks "Should I try again?"; with no terminal and no answerer it says no and dies with
    /// "unable to write new index file", having already rewritten the working tree. On 2026-09-26
    /// that tore a merge abort in half: every file back at HEAD, the index still the merge's, and
    /// MERGE_HEAD left behind. This answers yes after a second's wait, so a reader holding the
    /// file costs a retry instead of the repo. It is bounded by the caller's own timeout - which is
    /// why a merge first asks <see cref="HeldFiles"/> whether anything is holding a file for good.
    /// </para>
    /// Null off Windows, or if the file cannot be written, which leaves git's default in place.
    /// </summary>
    private static readonly Lazy<string?> RetryAnswerer = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "whetstone-git-retry.cmd");
            const string body = "@ping -n 2 127.0.0.1 >nul\r\n@exit 0\r\n";
            if (!File.Exists(path) || File.ReadAllText(path) != body) File.WriteAllText(path, body);
            return path;
        }
        catch { return null; }
    });

    private Task<RunResult> GitAsync(string dir, CancellationToken ct, params string[] args) =>
        GitAsync(dir, ct, TimeSpan.FromSeconds(30), args);

    private Task<RunResult> GitAsync(string dir, CancellationToken ct, TimeSpan timeout, params string[] args)
    {
        // core.quotepath=false keeps non-ASCII paths readable instead of \NNN-escaped, and the
        // pager/terminal-prompt pair stops a network call blocking forever on a credential prompt
        // nobody is there to answer.
        var full = new List<string> { "-c", "core.quotepath=false", "--no-pager" };
        full.AddRange(args);
        return RunAsync("git", dir, timeout, ct, full.ToArray());
    }

    private async Task<RunResult> RunAsync(string exe, string dir, TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = ProcessText.Utf8,
            StandardErrorEncoding = ProcessText.Utf8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Never let a network operation stop on a credential dialog: this runs headless behind an
        // HTTP request, so a prompt is just a hang. Fail instead, and say so.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (exe == "git" && RetryAnswerer.Value is { } yes) psi.Environment["GIT_ASK_YESNO"] = yes;

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new RunResult(false, -1, "", ex.Message);
        }

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new RunResult(false, -1, "", $"{exe} {args.FirstOrDefault()} did not finish within {(int)timeout.TotalSeconds}s.");
        }

        return new RunResult(proc.ExitCode == 0, proc.ExitCode, await stdout, await stderr);
    }
}
