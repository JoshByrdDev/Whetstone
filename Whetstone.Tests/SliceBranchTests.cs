using System.Diagnostics;
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
/// Piece 1 of docs/slice-branches.md: a ticket's base is its slice's branch while the slice is open,
/// and the project's base otherwise - a row each of section 2's table - and a project without the
/// switch is never told anything new. Against a real hub with a bare origin, and a database holding
/// the tickets and slices, as the board has them.
/// <para>
/// The worker's half of the same rule is in data/worker-base.tests.ps1 ("the ticket's base"), which
/// is handed the branch this side names.
/// </para>
/// </summary>
public class SliceBranchTests : IDisposable
{
    private const string Slice = "slice/13-cards";

    private readonly string _root;
    private readonly string _hub;
    private readonly ProjectRegistry _registry;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly TicketGitService _svc;

    public SliceBranchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-slice-" + Guid.NewGuid().ToString("N")[..8]);
        _hub = Path.Combine(_root, "hub");
        var origin = Path.Combine(_root, "origin.git");
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-q", "--bare", "-b", "main");
        Directory.CreateDirectory(_hub);
        Git(_hub, "init", "-q", "-b", "main");
        Git(_hub, "config", "user.email", "test@example.com");
        Git(_hub, "config", "user.name", "Test");
        Git(_hub, "config", "commit.gpgsign", "false");
        Git(_hub, "remote", "add", "origin", origin);
        Commit(_hub, "README.md", "init");
        Git(_hub, "push", "-q", "-u", "origin", "main");

        // alpha has switched; bravo is the same hub delivered the same way, not switched; charlie
        // has switched but raises pull requests, which slice branches do not reach yet.
        WriteProject(data, "alpha", $$"""{ "projectPath": {{Json(_hub)}}, "baseBranch": "main", "mergeInsteadOfPr": true, "sliceBranches": true }""");
        WriteProject(data, "bravo", $$"""{ "projectPath": {{Json(_hub)}}, "baseBranch": "main", "mergeInsteadOfPr": true }""");
        WriteProject(data, "charlie", $$"""{ "projectPath": {{Json(_hub)}}, "baseBranch": "main", "sliceBranches": true }""");
        _registry = new ProjectRegistry(new DashboardPaths(data));

        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using (var ctx = new DashboardDbContext(_options)) ctx.Database.EnsureCreated();

        _svc = new TicketGitService(_registry, new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
            NullLogger<TicketGitService>.Instance, new DbBases(_options));
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

    // ---- the name -----------------------------------------------------------------------------

    [Theory]
    [InlineData(13, "docs/slices/13-cards.md", "slice/13-cards")]
    [InlineData(3, "docs/units/03-first-look.md", "slice/03-first-look")]
    [InlineData(4, @"docs\units\4-Sign In & Out.md", "slice/04-sign-in-out")]
    [InlineData(7, "units/seven.md", "slice/07-seven")]
    [InlineData(12, "12.md", "slice/12")]
    [InlineData(3, "docs/units/03-u3.md", "slice/03-u3")]
    public void A_slice_branch_is_named_for_its_number_and_spec(int number, string file, string want) =>
        Assert.Equal(want, SliceBranches.NameFor(number, file));

    // ---- section 2's table, as a rule -------------------------------------------------------

    private static ProjectConfig Switched(bool slices = true, bool merge = true, bool multi = false) => new()
    {
        Name = "alpha", SliceBranches = slices, MergeInsteadOfPr = merge,
        ConfiguredRepos = multi ? new[] { new RepoMember("alpha-api", "", Array.Empty<string>(), Array.Empty<string>(), false) } : Array.Empty<RepoMember>(),
    };

    private static StewardUnit Row(int unit = 13, string? branch = Slice, bool released = false) => new()
    {
        ProjectKey = "alpha", Unit = unit, Branch = branch, AgreedAt = DateTime.UtcNow,
        ReleasedAt = released ? DateTime.UtcNow : null,
    };

