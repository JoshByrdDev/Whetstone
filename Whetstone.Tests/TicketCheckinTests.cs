using System.Diagnostics;
using Whetstone.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The TFVC check-in flow against real git repos, with tf.exe replaced by a batch file that prints
/// whatever the test put in <c>tf-out.txt</c>.
///
/// The fake is what makes the gate testable at all: the one thing worth pinning is that recording
/// REFUSES while TFVC still holds a change for one of the ticket's files, and nothing on a test
/// machine has a TFVC workspace to hold one. What git does around it is real - the merge, the abort,
/// the index-only commit.
/// </summary>
public class TicketCheckinTests : IDisposable
{
    private readonly string _root;
    private readonly string _data;
    private readonly string _repo;
    private readonly string _tfOut;
    private readonly TicketGitService _svc;

    public TicketCheckinTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-tfvc-" + Guid.NewGuid().ToString("N")[..8]);
        _data = Path.Combine(_root, "data");
        _repo = Path.Combine(_root, "hub");
        _tfOut = Path.Combine(_root, "tf-out.txt");

        // The shape of Bravo's hub: two mapped folders, and git-only files beside them.
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-b", "main");
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Test");
        Write("CLAUDE.md", "notes\n");
        Write("External/A.cs", "class A {}\n");
        Write("External/Old.cs", "class Old { /* long enough to be recognised as the same file */ }\n");
        Write("External/Other.cs", "class Other {}\n");
        Write("Internal/Gone.cs", "class Gone {}\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "init");

        // The ticket: one of every kind of change, and one git-only edit.
        Git(_repo, "checkout", "-b", "jb/1");
        Write("External/A.cs", "class A { int x; }\n");
        Write("External/New.cs", "class New {}\n");
        File.Delete(Path.Combine(_repo, "Internal", "Gone.cs"));
        Git(_repo, "mv", "External/Old.cs", "External/Renamed.cs");
        Write("CLAUDE.md", "notes, and more\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-m", "the ticket");
        Git(_repo, "checkout", "main");

        var fakeTf = Path.Combine(_root, "tf.cmd");
        File.WriteAllText(fakeTf, "@echo off\r\ntype \"%~dp0tf-out.txt\"\r\n");
        TfSays("There are no pending changes.\r\n");

        Directory.CreateDirectory(Path.Combine(_data, "projects", "bravo"));
        File.WriteAllText(Path.Combine(_data, "projects", "bravo", "project.defaults.json"),
            $$"""
            {
              "displayName": "Bravo",
              "projectPath": {{System.Text.Json.JsonSerializer.Serialize(_repo)}},
              "baseBranch": "main",
              "repoType": "tfvc",
              "tfvcFolders": ["External", "Internal"]
            }
            """);
        Directory.CreateDirectory(Path.Combine(_data, "projects", "alpha"));
        File.WriteAllText(Path.Combine(_data, "projects", "alpha", "project.defaults.json"),
            $$"""{ "displayName": "Alpha", "projectPath": {{System.Text.Json.JsonSerializer.Serialize(_repo)}} }""");
        File.WriteAllText(Path.Combine(_data, "config.defaults.json"),
            $$"""{ "baseBranch": "main", "tfPath": {{System.Text.Json.JsonSerializer.Serialize(fakeTf)}} }""");

        var registry = new ProjectRegistry(new DashboardPaths(_data));
        _svc = new TicketGitService(registry, new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
            NullLogger<TicketGitService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- status -------------------------------------------------------------

    [Fact]
    public async Task Before_staging_the_files_are_sorted_into_what_Team_Explorer_needs()
    {
        var s = await _svc.CheckinStatusAsync("bravo", "jb/1");

        Assert.True(s.Ok, s.Error);
        Assert.Equal("none", s.Merge);
        Assert.True(s.OnBase);
        Assert.False(s.TfvcChecked);   // nothing staged, so nothing for TFVC to see yet

        Assert.Equal("modified", FileIn("External/A.cs", s).Status);
        Assert.Equal("added", FileIn("External/New.cs", s).Status);
        Assert.Equal("deleted", FileIn("Internal/Gone.cs", s).Status);
        var renamed = FileIn("External/Renamed.cs", s);
        Assert.Equal("renamed", renamed.Status);
        Assert.Equal("External/Old.cs", renamed.RenamedFrom);
        Assert.True(renamed.RenamedFromInTfvc);

        var gitOnly = FileIn("CLAUDE.md", s);
        Assert.False(gitOnly.InTfvc);
        Assert.Equal(TfvcState.NotInTfvc, gitOnly.Tfvc);
    }

    [Fact]
    public async Task A_git_project_is_refused_every_check_in_step()
    {
        Assert.Contains("git project", (await _svc.CheckinStatusAsync("alpha", "jb/1")).Error);
        Assert.Contains("git project", (await _svc.StageCheckinAsync("alpha", "jb/1")).Error);
        Assert.Contains("git project", (await _svc.RecordCheckinAsync("alpha", "jb/1", 123, "t")).Error);
        Assert.Equal("", MergeHead());
    }

    // ---- stage --------------------------------------------------------------

    [Fact]
    public async Task Staging_merges_without_committing_and_the_status_then_asks_TFVC()
    {
        var before = Sha("main");
        TfSays(Listing(
            pending: new[] { ("A.cs", "edit", "External/A.cs") },
            detected: new[] { ("New.cs", "add", "External/New.cs"), ("Gone.cs", "delete", "Internal/Gone.cs") }));

        var r = await _svc.StageCheckinAsync("bravo", "jb/1");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(before, Sha("main"));            // nothing committed
        Assert.Equal(Sha("jb/1"), MergeHead());       // but the merge is staged
        Assert.True(System.IO.File.Exists(Path.Combine(_repo, "External", "New.cs")));

        var s = await _svc.CheckinStatusAsync("bravo", "jb/1");
        Assert.Equal("this", s.Merge);
        Assert.True(s.TfvcChecked);
        Assert.Empty(s.TfvcProblems);
        Assert.Equal(TfvcState.Pending, FileIn("External/A.cs", s).Tfvc);
        Assert.Equal(TfvcState.Detected, FileIn("External/New.cs", s).Tfvc);
        Assert.Equal(TfvcState.Detected, FileIn("Internal/Gone.cs", s).Tfvc);
        Assert.Equal(TfvcState.Clean, FileIn("External/Renamed.cs", s).Tfvc);
    }

    [Fact]
    public async Task Staging_refuses_while_a_tracked_file_is_modified_but_not_for_an_untracked_one()
    {
        Write("scratch.txt", "untracked\n");
        var first = await _svc.StageCheckinAsync("bravo", "jb/1");
        Assert.True(first.Ok, first.Error);
        TfSays(Listing(pending: new[] { ("A.cs", "edit", "External/A.cs") }));
        Assert.True((await _svc.AbortCheckinAsync("bravo", "jb/1")).Ok);

        Write("External/Other.cs", "class Other { /* a Get Latest landed this */ }\n");
        var r = await _svc.StageCheckinAsync("bravo", "jb/1");

        Assert.False(r.Ok);
        Assert.Contains("modified", r.Error);
        Assert.Contains(r.Blocking, c => c.Path == "External/Other.cs");
        Assert.Equal("", MergeHead());
    }

    [Fact]
    public async Task Staging_refuses_when_the_hub_is_not_on_the_base()
    {
        Git(_repo, "checkout", "-b", "somewhere-else");

        var r = await _svc.StageCheckinAsync("bravo", "jb/1");

        Assert.False(r.Ok);
        Assert.Contains("not 'main'", r.Error);
        Assert.Equal("", MergeHead());
    }

    [Fact]
    public async Task Staging_refuses_while_another_merge_is_in_progress()
    {
        Git(_repo, "checkout", "-b", "jb/2", "main");
        Write("External/Other.cs", "class Other { int y; }\n");
        Git(_repo, "commit", "-am", "another ticket");
        Git(_repo, "checkout", "main");
        Git(_repo, "merge", "--no-ff", "--no-commit", "jb/2");

        var r = await _svc.StageCheckinAsync("bravo", "jb/1");

        Assert.False(r.Ok);
        Assert.Contains("not this ticket's", r.Error);
        Assert.Equal(Sha("jb/2"), MergeHead());
    }

    [Fact]
    public async Task A_conflicting_merge_is_rolled_back_and_names_the_files()
    {
        Write("External/A.cs", "class A { string clash; }\n");
        Git(_repo, "commit", "-am", "main moved under the ticket");
        var before = Sha("main");

        var r = await _svc.StageCheckinAsync("bravo", "jb/1");

        Assert.False(r.Ok);
        Assert.True(r.Conflicted);
        Assert.Contains("External/A.cs", r.ConflictedFiles);
        Assert.Equal("", MergeHead());
        Assert.Equal(before, Sha("main"));
        Assert.Empty(Run(_repo, "git", "status", "--porcelain", "--untracked-files=no").Trim());
    }

    // ---- abort --------------------------------------------------------------

    [Fact]
    public async Task Abort_puts_the_hub_back_where_it_was()
    {
        TfSays(Listing(pending: new[] { ("A.cs", "edit", "External/A.cs") }));
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);

        var r = await _svc.AbortCheckinAsync("bravo", "jb/1");

        Assert.True(r.Ok, r.Error);
        Assert.Equal("", MergeHead());
        Assert.False(System.IO.File.Exists(Path.Combine(_repo, "External", "New.cs")));
        Assert.Empty(Run(_repo, "git", "status", "--porcelain", "--untracked-files=no").Trim());
    }

    /// <summary>
    /// After the check-in, aborting would put the old files back on disk - which TFVC reads as a new
    /// set of edits undoing what was just checked in.
    /// </summary>
    [Fact]
    public async Task Abort_refuses_once_TFVC_says_everything_is_checked_in()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        TfSays("There are no pending changes.\r\n");

        var r = await _svc.AbortCheckinAsync("bravo", "jb/1");

        Assert.False(r.Ok);
        Assert.Contains("checked in already", r.Error);
        Assert.Equal(Sha("jb/1"), MergeHead());
    }

