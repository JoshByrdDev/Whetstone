using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Discovery track (docs/discovery-track-plan.md): its path, its isolation from the other two
/// tracks, the phase each of its tools is refused outside, what each hand-off owes, and the
/// owner's Complete - the one way anything a discovery found becomes a ticket.
/// </summary>
public class DiscoveryTrackTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly DashboardPaths _paths;
    private readonly ProjectRegistry _registry;

    public DiscoveryTrackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-disc-" + Guid.NewGuid().ToString("N")[..8]);
        var data = Path.Combine(_root, "data");
        var repo = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(Path.Combine(data, "projects", "alpha"));
        File.WriteAllText(Path.Combine(data, "projects", "alpha", "project.defaults.json"),
            $$"""{ "displayName": "Alpha", "projectPath": {{System.Text.Json.JsonSerializer.Serialize(repo)}}, "baseBranch": "main", "discovery": { "siteUrl": "http://localhost:53590/", "maxProposals": 2 } }""");
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

    private (DashboardDbContext Db, TicketService Tickets, DiscoveryService Discovery) New()
    {
        var db = new DashboardDbContext(_options);
        var tickets = new TicketService(db);
        return (db, tickets, new DiscoveryService(db, tickets, _registry, _paths));
    }

    private async Task<int> DiscoveryIn(TicketPhase phase)
    {
        var (_, tickets, _) = New();
        var t = await tickets.CreateAsync("alpha", "Plan a month of events", "Plan your month as someone in a nearby town.",
            null, null, phase, track: TicketTrack.Discovery);
        return t.Id;
    }

    private async Task SetPhase(int id, TicketPhase phase)
    {
        await using var db = new DashboardDbContext(_options);
        await db.Tickets.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Phase, phase));
    }

    // ---- the path ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TicketPhase.Backlog, TicketPhase.NeedsCharting)]
    [InlineData(TicketPhase.NeedsCharting, TicketPhase.Charting)]
    [InlineData(TicketPhase.Charting, TicketPhase.NeedsScouting)]
    [InlineData(TicketPhase.NeedsScouting, TicketPhase.Scouting)]
    [InlineData(TicketPhase.Scouting, TicketPhase.NeedsSifting)]
    [InlineData(TicketPhase.NeedsSifting, TicketPhase.Sifting)]
    [InlineData(TicketPhase.Sifting, TicketPhase.NeedsVerdict)]
    [InlineData(TicketPhase.Sifting, TicketPhase.NeedsScouting)]
    [InlineData(TicketPhase.NeedsVerdict, TicketPhase.NeedsReview)]
    [InlineData(TicketPhase.NeedsVerdict, TicketPhase.NeedsSifting)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.Done)]
    [InlineData(TicketPhase.NeedsReview, TicketPhase.NeedsScouting)]
    public void Discovery_path_is_legal(TicketPhase from, TicketPhase to)
        => Assert.True(PhaseMachine.CanTransition(from, to, TicketTrack.Discovery));

    [Theory]
    [InlineData(TicketTrack.Delivery, TicketPhase.Backlog, TicketPhase.NeedsCharting)]
    [InlineData(TicketTrack.Review, TicketPhase.Backlog, TicketPhase.NeedsCharting)]
    [InlineData(TicketTrack.Delivery, TicketPhase.NeedsReview, TicketPhase.NeedsScouting)]
    [InlineData(TicketTrack.Discovery, TicketPhase.Backlog, TicketPhase.NeedsSpeculation)]
    [InlineData(TicketTrack.Discovery, TicketPhase.Backlog, TicketPhase.NeedsPeerReview)]
    [InlineData(TicketTrack.Discovery, TicketPhase.NeedsReview, TicketPhase.NeedsImplementation)]
    [InlineData(TicketTrack.Discovery, TicketPhase.Blocked, TicketPhase.NeedsQa)]
    public void No_track_reaches_another(TicketTrack track, TicketPhase from, TicketPhase to)
        => Assert.False(PhaseMachine.CanTransition(from, to, track));

    [Fact]
    public void A_scout_has_one_hand_off_whatever_it_found()
        => Assert.Equal(new[] { TicketPhase.NeedsSifting, TicketPhase.Blocked },
            PhaseMachine.NextPhases(TicketPhase.Scouting, TicketTrack.Discovery));

    [Theory]
    [InlineData(TicketPhase.Charting, "chart")]
    [InlineData(TicketPhase.Scouting, "scout")]
    [InlineData(TicketPhase.Sifting, "sift")]
    public void Each_working_phase_runs_its_own_skill(TicketPhase phase, string skill)
    {
        Assert.Equal(skill, PhaseMachine.SkillFor(phase));
        Assert.Contains(skill, PhaseMachine.AllSkills);
        Assert.DoesNotContain(skill, PhaseMachine.RequiredSkills);
    }

    [Fact]
    public void Ranks_rise_along_the_track_and_never_outrank_delivery_work_in_hand()
    {
        var path = new[] { TicketPhase.NeedsCharting, TicketPhase.Charting, TicketPhase.NeedsScouting, TicketPhase.Scouting,
            TicketPhase.NeedsSifting, TicketPhase.Sifting, TicketPhase.NeedsVerdict, TicketPhase.NeedsReview };
        for (var i = 1; i < path.Length; i++)
            Assert.True(PhaseMachine.ProgressRank(path[i]) > PhaseMachine.ProgressRank(path[i - 1]), $"{path[i - 1]} -> {path[i]}");
        Assert.True(PhaseMachine.ProgressRank(TicketPhase.Scouting) <= PhaseMachine.ProgressRank(TicketPhase.Implementing));
        Assert.True(PhaseMachine.IsBackward(TicketPhase.Sifting, TicketPhase.NeedsScouting));
    }

    [Fact]
    public void Scout_takes_the_stack_and_chart_and_sift_do_not()
    {
        var cfg = _registry.GetProjectConfig("alpha");
        Assert.Equal(new[] { "alpha:app-stack" }, cfg.ResourcesFor("scout"));
        Assert.Empty(cfg.ResourcesFor("chart"));
        Assert.Empty(cfg.ResourcesFor("sift"));
        Assert.Equal("http://localhost:53590", cfg.Discovery.SiteUrl);
    }

    // ---- each tool, only in its phase -------------------------------------------------------

    [Fact]
    public async Task A_route_is_written_only_while_charting()
    {
        var id = await DiscoveryIn(TicketPhase.Scouting);
        var (_, _, d) = New();
        var e = await Assert.ThrowsAsync<DiscoveryService.Refused>(() =>
            d.SetRouteAsync(id, "A parent in a nearby town", new[] { "plan the month" }, null, null, null, null, null));
        Assert.Contains("Charting", e.Message);
    }

    [Fact]
    public async Task Observations_are_logged_only_while_scouting()
    {
        var id = await DiscoveryIn(TicketPhase.Sifting);
        var (_, _, d) = New();
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.LogObservationAsync(id,
            new DiscoveryService.ObservationInput("Wall", "No calendar", "Cannot see the month", null, 0, null, null, null, null, null, null, null, null, null, null)));
    }

    [Fact]
    public async Task Proposals_are_written_only_while_sifting()
    {
        var id = await DiscoveryIn(TicketPhase.NeedsVerdict);
        var (_, _, d) = New();
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.ProposeAsync(id,
            new DiscoveryService.ProposalInput("Ticket", "A month view", null, "Body", null, null, null, null, new[] { 1 })));
    }

    [Fact]
    public async Task A_delivery_ticket_refuses_every_discovery_tool()
    {
        var (_, tickets, d) = New();
        var t = await tickets.CreateAsync("alpha", "Ours", "x", null, null, TicketPhase.Implementing);
        var e = await Assert.ThrowsAsync<DiscoveryService.Refused>(() =>
            d.SetRouteAsync(t.Id, "p", new[] { "j" }, null, null, null, null, null));
        Assert.Contains("not a discovery", e.Message);
    }

    [Fact]
    public async Task An_observation_keeps_its_screenshot_under_the_data_root()
    {
        var id = await DiscoveryIn(TicketPhase.Scouting);
        var shot = Path.Combine(_root, "shot.png");
        await File.WriteAllBytesAsync(shot, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var (_, _, d) = New();
        var o = await d.LogObservationAsync(id, new DiscoveryService.ObservationInput("rough-edge", "Filter forgotten",
            "The detail page reset the place I chose", "pick a place", 1, "http://localhost:53590/events/x", "390x844",
            "Dev Consumer", "events", shot, 0, 0, 100, 40, "abc123"));
        Assert.Equal(ObservationKind.RoughEdge, o.Kind);
        Assert.Equal($"o{o.Id}.png", o.ShotPath);
        Assert.NotNull(d.ShotDiskPath(id, o.ShotPath!));
        Assert.Null(d.ShotDiskPath(id, "../shot.png"));
    }

    // ---- what each hand-off owes -----------------------------------------------------------

    [Fact]
    public async Task Charting_hands_off_only_with_a_route()
    {
        var id = await DiscoveryIn(TicketPhase.Charting);
        var (_, tickets, d) = New();
        await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            tickets.TransitionAsync(id, TicketPhase.NeedsScouting, "done", TransitionActor.Worker));
        await d.SetRouteAsync(id, "A parent in a nearby town", new[] { "plan the month", "come back a week later" },
            null, new[] { "390x844" }, "Dev Consumer", null, null);
        var (_, t2, _) = New();
        var moved = await t2.TransitionAsync(id, TicketPhase.NeedsScouting, "done", TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsScouting, moved!.Phase);
    }

    [Fact]
    public async Task Scouting_hands_off_only_with_an_observation()
    {
        var id = await DiscoveryIn(TicketPhase.Scouting);
        var (_, tickets, d) = New();
        await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            tickets.TransitionAsync(id, TicketPhase.NeedsSifting, "done", TransitionActor.Worker));
        await d.LogObservationAsync(id, new DiscoveryService.ObservationInput("Idea", "A month view", "Planning a month means opening every event",
            null, 0, null, null, null, null, null, null, null, null, null, null));
        var (_, t2, _) = New();
        Assert.Equal(TicketPhase.NeedsSifting, (await t2.TransitionAsync(id, TicketPhase.NeedsSifting, "done", TransitionActor.Worker))!.Phase);
    }

    [Fact]
    public async Task Sifting_hands_off_with_a_dropped_list_alone()
    {
        var id = await DiscoveryIn(TicketPhase.Scouting);
        var (_, _, d) = New();
        var o = await d.LogObservationAsync(id, new DiscoveryService.ObservationInput("Idea", "Add search", "There is no search",
            null, null, null, null, null, null, null, null, null, null, null, null));
        await SetPhase(id, TicketPhase.Sifting);
        var (_, tickets, d2) = New();
        await Assert.ThrowsAsync<HandoffRequirementException>(() =>
            tickets.TransitionAsync(id, TicketPhase.NeedsVerdict, "done", TransitionActor.Worker));
        await d2.DropIdeaAsync(id, "Add search", "generic - could be said of any site", new[] { o.Id });
        var (_, t2, _) = New();
        Assert.Equal(TicketPhase.NeedsVerdict, (await t2.TransitionAsync(id, TicketPhase.NeedsVerdict, "done", TransitionActor.Worker))!.Phase);
    }

    // ---- the cap, and the owner's verdict ---------------------------------------------------

    private async Task<(int Id, int Obs)> Sifting()
    {
        var id = await DiscoveryIn(TicketPhase.Scouting);
        var (_, _, d) = New();
        var o = await d.LogObservationAsync(id, new DiscoveryService.ObservationInput("Wall", "No month view", "I had to open each event",
            null, 0, null, null, null, null, null, null, null, null, null, null));
        await SetPhase(id, TicketPhase.Sifting);
        return (id, o.Id);
    }

    [Fact]
    public async Task A_sift_keeps_at_most_the_projects_cap()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        await d.ProposeAsync(id, new("Ticket", "One", null, "b", null, null, null, null, new[] { obs }));
        await d.ProposeAsync(id, new("Ticket", "Two", null, "b", null, null, null, null, new[] { obs }));
        var e = await Assert.ThrowsAsync<DiscoveryService.Refused>(() =>
            d.ProposeAsync(id, new("Ticket", "Three", null, "b", null, null, null, null, new[] { obs })));
        Assert.Contains("cap", e.Message);
    }

    [Fact]
    public async Task A_proposal_must_stand_on_an_observation_of_its_own_discovery()
    {
        var (id, _) = await Sifting();
        var (_, _, d) = New();
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() =>
            d.ProposeAsync(id, new("Ticket", "One", null, "b", null, null, null, null, Array.Empty<int>())));
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() =>
            d.ProposeAsync(id, new("Ticket", "One", null, "b", null, null, null, null, new[] { 9999 })));
    }

    [Fact]
    public async Task Complete_waits_for_every_proposal_to_be_decided()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        await d.ProposeAsync(id, new("Ticket", "A month view", null, "b", null, null, null, null, new[] { obs }));
        await SetPhase(id, TicketPhase.NeedsVerdict);
        var (_, _, d2) = New();
        var e = await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d2.CompleteAsync(id));
        Assert.Contains("undecided", e.Message);
    }

    [Fact]
    public async Task A_verdict_is_given_only_while_the_discovery_waits_for_one()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        var p = await d.ProposeAsync(id, new("Ticket", "A month view", null, "b", null, null, null, null, new[] { obs }));
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.DecideAsync(p.Id, new(Status: "Approved")));
    }

    [Fact]
    public async Task Complete_files_approved_proposals_as_linked_delivery_tickets()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        var month = await d.ProposeAsync(id, new("Ticket", "A month view", "Planning a month is the job", "A calendar on /events",
            new[] { "Add a month grid", "Keep the place filter" }, "ticket", "high", "events", new[] { obs }));
        var bug = await d.ProposeAsync(id, new("Bug", "Detail page resets the place", null, "Keep it", null, null, null, null, new[] { obs }));
        await SetPhase(id, TicketPhase.Sifting);
        await d.DropIdeaAsync(id, "Add search", "generic", null);
        await SetPhase(id, TicketPhase.NeedsVerdict);

        var (_, _, v) = New();
        await v.DecideAsync(month.Id, new(Status: "Approved", Title: "A month view of events"));
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => v.DecideAsync(bug.Id, new(Status: "Rejected")));
        await v.DecideAsync(bug.Id, new(Status: "Approved"));
        var r = await v.CompleteAsync(id);

        Assert.Equal(2, r.Filed);
        Assert.Equal("NeedsReview", r.Phase);
        await using var db = new DashboardDbContext(_options);
        var filed = await db.Tickets.Include(t => t.WorkItems).Where(t => t.DiscoveryId == id).OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(2, filed.Count);
        Assert.All(filed, t => Assert.Equal(TicketTrack.Delivery, t.Track));
        Assert.All(filed, t => Assert.Equal(TicketPhase.Backlog, t.Phase));
        Assert.Equal("A month view of events", filed[0].Title);
        Assert.Equal(2, filed[0].WorkItems.Count);
        Assert.False(filed[0].IsBug);
        Assert.True(filed[1].IsBug);
        var props = await db.DiscoveryProposals.Where(p => p.TicketId == id).ToListAsync();
        Assert.Equal(filed[0].Id, props.Single(p => p.Id == month.Id).FiledTicketId);
        Assert.True(props.Single(p => p.Id == month.Id).Edited);
        Assert.Equal(ProposalStatus.Dropped, props.Single(p => p.Title == "Add search").Status);
    }

    [Fact]
    public async Task A_decide_proposal_is_approved_only_with_the_owners_answer()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        var q = await d.ProposeAsync(id, new("Decide", "Should past events stay listed?", null, "The spec hides them", null, null, null, null, new[] { obs }));
        await SetPhase(id, TicketPhase.NeedsVerdict);
        var (_, _, v) = New();
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => v.DecideAsync(q.Id, new(Status: "Approved")));
        await v.DecideAsync(q.Id, new(Status: "Approved", Answer: "Keep them for a week"));
        var r = await v.CompleteAsync(id);
        Assert.Equal(0, r.Filed);
        Assert.Equal(1, r.Answered);
    }

    // ---- what is measured: the verdict log, drafts and lineage ------------------------------

    [Fact]
    public async Task Every_verdict_is_appended_and_a_changed_mind_is_a_second_event()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        var p = await d.ProposeAsync(id, new("Ticket", "A month view", null, "A calendar on /events", null, null, null, null, new[] { obs }));
        await SetPhase(id, TicketPhase.NeedsVerdict);
        var (_, _, v) = New();
        await v.DecideAsync(p.Id, new(Status: "Approved", NewToMe: true));
        await v.DecideAsync(p.Id, new(Status: "Proposed"));
        await v.DecideAsync(p.Id, new(Status: "Rejected", RejectReason: "AlreadyDecided", RejectNote: "decision 12"));
        await v.DecideAsync(p.Id, new(NewToMe: false));

        await using var db = new DashboardDbContext(_options);
        var events = await db.DiscoveryVerdictEvents.Where(e => e.ProposalId == p.Id).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(new[] { VerdictEventKind.Approved, VerdictEventKind.Undone, VerdictEventKind.Rejected, VerdictEventKind.NewToMe },
            events.Select(e => e.Kind));
        Assert.True(events[0].NewToMe);
        Assert.Equal(RejectReason.AlreadyDecided, events[2].RejectReason);
        Assert.Equal("decision 12", events[2].Note);
        Assert.False(events[3].NewToMe);
        Assert.All(events, e => Assert.Equal("alpha", e.ProjectKey));
        Assert.All(events, e => Assert.False(e.Edited));
    }

    [Fact]
    public async Task Siftings_draft_is_kept_beside_the_owners_edit_and_the_edit_is_measured()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        var p = await d.ProposeAsync(id, new("Ticket", "A month view", "Planning is the job", "A calendar on /events",
            new[] { "Grid" }, null, null, null, new[] { obs }));
        await SetPhase(id, TicketPhase.NeedsVerdict);
        var (_, _, v) = New();
        await v.DecideAsync(p.Id, new(Title: "A month view of events", Kind: "Bug"));
        await v.DecideAsync(p.Id, new(Status: "Approved"));

        await using var db = new DashboardDbContext(_options);
        var row = await db.DiscoveryProposals.SingleAsync(x => x.Id == p.Id);
        Assert.Equal("A month view", row.DraftTitle);
        Assert.Equal(ProposalKind.Ticket, row.DraftKind);
        Assert.Equal("A calendar on /events", row.DraftBody);
        Assert.Equal(new[] { "Grid" }, row.DraftWorkItems);
        Assert.Equal("A month view of events", row.Title);
        var e = await db.DiscoveryVerdictEvents.SingleAsync(x => x.ProposalId == p.Id);
        Assert.True(e.Edited);
        Assert.True(e.KindChanged);
        Assert.Equal(" of events".Length, e.EditDistance);
        Assert.True(e.DraftLength > e.EditDistance);
    }

    [Fact]
    public async Task A_restored_drop_is_recorded_and_must_be_decided_before_complete()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        var keep = await d.ProposeAsync(id, new("Ticket", "A month view", null, "b", null, null, null, null, new[] { obs }));
        var dropped = await d.DropIdeaAsync(id, "Add search", "generic", new[] { obs });
        Assert.Equal("Add search", dropped.DraftTitle);
        await SetPhase(id, TicketPhase.NeedsVerdict);

        var (_, _, v) = New();
        await v.DecideAsync(keep.Id, new(Status: "Approved"));
        var restored = await v.RestoreAsync(dropped.Id);
        Assert.Equal(ProposalStatus.Proposed, restored.Status);
        Assert.Equal(2, restored.Rank);
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => v.RestoreAsync(keep.Id));
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => v.CompleteAsync(id));
        await v.DecideAsync(dropped.Id, new(Status: "Rejected", RejectReason: "NotNow"));
        await v.CompleteAsync(id);

        await using var db = new DashboardDbContext(_options);
        var kinds = await db.DiscoveryVerdictEvents.Where(e => e.TicketId == id).OrderBy(e => e.Id).Select(e => e.Kind).ToListAsync();
        Assert.Equal(new[] { VerdictEventKind.Approved, VerdictEventKind.Restored, VerdictEventKind.Rejected, VerdictEventKind.Filed }, kinds);
        var filed = await db.DiscoveryVerdictEvents.SingleAsync(e => e.TicketId == id && e.Kind == VerdictEventKind.Filed);
        Assert.Equal(keep.Id, filed.ProposalId);
        Assert.NotNull(filed.FiledTicketId);
        Assert.Equal(id, (await db.Tickets.SingleAsync(t => t.Id == filed.FiledTicketId)).DiscoveryId);
    }

    [Fact]
    public async Task Each_written_row_names_the_run_that_wrote_it()
    {
        var id = await DiscoveryIn(TicketPhase.Scouting);
        int runId;
        await using (var ctx = new DashboardDbContext(_options))
        {
            var leases = new ResourceLeaseService(ctx);
            await leases.TryAcquireAllAsync(new[] { ResourceLeaseService.TicketKey(id) }, id, "scout", TimeSpan.FromMinutes(60));
            runId = await ctx.ResourceLeases.Where(l => l.ResourceKey == ResourceLeaseService.TicketKey(id)).Select(l => l.Id).SingleAsync();
        }
        var (_, _, d) = New();
        var o = await d.LogObservationAsync(id, new DiscoveryService.ObservationInput("Wall", "No month view", "Each event alone",
            null, 0, null, null, null, null, null, null, null, null, null, "abc123"));
        var visit = await d.LogVisitAsync(id, "/events", "/events", "1440", "member", 30, "events", "case-1", "abc123");
        Assert.Equal(runId, o.RunId);
        Assert.Equal(runId, visit.RunId);
        await SetPhase(id, TicketPhase.Sifting);
        var (_, _, s) = New();
        var p = await s.ProposeAsync(id, new("Ticket", "A month view", null, "b", null, null, null, null, new[] { o.Id }));
        Assert.Equal(runId, p.RunId);
    }

    [Fact]
    public void Edit_distance_counts_characters()
    {
        Assert.Equal(0, DiscoveryService.EditDistance("same", "same"));
        Assert.Equal(3, DiscoveryService.EditDistance("kitten", "sitting"));
        Assert.Equal(4, DiscoveryService.EditDistance("", "four"));
    }
}