    [Fact]
    public void A_ticket_of_an_open_slice_is_based_on_its_branch() =>
        Assert.Equal(Slice, SliceBranches.BaseFor(Switched(), 13, Row()));

    [Fact]
    public void A_ticket_of_no_slice_is_based_on_the_project() =>
        Assert.Null(SliceBranches.BaseFor(Switched(), null, null));

    [Fact]
    public void A_ticket_of_a_released_slice_is_based_on_the_project() =>
        Assert.Null(SliceBranches.BaseFor(Switched(), 13, Row(released: true)));

    [Fact]
    public void A_slice_agreed_before_the_switch_has_no_branch_and_its_tickets_stay_on_the_project() =>
        Assert.Null(SliceBranches.BaseFor(Switched(), 13, Row(branch: null)));

    [Fact]
    public void A_row_of_another_slice_says_nothing() =>
        Assert.Null(SliceBranches.BaseFor(Switched(), 12, Row(unit: 13)));

    [Fact]
    public void Without_the_switch_nothing_changes() =>
        Assert.Null(SliceBranches.BaseFor(Switched(slices: false), 13, Row()));

    [Fact]
    public void A_project_that_raises_pull_requests_gets_no_slice_branches()
    {
        Assert.False(Switched(merge: false).SliceBranchesOn);
        Assert.Null(SliceBranches.BaseFor(Switched(merge: false), 13, Row()));
    }

    [Fact]
    public void A_project_of_several_repos_gets_no_slice_branches() =>
        Assert.False(Switched(multi: true).SliceBranchesOn);

    [Fact]
    public void The_switch_is_read_from_the_project_config()
    {
        Assert.True(_registry.GetProjectConfig("alpha").SliceBranchesOn);
        Assert.False(_registry.GetProjectConfig("bravo").SliceBranchesOn);
        Assert.True(_registry.GetProjectConfig("charlie").SliceBranches);
        Assert.False(_registry.GetProjectConfig("charlie").SliceBranchesOn);
    }

    // ---- the same table, from the database --------------------------------------------------

    [Fact]
    public async Task The_database_answers_each_row_by_ticket_and_by_branch()
    {
        await SeedAsync();
        await using var db = new DashboardDbContext(_options);
        var alpha = _registry.GetProjectConfig("alpha");
        var bravo = _registry.GetProjectConfig("bravo");

        Assert.Equal(Slice, await SliceBranches.ForBranchAsync(db, alpha, "t-13"));
        Assert.Null(await SliceBranches.ForBranchAsync(db, alpha, "t-bug"));
        Assert.Null(await SliceBranches.ForBranchAsync(db, alpha, "t-12"));
        Assert.Null(await SliceBranches.ForBranchAsync(db, alpha, "nobody-has-this"));
        // The same rows on a project that has not switched answer nothing.
        Assert.Null(await SliceBranches.ForBranchAsync(db, bravo, "b-13"));

        var t13 = await db.Tickets.FirstAsync(t => t.Branch == "t-13");
        Assert.Equal(Slice, await SliceBranches.ForTicketAsync(db, alpha, t13));
    }

    // ---- the board's git, measured against the ticket's base --------------------------------

    /// <summary>
    /// main: init, then M (a fix on the base after the slice was cut). slice/13-cards: init, then S
    /// (an earlier ticket of the slice). t-13: off the slice, then T. b-13: the same, on bravo.
    /// </summary>
    private async Task LayOutAsync()
    {
        await SeedAsync();
        var (sha, no) = await _svc.CutSliceBranchAsync("alpha", Slice);
        Assert.Null(no);
        Assert.Equal(Rev("main"), sha);
        OnBranch(Slice, "cards.txt", "S: an earlier ticket of slice 13");
        Git(_hub, "branch", "t-13", Slice);
        OnBranch("t-13", "mine.txt", "T: this ticket");
        Git(_hub, "branch", "b-13", "t-13");
        Commit(_hub, "fix.txt", "M: a fix on the base");
    }

