using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The capture retention pass over a discovery's media (docs/discovery-track-plan.md, "What is kept,
/// and for how long"): a recording 7 days after the verdict it fed, a kept clip 90 days after the
/// discovery is Done, one still per proposal beyond that, the project's budget behind all of it -
/// and never the record, an open discovery under the budget, or what the owner pinned.
/// </summary>
public class CaptureRetentionDiscoveryTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CaptureRetention.Policy Policy = new(QaCapturesBudgetMb: 0, QaShotsBudgetMb: 0);

    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly string _root;
    private readonly DashboardPaths _paths;

    public CaptureRetentionDiscoveryTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
        _root = Path.Combine(Path.GetTempPath(), "ccd-discret-" + Guid.NewGuid().ToString("N")[..8]);
        _paths = new DashboardPaths(_root);
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Seeded(int Id, int Approved, int Rejected, int Recording, int ShotA, int ShotB, int ShotC);

    /// <summary>
    /// A discovery in <paramref name="phase"/>: one recording whose verdict was <paramref name="verdictDaysAgo"/>
    /// days ago, an approved proposal citing shots A and B (with a clip), a rejected one citing C (its still),
    /// and - when Done - its arrival there <paramref name="doneDaysAgo"/> days ago.
    /// </summary>
    private async Task<Seeded> Seed(TicketPhase phase, double? verdictDaysAgo, double? doneDaysAgo = null, string project = "alpha", int bytes = 100)
    {
        await using var db = new DashboardDbContext(_options);
        var t = new Ticket { ProjectKey = project, Title = "d", Phase = phase, Track = TicketTrack.Discovery, UpdatedAt = Now };
        db.Tickets.Add(t);
        await db.SaveChangesAsync();
        if (doneDaysAgo is { } d)
            db.PhaseTransitions.Add(new PhaseTransition { TicketId = t.Id, ProjectKey = project, From = TicketPhase.NeedsReview, To = TicketPhase.Done, At = Now.AddDays(-d) });
        var oa = new DiscoveryObservation { TicketId = t.Id, Title = "a", Saw = "s", ShotPath = "oa.png" };
        var ob = new DiscoveryObservation { TicketId = t.Id, Title = "b", Saw = "s", ShotPath = "ob.png" };
        var oc = new DiscoveryObservation { TicketId = t.Id, Title = "c", Saw = "s", ShotPath = "oc.png" };
        db.DiscoveryObservations.AddRange(oa, ob, oc);
        var rec = new DiscoveryRecording { TicketId = t.Id, File = "rec.webm", Source = "runs/x/v.webm", VerdictAt = verdictDaysAgo is { } v ? Now.AddDays(-v) : null };
        db.DiscoveryRecordings.Add(rec);
        await db.SaveChangesAsync();
        var yes = new DiscoveryProposal { TicketId = t.Id, Title = "yes", Body = "b", Status = ProposalStatus.Filed, ObservationIds = new() { oa.Id, ob.Id }, ClipFile = "p-yes.mp4" };
        var no = new DiscoveryProposal { TicketId = t.Id, Title = "no", Body = "b", Status = ProposalStatus.Rejected, ObservationIds = new() { oc.Id }, ClipPrunedAt = Now.AddDays(-30) };
        db.DiscoveryProposals.AddRange(yes, no);
        await db.SaveChangesAsync();
        var dir = _paths.DiscoveryTicketDir(t.Id);
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "oa.png", "ob.png", "oc.png", "p-yes.mp4" }) File.WriteAllBytes(Path.Combine(dir, f), new byte[bytes]);
        File.WriteAllBytes(Path.Combine(dir, "rec.webm"), new byte[bytes * 10]);
        return new Seeded(t.Id, yes.Id, no.Id, rec.Id, oa.Id, ob.Id, oc.Id);
    }

    private async Task<IReadOnlyList<CaptureRetention.Pruned>> Prune(long budgetMb = 0)
    {
        await using var db = new DashboardDbContext(_options);
        return await CaptureRetention.PruneAsync(db, _paths, Policy, Now, NullLogger.Instance, discoveryBudgetMb: _ => budgetMb);
    }

    private bool Has(int id, string file) => File.Exists(Path.Combine(_paths.DiscoveryTicketDir(id), file));

    [Fact]
    public async Task A_recording_goes_seven_days_after_its_verdict_and_its_row_stays()
    {
        var old = await Seed(TicketPhase.AwaitingWork, verdictDaysAgo: 8);
        var recent = await Seed(TicketPhase.AwaitingWork, verdictDaysAgo: 6);
        var pruned = await Prune();
        Assert.False(Has(old.Id, "rec.webm"));
        Assert.True(Has(recent.Id, "rec.webm"));
        Assert.Contains(pruned, p => p.Store == CaptureRetention.Store.Discovery && p.TicketId == old.Id && p.Reason == "recording-age");
        await using var db = new DashboardDbContext(_options);
        Assert.Equal(Now, (await db.DiscoveryRecordings.SingleAsync(r => r.Id == old.Recording)).PrunedAt);
        // The evidence that outlives it stays: an open discovery's clips and shots are not this rule's.
        Assert.True(Has(old.Id, "p-yes.mp4"));
    }

    [Fact]
    public async Task A_recording_with_no_verdict_yet_is_kept_however_old()
    {
        var s = await Seed(TicketPhase.NeedsVerdict, verdictDaysAgo: null);
        await Prune();
        Assert.True(Has(s.Id, "rec.webm"));
    }

    [Fact]
    public async Task A_recording_that_fed_no_verdict_counts_from_the_discoverys_done()
    {
        var s = await Seed(TicketPhase.Done, verdictDaysAgo: null, doneDaysAgo: 10);
        await Prune();
        Assert.False(Has(s.Id, "rec.webm"));
    }

    [Fact]
    public async Task Approved_clips_go_ninety_days_after_done_keeping_one_still_per_proposal()
    {
        var young = await Seed(TicketPhase.Done, verdictDaysAgo: 100, doneDaysAgo: 89);
        var old = await Seed(TicketPhase.Done, verdictDaysAgo: 100, doneDaysAgo: 91);
        var pruned = await Prune();
        Assert.True(Has(young.Id, "p-yes.mp4"));
        Assert.True(Has(young.Id, "ob.png"));
        Assert.False(Has(old.Id, "p-yes.mp4"));
        Assert.True(Has(old.Id, "oa.png"));   // the approved proposal's still
        Assert.False(Has(old.Id, "ob.png"));
        Assert.True(Has(old.Id, "oc.png"));   // the rejected one's still, kept at Complete
        Assert.Contains(pruned, p => p.TicketId == old.Id && p.Reason == "clip-age");
        await using var db = new DashboardDbContext(_options);
        var yes = await db.DiscoveryProposals.SingleAsync(p => p.Id == old.Approved);
        Assert.Equal(Now, yes.ClipPrunedAt);
        Assert.Null(yes.ClipFile);
        Assert.Equal(Now, (await db.DiscoveryObservations.SingleAsync(o => o.Id == old.ShotB)).ShotPrunedAt);
        Assert.Equal("yes", yes.Title); // the record is never pruned
    }

    [Fact]
    public async Task A_pinned_proposal_keeps_its_clip_and_shots_past_every_rule()
    {
        var s = await Seed(TicketPhase.Done, verdictDaysAgo: 400, doneDaysAgo: 400);
        await using (var db = new DashboardDbContext(_options))
            await db.DiscoveryProposals.Where(p => p.Id == s.Approved).ExecuteUpdateAsync(x => x.SetProperty(p => p.Pinned, true));
        await Prune(budgetMb: 0);
        Assert.True(Has(s.Id, "p-yes.mp4"));
        Assert.True(Has(s.Id, "ob.png"));
    }

    [Fact]
    public async Task The_budget_takes_the_discovery_done_longest_first_and_never_an_open_one()
    {
        const int big = 300_000; // each discovery about 4.2MB
        var oldest = await Seed(TicketPhase.Done, verdictDaysAgo: 3, doneDaysAgo: 3, bytes: big);
        var newer = await Seed(TicketPhase.Done, verdictDaysAgo: 2, doneDaysAgo: 2, bytes: big);
        var open = await Seed(TicketPhase.NeedsVerdict, verdictDaysAgo: null, bytes: big);
        var other = await Seed(TicketPhase.Done, verdictDaysAgo: 3, doneDaysAgo: 30, project: "bravo", bytes: big);
        var pruned = await Prune(budgetMb: 9);
        Assert.Contains(pruned, p => p.TicketId == oldest.Id && p.Reason == "budget");
        Assert.False(Has(oldest.Id, "rec.webm"));
        Assert.False(Has(oldest.Id, "oc.png")); // the budget takes stills too
        Assert.True(Has(newer.Id, "rec.webm"));
        Assert.True(Has(open.Id, "rec.webm"));
        Assert.True(Has(other.Id, "rec.webm")); // another project's budget, and it is under it
    }

    [Fact]
    public async Task An_open_discovery_alone_over_budget_is_kept()
    {
        var open = await Seed(TicketPhase.NeedsVerdict, verdictDaysAgo: null, bytes: 300_000);
        await Prune(budgetMb: 1);
        Assert.True(Has(open.Id, "rec.webm"));
        Assert.True(Has(open.Id, "p-yes.mp4"));
    }

    [Fact]
    public async Task A_discovery_that_no_longer_exists_is_removed_whole()
    {
        var dir = _paths.DiscoveryTicketDir(4242);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "o1.png"), new byte[5]);
        var pruned = await Prune();
        Assert.False(Directory.Exists(dir));
        Assert.Contains(pruned, p => p.TicketId == 4242 && p.Reason == "orphan" && p.Store == CaptureRetention.Store.Discovery);
    }

    [Fact]
    public async Task A_run_folder_goes_a_day_after_its_run_and_never_while_one_drives()
    {
        var idle = await Seed(TicketPhase.NeedsSifting, verdictDaysAgo: null);
        var driving = await Seed(TicketPhase.Scouting, verdictDaysAgo: null);
        foreach (var id in new[] { idle.Id, driving.Id })
        {
            var run = Path.Combine(_paths.DiscoveryTicketDir(id), DiscoveryMedia.RunsFolder, "20300501-120000");
            Directory.CreateDirectory(run);
            var f = Path.Combine(run, "page.yml");
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, Now.AddDays(-2));
            Directory.SetLastWriteTimeUtc(run, Now.AddDays(-2));
        }
        await Prune();
        Assert.False(Directory.Exists(Path.Combine(_paths.DiscoveryTicketDir(idle.Id), DiscoveryMedia.RunsFolder, "20300501-120000")));
        Assert.True(Directory.Exists(Path.Combine(_paths.DiscoveryTicketDir(driving.Id), DiscoveryMedia.RunsFolder, "20300501-120000")));
    }

    [Fact]
    public void The_discovery_days_are_read_from_the_global_retention_block()
    {
        var cfg = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["retention"] = System.Text.Json.JsonDocument.Parse("""{ "discoveryRecordingDays": 3 }""").RootElement,
        };
        var p = CaptureRetention.Policy.From(cfg);
        Assert.Equal(3, p.DiscoveryRecordingDays);
        Assert.Equal(90, p.DiscoveryClipDays);
    }

    [Fact]
    public void A_projects_budget_defaults_to_two_gigabytes()
    {
        Assert.Equal(2048, DiscoveryConfig.From(new()).StorageBudgetMb);
        var cfg = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["discovery"] = System.Text.Json.JsonDocument.Parse("""{ "storageBudgetMb": 512 }""").RootElement,
        };
        Assert.Equal(512, DiscoveryConfig.From(cfg).StorageBudgetMb);
    }
}