    // ---- record -------------------------------------------------------------

    /// <summary>The whole point of the feature: a new file still under Detected Changes blocks the record.</summary>
    [Fact]
    public async Task Record_refuses_and_names_a_file_that_was_never_promoted()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        var before = Sha("main");
        TfSays(Listing(detected: new[] { ("New.cs", "add", "External/New.cs") }));

        var r = await _svc.RecordCheckinAsync("bravo", "jb/1", 4242, "AB#1: the ticket");

        Assert.False(r.Ok);
        var why = Assert.Single(r.NotCheckedIn);
        Assert.Contains("External/New.cs was never promoted - the server does not have it", why);
        Assert.Equal(before, Sha("main"));        // nothing committed
        Assert.Equal(Sha("jb/1"), MergeHead());   // still staged, ready for another go
    }

    [Fact]
    public async Task Record_names_every_half_of_a_rename_that_is_still_detected()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        TfSays(Listing(
            pending: new[] { ("A.cs", "edit", "External/A.cs") },
            detected: new[] { ("Renamed.cs", "add", "External/Renamed.cs"), ("Old.cs", "delete", "External/Old.cs") }));

        var r = await _svc.RecordCheckinAsync("bravo", "jb/1", 4242, "t");

        Assert.False(r.Ok);
        Assert.Equal(3, r.NotCheckedIn.Count);
        Assert.Contains(r.NotCheckedIn, s => s.StartsWith("External/A.cs is still a pending edit"));
        Assert.Contains(r.NotCheckedIn, s => s.StartsWith("External/Renamed.cs was never promoted"));
        Assert.Contains(r.NotCheckedIn, s => s.Contains("External/Old.cs was renamed to External/Renamed.cs"));
    }

    /// <summary>
    /// The state Bravo's hub was actually found in on 2026-09-11: the ticket's new test file staged
    /// as an add, and gone from disk. TFVC never saw it, so tf says nothing at all about it - which
    /// on its own reads exactly like a file that was checked in. The disk is the tiebreak.
    /// </summary>
    [Fact]
    public async Task Record_refuses_a_new_file_that_left_the_disk_even_though_TFVC_says_nothing()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        System.IO.File.Delete(Path.Combine(_repo, "External", "New.cs"));
        TfSays("There are no pending changes.\r\n");

        var s = await _svc.CheckinStatusAsync("bravo", "jb/1");
        Assert.Equal("missing", FileIn("External/New.cs", s).Disk);
        Assert.Null(FileIn("External/A.cs", s).Disk);
        Assert.Null(FileIn("External/Renamed.cs", s).RenamedFromDisk);   // the old name is gone, as it should be

        var r = await _svc.RecordCheckinAsync("bravo", "jb/1", 4242, "t");

        Assert.False(r.Ok);
        var why = Assert.Single(r.NotCheckedIn);
        Assert.Contains("External/New.cs is in this merge but is not on disk", why);
        Assert.Equal(Sha("jb/1"), MergeHead());
    }

    [Fact]
    public async Task Record_refuses_a_deleted_file_that_is_back_on_disk()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        Write("Internal/Gone.cs", "class Gone {}\n");
        TfSays("There are no pending changes.\r\n");

        var r = await _svc.RecordCheckinAsync("bravo", "jb/1", 4242, "t");

        Assert.False(r.Ok);
        Assert.Contains(r.NotCheckedIn, s => s.StartsWith("Internal/Gone.cs is deleted by this merge but is still on disk"));
    }

    [Fact]
    public async Task Record_refuses_when_tf_says_something_it_cannot_read()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        TfSays("TF400324: Team Foundation services are not available.\r\n");

        var r = await _svc.RecordCheckinAsync("bravo", "jb/1", 4242, "t");

        Assert.False(r.Ok);
        Assert.Contains(r.NotCheckedIn, s => s.Contains("TF400324"));
        Assert.Equal(Sha("jb/1"), MergeHead());
    }

    [Fact]
    public async Task Record_refuses_when_tf_cannot_be_found()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        File.WriteAllText(Path.Combine(_data, "config.json"), """{ "tfPath": "C:/nowhere/TF.exe" }""");

        var r = await _svc.RecordCheckinAsync("bravo", "jb/1", 4242, "t");

        Assert.False(r.Ok);
        Assert.Contains("does not exist", r.Error);
        Assert.Equal(Sha("jb/1"), MergeHead());
    }

    [Fact]
    public async Task Record_refuses_without_a_staged_merge()
    {
        var r = await _svc.RecordCheckinAsync("bravo", "jb/1", 4242, "t");

        Assert.False(r.Ok);
        Assert.Contains("Stage it", r.Error);
    }

    /// <summary>
    /// Clean in TFVC: the merge is committed with the changeset in its message - index only, so a
    /// Get Latest that landed after staging stays out of it and is reported for a sync commit.
    /// </summary>
    [Fact]
    public async Task Record_commits_the_staged_merge_only_and_reports_what_it_left_behind()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        Write("External/Other.cs", "class Other { /* someone else's changeset, via Get Latest */ }\n");
        TfSays("There are no pending changes.\r\n");

        var r = await _svc.RecordCheckinAsync("bravo", "jb/1", 4242, "AB#3981: Move loan to customer");

        Assert.True(r.Ok, r.Error + string.Join("; ", r.NotCheckedIn));
        Assert.Equal("", MergeHead());
        Assert.Equal(Sha("HEAD"), r.CommitSha);
        Assert.Equal("Changeset 4242: AB#3981: Move loan to customer", Run(_repo, "git", "log", "-1", "--format=%s").Trim());
        Assert.Contains("changeset 4242", Run(_repo, "git", "log", "-1", "--format=%b"));
        // A real merge: the ticket's branch is the second parent.
        Assert.Equal(Sha("jb/1"), Sha("HEAD^2"));
        // The Get Latest landing is still in the working tree, not in the commit.
        Assert.DoesNotContain("External/Other.cs", Run(_repo, "git", "show", "--name-only", "--format=", "HEAD", "-m", "--first-parent"));
        Assert.Contains(r.LeftUnstaged, c => c.Path == "External/Other.cs");
        Assert.Empty(r.TicketFilesDiffer);
    }

    [Fact]
    public async Task Record_flags_a_ticket_file_whose_disk_copy_differs_from_what_git_staged()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        // Edited after staging: TFVC checks in the disk, so this went to the server and not to git.
        Write("External/A.cs", "class A { int x; int tweakedDuringCheckin; }\n");
        TfSays("There are no pending changes.\r\n");

        var r = await _svc.RecordCheckinAsync("bravo", "jb/1", 4243, "t");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(new[] { "External/A.cs" }, r.TicketFilesDiffer);
    }

    // ---- sync with TFVC -----------------------------------------------------

    /// <summary>
    /// What a Get Latest leaves in the hub - someone else's edit, a new file and a delete - beside the
    /// three kinds of change that are the human's own and must be left alone.
    /// </summary>
    private void GetLatestLanded()
    {
        Write("External/Other.cs", "class Other { /* someone else's changeset */ }\n");
        Write("External/FromServer.cs", "class FromServer {}\n");
        System.IO.File.Delete(Path.Combine(_repo, "Internal", "Gone.cs"));
        Write("External/A.cs", "class A { /* my own pending edit */ }\n");
        Write("External/Scratch.cs", "class Scratch {}\n");
        Write("CLAUDE.md", "notes, edited in git only\n");
        TfSays(Listing(
            pending: new[] { ("A.cs", "edit", "External/A.cs") },
            detected: new[] { ("Scratch.cs", "add", "External/Scratch.cs") }));
    }

    private static string SourceOf(TfvcSyncPreview p, string path) => p.Files.Single(f => f.Path == path).Source;

    [Fact]
    public async Task Sync_preview_tells_the_servers_changes_from_the_humans_own()
    {
        GetLatestLanded();

        var p = await _svc.TfvcSyncPreviewAsync("bravo");

        Assert.True(p.Ok, p.Error);
        Assert.Empty(p.TfvcProblems);
        Assert.Equal(SyncSource.Tfvc, SourceOf(p, "External/Other.cs"));
        Assert.Equal(SyncSource.Tfvc, SourceOf(p, "External/FromServer.cs"));
        Assert.Equal(SyncSource.Tfvc, SourceOf(p, "Internal/Gone.cs"));
        Assert.Equal("added", p.Files.Single(f => f.Path == "External/FromServer.cs").Git);
        Assert.Equal("deleted", p.Files.Single(f => f.Path == "Internal/Gone.cs").Git);
        Assert.Equal(SyncSource.Pending, SourceOf(p, "External/A.cs"));
        Assert.Equal(SyncSource.Detected, SourceOf(p, "External/Scratch.cs"));
        Assert.Equal(SyncSource.GitOnly, SourceOf(p, "CLAUDE.md"));
        Assert.StartsWith("Sync with TFVC after the ", p.SuggestedMessage);
    }

    [Fact]
    public async Task Sync_commits_only_the_servers_changes_and_leaves_the_rest_in_the_tree()
    {
        GetLatestLanded();
        var p = await _svc.TfvcSyncPreviewAsync("bravo");
        var paths = p.Files.Where(f => f.Source == SyncSource.Tfvc).Select(f => f.Path).ToList();

        var r = await _svc.TfvcSyncAsync("bravo", paths, "Sync with TFVC after the Friday Get Latest");

        Assert.True(r.Ok, r.Error);
        Assert.Equal(3, r.Committed);
        Assert.Equal(Sha("HEAD"), r.CommitSha);
        Assert.Equal("Sync with TFVC after the Friday Get Latest", Run(_repo, "git", "log", "-1", "--format=%s").Trim());

        var committed = Run(_repo, "git", "show", "--name-only", "--format=", "HEAD").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "External/FromServer.cs", "External/Other.cs", "Internal/Gone.cs" }, committed.OrderBy(x => x, StringComparer.Ordinal));

        // The human's own work is exactly where it was.
        var left = Run(_repo, "git", "status", "--porcelain", "--untracked-files=all");
        Assert.Contains("External/A.cs", left);
        Assert.Contains("External/Scratch.cs", left);
        Assert.Contains("CLAUDE.md", left);
        Assert.DoesNotContain("Other.cs", left);
        Assert.Empty(Run(_repo, "git", "diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    public async Task Sync_refuses_a_list_that_no_longer_matches_the_hub()
    {
        GetLatestLanded();
        var before = Sha("HEAD");

        var r = await _svc.TfvcSyncAsync("bravo", new[] { "External/Other.cs" }, null);

        Assert.False(r.Ok);
        Assert.True(r.Changed);
        Assert.Equal(before, Sha("HEAD"));
        Assert.Empty(Run(_repo, "git", "diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    public async Task Sync_refuses_while_a_tickets_merge_is_staged()
    {
        Assert.True((await _svc.StageCheckinAsync("bravo", "jb/1")).Ok);
        Write("External/Other.cs", "class Other { /* landed during the check-in */ }\n");

        var p = await _svc.TfvcSyncPreviewAsync("bravo");

        Assert.False(p.Ok);
        Assert.Contains("merge is staged", p.Error);
        Assert.False((await _svc.TfvcSyncAsync("bravo", new[] { "External/Other.cs" }, null)).Ok);
        Assert.Equal(Sha("jb/1"), MergeHead());
    }

    [Fact]
    public async Task Sync_refuses_when_TFVCs_answer_cannot_be_read()
    {
        GetLatestLanded();
        TfSays("TF400324: Team Foundation services are not available.\r\n");
        var before = Sha("HEAD");

        var p = await _svc.TfvcSyncPreviewAsync("bravo");
        Assert.NotEmpty(p.TfvcProblems);
        Assert.DoesNotContain(p.Files, f => f.Source == SyncSource.Tfvc);   // silence is not "the server's" when the answer is broken

        var r = await _svc.TfvcSyncAsync("bravo", new[] { "External/Other.cs" }, null);
        Assert.False(r.Ok);
        Assert.Equal(before, Sha("HEAD"));
    }

    [Fact]
    public async Task Sync_refuses_while_something_is_already_staged_in_the_index()
    {
        Write("External/Other.cs", "class Other { /* staged by hand */ }\n");
        Git(_repo, "add", "External/Other.cs");

        var p = await _svc.TfvcSyncPreviewAsync("bravo");

        Assert.False(p.Ok);
        Assert.Contains("already staged", p.Error);
    }

    [Fact]
    public async Task A_git_project_has_no_TFVC_to_sync_with()
    {
        var p = await _svc.TfvcSyncPreviewAsync("alpha");

        Assert.False(p.Ok);
        Assert.Contains("git project", p.Error);
    }

    // ---- helpers ------------------------------------------------------------

    private static CheckinFile FileIn(string path, CheckinStatus s) => s.Files.Single(f => f.Path == path);

    /// <summary>The next thing the fake tf.exe prints.</summary>
    private void TfSays(string text) => System.IO.File.WriteAllText(_tfOut, text);

    /// <summary>A tf status listing in the brief format, with this hub's real local paths.</summary>
    private string Listing(
        (string Name, string Change, string Rel)[]? pending = null,
        (string Name, string Change, string Rel)[]? detected = null)
    {
        pending ??= Array.Empty<(string, string, string)>();
        detected ??= Array.Empty<(string, string, string)>();
        var sb = new System.Text.StringBuilder();
        void Rows((string Name, string Change, string Rel)[] rows)
        {
            sb.Append("File name   Change   Local path\r\n----------- -------- ----------\r\n");
            foreach (var (name, change, rel) in rows)
            {
                sb.Append("$/").Append(Path.GetDirectoryName(rel)!.Replace('\\', '/')).Append("\r\n");
                sb.Append(name).Append("   ").Append(change).Append("   ")
                  .Append(Path.Combine(_repo, rel.Replace('/', '\\'))).Append("\r\n\r\n");
            }
        }
        if (pending.Length > 0) Rows(pending);
        if (detected.Length > 0)
        {
            sb.Append("Detected Changes:\r\n----------------------------------\r\n");
            Rows(detected);
        }
        sb.Append($"{pending.Length} change(s), {detected.Length} detected change(s)\r\n");
        return sb.ToString();
    }

    private string MergeHead() => Run(_repo, "git", "rev-parse", "--verify", "--quiet", "MERGE_HEAD").Trim();

    private string Sha(string rev) => Run(_repo, "git", "rev-parse", rev).Trim();

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

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