    [Fact]
    public async Task A_slice_ticket_is_measured_against_its_slice_and_one_of_no_switch_against_the_base()
    {
        await LayOutAsync();

        var slice = await _svc.BaseCheckAsync("alpha", "t-13");
        Assert.True(slice.Ok, slice.Error);
        Assert.Equal(Slice, slice.BaseRef);
        Assert.Equal((0, 1), (slice.Behind, slice.Ahead));
        Assert.True(slice.Fetched);

        var plain = await _svc.BaseCheckAsync("bravo", "b-13");
        Assert.Equal("main", plain.BaseRef);
        Assert.Equal((1, 2), (plain.Behind, plain.Ahead));

        var touched = (await _svc.TouchedAsync("alpha", "t-13")).Single();
        Assert.Equal((Slice, 1), (touched.BaseRef, touched.Ahead));
        Assert.Equal(Slice, await _svc.TicketBaseBranchAsync("alpha", "t-13"));
        Assert.Equal("main", await _svc.TicketBaseBranchAsync("bravo", "b-13"));
    }

    [Fact]
    public async Task The_boards_rebase_of_a_slice_ticket_is_onto_the_slice_and_never_takes_the_base()
    {
        await LayOutAsync();
        OnBranch(Slice, "more.txt", "S2: another ticket of slice 13");

        var rebase = await _svc.RebaseAsync("alpha", "t-13");
        Assert.True(rebase.Ok, rebase.Error);
        Assert.Equal(Rev(Slice), Rev("t-13~1"));
        Assert.DoesNotContain(Lines("log", "--format=%s", "t-13"), s => s.StartsWith("M:"));
        Assert.DoesNotContain(rebase.Output, l => l.Contains("could not fetch"));
    }

    [Fact]
    public async Task A_round_under_review_may_not_be_anchored_on_what_the_slice_already_holds()
    {
        await LayOutAsync();
        // S is on the slice branch, so it is not part of the ticket's own change.
        var (point, error) = await _svc.ReviewedPointAsync("alpha", "t-13", Rev(Slice));
        Assert.Null(point);
        Assert.Contains($"already on {Slice}", error);
        (point, error) = await _svc.ReviewedPointAsync("alpha", "t-13", Rev("t-13"));
        Assert.Null(error);
        Assert.NotNull(point);
    }

    [Fact]
    public async Task Cutting_a_slice_branch_twice_keeps_the_first_and_never_resets_it()
    {
        var (first, _) = await _svc.CutSliceBranchAsync("alpha", Slice);
        OnBranch(Slice, "cards.txt", "S: work merged into the slice");
        Commit(_hub, "fix.txt", "M: the base moves on");
        var (again, no) = await _svc.CutSliceBranchAsync("alpha", Slice);
        Assert.Null(no);
        Assert.NotEqual(first, again);
        Assert.Equal(Rev(Slice), again);
        Assert.Equal("main", Lines("rev-parse", "--abbrev-ref", "HEAD").Single());
    }

    [Fact]
    public async Task Only_a_slice_name_is_cut()
    {
        var (sha, no) = await _svc.CutSliceBranchAsync("alpha", "main2");
        Assert.Null(sha);
        Assert.Contains("not a slice branch", no);
        (sha, no) = await _svc.CutSliceBranchAsync("alpha", "slice/bad..name");
        Assert.Null(sha);
        Assert.NotNull(no);
    }

    // ---- piece 2: work lands on the slice, and the slice keeps up with the base ----------------

