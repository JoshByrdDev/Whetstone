using System.Diagnostics;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A ticket under PR review (<see cref="Ticket.ReviewedOn"/>): once a branch is in a pull request
/// someone has read, each round of fixes goes on top of what they saw instead of being rewritten into
/// it. The state, how it is turned on (by the owner, or by a run classifying a send-back as found in
/// PR review), when it ends, and the board's refusal to rewrite such a branch.
/// </summary>
public class UnderReviewTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public UnderReviewTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private async Task<int> Seed(TicketPhase phase, TicketTrack track = TicketTrack.Delivery, string? reviewedOn = null)
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "Export", Phase = phase, Branch = "jb/PROJ-1", Track = track,
                             ReviewedOn = reviewedOn, ReviewedOnAt = reviewedOn is null ? null : DateTime.UtcNow };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private async Task<Ticket> Load(int id)
    {
        await using var ctx = NewCtx();
        return await ctx.Tickets.AsNoTracking().FirstAsync(t => t.Id == id);
    }

    // ---- The owner's switch -------------------------------------------------------------------

    [Fact]
    public async Task The_owner_puts_a_ticket_under_review_and_takes_it_out_again()
    {
        var id = await Seed(TicketPhase.NeedsReview);
        await using (var ctx = NewCtx())
        {
            var (t, problem) = await new TicketService(ctx).SetReviewedOnAsync(id, "abc1234");
            Assert.Null(problem);
            Assert.True(t!.UnderReview);
        }
        var on = await Load(id);
        Assert.Equal("abc1234", on.ReviewedOn);
        Assert.NotNull(on.ReviewedOnAt);
        Assert.Equal("abc1234", TicketMapper.ToDetail(on).ReviewedOn);

        await using (var ctx = NewCtx())
            await new TicketService(ctx).SetReviewedOnAsync(id, null);
        var off = await Load(id);
        Assert.False(off.UnderReview);
        Assert.Null(off.ReviewedOnAt);
    }

    [Fact]
    public async Task A_review_track_ticket_or_a_done_one_cannot_be_put_under_review()
    {
        var peer = await Seed(TicketPhase.NeedsPeerReview, TicketTrack.Review);
        var done = await Seed(TicketPhase.Done);
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        Assert.Contains("delivery", (await svc.SetReviewedOnAsync(peer, "abc1234")).Problem);
        Assert.Contains("Done", (await svc.SetReviewedOnAsync(done, "abc1234")).Problem);
        Assert.Null((await Load(peer)).ReviewedOn);
    }

    // ---- When it ends -------------------------------------------------------------------------

    [Fact]
    public async Task Done_ends_the_review()
    {
        var id = await Seed(TicketPhase.NeedsReview, reviewedOn: "abc1234");
        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.Done, "signed off", TransitionActor.Human);
        Assert.Null((await Load(id)).ReviewedOn);
    }

    [Fact]
    public async Task A_merge_ends_the_review()
    {
        var id = await Seed(TicketPhase.NeedsReview, reviewedOn: "abc1234");
        await using (var ctx = NewCtx())
            await new TicketService(ctx).SetMergedAsync(id, "fff0000");
        var t = await Load(id);
        Assert.Null(t.ReviewedOn);
        Assert.Null(t.ReviewedOnAt);
    }

    [Fact]
    public async Task Going_round_again_does_not_end_it()
    {
        var id = await Seed(TicketPhase.NeedsReview, reviewedOn: "abc1234");
        await using (var ctx = NewCtx())
            await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, "Reviewer: rename the column",
                TransitionActor.Human, build: "def5678");
        Assert.Equal("abc1234", (await Load(id)).ReviewedOn);
    }

    // ---- Turned on by the run that classifies a PR review's send-back -------------------------

    /// <summary>Sent back from review by a person with this build, and claimed by an implementation run.</summary>
    private async Task<int> SentBack(string? build, string? reviewedOn = null)
    {
        var id = await Seed(TicketPhase.NeedsReview, reviewedOn: reviewedOn);
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.TransitionAsync(id, TicketPhase.NeedsImplementation, "Reviewer: rename the column", TransitionActor.Human, build: build);
        await svc.TransitionAsync(id, TicketPhase.Implementing, null, TransitionActor.System);
        return id;
    }

    [Fact]
    public async Task Classifying_a_send_back_as_found_in_PR_review_puts_the_ticket_under_review_at_the_build_sent_back()
    {
        var id = await SentBack("alpha-web@abc1234, alpha-api@def5678");
        await using (var ctx = NewCtx())
        {
            var (e, problem) = await new TicketService(ctx).ClassifyEscapeByRunAsync(id, EscapeKind.Logic, EscapeSource.PrReview,
                "The reviewer found the export ignores the date filter; nothing drove a filtered export.");
            Assert.Null(problem);
            Assert.NotNull(e);
        }
        Assert.Equal("alpha-web@abc1234, alpha-api@def5678", (await Load(id)).ReviewedOn);
    }

    [Fact]
    public async Task A_later_round_moves_the_point_up_to_what_the_reviewer_saw_then()
    {
        var id = await SentBack("bbb2222", reviewedOn: "aaa1111");
        await using (var ctx = NewCtx())
            await new TicketService(ctx).ClassifyEscapeByRunAsync(id, EscapeKind.Logic, EscapeSource.PrReview,
                "The reviewer found the round's rename missed the API.");
        Assert.Equal("bbb2222", (await Load(id)).ReviewedOn);
    }

    [Theory]
    [InlineData(EscapeSource.Owner)]
    [InlineData(EscapeSource.Other)]
    public async Task Another_source_leaves_the_review_state_alone(EscapeSource source)
    {
        var fresh = await SentBack("abc1234");
        var under = await SentBack("bbb2222", reviewedOn: "aaa1111");
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.ClassifyEscapeByRunAsync(fresh, EscapeKind.UiPolish, source, "The badge sat left of the total.");
            await svc.ClassifyEscapeByRunAsync(under, EscapeKind.UiPolish, source, "The badge sat left of the total.");
        }
        Assert.Null((await Load(fresh)).ReviewedOn);
        Assert.Equal("aaa1111", (await Load(under)).ReviewedOn);
    }

    [Fact]
    public async Task With_no_build_recorded_there_is_nothing_to_anchor_a_round_to()
    {
        var id = await SentBack(null);
        await using (var ctx = NewCtx())
            await new TicketService(ctx).ClassifyEscapeByRunAsync(id, EscapeKind.Logic, EscapeSource.PrReview,
                "The reviewer found the export ignores the date filter.");
        Assert.Null((await Load(id)).ReviewedOn);
    }

    // ---- The board will not rewrite it --------------------------------------------------------

    [Fact]
    public void Rebase_and_force_push_are_refused_under_review_and_say_how_to_get_past_it()
    {
        var under = new Ticket { ReviewedOn = "abc1234" };
        var why = GitEndpoints.RefuseRewriteUnderReview(under, "rebase it");
        Assert.NotNull(why);
        Assert.Contains("abc1234", why);
        Assert.Contains("fast-forward", why);
        Assert.Contains("drawer", why);
        Assert.Null(GitEndpoints.RefuseRewriteUnderReview(new Ticket(), "rebase it"));
    }

    [Fact]
    public void The_reviewed_sha_is_read_per_repo()
    {
        Assert.Equal("abc1234", TicketGitService.ReviewedShaFor("abc1234", ""));
        Assert.Equal("def5678", TicketGitService.ReviewedShaFor("alpha-web@abc1234, alpha-api@def5678", "alpha-api"));
        Assert.Null(TicketGitService.ReviewedShaFor("alpha-web@abc1234", "alpha-api"));
        Assert.Null(TicketGitService.ReviewedShaFor(null, ""));
    }
}

