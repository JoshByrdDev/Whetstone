using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Bug tracing against real repos (docs/bugs-and-experiments-plan.md, section 1): a fix's changed
/// lines blamed at its parent and mapped back to the tickets whose merges wrote them, and the owner's
/// verdicts kept across a re-trace.
/// </summary>
public class BugTraceTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _data;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly TicketGitService _git;

    public BugTraceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-bug-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_root, "alpha");
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-q", "-b", "main");
        Identity(_repo);
        Write(_repo, "app.cs", Enumerable.Range(1, 10).Select(i => $"line{i}"));
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "init");

        WriteProject("alpha", $$"""{ "displayName": "Alpha", "projectPath": {{Json(_repo)}}, "baseBranch": "main", "mergeInsteadOfPr": true }""");
        File.WriteAllText(Path.Combine(_data, "config.defaults.json"), """{ "baseBranch": "main" }""");
        var registry = new ProjectRegistry(new DashboardPaths(_data));
        _git = new TicketGitService(registry, new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
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

    private DashboardDbContext Ctx() => new(_options);

    // ---- the scenario ------------------------------------------------------------------

    /// <summary>A ticket whose branch rewrites some lines of app.cs and is merged --no-ff, as the board merges.</summary>
    private async Task<Ticket> Land(string branch, Func<string[], string[]> edit, bool isBug = false, DateTime? doneAt = null,
        string file = "app.cs", string? repo = null)
    {
        repo ??= _repo;
        Git(repo, "checkout", "-q", "-b", branch);
        var path = Path.Combine(repo, file);
        Write(repo, file, edit(File.ReadAllLines(path)));
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", $"{branch}: work");
        Git(repo, "checkout", "-q", "main");
        Git(repo, "merge", "-q", "--no-ff", "--no-edit", "-m", $"Merge {branch}", branch);
        var merge = Sha(repo, "HEAD");

        await using var ctx = Ctx();
        var t = new Ticket
        {
            ProjectKey = "alpha", Title = branch, Phase = TicketPhase.Done, Branch = branch, IsBug = isBug,
            MergeSha = merge, MergedAt = doneAt ?? DateTime.UtcNow.AddDays(-10), CreatedAt = DateTime.UtcNow.AddDays(-1),
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    private static string[] Set(string[] lines, int line, string text) { lines[line - 1] = text; return lines; }

    private async Task<BugTraceService.BugView> Trace(int bugId)
    {
        await using var ctx = Ctx();
        var svc = new BugTraceService(ctx, _git);
        await svc.TraceAsync(bugId);
        return (await svc.ForBugAsync(bugId))!;
    }

    [Fact]
    public async Task A_fix_is_blamed_back_to_the_tickets_that_wrote_its_lines()
    {
        var a = await Land("a", l => Set(Set(l, 2, "a2"), 3, "a3"));
        var b = await Land("b", l => Set(l, 7, "b7"));
        var bug = await Land("fix", l => Set(Set(Set(l, 2, "fixed2"), 7, "fixed7"), 9, "fixed9"), isBug: true);

        var view = await Trace(bug.Id);

        var bySource = view.Links.ToDictionary(l => l.SourceTicketId ?? 0);
        Assert.Equal(3, view.Links.Count);
        Assert.Equal(1, bySource[a.Id].Lines);
        Assert.Equal(1, bySource[b.Id].Lines);
        // line9 is the init commit's, which no ticket delivered.
        Assert.Equal(1, bySource[0].Lines);
        Assert.All(view.Links, l => Assert.Equal("Suggested", l.Status));
        Assert.Equal(1.0 / 3, bySource[a.Id].Share, 3);
        Assert.Equal(3, view.LinesBlamed);
        Assert.Contains(bySource[a.Id].Commits, c => c.Subject == "a: work" && c.Files.Contains("app.cs"));
    }

    [Fact]
    public async Task A_reindent_is_not_where_the_line_was_written()
    {
        var a = await Land("a", l => Set(l, 3, "a3"));
        await Land("indent", l => Set(l, 3, "      a3"));
        var bug = await Land("fix", l => Set(l, 3, "fixed3"), isBug: true);

        var view = await Trace(bug.Id);

        var link = Assert.Single(view.Links);
        Assert.Equal(a.Id, link.SourceTicketId);
    }

    [Fact]
    public async Task A_fix_that_only_adds_lines_blames_its_neighbours_at_half_weight()
    {
        var a = await Land("a", l => Set(Set(l, 4, "a4"), 5, "a5"));
        var bug = await Land("guard", l => l.Take(4).Append("if (x is null) return;").Concat(l.Skip(4)).ToArray(), isBug: true);

        var view = await Trace(bug.Id);

        var link = Assert.Single(view.Links);
        Assert.Equal(a.Id, link.SourceTicketId);
        Assert.True(link.Neighbours);
        Assert.Equal(1.0, link.Lines);   // two neighbours, half each
    }

    [Fact]
    public async Task A_retrace_keeps_what_the_owner_decided()
    {
        var a = await Land("a", l => Set(l, 2, "a2"));
        var b = await Land("b", l => Set(l, 7, "b7"));
        var bug = await Land("fix", l => Set(Set(l, 2, "fixed2"), 7, "fixed7"), isBug: true);
        var first = await Trace(bug.Id);

        await using (var ctx = Ctx())
        {
            var svc = new BugTraceService(ctx, _git);
            await svc.ConfirmAsync(first.Links.Single(l => l.SourceTicketId == a.Id).Id, BugFoundBy.Owner, "qa-test", "seen in use");
            await svc.RejectAsync(first.Links.Single(l => l.SourceTicketId == b.Id).Id, "unrelated");
        }

        var again = await Trace(bug.Id);

        Assert.Equal("Confirmed", again.Links.Single(l => l.SourceTicketId == a.Id).Status);
        Assert.Equal("qa-test", again.Links.Single(l => l.SourceTicketId == a.Id).MissedBy);
        Assert.Equal("Rejected", again.Links.Single(l => l.SourceTicketId == b.Id).Status);
        Assert.Equal(2, again.Links.Count);
    }

    [Fact]
    public async Task Confirming_needs_who_found_it_and_which_stage_missed_it()
    {
        await Land("a", l => Set(l, 2, "a2"));
        var bug = await Land("fix", l => Set(Set(l, 2, "fixed2"), 9, "fixed9"), isBug: true);
        var view = await Trace(bug.Id);

        await using var ctx = Ctx();
        var svc = new BugTraceService(ctx, _git);
        var mapped = view.Links.Single(l => l.SourceTicketId != null);
        await Assert.ThrowsAsync<BugTraceService.LinkRefused>(() => svc.ConfirmAsync(mapped.Id, BugFoundBy.Owner, "", null));
        await Assert.ThrowsAsync<BugTraceService.LinkRefused>(() => svc.ConfirmAsync(mapped.Id, BugFoundBy.Owner, "deploy", null));
        // Lines from outside the board have no ticket to confirm.
        var unmapped = view.Links.Single(l => l.SourceTicketId == null);
        await Assert.ThrowsAsync<BugTraceService.LinkRefused>(() => svc.ConfirmAsync(unmapped.Id, BugFoundBy.Owner, "implement", null));
    }

    [Fact]
    public async Task A_rebased_commit_maps_to_its_ticket_by_patch_id()
    {
        // A ticket delivered by pull request: its branch commit is recorded at Done, and what lands on
        // the base is a different commit with the same change.
        Git(_repo, "checkout", "-q", "-b", "pr");
        Write(_repo, "app.cs", Set(File.ReadAllLines(Path.Combine(_repo, "app.cs")), 5, "pr5"));
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "PROJ-9 change five");
        var branchSha = Sha(_repo, "HEAD");
        Git(_repo, "checkout", "-q", "main");
        Git(_repo, "commit", "-q", "--allow-empty", "-m", "someone else");
        Git(_repo, "cherry-pick", branchSha);
        Assert.NotEqual(branchSha, Sha(_repo, "HEAD"));

        int prId;
        await using (var ctx = Ctx())
        {
            var t = new Ticket { ProjectKey = "alpha", Title = "pr", Phase = TicketPhase.Done };
            ctx.Tickets.Add(t);
            await ctx.SaveChangesAsync();
            prId = t.Id;
            ctx.TicketCommits.Add(new TicketCommit
            {
                TicketId = t.Id, ProjectKey = "alpha", Sha = branchSha, Source = "branch",
                PatchId = await _git.PatchIdAsync(_repo, branchSha),
            });
            await ctx.SaveChangesAsync();
        }
        var bug = await Land("fix", l => Set(l, 5, "fixed5"), isBug: true);

        var view = await Trace(bug.Id);

        Assert.Equal(prId, Assert.Single(view.Links).SourceTicketId);
    }

    [Fact]
    public async Task Escaped_means_landed_before_the_bug_was_filed()
    {
        Assert.True(BugTraceService.IsEscaped(DateTime.UtcNow.AddDays(-3), null, DateTime.UtcNow));
        Assert.True(BugTraceService.IsEscaped(null, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow));
        Assert.False(BugTraceService.IsEscaped(DateTime.UtcNow, null, DateTime.UtcNow.AddDays(-1)));
        Assert.False(BugTraceService.IsEscaped(null, null, DateTime.UtcNow));

        var a = await Land("a", l => Set(l, 2, "a2"));
        var bug = await Land("fix", l => Set(l, 2, "fixed2"), isBug: true);
        var view = await Trace(bug.Id);
        Assert.True(view.Links.Single(l => l.SourceTicketId == a.Id).Escaped);
    }

    [Fact]
    public async Task A_ticket_that_is_not_a_bug_is_not_traced()
    {
        var t = await Land("a", l => Set(l, 2, "a2"));
        await using var ctx = Ctx();
        Assert.Null(await new BugTraceService(ctx, _git).TraceAsync(t.Id));
    }

    [Fact]
    public async Task The_run_that_wrote_it_is_found_from_its_recorded_files()
    {
        var a = await Land("a", l => Set(l, 2, "a2"));
        await using (var ctx = Ctx())
        {
            ctx.CycleRuns.AddRange(
                new CycleRun { TicketId = a.Id, ProjectKey = "alpha", Skill = "implement", Pass = 1, RunCommits = 1,
                    StartedAt = DateTime.UtcNow.AddDays(-12), RunFilesJson = "[\"other.cs\"]" },
                new CycleRun { TicketId = a.Id, ProjectKey = "alpha", Skill = "implement", Pass = 2, RunCommits = 1,
                    StartedAt = DateTime.UtcNow.AddDays(-11), RunFilesJson = "[\"app.cs\"]" },
                new CycleRun { TicketId = a.Id, ProjectKey = "alpha", Skill = "qa-test", Pass = 1,
                    StartedAt = DateTime.UtcNow.AddDays(-11) });
            await ctx.SaveChangesAsync();
        }
        var bug = await Land("fix", l => Set(l, 2, "fixed2"), isBug: true);

        var view = await Trace(bug.Id);

        var run = Assert.Single(view.Links.Single().Runs);
        Assert.Equal(2, run.Pass);
        Assert.Equal("likely", run.Tier);
    }

    [Fact]
    public async Task A_run_whose_range_still_holds_the_commit_is_exact()
    {
        var before = Sha(_repo, "main");
        var a = await Land("a", l => Set(l, 2, "a2"));
        var commit = Sha(_repo, "refs/heads/a");
        await using (var ctx = Ctx())
        {
            ctx.CycleRuns.Add(new CycleRun
            {
                TicketId = a.Id, ProjectKey = "alpha", Skill = "implement", Pass = 1, RunCommits = 1,
                StartedAt = DateTime.UtcNow.AddDays(-12),
                CommitRangeJson = $$"""[{"repo":"","from":"{{before}}","to":"{{commit}}"}]""",
            });
            await ctx.SaveChangesAsync();
        }
        var bug = await Land("fix", l => Set(l, 2, "fixed2"), isBug: true);

        var view = await Trace(bug.Id);

        Assert.Equal("exact", Assert.Single(view.Links.Single().Runs).Tier);
    }

    [Theory]
    [InlineData("ClientApp/package-lock.json", true)]
    [InlineData("wwwroot/assets/index.js", true)]
    [InlineData("web/dist/app.js", true)]
    [InlineData("src/App.cs", false)]
    public void Generated_files_are_not_blamed(string path, bool noise) =>
        Assert.Equal(noise, TicketGitService.IsBlameNoise(path));

    // ---- plumbing ------------------------------------------------------------------------

    private void WriteProject(string key, string json)
    {
        Directory.CreateDirectory(Path.Combine(_data, "projects", key));
        File.WriteAllText(Path.Combine(_data, "projects", key, "project.defaults.json"), json);
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static void Write(string dir, string file, IEnumerable<string> lines) =>
        File.WriteAllText(Path.Combine(dir, file), string.Join("\n", lines) + "\n");

    private static void Identity(string dir)
    {
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "commit.gpgsign", "false");
        Git(dir, "config", "core.autocrlf", "false");
    }

    private static string Sha(string dir, string rev) => Run(dir, "rev-parse", "--verify", "--quiet", rev).Output.Trim();

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
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr);
    }
}