    [Fact]
    public async Task A_slice_ticket_merges_into_its_slice_without_touching_the_base_or_the_checkout()
    {
        await LayOutAsync();
        var baseTip = Rev("main");
        var sliceTip = Rev(Slice);

        var merged = await _svc.MergeAsync("alpha", "t-13", "Cards (#1)", null);
        Assert.True(merged.Ok, merged.Error);
        Assert.Equal(Slice, merged.BaseBranch);
        Assert.Equal(Rev(Slice), merged.MergeSha);
        Assert.Equal(new[] { sliceTip, Rev("t-13") }, Lines("rev-list", "--parents", "-n", "1", Slice).Single().Split(' ').Skip(1));
        Assert.Equal("Cards (#1)", Lines("log", "-1", "--format=%s", Slice).Single());
        Assert.Equal(baseTip, Rev("main"));
        Assert.Equal("main", Lines("rev-parse", "--abbrev-ref", "HEAD").Single());
        Assert.Empty(Lines("status", "--porcelain", "--untracked-files=no"));
    }

    [Fact]
    public async Task A_ticket_of_a_project_without_the_switch_still_merges_into_the_base()
    {
        await LayOutAsync();
        var sliceTip = Rev(Slice);
        var merged = await _svc.MergeAsync("bravo", "b-13", "Cards (#4)", null);
        Assert.True(merged.Ok, merged.Error);
        Assert.Equal("main", merged.BaseBranch);
        Assert.Equal(Rev("main"), merged.MergeSha);
        Assert.Equal(sliceTip, Rev(Slice));
    }

    [Fact]
    public async Task A_merge_into_the_slice_that_conflicts_changes_nothing()
    {
        await LayOutAsync();
        OnBranch(Slice, "mine.txt", "S2: another ticket of slice 13 wrote the same file");
        var sliceTip = Rev(Slice);
        var merged = await _svc.MergeAsync("alpha", "t-13", "Cards (#1)", null);
        Assert.False(merged.Ok);
        Assert.True(merged.Conflicted);
        Assert.Contains("mine.txt", merged.ConflictedFiles);
        Assert.Equal(sliceTip, Rev(Slice));
    }

    [Fact]
    public async Task A_slice_branch_a_person_has_checked_out_is_not_moved_under_them()
    {
        await LayOutAsync();
        var held = Path.Combine(_root, "held");
        Git(_hub, "worktree", "add", "-q", held, Slice);
        var sliceTip = Rev(Slice);
        var merged = await _svc.MergeAsync("alpha", "t-13", null, null);
        Assert.False(merged.Ok);
        Assert.Contains("is checked out in", merged.Error);
        Assert.Equal("refused", (await _svc.SyncSliceAsync("alpha", Slice, dryRun: false)).Answer);
        Assert.Equal(sliceTip, Rev(Slice));
    }

    [Fact]
    public async Task The_stewards_reading_of_a_slice_ticket_is_against_the_slice()
    {
        await LayOutAsync();
        var change = await _svc.BranchChangeAsync("alpha", "t-13");
        Assert.Equal((Slice, 0, 1), (change!.BaseRef, change.Behind, change.Ahead));
        Assert.Equal(new[] { "mine.txt" }, change.Files);
        var diff = await _svc.BranchDiffAsync("alpha", "t-13", null, 10_000);
        Assert.DoesNotContain("cards.txt", diff!.Value.Text);
    }

    [Fact]
    public async Task A_slice_takes_the_base_by_a_merge_with_itself_first_and_never_a_rebase()
    {
        await LayOutAsync();
        var sliceTip = Rev(Slice);
        var baseTip = Rev("main");

        var dry = await _svc.SyncSliceAsync("alpha", Slice, dryRun: true);
        Assert.Equal(("proposed", 1), (dry.Answer, dry.Commits));
        Assert.Equal(sliceTip, Rev(Slice));

        var sync = await _svc.SyncSliceAsync("alpha", Slice, dryRun: false);
        Assert.Equal("merged", sync.Answer);
        Assert.Equal(new[] { "fix.txt" }, sync.Files);
        Assert.Equal(new[] { sliceTip, baseTip }, Lines("rev-list", "--parents", "-n", "1", Slice).Single().Split(' ').Skip(1));
        Assert.Equal(baseTip, Rev("main"));
        // The slice's earlier work is still where it was: an ancestor, not rewritten.
        Assert.Equal(0, Run(_hub, "merge-base", "--is-ancestor", sliceTip, Slice).Code);
        Assert.Equal("up-to-date", (await _svc.SyncSliceAsync("alpha", Slice, dryRun: false)).Answer);
    }

