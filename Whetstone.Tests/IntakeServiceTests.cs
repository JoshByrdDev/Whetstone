using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Whetstone.Services.Intake;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Intake end to end against the stand-in queue (docs/intake-plan.md, sections 4, 5 and 9): one
/// problem one ticket, a repeat as a note, a regression after Done, a clear, a mute, a redelivery,
/// an unrecognised message, the limits and the storm, and a project's test report.
/// </summary>
public class IntakeServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private DateTime _now = new(2026, 11, 16, 14, 10, 0, DateTimeKind.Utc);
    private IntakeConfig _cfg = new(true, [new IntakeSource("prod", IntakeSource.StandIn, "", "", "", "", 60, "")],
        new IntakeLimits(5, 20), null, []);
    private readonly InMemoryIntakeTransport _queue = new();
    private string? _runbook;

    public IntakeServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private sealed class Settings(Func<IntakeConfig> cfg) : IIntakeSettings
    {
        public IntakeConfig For(string project) => cfg();
        public IReadOnlyList<string> Projects => ["alpha"];
    }

    private sealed class Runbook(Func<string?> text) : IIntakeRunbook
    {
        public string? AskedFor;
        public Task<string?> ReadAsync(string project, string path, CancellationToken ct) { AskedFor = path; return Task.FromResult(text()); }
    }

    private DashboardDbContext Ctx() => new(_options);

    private async Task<IntakeService.PassResult> Poll()
    {
        await using var db = Ctx();
        var svc = new IntakeService(db, new TicketService(db), new Settings(() => _cfg), new Runbook(() => _runbook), clock: () => _now);
        return await svc.PollAsync("alpha", _cfg.Sources[0], _queue);
    }

    private IntakeEnvelope SendSentry(string file = "sentry-issue.json", Func<string, string>? edit = null, string? id = null) =>
        _queue.Send((edit ?? (s => s))(IntakeAdapterTests.Fixture(file)), new Dictionary<string, string> { ["kind"] = "sentry-issue" }, id);

    private IntakeEnvelope SendAlarm(string file = "cloudwatch-alarm-sns.json") => _queue.Send(IntakeAdapterTests.Fixture(file));

    private static string IssueId(string body, string id) => body.Replace("4510000001", id);

    private async Task<List<Ticket>> Tickets()
    {
        await using var db = Ctx();
        return await db.Tickets.AsNoTracking().Include(t => t.Comments).OrderBy(t => t.Id).ToListAsync();
    }

    private async Task SetPhase(int id, TicketPhase p)
    {
        await using var db = Ctx();
        await db.Tickets.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Phase, p));
    }

    [Fact]
    public async Task A_first_report_is_one_ticket_with_the_problem_in_it_and_the_right_origin()
    {
        SendSentry();
        var pass = await Poll();
        Assert.Equal((1, 1, 0), (pass.Received, pass.Handled, pass.Failed));

        var t = Assert.Single(await Tickets());
        Assert.Equal("[sentry] TypeError: Cannot read properties of null (reading 'id') in bookVisit", t.Title);
        Assert.Equal(IntakeService.Landing, t.Phase);
        Assert.Equal(TicketOriginKind.Intake, t.Origin);
        Assert.Equal(IntakeChannels.Sentry, t.IntakeChannel);
        Assert.NotNull(t.IntakeIssueId);
        Assert.Null(t.BaseBranch);
        Assert.Null(t.FoundOn);
        Assert.True(t.IsBug);
        Assert.Contains("`src/booking/visit.ts:42` in `bookVisit`", t.Description);
        Assert.Contains("read the stack against that commit", t.Description);
        Assert.Contains("a test that fails locally", t.Description);
        // The owner's link sits in the owner's part, after everything the worker is told.
        var owner = t.Description!.IndexOf(IntakeTicketText.OwnerMarker, StringComparison.Ordinal);
        Assert.True(owner > t.Description.IndexOf("Nothing outside this ticket", StringComparison.Ordinal));
        Assert.Contains("example-org.sentry.io/issues/4510000001", t.Description[owner..]);
        // Deleted from the queue only once handled.
        Assert.Single(_queue.Deleted);
    }

    [Fact]
    public async Task A_repeat_is_a_note_at_most_once_an_hour_not_a_ticket()
    {
        SendSentry();
        await Poll();
        _now = _now.AddMinutes(5);
        SendSentry();
        SendSentry();
        await Poll();
        var t = Assert.Single(await Tickets());
        Assert.DoesNotContain(t.Comments, c => c.Body.StartsWith("Intake: seen"));

        _now = _now.AddMinutes(61);
        SendSentry();
        await Poll();
        t = Assert.Single(await Tickets());
        var note = Assert.Single(t.Comments, c => c.Body.StartsWith("Intake: seen"));
        Assert.Contains("seen 3 more times", note.Body);
        Assert.Contains("(17 in all", note.Body);

        await using var db = Ctx();
        var issue = await db.IntakeIssues.SingleAsync();
        Assert.Equal(4, issue.Occurrences);
    }

    [Fact]
    public async Task The_problem_coming_back_after_Done_is_a_linked_regression_and_still_from_intake()
    {
        SendSentry();
        await Poll();
        var first = Assert.Single(await Tickets());
        await SetPhase(first.Id, TicketPhase.Done);

        SendSentry("sentry-issue-raw-event.json", b => b.Replace("4510000002", "4510000001").Replace("[\"test\", \"true\"]", "[\"test\", \"false\"]"));
        await Poll();
        var all = await Tickets();
        Assert.Equal(2, all.Count);
        var again = all[1];
        Assert.Equal(first.Id, again.RegressionOf);
        Assert.Equal(TicketOriginKind.Intake, again.Origin);
        Assert.Equal(first.IntakeIssueId, again.IntakeIssueId);
        Assert.StartsWith($"**This came back.** The same problem was ticket #{first.Id}", again.Description);

        await using var db = Ctx();
        var issue = await db.IntakeIssues.SingleAsync();
        Assert.Equal(again.Id, issue.TicketId);
        Assert.Equal($"{first.Id},{again.Id}", issue.TicketHistory);
    }

    [Fact]
    public async Task A_clear_is_a_note_and_moves_nothing()
    {
        SendAlarm();
        await Poll();
        var t = Assert.Single(await Tickets());
        Assert.False(t.IsBug);
        Assert.Contains("cause may not be code", t.Description);
        Assert.Contains("owner's to-do list", t.Description);

        SendAlarm("cloudwatch-ok-raw.json");
        await Poll();
        t = Assert.Single(await Tickets());
        Assert.Equal(IntakeService.Landing, t.Phase);
        Assert.Single(t.Comments, c => c.Body.StartsWith("Intake: the alarm cleared at 2026-11-16 14:20 UTC"));
    }

    [Fact]
    public async Task A_clear_with_nothing_filed_files_nothing()
    {
        SendAlarm("cloudwatch-ok-raw.json");
        await Poll();
        Assert.Empty(await Tickets());
        await using var db = Ctx();
        Assert.Equal(IntakeOutcomes.Ignored, (await db.IntakeMessages.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task An_alarm_quotes_its_section_of_the_runbook()
    {
        _cfg = _cfg with { Runbook = "docs/runbook.md" };
        _runbook = "# Runbook\n\n## alpha-db-cpu\n\nNot this one.\n\n## alpha-api-5xx\n\nCheck the last deploy first.\n\n### Rolling back\n\nRedeploy the previous release.\n\n## alpha-queue-depth\n\nNor this.";
        SendAlarm();
        await Poll();
        var t = Assert.Single(await Tickets());
        Assert.Contains("### From the runbook (`docs/runbook.md`)", t.Description);
        Assert.Contains("> Check the last deploy first.", t.Description);
        Assert.Contains("> Redeploy the previous release.", t.Description);
        Assert.DoesNotContain("Not this one", t.Description);
        Assert.DoesNotContain("Nor this", t.Description);
    }

    [Fact]
    public async Task Mute_closes_the_ticket_and_files_nothing_again()
    {
        SendSentry();
        await Poll();
        var t = Assert.Single(await Tickets());
        await using (var db = Ctx())
        {
            var r = await new IntakeService(db, new TicketService(db), new Settings(() => _cfg), clock: () => _now).MuteFromTicketAsync(t.Id);
            Assert.True(r!.TicketClosed);
        }
        t = Assert.Single(await Tickets());
        Assert.Equal(TicketPhase.Done, t.Phase);

        SendSentry();
        await Poll();
        Assert.Single(await Tickets());
        await using var read = Ctx();
        var issue = await read.IntakeIssues.SingleAsync();
        Assert.True(issue.Muted);
        Assert.Equal(2, issue.Occurrences);
        Assert.Contains(await read.IntakeMessages.ToListAsync(), m => m.Outcome == IntakeOutcomes.Muted);
        Assert.Contains(await read.PhaseTransitions.ToListAsync(), p => p.To == TicketPhase.Done && p.Actor == TransitionActor.Human);
    }

    [Fact]
    public async Task Mute_does_not_pull_a_ticket_out_from_under_a_run()
    {
        SendSentry();
        await Poll();
        var t = Assert.Single(await Tickets());
        await SetPhase(t.Id, TicketPhase.Implementing);
        await using var db = Ctx();
        var r = await new IntakeService(db, new TicketService(db), new Settings(() => _cfg)).MuteFromTicketAsync(t.Id);
        Assert.True(r!.Muted);
        Assert.False(r.TicketClosed);
        Assert.Equal(TicketPhase.Implementing, (await Tickets())[0].Phase);
    }

    [Fact]
    public async Task A_redelivered_message_changes_nothing()
    {
        var m = SendSentry();
        await Poll();
        _queue.Redeliver(m);
        var pass = await Poll();
        Assert.Equal((1, 0, 1), (pass.Received, pass.Handled, pass.Redelivered));
        Assert.Single(await Tickets());
        await using var db = Ctx();
        Assert.Equal(1, (await db.IntakeIssues.SingleAsync()).Occurrences);
        Assert.Equal(2, _queue.Deleted.Count(id => id == m.MessageId));
    }

    [Fact]
    public async Task An_unrecognised_message_is_kept_and_deleted_and_blocks_nothing()
    {
        _queue.Send("""{ "something": "new" }""", new Dictionary<string, string> { ["kind"] = "uptime-check" });
        SendSentry();
        await Poll();
        Assert.Single(await Tickets());
        await using var db = Ctx();
        var kept = await db.IntakeMessages.SingleAsync(m => m.Outcome == IntakeOutcomes.Unrecognised);
        Assert.Equal("""{ "something": "new" }""", kept.RawBody);
        Assert.Contains("uptime-check", kept.RawAttributes);
        Assert.Equal(2, _queue.Deleted.Count);
    }

    [Fact]
    public async Task Past_the_hourly_limit_reports_are_held_on_one_storm_ticket_and_filed_in_the_next_window()
    {
        _cfg = _cfg with { Limits = new IntakeLimits(2, 20) };
        foreach (var id in new[] { "1", "2", "3", "4" }) SendSentry(edit: b => IssueId(b, id));
        SendSentry(edit: b => IssueId(b, "4"));
        await Poll();

        var all = await Tickets();
        Assert.Equal(3, all.Count);
        var storm = Assert.Single(all, t => t.IntakeChannel == IntakeChannels.Storm);
        Assert.Equal(TicketOriginKind.Intake, storm.Origin);
        Assert.False(storm.IsBug);
        Assert.Contains("2 reports held back in the hour from 2026-11-16 14:00 UTC", storm.Title);
        Assert.Contains("`3`, 1 report", storm.Description);
        Assert.Contains("`4`, 2 reports", storm.Description);

        // The next hour starts fresh: a held issue still firing is filed then.
        _now = _now.AddHours(1);
        SendSentry(edit: b => IssueId(b, "3"));
        await Poll();
        all = await Tickets();
        Assert.Equal(4, all.Count);
        Assert.Equal(IntakeChannels.Sentry, all[3].IntakeChannel);
        await using var db = Ctx();
        Assert.Null((await db.IntakeIssues.SingleAsync(i => i.Fingerprint == "3")).HeldIn);
        Assert.Equal("h:2026-11-16T14", (await db.IntakeIssues.SingleAsync(i => i.Fingerprint == "4")).HeldIn);
        Assert.Single(await db.IntakeStorms.ToListAsync());
    }

    [Fact]
    public async Task The_daily_limit_holds_for_the_rest_of_the_day()
    {
        _cfg = _cfg with { Limits = new IntakeLimits(10, 1) };
        SendSentry(edit: b => IssueId(b, "1"));
        await Poll();
        _now = _now.AddHours(3);
        SendSentry(edit: b => IssueId(b, "2"));
        await Poll();
        var storm = Assert.Single(await Tickets(), t => t.IntakeChannel == IntakeChannels.Storm);
        Assert.Contains("the day 2026-11-16", storm.Title);
    }

    [Fact]
    public async Task A_test_report_is_filed_straight_to_Done_once_an_hour()
    {
        SendSentry("sentry-issue-raw-event.json");
        SendSentry("sentry-issue-raw-event.json");
        await Poll();
        var t = Assert.Single(await Tickets());
        Assert.Equal(TicketPhase.Done, t.Phase);
        Assert.StartsWith("[sentry] [test]", t.Title);
        Assert.StartsWith("**This is a test.**", t.Description);
        Assert.False(t.IsBug);
        Assert.Equal(TicketOriginKind.Intake, t.Origin);
        await using var db = Ctx();
        Assert.Empty(await db.IntakeIssues.ToListAsync());
    }

    [Fact]
    public async Task Handled_ids_are_kept_fourteen_days()
    {
        var m = SendSentry();
        await Poll();
        _now = _now.AddDays(15);
        await Poll();
        await using var db = Ctx();
        Assert.Empty(await db.IntakeMessages.Where(x => x.MessageId == m.MessageId).ToListAsync());
    }
}
