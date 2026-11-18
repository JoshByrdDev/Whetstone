using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// What a run's bug noticed in passing becomes, as the owner has set it for this machine's fleet:
/// filed into the Backlog when it is about the project's base (<see cref="ProjectConfig.BaseBugsToBacklog"/>),
/// filed at all only from the pipelines the owner takes bugs from (<see cref="ProjectConfig.BugFilingTracks"/>),
/// and a queued ticket nobody has started can be parked back in the Backlog by a person, and only a person.
/// </summary>
public class BugFilingRulesTests : IDisposable
{
    private readonly string _data;
    private readonly ProjectRegistry _registry;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public BugFilingRulesTests()
    {
        _data = Path.Combine(Path.GetTempPath(), "ccd-bugrules-" + Guid.NewGuid().ToString("N")[..8]);
        WriteProject("alpha", """{ "projectPath": "C:\\nowhere", "baseBranch": "main" }""");
        WriteProject("bravo", """{ "projectPath": "C:\\nowhere", "baseBranch": "main", "baseBugsToBacklog": true, "bugFilingTracks": ["Delivery"] }""");
        WriteProject("charlie", """{ "projectPath": "C:\\nowhere", "baseBranch": "main", "bugFilingTracks": [] }""");
        _registry = new ProjectRegistry(new DashboardPaths(_data));

        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_data, true); } catch { /* temp */ }
    }

    [Fact]
    public void Unset_every_pipeline_files_bugs_and_none_is_parked()
    {
        var cfg = _registry.GetProjectConfig("alpha");
        Assert.False(cfg.BaseBugsToBacklog);
        Assert.All(Enum.GetValues<TicketTrack>(), t => Assert.True(cfg.FilesBugsFrom(t)));
        Assert.False(TicketEndpoints.ParksAsBaseBug(cfg, 7, true, TicketTrack.Delivery, null, null, TicketPhase.NeedsSpeculation));
    }

    [Fact]
    public void Only_the_pipelines_listed_file_bugs_and_an_empty_list_files_none()
    {
        var bravo = _registry.GetProjectConfig("bravo");
        Assert.True(bravo.FilesBugsFrom(TicketTrack.Delivery));
        Assert.False(bravo.FilesBugsFrom(TicketTrack.Review));
        Assert.False(bravo.FilesBugsFrom(TicketTrack.Discovery));
        Assert.All(Enum.GetValues<TicketTrack>(), t => Assert.False(_registry.GetProjectConfig("charlie").FilesBugsFrom(t)));

        var why = TicketEndpoints.NotFiledReason(bravo, TicketTrack.Review);
        Assert.Contains("bugs from delivery runs only", why);
        Assert.Contains("peer review ticket", why);
        Assert.Contains("no run's bugs", TicketEndpoints.NotFiledReason(_registry.GetProjectConfig("charlie"), TicketTrack.Delivery));
    }

    [Fact]
    public void A_runs_bug_on_the_base_is_parked_and_one_on_a_feature_or_slice_is_queued()
    {
        var cfg = _registry.GetProjectConfig("bravo");
        Assert.True(TicketEndpoints.ParksAsBaseBug(cfg, 7, true, TicketTrack.Delivery, null, null, TicketPhase.NeedsSpeculation));
        Assert.False(TicketEndpoints.ParksAsBaseBug(cfg, 7, true, TicketTrack.Delivery, "feature/PROJ-123", null, TicketPhase.NeedsSpeculation));
        Assert.False(TicketEndpoints.ParksAsBaseBug(cfg, 7, true, TicketTrack.Delivery, null, 13, TicketPhase.NeedsSpeculation));
        // Not a run's bug: a person's filing, a ticket that is no bug, one already for the Backlog.
        Assert.False(TicketEndpoints.ParksAsBaseBug(cfg, null, true, TicketTrack.Delivery, null, null, TicketPhase.NeedsSpeculation));
        Assert.False(TicketEndpoints.ParksAsBaseBug(cfg, 7, false, TicketTrack.Delivery, null, null, TicketPhase.NeedsSpeculation));
        Assert.False(TicketEndpoints.ParksAsBaseBug(cfg, 7, true, TicketTrack.Delivery, null, null, TicketPhase.Backlog));
    }

    [Fact]
    public async Task A_person_parks_a_queued_ticket_in_the_backlog_and_nobody_else_can()
    {
        var id = await Seed(TicketPhase.NeedsSpeculation);
        await using var ctx = new DashboardDbContext(_options);
        var svc = new TicketService(ctx);

        foreach (var actor in new[] { TransitionActor.Worker, TransitionActor.Steward, TransitionActor.System })
        {
            var ex = await Assert.ThrowsAsync<InvalidTransitionException>(() => svc.TransitionAsync(id, TicketPhase.Backlog, "park", actor));
            Assert.DoesNotContain(TicketPhase.Backlog, ex.Allowed);
        }

        var t = await svc.TransitionAsync(id, TicketPhase.Backlog, "Not wanted yet", TransitionActor.Human);
        Assert.Equal(TicketPhase.Backlog, t!.Phase);
        Assert.Contains(TicketPhase.Backlog, PhaseMachine.NextPhasesFor(new Ticket { Phase = TicketPhase.NeedsSpeculation }));
    }

    private async Task<int> Seed(TicketPhase phase)
    {
        await using var ctx = new DashboardDbContext(_options);
        var t = new Ticket { ProjectKey = "alpha", Title = "A bug", Phase = phase };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private void WriteProject(string key, string json)
    {
        var dir = Path.Combine(_data, "projects", key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.json"), json);
    }
}