    [Fact]
    public async Task A_slice_that_conflicts_with_the_base_is_left_for_the_owner_when_nothing_settles_it()
    {
        await LayOutAsync();
        OnBranch(Slice, "fix.txt", "S2: the slice wrote the base's file too");
        var sliceTip = Rev(Slice);
        var sync = await _svc.SyncSliceAsync("alpha", Slice, dryRun: false);
        Assert.Equal("conflict", sync.Answer);
        Assert.Contains("fix.txt", sync.Files);
        Assert.Equal(sliceTip, Rev(Slice));
    }

    [Fact]
    public async Task The_projects_resolver_settles_a_slice_sync_in_a_rebases_orientation()
    {
        await LayOutAsync();
        OnBranch(Slice, "fix.txt", "S2: the slice wrote the base's file too");
        var sliceTip = Rev(Slice);
        // A resolver that keeps "ours" - which at a rebase's stop is the base it is replayed onto.
        File.WriteAllText(Path.Combine(_root, "data", "projects", "alpha", "rebase-resolve.ps1"), """
            $in = [Console]::In.ReadToEnd() | ConvertFrom-Json
            foreach ($f in $in.conflicted) { git checkout --ours -- $f; git add -- $f }
            Write-Output "[rebase-resolve] kept the base's $($in.conflicted -join ', ') (onto $($in.baseRef))"
            exit 0
            """);

        var sync = await _svc.SyncSliceAsync("alpha", Slice, dryRun: false);
        Assert.Equal("merged", sync.Answer);
        Assert.Contains("resolver settled fix.txt", sync.Why);
        Assert.Equal(sliceTip, Lines("rev-list", "--parents", "-n", "1", Slice).Single().Split(' ')[1]);
        Assert.Equal(Lines("show", "main:fix.txt"), Lines("show", $"{Slice}:fix.txt"));
        Assert.DoesNotContain(Lines("worktree", "list"), l => l.Contains("whetstone-slice-sync"));
    }

    // ---- the Classroom and analysis read a slice ticket's own change -----------------------------

    [Fact]
    public async Task A_slice_tickets_changed_files_and_diff_are_its_own_not_the_whole_slices()
    {
        await LayOutAsync();
        var head = Rev("t-13");

        var (own, error) = await _svc.ChangedFilesAsync("alpha", head, branch: "t-13");
        Assert.Null(error);
        Assert.Equal(new[] { "mine.txt" }, own.Select(f => f.Path));
        var earlier = await _svc.FileDiffAsync("alpha", head, "cards.txt", branch: "t-13");
        Assert.Null(earlier.Error);
        Assert.Equal("", earlier.Patch);
        Assert.Equal(1, (await _svc.FileDiffAsync("alpha", head, "mine.txt", branch: "t-13")).Added);

        // Named by no ticket, or on a project without the switch, it is measured against the base as before.
        var (whole, _) = await _svc.ChangedFilesAsync("alpha", head);
        Assert.Equal(new[] { "cards.txt", "mine.txt" }, whole.Select(f => f.Path).OrderBy(p => p));
        var (bravo, _) = await _svc.ChangedFilesAsync("bravo", Rev("b-13"), branch: "b-13");
        Assert.Equal(new[] { "cards.txt", "mine.txt" }, bravo.Select(f => f.Path).OrderBy(p => p));
    }

