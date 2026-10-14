using System.Diagnostics;
using Whetstone.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The human's git moves on a ticket, against real repos.
///
/// The behaviour worth pinning here is refusal and honesty. Checkout must not run while the user
/// has uncommitted work — that is the one way this feature could destroy something — and the PR
/// preflight must count "behind the base" correctly, because a wrong zero there is an approval
/// nobody has any reason to doubt.
/// </summary>
public class TicketGitServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _data;
    private readonly string _repo;
    private readonly string _origin;
    private readonly TicketGitService _svc;

    public TicketGitServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-git-" + Guid.NewGuid().ToString("N")[..8]);
        _data = Path.Combine(_root, "data");
        _repo = Path.Combine(_root, "repo");
        _origin = Path.Combine(_root, "origin.git");

        // A bare origin so push and fetch are exercised for real, without a network.
        Directory.CreateDirectory(_origin);
        Git(_origin, "init", "--bare", "-b", "main");

        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-b", "main");
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Test");
        Git(_repo, "remote", "add", "origin", _origin);
        Write("README.md", "hello\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");
        Git(_repo, "push", "-u", "origin", "main");

        Directory.CreateDirectory(Path.Combine(_data, "projects", "alpha"));
        File.WriteAllText(
            Path.Combine(_data, "projects", "alpha", "project.defaults.json"),
            $$"""
            {
              "displayName": "Alpha",
              "projectPath": {{System.Text.Json.JsonSerializer.Serialize(_repo)}},
              "baseBranch": "main"
            }
            """);
        // The same repo again, as a project that merges into its local base instead of raising a PR.
        Directory.CreateDirectory(Path.Combine(_data, "projects", "local"));
        File.WriteAllText(
            Path.Combine(_data, "projects", "local", "project.defaults.json"),
            $$"""
            {
              "displayName": "Local",
              "projectPath": {{System.Text.Json.JsonSerializer.Serialize(_repo)}},
              "baseBranch": "main",
              "mergeInsteadOfPr": true
            }
            """);
        File.WriteAllText(Path.Combine(_data, "config.defaults.json"), """{ "baseBranch": "main" }""");

        var registry = new ProjectRegistry(new DashboardPaths(_data));
        _svc = new TicketGitService(registry, new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
            NullLogger<TicketGitService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- checkout -----------------------------------------------------------

    [Fact]
    public async Task Checks_out_a_branch_that_nothing_else_holds()
    {
        Git(_repo, "branch", "jb/ALPHA-1", "main");

        var r = await _svc.CheckoutAsync("alpha", "jb/ALPHA-1");

        Assert.True(r.Ok, r.Error);
        Assert.False(r.Forced);
        Assert.Equal("jb/ALPHA-1", CurrentBranch());
    }

    [Fact]
    public async Task Refuses_while_the_working_tree_has_uncommitted_changes()
    {
        Git(_repo, "branch", "jb/ALPHA-1", "main");
        Write("README.md", "edited by the human\n");

        var r = await _svc.CheckoutAsync("alpha", "jb/ALPHA-1");

        Assert.False(r.Ok);
        Assert.Equal("main", CurrentBranch());
        Assert.Contains(r.Pending, c => c.Path == "README.md");
    }

    [Fact]
    public async Task Untracked_files_count_as_pending_work()
    {
        // An untracked file is exactly what a half-done experiment looks like, and a checkout can
        // carry one onto the wrong branch. "No pending changes" would be a lie that excluded it.
        Git(_repo, "branch", "jb/ALPHA-1", "main");
        Write("scratch.txt", "notes\n");

        var r = await _svc.CheckoutAsync("alpha", "jb/ALPHA-1");

        Assert.False(r.Ok);
        Assert.Contains(r.Pending, c => c.Path == "scratch.txt");
    }

    [Fact]
    public async Task Takes_a_branch_an_agent_worktree_holds_and_says_that_it_forced_it()
    {
        var wt = Path.Combine(_root, "wt", "jb-ALPHA-1");
        Git(_repo, "worktree", "add", "-b", "jb/ALPHA-1", wt, "main");

        var r = await _svc.CheckoutAsync("alpha", "jb/ALPHA-1");

        Assert.True(r.Ok, r.Error);
        Assert.True(r.Forced);
        Assert.Equal(Path.GetFullPath(wt), Path.GetFullPath(r.SharedWith!));
        Assert.Equal("jb/ALPHA-1", CurrentBranch());
    }

    [Fact]
    public async Task Recreates_a_branch_that_only_exists_on_origin()
    {
        // The worktree has been reaped and the local branch with it — the remote is all that is left.
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "work");
        Git(_repo, "push", "-u", "origin", "jb/ALPHA-1");
        Git(_repo, "checkout", "main");
        Git(_repo, "branch", "-D", "jb/ALPHA-1");

        var r = await _svc.CheckoutAsync("alpha", "jb/ALPHA-1");

        Assert.True(r.Ok, r.Error);
        Assert.True(r.CreatedFromRemote);
        Assert.Equal("jb/ALPHA-1", CurrentBranch());
    }

    [Fact]
    public async Task Refuses_a_branch_that_exists_nowhere()
    {
        var r = await _svc.CheckoutAsync("alpha", "jb/ALPHA-404");

        Assert.False(r.Ok);
        Assert.Contains("does not exist", r.Error);
        Assert.Equal("main", CurrentBranch());
    }

    [Fact]
    public async Task Already_being_on_the_branch_is_success_not_a_refusal()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("dirty.txt", "uncommitted\n");

        // Dirty, but there is nothing to move — refusing here would be refusing to do nothing.
        var r = await _svc.CheckoutAsync("alpha", "jb/ALPHA-1");

        Assert.True(r.Ok, r.Error);
        Assert.Empty(r.Pending);
    }

    // ---- PR preflight -------------------------------------------------------

    [Fact]
    public async Task A_branch_forked_from_the_current_base_reads_as_up_to_date()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "checkout", "main");

        var p = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");

        Assert.True(p.Ok, p.Error);
        Assert.Equal(0, p.Behind);
        Assert.Equal(1, p.Ahead);
        Assert.True(p.UpToDateWithBase);
        Assert.Contains(p.Files, f => f.Path == "a.txt");
        Assert.Single(p.Commits);
        Assert.Equal("add a", p.Commits[0].Subject);
    }

    [Fact]
    public async Task A_branch_that_missed_commits_pushed_to_the_base_reads_as_behind()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");

        // Someone else moves main on the server. This is the whole reason the check exists: nothing
        // in the local repo changed, and without a fetch the branch would still look current.
        Git(_repo, "checkout", "main");
        Write("b.txt", "someone else\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add b");
        Git(_repo, "push", "origin", "main");
        Git(_repo, "reset", "--hard", "HEAD~1");

        var p = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");

        Assert.True(p.Ok, p.Error);
        Assert.True(p.Fetched);
        Assert.Equal("origin/main", p.BaseRef);
        Assert.Equal(1, p.Behind);
        Assert.False(p.UpToDateWithBase);
    }

    /// <summary>
    /// The one commit's body is what the PR description is drafted from, so it has to survive the
    /// trip intact — subject stripped (the PR title carries that), body whole.
    /// </summary>
    [Fact]
    public async Task A_single_commit_hands_over_its_message_body_without_the_subject()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "ALPHA-1: add a\n\nWhat this delivers.\n\n- One specific\n- Another");

        var p = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");

        Assert.Equal("What this delivers.\n\n- One specific\n- Another", p.CommitBody?.Replace("\r\n", "\n"));
        Assert.DoesNotContain("ALPHA-1: add a", p.CommitBody);
    }

    /// <summary>
    /// More than one commit means refinement has not squashed the branch yet, so the messages are
    /// the provisional ones implementation and QA left behind. Better nothing than a concatenation.
    /// </summary>
    [Fact]
    public async Task An_unsquashed_branch_offers_no_message_at_all()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a\n\nA body worth having.");
        Write("b.txt", "more\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "fix casing after QA\n\nOops.");

        var p = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");

        Assert.Equal(2, p.Commits.Count);
        Assert.Null(p.CommitBody);
    }

    /// <summary>A subject-only commit has no body, which is a fallback rather than an empty draft.</summary>
    [Fact]
    public async Task A_subject_only_commit_offers_no_message()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");

        var p = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");

        Assert.Null(p.CommitBody);
    }

    [Fact]
    public async Task The_diff_excludes_base_commits_the_branch_has_not_merged()
    {
        // Two-dot would show b.txt as a deletion by this branch. Three-dot — what the service uses —
        // shows only what the branch itself did, which is what a reviewer is being asked to approve.
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");

        Git(_repo, "checkout", "main");
        Write("b.txt", "someone else\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add b");
        Git(_repo, "push", "origin", "main");

        var p = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");

        Assert.Contains(p.Files, f => f.Path == "a.txt");
        Assert.DoesNotContain(p.Files, f => f.Path == "b.txt");
        Assert.Contains("+work", p.Files.Single(f => f.Path == "a.txt").Patch);
    }

    [Fact]
    public async Task Counts_the_uncommitted_files_the_agent_left_in_its_worktree()
    {
        var wt = Path.Combine(_root, "wt", "jb-ALPHA-1");
        Git(_repo, "worktree", "add", "-b", "jb/ALPHA-1", wt, "main");
        File.WriteAllText(Path.Combine(wt, "a.txt"), "half done\n");
        Git(wt, "add", "-A");

        var p = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");

        Assert.Equal(1, p.WorktreeDirtyFiles);
        Assert.Equal(Path.GetFullPath(wt), Path.GetFullPath(p.WorktreePath!));
    }

    [Fact]
    public async Task Refuses_to_preflight_the_base_branch_itself()
    {
        var p = await _svc.PreflightAsync("alpha", "main");

        Assert.False(p.Ok);
        Assert.Contains("base branch", p.Error);
    }

    // ---- the two halves the dialog loads ------------------------------------

    /// <summary>
    /// The local half must not touch the network — that is the entire point of it — and the way to
    /// prove it is a remote that has moved. The full preflight sees the new base commit because it
    /// fetches; this one cannot, and says so with BaseChecked false rather than reporting a zero
    /// that would read as "properly based off the latest".
    /// </summary>
    [Fact]
    public async Task The_local_half_reads_the_branch_without_asking_origin_anything()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");

        // Someone else moves main on the server, exactly as in the behind-the-base test above.
        Git(_repo, "checkout", "main");
        Write("b.txt", "someone else\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add b");
        Git(_repo, "push", "origin", "main");
        Git(_repo, "reset", "--hard", "HEAD~1");
        Git(_repo, "update-ref", "refs/remotes/origin/main", Run(_repo, "git", "rev-parse", "main").Trim());

        var p = await _svc.LocalPreflightAsync("alpha", "jb/ALPHA-1");

        Assert.True(p.Ok, p.Error);
        Assert.False(p.BaseChecked);
        Assert.Equal(0, p.Behind); // the last-known answer, and stale — which BaseChecked declares
        // What the reader came for is all here regardless.
        Assert.Contains(p.Files, f => f.Path == "a.txt");
        Assert.Single(p.Commits);
        Assert.Equal("add a", p.Commits[0].Subject);
    }

    [Fact]
    public async Task The_base_check_fetches_and_answers_what_the_local_half_could_not()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");

        Git(_repo, "checkout", "main");
        Write("b.txt", "someone else\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add b");
        Git(_repo, "push", "origin", "main");
        Git(_repo, "reset", "--hard", "HEAD~1");

        var b = await _svc.BaseCheckAsync("alpha", "jb/ALPHA-1");

        Assert.True(b.Ok, b.Error);
        Assert.True(b.Fetched);
        Assert.Equal("origin/main", b.BaseRef);
        Assert.Equal(1, b.Behind);
        Assert.Equal(1, b.Ahead);
    }

    /// <summary>
    /// The one case where reading the diff before fetching gets it wrong, and the signal that says
    /// so. A remote-tracking ref older than the branch's own fork point puts the merge base too far
    /// back, so the diff carries commits this ticket never wrote. The fetch names it for free: those
    /// commits stop counting as the branch's own, so Ahead drops — and a caller that sees it drop
    /// knows to read the diff again instead of trusting what it has.
    /// </summary>
    [Fact]
    public async Task A_base_ref_older_than_the_fork_point_shows_up_as_a_drop_in_ahead()
    {
        var forkedFrom = Run(_repo, "git", "rev-parse", "main").Trim();

        Git(_repo, "checkout", "main");
        Write("b.txt", "someone else\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add b");
        Git(_repo, "push", "origin", "main");

        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");

        // Rewind what this repo believes origin/main to be, back past where the branch forked.
        Git(_repo, "update-ref", "refs/remotes/origin/main", forkedFrom);

        var local = await _svc.LocalPreflightAsync("alpha", "jb/ALPHA-1");
        Assert.Equal(2, local.Ahead);
        Assert.Contains(local.Files, f => f.Path == "b.txt"); // not this ticket's work

        var b = await _svc.BaseCheckAsync("alpha", "jb/ALPHA-1");
        Assert.Equal(1, b.Ahead);
        Assert.True(b.Ahead < local.Ahead);

        // And the re-read the drop calls for gets the diff the reviewer should see.
        var again = await _svc.LocalPreflightAsync("alpha", "jb/ALPHA-1");
        Assert.Equal(1, again.Ahead);
        Assert.DoesNotContain(again.Files, f => f.Path == "b.txt");
    }

    [Fact]
    public async Task The_base_check_refuses_the_base_branch_itself()
    {
        var b = await _svc.BaseCheckAsync("alpha", "main");

        Assert.False(b.Ok);
        Assert.Contains("base branch", b.Error);
    }

    /// <summary>The halves must not disagree: same repo, same branch, same numbers.</summary>
    [Fact]
    public async Task The_base_check_says_what_a_whole_preflight_says()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "push", "-u", "origin", "jb/ALPHA-1");

        var p = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");
        var b = await _svc.BaseCheckAsync("alpha", "jb/ALPHA-1");

        Assert.True(p.BaseChecked);
        Assert.Equal(p.Behind, b.Behind);
        Assert.Equal(p.Ahead, b.Ahead);
        Assert.Equal(p.BaseRef, b.BaseRef);
        Assert.Equal(p.Fetched, b.Fetched);
        Assert.Equal(p.RemoteBranchExists, b.RemoteBranchExists);
        Assert.Equal(p.RemoteUpToDate, b.RemoteUpToDate);
        Assert.Equal(p.RemoteAhead, b.RemoteAhead);
    }

    // ---- pushing ------------------------------------------------------------

    [Fact]
    public async Task Creating_pushes_the_branch_to_origin()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");

        var r = await _svc.CreateAsync("alpha", "jb/ALPHA-1", "ALPHA-1 add a", "body");

        Assert.True(r.Ok, r.Error);
        Assert.True(r.Pushed);
        // A file-path origin is neither GitHub nor Azure DevOps: pushed, and no PR flow to offer.
        Assert.Equal("none", r.Mode);
        Assert.Contains("jb/ALPHA-1", Run(_origin, "git", "branch", "--list", "jb/ALPHA-1"));
    }

    // ---- rebasing -----------------------------------------------------------

    [Fact]
    public async Task Rebases_a_branch_that_does_not_conflict_and_leaves_the_checkout_where_it_was()
    {
        MoveTheBaseOnTheServer("b.txt");
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "checkout", "main");

        var r = await _svc.RebaseAsync("alpha", "jb/ALPHA-1");

        Assert.True(r.Ok, r.Error);
        Assert.False(r.Conflicted);
        Assert.NotEqual(r.PreviousSha, r.NewSha);
        // Rebasing is not a checkout: the tree the user was looking at is where they left it.
        Assert.Equal("main", CurrentBranch());

        var p = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");
        Assert.Equal(0, p.Behind);
        Assert.Equal(1, p.Ahead);
        Assert.Contains(p.Files, f => f.Path == "a.txt");
    }

    /// <summary>
    /// The whole promise of the button. A conflict has to leave the branch byte-for-byte where it
    /// was, with nothing half-applied and no rebase left running for the next command to trip over.
    /// </summary>
    [Fact]
    public async Task A_conflicting_rebase_is_rolled_back_and_names_the_files_that_clashed()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("README.md", "our version\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "ours");
        var before = Sha("jb/ALPHA-1");

        Git(_repo, "checkout", "main");
        Write("README.md", "their version\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "theirs");
        Git(_repo, "push", "origin", "main");
        Git(_repo, "reset", "--hard", "HEAD~1");

        var r = await _svc.RebaseAsync("alpha", "jb/ALPHA-1");

        Assert.False(r.Ok);
        Assert.True(r.Conflicted);
        Assert.Contains("README.md", r.ConflictedFiles);
        Assert.Equal(before, Sha("jb/ALPHA-1"));
        Assert.Equal("main", CurrentBranch());
        Assert.Empty(Run(_repo, "git", "status", "--porcelain").Trim());
        // A rebase left in progress would break the next thing anyone does in this repo.
        Assert.DoesNotContain("rebase in progress", Run(_repo, "git", "status"));
    }

    [Fact]
    public async Task Refuses_to_rebase_while_the_tree_holding_the_branch_is_dirty()
    {
        MoveTheBaseOnTheServer("b.txt");
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        var before = Sha("jb/ALPHA-1");
        Write("a.txt", "still typing\n");

        var r = await _svc.RebaseAsync("alpha", "jb/ALPHA-1");

        Assert.False(r.Ok);
        Assert.False(r.Conflicted);
        Assert.Contains("uncommitted", r.Error);
        Assert.Equal(before, Sha("jb/ALPHA-1"));
    }

    /// <summary>
    /// The branch normally lives in an agent worktree, and git will not let the hub touch it there.
    /// The rebase has to happen in the tree that holds it.
    /// </summary>
    [Fact]
    public async Task Rebases_in_the_agent_worktree_when_that_is_what_holds_the_branch()
    {
        MoveTheBaseOnTheServer("b.txt");
        var wt = Path.Combine(_root, "wt", "jb-ALPHA-1");
        Git(_repo, "worktree", "add", "-b", "jb/ALPHA-1", wt, "main");
        File.WriteAllText(Path.Combine(wt, "a.txt"), "work\n");
        Git(wt, "add", "-A");
        Git(wt, "commit", "-m", "add a");

        var r = await _svc.RebaseAsync("alpha", "jb/ALPHA-1");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Path.GetFullPath(wt), Path.GetFullPath(r.RanIn));
        Assert.Equal("main", CurrentBranch());
        Assert.True(File.Exists(Path.Combine(wt, "b.txt")));
        Assert.Equal(0, (await _svc.PreflightAsync("alpha", "jb/ALPHA-1")).Behind);
    }

    [Fact]
    public async Task Refuses_to_rebase_a_branch_that_is_already_on_the_latest_base()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");

        var r = await _svc.RebaseAsync("alpha", "jb/ALPHA-1");

        Assert.False(r.Ok);
        Assert.Contains("nothing to rebase", r.Error);
    }

    // ---- force pushing ------------------------------------------------------

    [Fact]
    public async Task Force_pushing_replaces_what_origin_holds_with_this_branch()
    {
        MoveTheBaseOnTheServer("b.txt");
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "push", "-u", "origin", "jb/ALPHA-1");
        Git(_repo, "checkout", "main");

        // Rebasing after pushing is exactly the situation this button exists for: same work, new
        // commits, and a remote that can no longer be moved by an ordinary push.
        var rebase = await _svc.RebaseAsync("alpha", "jb/ALPHA-1");
        Assert.True(rebase.Ok, rebase.Error);
        Assert.Equal(1, (await _svc.PreflightAsync("alpha", "jb/ALPHA-1")).RemoteAhead);

        var r = await _svc.ForcePushAsync("alpha", "jb/ALPHA-1");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(rebase.NewSha, r.Sha);
        Assert.Equal(rebase.NewSha, Run(_origin, "git", "rev-parse", "jb/ALPHA-1").Trim());
        Assert.True((await _svc.PreflightAsync("alpha", "jb/ALPHA-1")).RemoteUpToDate);
    }

    /// <summary>
    /// The lease is the entire difference between this and a plain <c>--force</c>. When origin has
    /// moved since the dialog last looked, the push must be the thing that gives way.
    /// </summary>
    [Fact]
    public async Task A_force_push_gives_way_when_origin_moved_since_the_last_fetch()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "push", "-u", "origin", "jb/ALPHA-1");

        // Someone else's push, landing after this repo's remote-tracking ref was last updated.
        var theirs = Run(_origin, "git", "rev-parse", "main").Trim();
        Git(_origin, "update-ref", "refs/heads/jb/ALPHA-1", theirs);

        var r = await _svc.ForcePushAsync("alpha", "jb/ALPHA-1");

        Assert.False(r.Ok);
        Assert.True(r.Stale);
        Assert.Equal(theirs, Run(_origin, "git", "rev-parse", "jb/ALPHA-1").Trim());
    }

    [Fact]
    public async Task Refuses_to_force_push_the_base_branch()
    {
        var r = await _svc.ForcePushAsync("alpha", "main");

        Assert.False(r.Ok);
        Assert.Contains("base branch", r.Error);
    }

    // ---- merging instead of a PR ---------------------------------------------

    // ---- a unit's range ---------------------------------------------------------

    private void Branch(string name, string file)
    {
        Git(_repo, "checkout", "-b", name, "main");
        Write(file, "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add " + file);
        Git(_repo, "checkout", "main");
    }

    [Fact]
    public async Task A_units_range_runs_from_before_its_first_merge_and_counts_what_is_not_its_own()
    {
        var start = Sha("main");
        Branch("t1", "a.txt");
        Branch("t2", "b.txt");
        var m1 = (await _svc.MergeAsync("local", "t1", null, null)).MergeSha;
        // Somebody else's work, landing between two of the unit's merges.
        Write("other.txt", "not the unit's\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "other work");
        var m2 = (await _svc.MergeAsync("local", "t2", null, null)).MergeSha;

        var (range, error) = await _svc.UnitRangeAsync("local", new[]
        {
            new UnitTicketRef(1, "t1", m1, null),
            new UnitTicketRef(2, "t2", m2, null),
        }, previousReleasedSha: null);

        Assert.Null(error);
        Assert.Equal(start, range!.FromSha);
        Assert.Equal(Sha("main"), range.ToSha);
        Assert.Equal(3, range.RangeCommits);
        Assert.Equal(2, range.OwnCommits);
        Assert.Equal(Sha("t1"), range.Tips[1].HeadSha);
        Assert.Equal(Sha("t2^{tree}"), range.Tips[2].TreeSha);
    }

    [Fact]
    public async Task A_unit_never_merged_here_starts_where_its_branches_left_the_base()
    {
        var start = Sha("main");
        Branch("t1", "a.txt");
        Write("other.txt", "later\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "later work");

        var (range, _) = await _svc.UnitRangeAsync("local", new[] { new UnitTicketRef(1, "t1", null, null) }, null);

        Assert.Equal(start, range!.FromSha);
        Assert.Equal(0, range.OwnCommits);
    }

    [Fact]
    public async Task A_unit_is_read_from_the_hub_when_it_is_already_there_and_from_a_worktree_when_not()
    {
        var old = Sha("main");
        Write("other.txt", "later\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "later work");
        var scratch = Path.Combine(_root, "scratch");

        var (here, releaseHere, _) = await _svc.CheckoutForReadingAsync("local", Sha("main"), scratch);
        Assert.Equal(_repo, here);
        await releaseHere();

        var (dir, release, error) = await _svc.CheckoutForReadingAsync("local", old, scratch);
        Assert.Null(error);
        Assert.NotEqual(_repo, dir);
        Assert.False(File.Exists(Path.Combine(dir!, "other.txt")));
        Assert.Equal("main", CurrentBranch());
        await release();
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Merges_the_branch_into_the_base_with_a_merge_commit_and_pushes_nothing()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "checkout", "main");
        var originBefore = Run(_origin, "git", "rev-parse", "main").Trim();

        var r = await _svc.MergeAsync("alpha", "jb/ALPHA-1", "Merge: add a", "The body.");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Sha("main"), r.MergeSha);
        // A real merge commit, not a fast-forward: two parents, the branch as the second.
        Assert.Equal(Sha("jb/ALPHA-1"), Sha("main^2"));
        Assert.Equal("Merge: add a", Run(_repo, "git", "log", "-1", "--format=%s", "main").Trim());
        Assert.Equal(originBefore, Run(_origin, "git", "rev-parse", "main").Trim());
        // The branch's commit and the merge commit itself.
        Assert.Equal(2, r.AheadOfOrigin);
        Assert.Equal("main", CurrentBranch());
    }

    [Fact]
    public async Task A_merge_lands_on_top_of_origin_when_the_local_base_trails_it()
    {
        MoveTheBaseOnTheServer("b.txt");
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "checkout", "main");

        var r = await _svc.MergeAsync("alpha", "jb/ALPHA-1", null, null);

        Assert.True(r.Ok, r.Error);
        Assert.True(r.FastForwardedBase);
        Assert.Equal(Run(_origin, "git", "rev-parse", "main").Trim(), Sha("main^1"));
        Assert.True(File.Exists(Path.Combine(_repo, "b.txt")));
        Assert.Equal("Merge branch 'jb/ALPHA-1'", Run(_repo, "git", "log", "-1", "--format=%s", "main").Trim());
    }

    [Fact]
    public async Task A_conflicting_merge_is_aborted_and_leaves_the_base_where_it_was()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("README.md", "our version\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "ours");
        Git(_repo, "checkout", "main");
        Write("README.md", "their version\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "theirs");
        var before = Sha("main");

        var r = await _svc.MergeAsync("alpha", "jb/ALPHA-1", null, null);

        Assert.False(r.Ok);
        Assert.True(r.Conflicted);
        Assert.Contains("README.md", r.ConflictedFiles);
        Assert.Equal(before, Sha("main"));
        Assert.Empty(Run(_repo, "git", "status", "--porcelain").Trim());
        // Refused from the in-memory pre-check: no merge was started, so none had to be rolled back.
        Assert.DoesNotContain(r.Output, l => l.StartsWith("git merge --no-ff"));
    }

    [Fact]
    public async Task A_merge_survives_another_process_holding_the_index_open()
    {
        // Windows refuses the rename that replaces .git/index while anyone has it open - the Claude
        // desktop app's status polling does - and without an answer to git's "try again?" the
        // merge dies with "unable to write new index file" after rewriting the working tree.
        if (!OperatingSystem.IsWindows()) return;
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "checkout", "main");

        Task<MergeResult> merging;
        using (new FileStream(Path.Combine(_repo, ".git", "index"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            merging = _svc.MergeAsync("alpha", "jb/ALPHA-1", null, null);
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        var r = await merging;

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Sha("jb/ALPHA-1"), Sha("main^2"));
        Assert.True(File.Exists(Path.Combine(_repo, "a.txt")));
        Assert.Empty(Run(_repo, "git", "status", "--porcelain").Trim());
        Assert.False(File.Exists(Path.Combine(_repo, ".git", "MERGE_HEAD")));
    }

    [Fact]
    public async Task A_merge_is_refused_up_front_while_a_file_it_rewrites_is_open_elsewhere()
    {
        // An editor or dev server holding a file without delete-sharing makes git's unlink fail, and
        // the retry answerer keeps saying "try again" - ticket 15's merge sat silently on exactly
        // that until the file was let go. Refused before the checkout is touched, naming the file.
        if (!OperatingSystem.IsWindows()) return;
        Write("a.txt", "one\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "two\n");
        Git(_repo, "commit", "-am", "change a");
        Git(_repo, "checkout", "main");
        var before = Sha("main");

        MergeResult r;
        var clock = Stopwatch.StartNew();
        using (new FileStream(Path.Combine(_repo, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            r = await _svc.MergeAsync("alpha", "jb/ALPHA-1", null, null);

        Assert.False(r.Ok);
        Assert.Contains("a.txt", r.Error);
        Assert.Contains($"pid {Environment.ProcessId}", r.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
        Assert.Equal(before, Sha("main"));
        Assert.DoesNotContain(r.Output, l => l.StartsWith("git merge --no-ff"));

        // Let go of it and the same merge goes through.
        var again = await _svc.MergeAsync("alpha", "jb/ALPHA-1", null, null);
        Assert.True(again.Ok, again.Error);
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(_repo, "a.txt")).Replace("\r", ""));
    }

    [Fact]
    public async Task Refuses_to_merge_while_the_hub_is_on_another_branch_or_dirty()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");

        var elsewhere = await _svc.MergeAsync("alpha", "jb/ALPHA-1", null, null);
        Assert.False(elsewhere.Ok);
        Assert.Contains("not 'main'", elsewhere.Error);

        Git(_repo, "checkout", "main");
        var before = Sha("main");
        Write("README.md", "half-typed\n");
        var dirty = await _svc.MergeAsync("alpha", "jb/ALPHA-1", null, null);
        Assert.False(dirty.Ok);
        Assert.Contains("uncommitted", dirty.Error);
        Assert.Equal(before, Sha("main"));
    }

    // ---- the base of a project that merges locally ---------------------------

    /// <summary>
    /// On a project that merges instead of raising a PR, local main is where every finished ticket
    /// lands and nothing pushes it, so a branch cut from it sits on work origin has never seen.
    /// Measured against origin that work reads as this ticket's own - in the commit count and in the
    /// diff a person is asked to review.
    /// </summary>
    [Fact]
    public async Task A_merging_project_measures_a_branch_against_its_local_base()
    {
        Write("earlier.txt", "an earlier ticket, merged here and never pushed\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "an earlier ticket");
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "checkout", "main");

        var local = await _svc.PreflightAsync("local", "jb/ALPHA-1");

        Assert.True(local.Ok, local.Error);
        Assert.Equal("main", local.BaseRef);
        Assert.Equal(1, local.Ahead);
        Assert.Equal(0, local.Behind);
        Assert.Equal("a.txt", Assert.Single(local.Files).Path);

        // The same branch on a pull-request project, for contrast: origin is the base there, and
        // the unpushed commit is part of what a PR would carry.
        var pr = await _svc.PreflightAsync("alpha", "jb/ALPHA-1");
        Assert.Equal("origin/main", pr.BaseRef);
        Assert.Equal(2, pr.Ahead);
    }

    [Fact]
    public async Task A_merging_project_falls_back_to_origin_only_when_its_local_base_is_strictly_behind()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "checkout", "main");
        MoveTheBaseOnTheServer("b.txt");

        var behind = await _svc.BaseCheckAsync("local", "jb/ALPHA-1");
        Assert.Equal("origin/main", behind.BaseRef);
        Assert.Equal(1, behind.Behind);

        // Diverged: local main has a commit of its own as well. That is where the merge will land.
        Write("c.txt", "merged here\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add c");

        var diverged = await _svc.BaseCheckAsync("local", "jb/ALPHA-1");
        Assert.Equal("main", diverged.BaseRef);
        Assert.Equal(1, diverged.Behind);
    }

    [Fact]
    public async Task A_merging_project_rebases_onto_what_was_merged_locally()
    {
        Git(_repo, "checkout", "-b", "jb/ALPHA-1", "main");
        Write("a.txt", "work\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add a");
        Git(_repo, "checkout", "main");
        Write("b.txt", "another ticket, merged while this one was in flight\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "add b");

        var r = await _svc.RebaseAsync("local", "jb/ALPHA-1");

        Assert.True(r.Ok, r.Error);
        Assert.Equal("main", r.BaseRef);
        Assert.Equal(Sha("main"), Sha("jb/ALPHA-1~1"));

        // A pull-request project sees nothing to do: origin has not moved.
        var pr = await _svc.RebaseAsync("alpha", "jb/ALPHA-1");
        Assert.False(pr.Ok);
        Assert.Contains("already based off the latest origin/main", pr.Error);
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// Someone else lands a commit on the server's base while this repo is none the wiser — the
    /// state every rebase here is about. The local base is left where it was, so the only thing that
    /// has moved is origin/main.
    /// </summary>
    private void MoveTheBaseOnTheServer(string file)
    {
        var was = CurrentBranch();
        if (was != "main") Git(_repo, "checkout", "main");
        Write(file, "someone else\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", $"add {file}");
        Git(_repo, "push", "origin", "main");
        Git(_repo, "reset", "--hard", "HEAD~1");
        if (was != "main") Git(_repo, "checkout", was);
    }

    private string Sha(string rev) => Run(_repo, "git", "rev-parse", rev).Trim();

    private string CurrentBranch() => Run(_repo, "git", "rev-parse", "--abbrev-ref", "HEAD").Trim();

    private void Write(string relative, string content) =>
        File.WriteAllText(Path.Combine(_repo, relative), content);

    private static void Git(string dir, params string[] args) => Run(dir, "git", args);

    private static string Run(string dir, string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return stdout;
    }
}

/// <summary>
/// Turning an <c>origin</c> URL into the page that raises the PR.
///
/// Pure string work, and worth pinning precisely: the URL is the entire deliverable of the button
/// on every host without a CLI, and a wrong org or project segment produces a 404 at the exact
/// moment the user expected a pull request.
/// </summary>
public class PrUrlTests
{
    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "github")]
    [InlineData("git@github.com:acme/widgets.git", "github")]
    [InlineData("https://Alpha-Prod@dev.azure.com/Alpha-Prod/Alpha/_git/Alpha", "azure-devops")]
    [InlineData("https://contoso.visualstudio.com/Alpha/_git/Alpha", "azure-devops")]
    [InlineData("https://gitlab.com/acme/widgets.git", "other")]
    public void Recognises_the_host_from_the_url_alone(string url, string expected)
        => Assert.Equal(expected, TicketGitService.ProviderOf(url));

    [Fact]
    public void Azure_devops_url_carries_the_org_project_repo_and_both_branches()
    {
        var url = TicketGitService.AzureDevOpsPrUrl(
            "https://Alpha-Prod@dev.azure.com/Alpha-Prod/Alpha/_git/Alpha", "main", "jb/ALPHA-1", null, null)!;

        Assert.StartsWith("https://dev.azure.com/Alpha-Prod/Alpha/_git/Alpha/pullrequestcreate?", url);
        // The slash in the branch name must survive as an escape, or Azure reads it as a path.
        Assert.Contains("sourceRef=jb%2FALPHA-1", url);
        Assert.Contains("targetRef=main", url);
    }

    [Fact]
    public void Azure_devops_handles_the_older_visualstudio_com_form()
    {
        var url = TicketGitService.AzureDevOpsPrUrl(
            "https://contoso.visualstudio.com/DefaultCollection/Alpha/_git/Alpha", "main", "feature", null, null)!;

        Assert.StartsWith("https://dev.azure.com/contoso/Alpha/_git/Alpha/pullrequestcreate?", url);
    }

    [Fact]
    public void Azure_devops_prefills_the_title_and_description()
    {
        var url = TicketGitService.AzureDevOpsPrUrl(
            "https://dev.azure.com/org/proj/_git/repo", "main", "feature", "ALPHA-1 Do the thing", "Because.")!;

        Assert.Contains("title=ALPHA-1%20Do%20the%20thing", url);
        Assert.Contains("description=Because.", url);
    }

    [Fact]
    public void A_url_with_no_git_segment_yields_nothing_rather_than_a_guess()
        => Assert.Null(TicketGitService.AzureDevOpsPrUrl("https://dev.azure.com/org", "main", "feature", null, null));

    [Theory]
    [InlineData("https://github.com/acme/widgets.git")]
    [InlineData("git@github.com:acme/widgets.git")]
    public void Github_compare_url_is_the_same_for_both_remote_forms(string remote)
        => Assert.Equal(
            "https://github.com/acme/widgets/compare/main...feature?expand=1",
            TicketGitService.GitHubCompareUrl(remote, "main", "feature"));
}
