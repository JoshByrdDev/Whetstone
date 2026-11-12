using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A ticket's branch name: the default the board gives it (<see cref="BranchNaming"/>, handed to the
/// worker on the claim), a name chosen at filing, and renaming a branch a run has already made - in
/// every writable repo of a project of several, all or nothing, and never where a rename would lose
/// track of work. The worker's half is in data/worker-base.tests.ps1 ("Get-TicketBranchName").
/// </summary>
public class TicketBranchNameTests : IDisposable
{
    private const string Old = "jb/PROJ-1";
    private const string New = "jb/PROJ-1-signing-foundation";

    private readonly string _root;
    private readonly string _project;
    private readonly ProjectRegistry _registry;
    private readonly TicketGitService _git;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public TicketBranchNameTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-bname-" + Guid.NewGuid().ToString("N")[..8]);
        _project = Path.Combine(_root, "Alpha");
        var data = Path.Combine(_root, "data");
        NewRepo("alpha-web");
        NewRepo("alpha-api");
        NewRepo("alpha-infra");
        WriteProject(data, "alpha", $$"""
            {
              "displayName": "Alpha", "projectPath": {{Json(_project)}}, "baseBranch": "main",
              "branchPrefix": "jb/", "branchNameStyle": "key-slug", "worktreeRoot": {{Json(Path.Combine(_root, "wt"))}},
              "repos": [ "alpha-web", "alpha-api", { "dir": "alpha-infra", "readOnly": true } ]
            }
            """);
        WriteProject(data, "bravo", $$"""{ "displayName": "Bravo", "projectPath": {{Json(Path.Combine(_project, "alpha-web"))}}, "worktreeRoot": {{Json(Path.Combine(_root, "wt-bravo"))}} }""");
        File.WriteAllText(Path.Combine(data, "config.defaults.json"), """{ "baseBranch": "main", "branchPrefix": "zz/" }""");
        _registry = new ProjectRegistry(new DashboardPaths(data));
        _git = new TicketGitService(_registry, new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
            NullLogger<TicketGitService>.Instance);

        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _conn.Dispose();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best effort */ }
    }

    // ---- the default name ---------------------------------------------------------------------

    [Fact]
    public void The_key_style_is_the_prefix_and_the_issue_key_or_the_ticket_id()
    {
        Assert.Equal("jb/123", BranchNaming.Default("jb/", "key", 7, "123", "Open the signing link"));
        Assert.Equal("jb/ticket-7", BranchNaming.Default("jb/", "key", 7, "", "Open the signing link"));
        Assert.Equal("PROJ-123", BranchNaming.Default("", null, 7, "PROJ-123", null));
        // Anything the setting does not know is the key style, never a guess at the other.
        Assert.Equal("jb/123", BranchNaming.Default("jb/", "slug-please", 7, "123", "Open the signing link"));
    }

    [Fact]
    public void The_key_slug_style_adds_the_title()
    {
        Assert.Equal("jb/123-open-the-signing-link", BranchNaming.Default("jb/", "key-slug", 7, "123", "Open the signing link"));
        Assert.Equal("jb/ticket-7-fix-the-wizard-roles", BranchNaming.Default("jb/", "KEY-SLUG", 7, null, "Fix the wizard roles"));
        // No title to read (an unreachable tracker), or one that is only the key again: no slug.
        Assert.Equal("jb/123", BranchNaming.Default("jb/", "key-slug", 7, "123", null));
        Assert.Equal("jb/PROJ-123", BranchNaming.Default("jb/", "key-slug", 7, "PROJ-123", "PROJ-123"));
        Assert.Equal("jb/123", BranchNaming.Default("jb/", "key-slug", 7, "123", "!!! ???"));
    }

    [Theory]
    [InlineData("Open the signing link", "open-the-signing-link")]
    [InlineData("  Signing: records & roles (v2)!  ", "signing-records-roles-v2")]
    [InlineData("Don't re-send the café's e-mail", "dont-re-send-the-cafes-e-mail")]
    [InlineData("--- leading and trailing ---", "leading-and-trailing")]
    [InlineData("UPPER case_and_underscores", "upper-case-and-underscores")]
    [InlineData("??!!", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    // Cut at the last word inside 40, never mid-word when a word ends near the limit...
    [InlineData("Let a ticket carry its own branch name from filing to merge", "let-a-ticket-carry-its-own-branch-name")]
    // ...and at the limit itself when the only word boundary would throw most of it away.
    [InlineData("a supercalifragilisticexpialidociousandthensome", "a-supercalifragilisticexpialidociousandt")]
    public void A_slug_is_lowercase_letters_digits_and_single_hyphens(string? title, string want)
    {
        var slug = BranchNaming.Slug(title);
        Assert.Equal(want, slug);
        Assert.True(slug.Length <= BranchNaming.SlugMax);
        Assert.False(slug.StartsWith('-') || slug.EndsWith('-'));
        Assert.DoesNotContain("--", slug);
    }

    [Fact]
    public void A_cut_never_leaves_a_trailing_hyphen()
    {
        // 40th character is a hyphen: the cut lands just before it.
        var title = new string('a', 39) + " bcd";
        Assert.Equal(new string('a', 39), BranchNaming.Slug(title));
    }

    [Fact]
    public void The_prefix_is_the_projects_else_the_global_one()
    {
        var global = _registry.GetGlobalConfig();
        Assert.Equal("jb/", BranchNaming.PrefixFor(_registry.GetProjectConfig("alpha"), global));
        Assert.Equal("zz/", BranchNaming.PrefixFor(_registry.GetProjectConfig("bravo"), global));
        Assert.Equal("key-slug", _registry.GetProjectConfig("alpha").BranchNameStyle);
        Assert.Equal("key", _registry.GetProjectConfig("bravo").BranchNameStyle);
    }

    // ---- a name chosen at filing ---------------------------------------------------------------

    [Fact]
    public async Task A_name_chosen_at_filing_is_recorded_but_not_made()
    {
        await using var ctx = new DashboardDbContext(_options);
        var svc = new TicketService(ctx);
        var t = await svc.CreateAsync("alpha", "Story", null, null, null, TicketPhase.Backlog, branch: " jb/122-signing ");
        Assert.Equal("jb/122-signing", t.Branch);
        Assert.False(t.BranchStarted);

        var made = await svc.RecordRunBranchAsync(t.Id, "jb/122-signing");
        Assert.True(made!.BranchStarted);
    }

    [Fact]
    public async Task A_new_name_is_refused_when_another_open_ticket_or_a_repo_has_it()
    {
        await using var ctx = new DashboardDbContext(_options);
        var svc = new TicketService(ctx);
        var first = await svc.CreateAsync("alpha", "One", null, null, null, TicketPhase.Backlog, branch: "jb/taken");

        Assert.Contains($"#{first.Id}", await TicketEndpoints.BranchNameProblemAsync(svc, _git, "alpha", "JB/Taken", null));
        Assert.Null(await TicketEndpoints.BranchNameProblemAsync(svc, _git, "alpha", "jb/taken", first.Id));
        Assert.Null(await TicketEndpoints.BranchNameProblemAsync(svc, _git, "bravo", "jb/taken", null));

        // A ticket that is done gives its name back.
        (await ctx.Tickets.FindAsync(first.Id))!.Phase = TicketPhase.Done;
        await ctx.SaveChangesAsync();
        Assert.Null(await TicketEndpoints.BranchNameProblemAsync(svc, _git, "alpha", "jb/taken", null));

        // A branch already in a writable repo, locally or on origin. Not in a read-only one.
        Git(Repo("alpha-api"), "branch", "jb/local-only");
        Assert.Contains("alpha-api", await _git.NewBranchNameProblemAsync("alpha", "jb/local-only"));
        Git(Repo("alpha-web"), "push", "-q", "origin", "main:jb/on-origin");
        Git(Repo("alpha-web"), "fetch", "-q", "origin");
        Assert.Contains("on origin", await _git.NewBranchNameProblemAsync("alpha", "jb/on-origin"));
        Git(Repo("alpha-infra"), "branch", "jb/infra-only");
        Assert.Null(await _git.NewBranchNameProblemAsync("alpha", "jb/infra-only"));

        Assert.NotNull(await _git.NewBranchNameProblemAsync("alpha", "bad..name"));
        Assert.NotNull(await _git.NewBranchNameProblemAsync("alpha", "slice/03-cards"));
    }

    // ---- renaming a branch a run made ----------------------------------------------------------

    [Fact]
    public async Task A_rename_moves_the_branch_in_every_writable_repo_that_has_it_and_keeps_its_commits()
    {
        var web = Repo("alpha-web");
        var api = Repo("alpha-api");
        var infra = Repo("alpha-infra");
        var webWt = Workspace("alpha-web", touch: true);
        var apiWt = Workspace("alpha-api", touch: true);
        Git(infra, "branch", Old);            // read-only: never renamed
        var webTip = Sha(web, Old);

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(new[] { "alpha-web", "alpha-api" }, r.Renamed);
        Assert.Equal(webTip, Sha(web, New));
        Assert.Equal("", Sha(web, Old));
        Assert.Equal("", Sha(api, Old));
        // Each worktree's HEAD went with its branch, and the worktree to the new name's folder.
        Assert.Equal(New, Head(Moved(webWt)));
        Assert.Equal(New, Head(Moved(apiWt)));
        Assert.NotEqual("", Sha(infra, Old));
        Assert.Equal("", Sha(infra, New));
    }

    [Fact]
    public void A_worktree_folder_is_named_as_the_worker_names_it()
    {
        // The same cases as worker-base.tests.ps1's Get-WorktreeDirName: the two must agree.
        Assert.Equal("jb-PROJ-1", BranchNaming.WorktreeDirName("jb/PROJ-1"));
        Assert.Equal("a-b-c-d", BranchNaming.WorktreeDirName("a b\\c:d"));
        Assert.Equal("jb-x", BranchNaming.WorktreeDirName("/jb/x."));
    }

    [Fact]
    public async Task A_rename_moves_the_whole_workspace_to_the_new_names_folder()
    {
        var web = Repo("alpha-web");
        var infra = Repo("alpha-infra");
        var webWt = Workspace("alpha-web", touch: true);
        Workspace("alpha-api", touch: false);
        // The read-only repo's worktree is in the workspace too, detached at its base.
        var infraWt = Path.Combine(OldWs, "alpha-infra");
        Git(infra, "worktree", "add", "-q", "--detach", infraWt, "main");
        // And the conventions file the project keeps at a workspace's root.
        File.WriteAllText(Path.Combine(OldWs, "CLAUDE.md"), "# the workspace");

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(OldWs, r.WorkspaceFrom);
        Assert.Equal(NewWs, r.WorkspaceTo);
        Assert.False(Directory.Exists(OldWs));
        Assert.Equal(New, Head(Path.Combine(NewWs, "alpha-web")));
        Assert.Equal(New, Head(Path.Combine(NewWs, "alpha-api")));
        Assert.Equal("HEAD", Head(Path.Combine(NewWs, "alpha-infra")));
        Assert.Equal("# the workspace", File.ReadAllText(Path.Combine(NewWs, "CLAUDE.md")));
        // Git knows each one where it is now, so the worker's lookup by branch finds the new folder.
        Assert.Contains(Path.Combine(NewWs, "alpha-web").Replace('\\', '/'), Run(web, "worktree", "list", "--porcelain").Output.Replace('\\', '/'));
        Assert.Contains(Path.Combine(NewWs, "alpha-infra").Replace('\\', '/'), Run(infra, "worktree", "list", "--porcelain").Output.Replace('\\', '/'));
        Assert.Equal(Sha(web, New), Run(Path.Combine(NewWs, "alpha-web"), "rev-parse", "HEAD").Output.Trim());
        Assert.False(Directory.Exists(webWt));
    }

    [Fact]
    public async Task A_rename_onto_a_folder_that_is_already_there_is_refused_before_anything_moves()
    {
        var web = Repo("alpha-web");
        var webWt = Workspace("alpha-web", touch: true);
        Directory.CreateDirectory(NewWs);
        File.WriteAllText(Path.Combine(NewWs, "notes.txt"), "someone's");

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.False(r.Ok);
        Assert.Contains("not empty", r.Error);
        Assert.NotEqual("", Sha(web, Old));
        Assert.Equal("", Sha(web, New));
        Assert.Equal(Old, Head(webWt));
        Assert.Equal("someone's", File.ReadAllText(Path.Combine(NewWs, "notes.txt")));
    }

    [Fact]
    public async Task A_worktree_that_will_not_move_puts_back_every_move_and_every_rename()
    {
        var web = Repo("alpha-web");
        var api = Repo("alpha-api");
        var webWt = Workspace("alpha-web", touch: true);
        var apiWt = Workspace("alpha-api", touch: true);
        File.WriteAllText(Path.Combine(OldWs, "CLAUDE.md"), "# the workspace");
        // Git refuses to move a locked worktree: web has moved by then, api has not.
        Git(api, "worktree", "lock", apiWt);

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.False(r.Ok);
        Assert.Contains(apiWt, r.Error);
        Assert.NotEqual("", Sha(web, Old));
        Assert.Equal("", Sha(web, New));
        Assert.NotEqual("", Sha(api, Old));
        Assert.Equal(Old, Head(webWt));
        Assert.Equal(Old, Head(apiWt));
        Assert.True(File.Exists(Path.Combine(OldWs, "CLAUDE.md")));
        Assert.False(Directory.Exists(NewWs));
    }

    [Fact]
    public async Task A_branch_renamed_by_hand_still_has_its_workspace_moved()
    {
        var web = Repo("alpha-web");
        Workspace("alpha-web", touch: true);
        Git(web, "branch", "-m", Old, New);

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.True(r.Ok, r.Error);
        Assert.Empty(r.Renamed);
        Assert.Equal(NewWs, r.WorkspaceTo);
        Assert.Equal(New, Head(Path.Combine(NewWs, "alpha-web")));
    }

    [Fact]
    public async Task On_one_repo_the_worktree_is_the_folder_that_moves()
    {
        var web = Repo("alpha-web");
        var oldWt = Path.Combine(_root, "wt-bravo", "jb-PROJ-1");
        var newWt = Path.Combine(_root, "wt-bravo", "jb-PROJ-1-signing-foundation");
        Git(web, "worktree", "add", "-q", "-b", Old, oldWt, "main");
        // An empty folder under the new name is no obstacle.
        Directory.CreateDirectory(newWt);

        var r = await _git.RenameTicketBranchAsync("bravo", Old, New);

        Assert.True(r.Ok, r.Error);
        Assert.Equal(oldWt, r.WorkspaceFrom);
        Assert.Equal(newWt, r.WorkspaceTo);
        Assert.False(Directory.Exists(oldWt));
        Assert.Equal(New, Head(newWt));
    }

    [Fact]
    public async Task A_worktree_kept_outside_the_root_stays_where_it_is()
    {
        var web = Repo("alpha-web");
        var mine = Path.Combine(_root, "mine", "web");
        Git(web, "worktree", "add", "-q", "-b", Old, mine, "main");

        var r = await _git.RenameTicketBranchAsync("bravo", Old, New);

        Assert.True(r.Ok, r.Error);
        Assert.Null(r.WorkspaceTo);
        Assert.Equal(New, Head(mine));
    }

    [Fact]
    public async Task A_repo_that_fails_puts_back_the_ones_already_renamed()
    {
        var web = Repo("alpha-web");
        var api = Repo("alpha-api");
        Workspace("alpha-web", touch: true);
        Workspace("alpha-api", touch: true);
        // A lock on the new name in the second repo makes git refuse there, after the first has moved.
        var lockFile = Path.Combine(api, ".git", "refs", "heads", "jb", "PROJ-1-signing-foundation.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        File.WriteAllText(lockFile, "");

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.False(r.Ok);
        Assert.Contains("alpha-api", r.Error);
        Assert.NotEqual("", Sha(web, Old));
        Assert.Equal("", Sha(web, New));
        Assert.NotEqual("", Sha(api, Old));
    }

    [Fact]
    public async Task A_branch_on_origin_is_not_renamed_and_the_owner_is_told_what_to_do()
    {
        var web = Repo("alpha-web");
        Workspace("alpha-web", touch: true);
        Git(web, "push", "-q", "origin", Old);

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.False(r.Ok);
        Assert.Contains("on origin in alpha-web", r.Error);
        Assert.Contains($"git push -u origin {New}", r.Error);
        Assert.Contains("reopen the pull request", r.Error);
        Assert.NotEqual("", Sha(web, Old));
        Assert.Equal("", Sha(web, New));
    }

    [Fact]
    public async Task A_rename_onto_a_name_a_repo_already_has_is_refused_before_anything_moves()
    {
        var web = Repo("alpha-web");
        Workspace("alpha-web", touch: true);
        Git(Repo("alpha-api"), "branch", New);

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.False(r.Ok);
        Assert.NotEqual("", Sha(web, Old));
    }

    [Fact]
    public async Task A_branch_checked_out_in_a_persons_checkout_is_not_renamed()
    {
        var web = Repo("alpha-web");
        Git(web, "checkout", "-q", "-b", Old);

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.False(r.Ok);
        Assert.Contains("person's checkout", r.Error);
        Assert.Equal(Old, Head(web));
    }

    [Fact]
    public async Task A_branch_already_renamed_by_hand_is_only_recorded()
    {
        var web = Repo("alpha-web");
        Workspace("alpha-web", touch: true);
        Git(web, "branch", "-m", Old, New);

        var r = await _git.RenameTicketBranchAsync("alpha", Old, New);

        Assert.True(r.Ok, r.Error);
        Assert.Empty(r.Renamed);
        Assert.NotEqual("", Sha(web, New));
    }

    [Fact]
    public async Task A_rename_is_refused_while_a_run_holds_the_ticket_under_review_or_after_a_worker_host()
    {
        await using var ctx = new DashboardDbContext(_options);
        var svc = new TicketService(ctx);
        var leases = new ResourceLeaseService(ctx);
        var t = await svc.CreateAsync("alpha", "Story", null, null, null, TicketPhase.Backlog);
        t = (await svc.RecordRunBranchAsync(t.Id, Old))!;
        Assert.Null(await TicketEndpoints.RenameRefusalAsync(t, svc, leases));

        // A run on the board host leaves nothing in the way.
        ctx.CycleRuns.Add(new CycleRun { TicketId = t.Id, ProjectKey = "alpha", Host = WorkerHostSettings.Board });
        await ctx.SaveChangesAsync();
        Assert.Null(await TicketEndpoints.RenameRefusalAsync(t, svc, leases));

        var held = await leases.TryAcquireAllAsync(new[] { ResourceLeaseService.TicketKey(t.Id) }, t.Id, "implement", TimeSpan.FromHours(1));
        Assert.NotNull(held);
        Assert.Contains("A run holds this ticket", await TicketEndpoints.RenameRefusalAsync(t, svc, leases));
        await leases.ReleaseForTicketAsync(t.Id);

        t.ReviewedOn = "alpha-web@0123456789abcdef";
        Assert.Contains("under review", await TicketEndpoints.RenameRefusalAsync(t, svc, leases));
        t.ReviewedOn = null;

        ctx.CycleRuns.Add(new CycleRun { TicketId = t.Id, ProjectKey = "alpha", Host = "bravo-host" });
        await ctx.SaveChangesAsync();
        Assert.Contains("bravo-host", await TicketEndpoints.RenameRefusalAsync(t, svc, leases));
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private string Repo(string name) => Path.Combine(_project, name);

    /// <summary>The ticket's workspace under <see cref="Old"/>'s folder, and under <see cref="New"/>'s.</summary>
    private string OldWs => Path.Combine(_root, "wt", "jb-PROJ-1");
    private string NewWs => Path.Combine(_root, "wt", "jb-PROJ-1-signing-foundation");

    /// <summary>Where a worktree under <see cref="OldWs"/> is once the rename has moved it.</summary>
    private string Moved(string wt) => Path.Combine(NewWs, Path.GetFileName(wt));

    /// <summary>
    /// The ticket's worktree in one repo, on <see cref="Old"/> forked from main, laid out as the
    /// worker lays out a workspace; with a commit on it when <paramref name="touch"/>.
    /// </summary>
    private string Workspace(string repo, bool touch)
    {
        var wt = Path.Combine(_root, "wt", "jb-PROJ-1", repo);
        Git(Repo(repo), "worktree", "add", "-q", "-b", Old, wt, "main");
        Identity(wt);
        if (touch) Commit(wt, "work.txt", $"PROJ-1: work in {repo}");
        return wt;
    }

    private void NewRepo(string name)
    {
        var hub = Repo(name);
        var origin = Path.Combine(_root, "origins", name + ".git");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-q", "--bare", "-b", "main");
        Directory.CreateDirectory(hub);
        Git(hub, "init", "-q", "-b", "main");
        Identity(hub);
        Git(hub, "remote", "add", "origin", origin);
        Commit(hub, "README.md", $"init {name}");
        Git(hub, "push", "-q", "-u", "origin", "main");
    }

    private static void WriteProject(string data, string key, string json)
    {
        Directory.CreateDirectory(Path.Combine(data, "projects", key));
        File.WriteAllText(Path.Combine(data, "projects", key, "project.defaults.json"), json);
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private static void Identity(string dir)
    {
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "commit.gpgsign", "false");
    }

    private static void Commit(string dir, string file, string subject)
    {
        File.AppendAllText(Path.Combine(dir, file), subject + "\n");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", subject);
    }

    private static string Sha(string dir, string branch)
    {
        var (code, output) = Run(dir, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        return code == 0 ? output.Trim() : "";
    }

    private static string Head(string dir) => Run(dir, "rev-parse", "--abbrev-ref", "HEAD").Output.Trim();

    private static void Git(string dir, params string[] args)
    {
        var (code, output) = Run(dir, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {dir}: {output}");
    }

    private static (int Code, string Output) Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
