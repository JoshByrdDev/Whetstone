using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Mcp;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The taste file and its fold (docs/discovery-track-plan.md, step 4): what is waiting to be folded
/// and what teaches nothing, when a fold is due, what the fold is told and what of its answer lands,
/// the owner's section no fold may touch, the commit in the project's own folder, and the version
/// every Sift and Recheck run records having read.
/// </summary>
public class DiscoveryTasteTests : IDisposable
{
    private readonly string _root;
    private readonly string _project;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly DashboardPaths _paths;
    private readonly ProjectRegistry _registry;

    public DiscoveryTasteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-taste-" + Guid.NewGuid().ToString("N")[..8]);
        var data = Path.Combine(_root, "data");
        var repo = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(repo);
        _project = Path.Combine(data, "projects", "alpha");
        Directory.CreateDirectory(_project);
        File.WriteAllText(Path.Combine(_project, "project.defaults.json"),
            $$"""{ "displayName": "Alpha", "projectPath": {{JsonSerializer.Serialize(repo)}}, "baseBranch": "main", "discovery": { "siteUrl": "http://localhost:53590/", "tasteFoldEvery": 2 } }""");
        File.WriteAllText(Path.Combine(data, "config.defaults.json"), """{ "baseBranch": "main" }""");
        _paths = new DashboardPaths(data);
        _registry = new ProjectRegistry(_paths);

        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (DashboardDbContext Db, TicketService Tickets, DiscoveryService Discovery, DiscoveryTaste Taste) New()
    {
        var db = new DashboardDbContext(_options);
        var tickets = new TicketService(db, registry: _registry);
        return (db, tickets, new DiscoveryService(db, tickets, _registry, _paths), new DiscoveryTaste(db, _registry, _paths));
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    private void MakeProjectRepo() => Assert.Null(ProjectRepo.Initialize(_project).Error);

    private async Task SetPhase(int id, TicketPhase phase)
    {
        await using var db = new DashboardDbContext(_options);
        await db.Tickets.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Phase, phase));
    }

    /// <summary>A discovery waiting for its verdict, with these proposals as Sift drafted them.</summary>
    private async Task<(int Ticket, int[] Proposals)> Discovery(params string[] titles)
    {
        var (db, tickets, _, _) = New();
        var t = await tickets.CreateAsync("alpha", "Plan a month of events", "Plan your month.", null, null, TicketPhase.NeedsVerdict, track: TicketTrack.Discovery);
        var ps = titles.Select((title, i) => new DiscoveryProposal
        {
            TicketId = t.Id, Rank = i + 1, Kind = ProposalKind.Ticket, Title = title, Body = $"Body of {title}", Why = "It matters",
            DraftKind = ProposalKind.Ticket, DraftTitle = title, DraftBody = $"Body of {title}", DraftWhy = "It matters",
        }).ToList();
        db.DiscoveryProposals.AddRange(ps);
        await db.SaveChangesAsync();
        return (t.Id, ps.Select(p => p.Id).ToArray());
    }

    private async Task Decide(int proposal, DiscoveryService.VerdictInput v)
    {
        var (_, _, disc, _) = New();
        await disc.DecideAsync(proposal, v);
    }

    /// <summary>The usual seeded verdict: one edited approval that is new to the owner, one generic rejection with a note, one "not now".</summary>
    private async Task<int> Verdicts()
    {
        var (t, p) = await Discovery("A month view of events", "Add search", "Weekly digest email");
        await Decide(p[0], new(Status: "Approved", Title: "A month view of events near you", NewToMe: true));
        await Decide(p[1], new(Status: "Rejected", RejectReason: "Generic", RejectNote: "Every site could have search"));
        await Decide(p[2], new(Status: "Rejected", RejectReason: "NotNow"));
        await SetPhase(t, TicketPhase.NeedsReview);
        return t;
    }

    private const string Learned = "- Wants proposals at the scale of a real month, not the seed's (1 approved, new to the owner)\n- Not features any site could have, like search (1 rejected as generic)";

    // ---- nothing yet ------------------------------------------------------------------------

    [Fact]
    public async Task With_no_verdicts_there_is_nothing_to_fold_and_it_says_so()
    {
        var (_, _, _, taste) = New();
        var s = await taste.StatusAsync("alpha");
        Assert.False(s.Exists);
        Assert.Null(s.Current);
        Assert.Equal(0, s.VerdictsEver);
        Assert.Equal(0, s.Fold.Verdicts);
        Assert.False(s.Fold.Due);
        Assert.Equal(2, s.Fold.Every);
        var b = await taste.BeginFoldAsync("alpha");
        Assert.False(b.Due);
        Assert.Equal("No verdict waits to be folded.", b.Reason);
        Assert.False(File.Exists(_paths.DiscoveryTasteFile("alpha")));
    }

    [Fact]
    public async Task Asking_for_a_fold_with_nothing_to_fold_is_still_not_due()
    {
        var (_, _, _, taste) = New();
        await taste.RequestFoldAsync("alpha", true);
        Assert.False((await taste.BeginFoldAsync("alpha")).Due);
    }

    [Fact]
    public async Task A_run_with_no_taste_file_is_told_plainly_and_the_read_is_still_recorded()
    {
        var (_, tickets, _, taste) = New();
        var t = await tickets.CreateAsync("alpha", "d", "d", null, null, TicketPhase.Sifting, track: TicketTrack.Discovery);
        var r = await taste.ReadForRunAsync(t.Id, "alpha", TicketPhase.Sifting, 0, runId: 41);
        Assert.Null(r.Version);
        Assert.Equal("", r.Text);
        Assert.Contains("no taste file yet", r.Note);
        await using var db = new DashboardDbContext(_options);
        var read = Assert.Single(db.DiscoveryTasteReads);
        Assert.Equal(41, read.RunId);
        Assert.Null(read.VersionId);
    }

    [Fact]
    public async Task An_unknown_project_is_refused_before_its_key_becomes_a_path()
    {
        var (_, _, _, taste) = New();
        await Assert.ThrowsAsync<DiscoveryTaste.Refused>(() => taste.StatusAsync("../alpha"));
        await Assert.ThrowsAsync<DiscoveryTaste.Refused>(() => taste.OwnerEditAsync("bravo", "x"));
    }

    // ---- what waits, and when it is due --------------------------------------------------------

    [Fact]
    public async Task An_open_verdict_waits_and_a_closed_one_counts_only_what_teaches()
    {
        var (t, p) = await Discovery("A month view", "Add search", "Weekly digest");
        await Decide(p[0], new(Status: "Approved"));
        await Decide(p[1], new(Status: "Rejected", RejectReason: "Generic"));
        await Decide(p[2], new(Status: "Snoozed"));
        var (_, _, _, taste) = New();
        // Still at the verdict: a change of mind is a click away, so none of it is folded yet.
        Assert.Empty(await taste.PendingAsync("alpha"));

        await SetPhase(t, TicketPhase.NeedsReview);
        var pending = await taste.PendingAsync("alpha");
        Assert.Equal(3, pending.Count);
        Assert.Equal(2, pending.Count(x => x.Teaches));
        var s = await taste.StatusAsync("alpha");
        Assert.Equal(2, s.Fold.Verdicts);
        Assert.Equal(1, s.Fold.TeachNothing);
        Assert.True(s.Fold.Due);
    }

    [Fact]
    public async Task Below_the_count_it_waits_unless_the_owner_asks()
    {
        var (t, p) = await Discovery("A month view");
        await Decide(p[0], new(Status: "Approved"));
        await SetPhase(t, TicketPhase.NeedsReview);
        var (_, _, _, taste) = New();
        var b = await taste.BeginFoldAsync("alpha");
        Assert.False(b.Due);
        Assert.Equal("1 of 2 verdicts toward the next fold.", b.Reason);
        await taste.RequestFoldAsync("alpha", true);
        Assert.True((await taste.BeginFoldAsync("alpha")).Due);
    }

    [Fact]
    public async Task Already_decided_is_listed_for_the_owner_and_not_learned_from()
    {
        var (t, p) = await Discovery("Show past events", "A month view");
        await Decide(p[0], new(Status: "Rejected", RejectReason: "AlreadyDecided", RejectNote: "We decided past events stay hidden"));
        await Decide(p[1], new(Status: "Approved"));
        await SetPhase(t, TicketPhase.NeedsReview);
        var (_, _, _, taste) = New();
        var s = await taste.StatusAsync("alpha");
        var missing = Assert.Single(s.AlreadyDecided);
        Assert.Equal(p[0], missing.ProposalId);
        Assert.Equal(1, s.Fold.Verdicts);
        await taste.RequestFoldAsync("alpha", true);
        var b = await taste.BeginFoldAsync("alpha");
        Assert.Contains("REJECTED as already decided", b.Prompt);
        Assert.Contains("(teaches nothing)", b.Prompt);
    }

    // ---- the fold --------------------------------------------------------------------------------

    [Fact]
    public async Task The_fold_is_told_each_verdict_in_words()
    {
        await Verdicts();
        var (_, _, _, taste) = New();
        var b = await taste.BeginFoldAsync("alpha", rodeOn: 9);
        Assert.True(b.Due);
        Assert.Equal(2, b.Verdicts);
        Assert.NotNull(b.Claim);
        var prompt = b.Prompt!;
        Assert.Contains("Title: A month view of events near you", prompt);
        Assert.Contains("APPROVED, EDITED before approval", prompt);
        Assert.Contains("the draft's title was \"A month view of events\"", prompt);
        Assert.Contains("NEW TO ME", prompt);
        Assert.Contains("REJECTED as GENERIC - the owner's note: \"Every site could have search\"", prompt);
        Assert.Contains("REJECTED as not now (teaches nothing)", prompt);
        Assert.Contains("on the direction \"Plan a month of events\"", prompt);
        Assert.Contains("(empty - this is the first fold)", prompt);
        Assert.Contains(DiscoveryTaste.SuggestionsMarker, prompt);
    }

    [Fact]
    public async Task A_fold_writes_the_file_commits_it_in_the_project_folder_and_records_its_version()
    {
        MakeProjectRepo();
        await Verdicts();
        var (_, _, _, taste) = New();
        var b = await taste.BeginFoldAsync("alpha", rodeOn: 9);
        var v = await taste.FinishFoldAsync("alpha", b.Claim!, Learned, null, rodeOn: 9);

        Assert.Equal(1, v.Number);
        Assert.Equal(TasteSource.Fold, v.Source);
        Assert.Equal(2, v.Verdicts);
        Assert.Equal(b.Events, v.EventIds.Count);
        Assert.Equal(9, v.RodeOnTicketId);
        var file = await File.ReadAllTextAsync(_paths.DiscoveryTasteFile("alpha"));
        Assert.Contains(DiscoveryTaste.OwnerHeading, file);
        Assert.Contains("## Learned\n\n- Wants proposals at the scale", file);
        Assert.Equal(DiscoveryTaste.HashOf(file), v.Hash);

        // Committed in the project's own repo, that file alone, with a message a revert can be chosen by.
        Assert.NotNull(v.Commit);
        Assert.Equal(v.Commit, Git(_project, "rev-parse", "HEAD"));
        Assert.Equal("discovery/taste.md", Git(_project, "show", "--name-only", "--format=", "HEAD"));
        Assert.StartsWith("Taste fold: 2 verdicts from discovery #", Git(_project, "log", "-1", "--format=%s"));

        // Folded is folded: nothing waits, nothing is due, and the panel says what happened.
        var (_, _, _, again) = New();
        Assert.Empty(await again.PendingAsync("alpha"));
        var s = await again.StatusAsync("alpha");
        Assert.False(s.Fold.Due);
        Assert.Equal("Folded 2 verdicts into version 1.", s.Fold.LastNote);
        Assert.Equal(1, s.Current!.Number);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repo_still_gets_the_file_and_says_it_is_not_committed()
    {
        await Verdicts();
        var (_, _, _, taste) = New();
        var b = await taste.BeginFoldAsync("alpha");
        var v = await taste.FinishFoldAsync("alpha", b.Claim!, Learned, null);
        Assert.Null(v.Commit);
        Assert.Contains("not a git repo", v.CommitNote);
        Assert.True(File.Exists(_paths.DiscoveryTasteFile("alpha")));
    }

    [Fact]
    public async Task Two_runs_finishing_together_fold_once()
    {
        await Verdicts();
        var (_, _, _, taste) = New();
        var first = await taste.BeginFoldAsync("alpha");
        var second = await taste.BeginFoldAsync("alpha");
        Assert.True(first.Due);
        Assert.False(second.Due);
        Assert.StartsWith("A fold has been under way", second.Reason);
        await Assert.ThrowsAsync<DiscoveryTaste.Refused>(() => taste.FinishFoldAsync("alpha", "not-the-claim", Learned, null));
    }

    [Fact]
    public async Task A_refused_answer_folds_nothing_and_lets_the_next_run_try()
    {
        await Verdicts();
        var (_, _, _, taste) = New();
        var b = await taste.BeginFoldAsync("alpha");
        var e = await Assert.ThrowsAsync<DiscoveryTaste.Refused>(() =>
            taste.FinishFoldAsync("alpha", b.Claim!, "## The owner's\n\n- Rewritten by the fold", null));
        Assert.Contains("headings", e.Message);
        Assert.False(File.Exists(_paths.DiscoveryTasteFile("alpha")));
        var (_, _, _, again) = New();
        var s = await again.StatusAsync("alpha");
        Assert.StartsWith("Not folded:", s.Fold.LastNote);
        Assert.True(s.Fold.Due);
        Assert.True((await again.BeginFoldAsync("alpha")).Due);
    }

    [Fact]
    public async Task A_run_that_failed_reports_it_and_folds_nothing()
    {
        await Verdicts();
        var (_, _, _, taste) = New();
        var b = await taste.BeginFoldAsync("alpha");
        await Assert.ThrowsAsync<DiscoveryTaste.Refused>(() => taste.FinishFoldAsync("alpha", b.Claim!, null, "timed out"));
        var s = await taste.StatusAsync("alpha");
        Assert.Contains("timed out", s.Fold.LastNote);
        Assert.Empty(s.Versions);
    }

    [Fact]
    public async Task The_owner_s_section_is_kept_word_for_word_and_a_suggestion_is_only_shown()
    {
        var (_, _, _, taste) = New();
        await taste.OwnerEditAsync("alpha", DiscoveryTaste.Compose("- Never propose dark mode.", ""));
        await Verdicts();
        var b = await taste.BeginFoldAsync("alpha");
        Assert.Contains("# The owner's section (read only)\n\n- Never propose dark mode.", b.Prompt!.Replace("\r\n", "\n"));
        var v = await taste.FinishFoldAsync("alpha", b.Claim!,
            Learned + "\n\nSUGGESTIONS FOR THE OWNER:\n- Dark mode came up twice as new to you; reconsider the rule.", null);
        var (owner, learned) = DiscoveryTaste.Split(v.Text);
        Assert.Equal("- Never propose dark mode.", owner);
        Assert.Equal(Learned, learned);
        Assert.Equal("- Dark mode came up twice as new to you; reconsider the rule.", v.Suggestions);
        Assert.Equal(2, v.Number);
        Assert.Equal(v.Suggestions, (await taste.StatusAsync("alpha")).Suggestions);
    }

    [Fact]
    public async Task A_file_changed_while_the_fold_ran_is_not_written_over()
    {
        await Verdicts();
        var (_, _, _, taste) = New();
        var b = await taste.BeginFoldAsync("alpha");
        await taste.OwnerEditAsync("alpha", DiscoveryTaste.Compose("- Mine.", ""));
        var e = await Assert.ThrowsAsync<DiscoveryTaste.Refused>(() => taste.FinishFoldAsync("alpha", b.Claim!, Learned, null));
        Assert.Contains("changed while the fold ran", e.Message);
        Assert.DoesNotContain("Wants proposals", await File.ReadAllTextAsync(_paths.DiscoveryTasteFile("alpha")));
    }

    [Theory]
    [InlineData("", "nothing")]
    [InlineData("Sorry, I cannot help with that.", "no rule")]
    [InlineData("# Taste\n- a rule", "headings")]
    public void An_answer_that_is_not_a_learned_section_is_refused(string reply, string why)
    {
        var e = Assert.Throws<DiscoveryTaste.Refused>(() => DiscoveryTaste.ParseReply(reply));
        Assert.Contains(why, e.Message);
    }

    [Fact]
    public void Learned_is_kept_short()
    {
        var long_ = string.Join("\n", Enumerable.Range(1, 41).Select(i => $"- rule {i}"));
        Assert.Contains("kept to 40", Assert.Throws<DiscoveryTaste.Refused>(() => DiscoveryTaste.ParseReply(long_)).Message);
    }

    [Fact]
    public void An_answer_is_read_through_its_fence_heading_and_an_empty_suggestion()
    {
        var (learned, suggestions) = DiscoveryTaste.ParseReply("```markdown\n## Learned\n- a rule\n\nSuggestions for the owner:\n- none\n```");
        Assert.Equal("- a rule", learned);
        Assert.Null(suggestions);
    }

    [Fact]
    public void A_hand_written_file_without_the_headings_is_all_the_owner_s()
    {
        var (owner, learned) = DiscoveryTaste.Split("Things I like:\n- small, concrete changes\n");
        Assert.Equal("Things I like:\n- small, concrete changes", owner);
        Assert.Equal("", learned);
        Assert.Equal(("- o", "- l"), DiscoveryTaste.Split(DiscoveryTaste.Compose("- o", "- l")));
    }

    // ---- versions -----------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_made_in_the_folder_becomes_a_version_the_next_time_it_is_read()
    {
        MakeProjectRepo();
        var (_, _, _, taste) = New();
        var first = await taste.OwnerEditAsync("alpha", DiscoveryTaste.Compose("- Mine.", ""));
        Assert.Equal(TasteSource.Owner, first.Source);
        Assert.NotNull(first.Commit);
        await File.WriteAllTextAsync(_paths.DiscoveryTasteFile("alpha"), DiscoveryTaste.Compose("- Mine, edited by hand.", ""));
        var now = await taste.CurrentAsync("alpha");
        Assert.Equal(2, now!.Number);
        Assert.Equal(TasteSource.Outside, now.Source);
        Assert.Null(now.Commit); // edited, not committed
        // The same file read again is the same version.
        Assert.Equal(now.Id, (await taste.CurrentAsync("alpha"))!.Id);
    }

    [Fact]
    public async Task A_revert_puts_an_earlier_version_back_as_a_new_one()
    {
        MakeProjectRepo();
        var (_, _, _, taste) = New();
        var one = await taste.OwnerEditAsync("alpha", DiscoveryTaste.Compose("- One.", ""));
        await taste.OwnerEditAsync("alpha", DiscoveryTaste.Compose("- Two.", ""));
        var back = await taste.RevertAsync("alpha", one.Id);
        Assert.Equal(3, back.Number);
        Assert.Equal(TasteSource.Revert, back.Source);
        Assert.Equal(one.Id, back.RevertOf);
        Assert.Equal(one.Hash, back.Hash);
        Assert.Equal("Taste: back to version 1, by the owner", Git(_project, "log", "-1", "--format=%s"));
        await Assert.ThrowsAsync<DiscoveryTaste.Refused>(() => taste.RevertAsync("alpha", one.Id));
    }

    [Fact]
    public async Task The_same_text_written_twice_is_one_version()
    {
        var (_, _, _, taste) = New();
        var a = await taste.OwnerEditAsync("alpha", "- x\n");
        var b = await taste.OwnerEditAsync("alpha", "- x\r\n\r\n");
        Assert.Equal(a.Id, b.Id);
    }

    // ---- what a run reads -----------------------------------------------------------------------

    [Fact]
    public async Task A_sift_is_handed_the_taste_and_its_read_is_recorded_once_per_run()
    {
        var (_, _, _, taste) = New();
        var v = await taste.OwnerEditAsync("alpha", DiscoveryTaste.Compose("- Never dark mode.", ""));
        var (_, tickets, disc, _) = New();
        var t = await tickets.CreateAsync("alpha", "d", "d", null, null, TicketPhase.Sifting, track: TicketTrack.Discovery);

        var (db, tk, dsc, ts) = New();
        var got = JsonSerializer.SerializeToElement(await DiscoveryTools.GetDiscovery(dsc, ts, tk, _paths, _registry, t.Id));
        var tasteJson = got.GetProperty("taste");
        Assert.Equal(1, tasteJson.GetProperty("version").GetInt32());
        Assert.Contains("Never dark mode", tasteJson.GetProperty("text").GetString());
        await DiscoveryTools.GetDiscovery(dsc, ts, tk, _paths, _registry, t.Id);
        var read = Assert.Single(db.DiscoveryTasteReads.AsNoTracking());
        Assert.Equal(v.Id, read.VersionId);
        Assert.Equal(1, read.VersionNumber);
        Assert.Equal("Sifting", read.Phase);
    }

    [Fact]
    public async Task A_scout_is_not_handed_the_taste()
    {
        var (_, _, _, taste) = New();
        await taste.OwnerEditAsync("alpha", DiscoveryTaste.Compose("- Never dark mode.", ""));
        var (db, tk, dsc, ts) = New();
        var t = await tk.CreateAsync("alpha", "d", "d", null, null, TicketPhase.Scouting, track: TicketTrack.Discovery);
        var got = JsonSerializer.SerializeToElement(await DiscoveryTools.GetDiscovery(dsc, ts, tk, _paths, _registry, t.Id));
        Assert.Equal(JsonValueKind.Null, got.GetProperty("taste").ValueKind);
        Assert.Empty(db.DiscoveryTasteReads.AsNoTracking());
    }

    [Fact]
    public async Task A_fold_between_two_runs_shows_as_two_versions_read()
    {
        var (_, _, _, taste) = New();
        var (_, tk, _, _) = New();
        var t = await tk.CreateAsync("alpha", "d", "d", null, null, TicketPhase.Rechecking, track: TicketTrack.Discovery);
        await taste.OwnerEditAsync("alpha", "- one\n");
        await taste.ReadForRunAsync(t.Id, "alpha", TicketPhase.Rechecking, 1, runId: 5);
        await taste.OwnerEditAsync("alpha", "- two\n");
        await taste.ReadForRunAsync(t.Id, "alpha", TicketPhase.Rechecking, 2, runId: 6);
        await using var db = new DashboardDbContext(_options);
        Assert.Equal(new int?[] { 1, 2 }, db.DiscoveryTasteReads.OrderBy(r => r.Id).Select(r => r.VersionNumber).ToArray());
    }

    // ---- configuration ----------------------------------------------------------------------------

    [Theory]
    [InlineData("{}", 10)]
    [InlineData("""{ "discovery": { "tasteFoldEvery": 25 } }""", 25)]
    [InlineData("""{ "discovery": { "tasteFoldEvery": 0 } }""", 1)]
    [InlineData("""{ "discovery": { "tasteFoldEvery": 1000 } }""", 100)]
    public void The_count_is_the_project_s_and_ten_by_default(string json, int every)
    {
        var cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Equal(every, DiscoveryConfig.From(cfg).TasteFoldEvery);
    }
}