/// <summary>
/// The git half of a ticket under review, against a real repo: what may be recorded as "what the
/// reviewer saw", and the diff since it that the PR dialog shows.
/// </summary>
public class UnderReviewGitTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly TicketGitService _svc;
    private readonly string _reviewed;
    private const string Branch = "jb/PROJ-1";

    public UnderReviewGitTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-review-" + Guid.NewGuid().ToString("N")[..8]);
        var data = Path.Combine(_root, "data");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        Git("init", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Commit("README.md", "hello\n", "init");
        Git("checkout", "-q", "-b", Branch);
        Commit("feature.txt", "reviewed\n", "PROJ-1: Add export");
        _reviewed = Git("rev-parse", "HEAD").Trim();
        Commit("round.txt", "fix\n", "PROJ-1: Address review - name the file");
        Git("checkout", "-q", "main");

        Directory.CreateDirectory(Path.Combine(data, "projects", "alpha"));
        File.WriteAllText(Path.Combine(data, "projects", "alpha", "project.defaults.json"),
            $$"""
            { "displayName": "Alpha", "projectPath": {{System.Text.Json.JsonSerializer.Serialize(_repo)}}, "baseBranch": "main" }
            """);
        File.WriteAllText(Path.Combine(data, "config.defaults.json"), """{ "baseBranch": "main" }""");
        _svc = new TicketGitService(new ProjectRegistry(new DashboardPaths(data)),
            new GitWorktreeService(NullLogger<GitWorktreeService>.Instance), NullLogger<TicketGitService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Left_unnamed_the_point_is_the_branch_head_as_it_stands()
    {
        var (point, error) = await _svc.ReviewedPointAsync("alpha", Branch, null);
        Assert.Null(error);
        Assert.Equal(Git("rev-parse", Branch).Trim(), point);
    }

    [Fact]
    public async Task A_named_commit_under_the_branch_head_is_recorded_in_full()
    {
        var (point, error) = await _svc.ReviewedPointAsync("alpha", Branch, _reviewed[..7]);
        Assert.Null(error);
        Assert.Equal(_reviewed, point);
    }

    [Fact]
    public async Task A_commit_the_branch_does_not_hold_is_refused()
    {
        Commit("elsewhere.txt", "x\n", "on main only");
        var onMain = Git("rev-parse", "HEAD").Trim();
        var (point, error) = await _svc.ReviewedPointAsync("alpha", Branch, onMain);
        Assert.Null(point);
        Assert.Contains("is not on", error);

        (point, error) = await _svc.ReviewedPointAsync("alpha", Branch, "deadbeef");
        Assert.Null(point);
        Assert.Contains("not a commit", error);
    }

    [Fact]
    public async Task A_commit_the_base_already_holds_is_not_what_the_reviewer_read()
    {
        var fork = Git("merge-base", "main", Branch).Trim();
        var (point, error) = await _svc.ReviewedPointAsync("alpha", Branch, fork);
        Assert.Null(point);
        Assert.Contains("already on main", error);
    }

    [Fact]
    public async Task One_repo_takes_one_commit_and_no_repo_name()
    {
        Assert.Contains("one repo", (await _svc.ReviewedPointAsync("alpha", Branch, $"{_reviewed}, {_reviewed}")).Error);
        Assert.NotNull((await _svc.ReviewedPointAsync("alpha", Branch, $"alpha-web@{_reviewed}")).Error);
        Assert.Contains("no branch", (await _svc.ReviewedPointAsync("alpha", null, null)).Error);
    }

    [Fact]
    public async Task The_diff_since_the_reviewer_is_the_round_alone()
    {
        var whole = await _svc.LocalPreflightAsync("alpha", Branch);
        var since = await _svc.LocalPreflightAsync("alpha", Branch, since: _reviewed);

        Assert.Equal(2, whole.Commits.Count);
        Assert.Equal(new[] { "feature.txt", "round.txt" }, whole.Files.Select(f => f.Path).OrderBy(p => p));
        var round = Assert.Single(since.Commits);
        Assert.Equal("PROJ-1: Address review - name the file", round.Subject);
        Assert.Equal("round.txt", Assert.Single(since.Files).Path);
        // Where the branch sits against its base is the same question either way.
        Assert.Equal(whole.Ahead, since.Ahead);
    }

    private void Commit(string file, string content, string subject)
    {
        File.WriteAllText(Path.Combine(_repo, file), content);
        Git("add", "-A");
        Git("commit", "-q", "-m", subject);
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return stdout;
    }
}
