using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Review track on a project of several repos (docs/multi-repo-plan.md, phase 2): a review names
/// the one repo its branch is in, its comments are about that repo, and the board shows it that repo
/// alone. The workspace itself is the worker's, tested in data/worker-repos.tests.ps1.
/// </summary>
public class MultiRepoReviewTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public MultiRepoReviewTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private static ProjectConfig Several() => new()
    {
        Name = "alpha", DisplayName = "Alpha", ProjectPath = Path.Combine(Path.GetTempPath(), "Alpha"),
        ConfiguredRepos = new[] { Member("alpha-web"), Member("alpha-api"), Member("alpha-infra", readOnly: true) },
    };

    private static ProjectConfig One() => new() { Name = "alpha", DisplayName = "Alpha", ProjectPath = Path.Combine(Path.GetTempPath(), "Alpha") };

    private static RepoMember Member(string dir, bool readOnly = false) =>
        new(dir, "", Array.Empty<string>(), Array.Empty<string>(), readOnly);

    private static Ticket Review(string? repo) => new()
    {
        ProjectKey = "alpha", Title = "Review: someone/theirs", Track = TicketTrack.Review, Phase = TicketPhase.NeedsPeerReview,
        ReviewTarget = new ReviewTarget { SourceBranch = "someone/theirs", Repo = repo },
    };

    // ---- Which repo a review is of --------------------------------------------------------------

    [Fact]
    public void A_project_of_several_needs_to_be_told_which_repo()
    {
        var error = TicketEndpoints.ReviewRepoError(Several(), null, out var repo);
        Assert.Null(repo);
        Assert.Contains("say which one the branch is in (alpha-web, alpha-api)", error);
    }

    [Fact]
    public void It_takes_a_writable_repo_by_its_folder_name_as_configured()
    {
        Assert.Null(TicketEndpoints.ReviewRepoError(Several(), " ALPHA-API ", out var repo));
        Assert.Equal("alpha-api", repo);
    }

    [Fact]
    public void It_refuses_a_repo_the_project_does_not_have()
    {
        var error = TicketEndpoints.ReviewRepoError(Several(), "alpha-nope", out var repo);
        Assert.Null(repo);
        Assert.Contains("has no repo named 'alpha-nope'", error);
    }

    [Fact]
    public void It_refuses_a_read_only_repo_which_has_no_pull_requests()
    {
        var error = TicketEndpoints.ReviewRepoError(Several(), "alpha-infra", out var repo);
        Assert.Null(repo);
        Assert.Contains("read-only", error);
    }

    [Fact]
    public void A_project_of_one_names_no_repo_and_refuses_one_named()
    {
        Assert.Null(TicketEndpoints.ReviewRepoError(One(), null, out var none));
        Assert.Null(none);
        Assert.Contains("is one repo", TicketEndpoints.ReviewRepoError(One(), "alpha-api", out _));
    }

    [Fact]
    public async Task The_repo_is_kept_on_the_target_and_shown_on_the_row_and_the_detail()
    {
        int id;
        await using (var ctx = NewCtx())
        {
            var t = Review("alpha-api");
            ctx.Tickets.Add(t);
            await ctx.SaveChangesAsync();
            id = t.Id;
        }

        await using var readCtx = NewCtx();
        var svc = new TicketService(readCtx);
        var read = (await svc.GetAsync(id))!;
        Assert.Equal("alpha-api", read.ReviewTarget!.Repo);
        Assert.Equal("alpha-api", TicketMapper.ToSummary(read).ReviewRepo);
        Assert.Equal("alpha-api", TicketMapper.ToDetail(read).Review!.Repo);
    }

    // ---- What its comments are about ------------------------------------------------------------

    [Theory]
    [InlineData("alpha-api/src/Foo.cs", "src/Foo.cs")]
    [InlineData(@"alpha-api\src\Foo.cs", "src/Foo.cs")]
    [InlineData("ALPHA-API/src/Foo.cs", "src/Foo.cs")]
    [InlineData("src/Foo.cs", "src/Foo.cs")]
    [InlineData("alpha-apiary/src/Foo.cs", "alpha-apiary/src/Foo.cs")]
    [InlineData("alpha-web/src/api.ts", "alpha-web/src/api.ts")]
    public void A_comment_path_is_relative_to_the_repo_under_review(string written, string kept)
        => Assert.Equal(kept, TicketService.ReviewFilePath(written, "alpha-api"));

    [Fact]
    public void On_a_project_of_one_a_comment_path_is_only_tidied()
    {
        Assert.Equal("alpha-api/src/Foo.cs", TicketService.ReviewFilePath(@" alpha-api\src\Foo.cs ", null));
        Assert.Null(TicketService.ReviewFilePath("  ", "alpha-api"));
    }

    [Fact]
    public async Task A_staged_comment_drops_the_repo_folder_the_workspace_named_it_with()
    {
        int id;
        await using (var ctx = NewCtx())
        {
            var t = Review("alpha-api");
            ctx.Tickets.Add(t);
            await ctx.SaveChangesAsync();
            id = t.Id;
        }

        await using var svcCtx = NewCtx();
        var c = await new TicketService(svcCtx).AddReviewCommentAsync(id, "this null-refs", ReviewSeverity.Blocking,
            AuthorKind.Claude, "alpha-api/src/Foo.cs", 12);
        Assert.Equal("src/Foo.cs", c!.FilePath);
    }

    // ---- What the board shows it ----------------------------------------------------------------

    private static readonly RepoTouch[] Touches =
    {
        new("alpha-web", false, false, 0, 0, null, null),
        new("alpha-api", false, true, 2, 0, "origin/main", null),
        new("alpha-infra", true, false, 0, 0, null, null),
    };

    [Fact]
    public void A_review_is_shown_the_repo_under_review_alone()
        => Assert.Equal(new[] { "alpha-api" }, GitEndpoints.OnlyTheReviewedRepo(Review("alpha-api"), Touches).Select(t => t.Dir));

    [Fact]
    public void A_delivery_ticket_and_a_review_of_one_repo_see_every_repo_as_before()
    {
        Assert.Equal(3, GitEndpoints.OnlyTheReviewedRepo(Review(null), Touches).Count);
        var delivery = new Ticket { ProjectKey = "alpha", Track = TicketTrack.Delivery, Branch = "jb/PROJ-1" };
        Assert.Equal(3, GitEndpoints.OnlyTheReviewedRepo(delivery, Touches).Count);
    }

    [Fact]
    public void A_review_refuses_a_read_or_checkout_of_another_repo()
    {
        Assert.NotNull(GitEndpoints.NotTheReviewedRepo(Review("alpha-api"), "alpha-web"));
        Assert.NotNull(GitEndpoints.NotTheReviewedRepo(Review("alpha-api"), null));
        Assert.Null(GitEndpoints.NotTheReviewedRepo(Review("alpha-api"), "Alpha-Api"));
        Assert.Null(GitEndpoints.NotTheReviewedRepo(Review(null), null));
    }
}