    [Fact]
    public void The_analysis_run_is_told_to_read_against_the_tickets_base()
    {
        var cfg = _registry.GetProjectConfig("alpha");
        var t = new Ticket { ProjectKey = "alpha", Title = "Cards", Branch = "t-13", PlanUnit = 13 };
        var empty = DescriptionSegmenter.Compose(new[] { new DescriptionSegmenter.Source(null, null, null) });
        var slice = AnalysisRunner.BuildPrompt(t, cfg, AnalysisLens.Full, _hub, "Cards", "d", empty, Array.Empty<Whetstone.Services.Tracker.IssueRef>(), Slice);
        Assert.Contains($"git log --oneline {Slice}..HEAD", slice);
        var plain = AnalysisRunner.BuildPrompt(t, cfg, AnalysisLens.Full, _hub, "Cards", "d", empty, Array.Empty<Whetstone.Services.Tracker.IssueRef>());
        Assert.Contains("git log --oneline main..HEAD", plain);
    }

    // ---- piece 3: release ----------------------------------------------------------------------

    [Fact]
    public async Task Releasing_merges_the_slice_into_the_local_base_with_the_conventions_and_drops_its_branch()
    {
        await LayOutAsync();
        var originBefore = Rev("origin/main");
        var prep = await _svc.PrepareSliceReleaseAsync("alpha", Slice);
        Assert.Null(prep.Refusal);
        // Brought up to the base first, so the release's tree is the slice's own.
        Assert.Equal(0, Run(_hub, "merge-base", "--is-ancestor", "main", Slice).Code);
        Assert.Equal(Rev(Slice), prep.SliceTip);
        Assert.Contains("cards.txt", prep.Files);
        Assert.DoesNotContain("fix.txt", prep.Files);
        var baseTip = Rev("main");

        var (sha, no) = await _svc.ReleaseSliceAsync("alpha", Slice, prep.SliceTip!, "Slice 13: cards\n\nReleases it.",
            "docs/conventions.md", new[] { "- Prices are whole cents. (#4)" });
        Assert.Null(no);
        Assert.Equal(sha, Rev("main"));
        Assert.Equal(new[] { baseTip, prep.SliceTip! }, Lines("rev-list", "--parents", "-n", "1", "main").Single().Split(' ').Skip(1));
        Assert.Equal("Slice 13: cards", Lines("log", "-1", "--format=%s", "main").Single());
        // The conventions line is in the release's own commit, and the hub's files and index agree with it.
        var conventions = string.Join("\n", Lines("show", "main:docs/conventions.md"));
        Assert.StartsWith("# Conventions", conventions);
        Assert.EndsWith("- Prices are whole cents. (#4)", conventions);
        Assert.Equal(new[] { "docs/conventions.md" }, Lines("diff", "--name-only", $"{prep.SliceTip}", "main"));
        Assert.True(File.Exists(Path.Combine(_hub, "cards.txt")));
        Assert.True(File.Exists(Path.Combine(_hub, "docs", "conventions.md")));
        Assert.Empty(Lines("status", "--porcelain"));
        Assert.NotEqual(0, Run(_hub, "rev-parse", "--verify", "--quiet", $"refs/heads/{Slice}").Code);
        // Never pushed.
        Assert.Equal(originBefore, Rev("origin/main"));
    }

    [Fact]
    public async Task A_second_release_appends_to_the_conventions_the_first_wrote()
    {
        await LayOutAsync();
        Directory.CreateDirectory(Path.Combine(_hub, "docs"));
        Commit(_hub, "docs/conventions.md", "- Everything is local. (#1)");
        var prep = await _svc.PrepareSliceReleaseAsync("alpha", Slice);
        var (_, no) = await _svc.ReleaseSliceAsync("alpha", Slice, prep.SliceTip!, "Slice 13: cards", "docs/conventions.md",
            new[] { "- Prices are whole cents. (#4)" });
        Assert.Null(no);
        Assert.Equal(new[] { "- Everything is local. (#1)", "- Prices are whole cents. (#4)" }, Lines("show", "main:docs/conventions.md"));
    }

    [Fact]
    public async Task A_base_or_a_slice_that_moves_after_the_checks_is_not_released_over()
    {
        await LayOutAsync();
        var prep = await _svc.PrepareSliceReleaseAsync("alpha", Slice);
        OnBranch(Slice, "late.txt", "S3: landed while the checks ran");
        var (sha, no) = await _svc.ReleaseSliceAsync("alpha", Slice, prep.SliceTip!, "Slice 13: cards", null, Array.Empty<string>());
        Assert.Null(sha);
        Assert.Contains("moved while it was being checked", no);

        prep = await _svc.PrepareSliceReleaseAsync("alpha", Slice);
        Commit(_hub, "late-base.txt", "M2: landed on the base while the checks ran");
        var baseTip = Rev("main");
        (sha, no) = await _svc.ReleaseSliceAsync("alpha", Slice, prep.SliceTip!, "Slice 13: cards", null, Array.Empty<string>());
        Assert.Null(sha);
        Assert.Contains("main moved", no);
        Assert.Equal(baseTip, Rev("main"));
        Assert.Equal(prep.SliceTip, Rev(Slice));
    }

    [Fact]
    public async Task A_hub_off_its_base_or_with_changes_is_not_released_into()
    {
        await LayOutAsync();
        File.AppendAllText(Path.Combine(_hub, "README.md"), "half-done\n");
        var dirty = await _svc.PrepareSliceReleaseAsync("alpha", Slice);
        Assert.Contains("uncommitted change", dirty.Refusal);
        Git(_hub, "checkout", "--", "README.md");

        Git(_hub, "checkout", "-q", "-b", "mine");
        var off = await _svc.PrepareSliceReleaseAsync("alpha", Slice);
        Assert.Contains("not 'main'", off.Refusal);
    }

    // ---- plumbing ----------------------------------------------------------------------------

    private async Task SeedAsync()
    {
        await using var db = new DashboardDbContext(_options);
        db.Tickets.AddRange(
            new Ticket { ProjectKey = "alpha", Title = "Cards", Branch = "t-13", PlanUnit = 13 },
            new Ticket { ProjectKey = "alpha", Title = "A bug on released work", Branch = "t-bug" },
            new Ticket { ProjectKey = "alpha", Title = "Released", Branch = "t-12", PlanUnit = 12 },
            new Ticket { ProjectKey = "bravo", Title = "Cards", Branch = "b-13", PlanUnit = 13 });
        db.StewardUnits.AddRange(
            new StewardUnit { ProjectKey = "alpha", Unit = 13, Branch = Slice, AgreedAt = DateTime.UtcNow },
            new StewardUnit { ProjectKey = "alpha", Unit = 12, Branch = "slice/12-older", AgreedAt = DateTime.UtcNow, ReleasedAt = DateTime.UtcNow },
            new StewardUnit { ProjectKey = "bravo", Unit = 13, Branch = Slice, AgreedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private sealed class DbBases(DbContextOptions<DashboardDbContext> options) : ITicketBases
    {
        public async Task<string?> BaseOfBranchAsync(ProjectConfig cfg, string branch, CancellationToken ct = default)
        {
            await using var db = new DashboardDbContext(options);
            return await SliceBranches.ForBranchAsync(db, cfg, branch, ct);
        }
    }

    /// <summary>A commit on <paramref name="branch"/> without moving the hub off main, as a merge into it would land.</summary>
    private void OnBranch(string branch, string file, string subject)
    {
        var wt = Path.Combine(_root, "wt-" + Guid.NewGuid().ToString("N")[..6]);
        Git(_hub, "worktree", "add", "-q", wt, branch);
        Commit(wt, file, subject);
        Git(_hub, "worktree", "remove", "--force", wt);
    }

    private string Rev(string rev) => Lines("rev-parse", rev).Single();

    private string[] Lines(params string[] args)
    {
        var (code, output) = Run(_hub, args);
        Assert.Equal(0, code);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static void WriteProject(string data, string key, string json)
    {
        Directory.CreateDirectory(Path.Combine(data, "projects", key));
        File.WriteAllText(Path.Combine(data, "projects", key, "project.defaults.json"), json);
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static void Commit(string dir, string file, string subject)
    {
        File.AppendAllText(Path.Combine(dir, file), subject + "\n");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", subject);
    }

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
