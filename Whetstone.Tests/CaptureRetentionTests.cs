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
/// The capture retention policy (docs/capture-retention.md). Most of what is specified here is what
/// a pass must leave alone: an open ticket's pictures at any age and any size, a ticket done
/// recently, and the record of a shot once its file has gone.
/// </summary>
public class CaptureRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2030, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CaptureRetention.Policy NoBudget = new(QaCapturesDays: 7, QaShotsDays: 90, QaCapturesBudgetMb: 0, QaShotsBudgetMb: 0);

    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly string _root;
    private readonly DashboardPaths _paths;

    public CaptureRetentionTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
        _root = Path.Combine(Path.GetTempPath(), "ccd-retention-" + Guid.NewGuid().ToString("N")[..8]);
        _paths = new DashboardPaths(_root);
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DashboardDbContext NewCtx() => new(_options);

    /// <summary>A ticket in <paramref name="phase"/>, which last reached it that many days ago, with a shot row and files in both stores.</summary>
    private async Task<int> Seed(TicketPhase phase, double daysAgo, int captureBytes = 10, int shotBytes = 10, bool transition = true)
    {
        using var ctx = NewCtx();
        var at = Now - TimeSpan.FromDays(daysAgo);
        var t = new Ticket { ProjectKey = "alpha", Title = "t", Phase = phase, UpdatedAt = at };
        t.QaShots.Add(new QaShot { Order = 0, Caption = "the grid, sorted", ImageFile = "0.png", CreatedAt = at });
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        if (transition)
        {
            ctx.PhaseTransitions.Add(new PhaseTransition { TicketId = t.Id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = phase, At = at });
            await ctx.SaveChangesAsync();
        }
        Write(_paths.QaCaptureTicketDir(t.Id), "page-1.png", captureBytes);
        Write(_paths.QaShotTicketDir(t.Id), "0.png", shotBytes);
        return t.Id;
    }

    private static void Write(string dir, string name, int bytes)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), new byte[bytes]);
    }

    private async Task<IReadOnlyList<CaptureRetention.Pruned>> Prune(CaptureRetention.Policy? policy = null)
    {
        using var ctx = NewCtx();
        return await CaptureRetention.PruneAsync(ctx, _paths, policy ?? NoBudget, Now, NullLogger.Instance);
    }

    private bool Captures(int id) => Directory.Exists(_paths.QaCaptureTicketDir(id));
    private bool Shots(int id) => Directory.Exists(_paths.QaShotTicketDir(id));

    private QaShot Shot(int id)
    {
        using var ctx = NewCtx();
        return ctx.QaShots.Single(s => s.TicketId == id);
    }

    [Fact]
    public async Task Captures_go_a_week_after_done_and_shots_stay_until_ninety_days()
    {
        var id = await Seed(TicketPhase.Done, daysAgo: 8);

        var pruned = await Prune();

        Assert.False(Captures(id));
        Assert.True(Shots(id));
        Assert.Null(Shot(id).PrunedAt);
        var p = Assert.Single(pruned);
        Assert.Equal((CaptureRetention.Store.QaCaptures, id, "age"), (p.Store, p.TicketId, p.Reason));
    }

    [Fact]
    public async Task A_ticket_done_recently_keeps_everything()
    {
        var id = await Seed(TicketPhase.Done, daysAgo: 2);
        Assert.Empty(await Prune());
        Assert.True(Captures(id));
        Assert.True(Shots(id));
    }

    [Fact]
    public async Task Shots_past_ninety_days_go_and_the_row_says_when()
    {
        var id = await Seed(TicketPhase.Done, daysAgo: 91);

        await Prune();

        Assert.False(Shots(id));
        var shot = Shot(id);
        Assert.Equal(Now, shot.PrunedAt);
        Assert.Equal("the grid, sorted", shot.Caption);
        // What the drawer, the Classroom and the deck's player are told.
        Assert.Equal(Now, QaShotDto.From(id, shot).RemovedOn);
    }

    [Theory]
    [InlineData(TicketPhase.Blocked)]
    [InlineData(TicketPhase.Backlog)]
    [InlineData(TicketPhase.NeedsReview)]
    [InlineData(TicketPhase.QaTesting)]
    public async Task An_open_ticket_is_never_touched_by_age_or_budget(TicketPhase phase)
    {
        var id = await Seed(phase, daysAgo: 400, captureBytes: 3 * 1024 * 1024, shotBytes: 3 * 1024 * 1024);

        Assert.Empty(await Prune(new CaptureRetention.Policy(1, 1, 1, 1)));

        Assert.True(Captures(id));
        Assert.True(Shots(id));
        Assert.Null(Shot(id).PrunedAt);
    }

    [Fact]
    public async Task Done_is_the_last_arrival_so_a_ticket_reopened_and_finished_again_counts_from_then()
    {
        var id = await Seed(TicketPhase.Done, daysAgo: 100);
        using (var ctx = NewCtx())
        {
            ctx.PhaseTransitions.Add(new PhaseTransition { TicketId = id, ProjectKey = "alpha", From = TicketPhase.NeedsReview, To = TicketPhase.Done, At = Now - TimeSpan.FromDays(3) });
            await ctx.SaveChangesAsync();
        }
        Assert.Empty(await Prune());
        Assert.True(Shots(id));
    }

    [Fact]
    public async Task A_ticket_older_than_the_transitions_table_is_dated_by_its_last_edit()
    {
        var old = await Seed(TicketPhase.Done, daysAgo: 95, transition: false);
        var recent = await Seed(TicketPhase.Done, daysAgo: 1, transition: false);
        await Prune();
        Assert.False(Shots(old));
        Assert.True(Captures(recent));
    }

    [Fact]
    public async Task A_folder_for_a_ticket_that_no_longer_exists_goes_at_once()
    {
        Write(_paths.QaCaptureTicketDir(4242), "x.png", 5);
        Write(_paths.QaShotTicketDir(4242), "0.png", 5);
        // Not a ticket's folder: left alone.
        Write(Path.Combine(_paths.QaShotsDir, "notes"), "readme.txt", 5);

        var pruned = await Prune();

        Assert.Equal(2, pruned.Count);
        Assert.All(pruned, p => Assert.Equal("orphan", p.Reason));
        Assert.True(File.Exists(Path.Combine(_paths.QaShotsDir, "notes", "readme.txt")));
    }

    [Fact]
    public async Task Over_budget_the_longest_done_goes_first_until_it_fits_and_open_tickets_count_but_stay()
    {
        const int Mb = 1024 * 1024;
        var open = await Seed(TicketPhase.Blocked, daysAgo: 300, shotBytes: Mb);
        var oldest = await Seed(TicketPhase.Done, daysAgo: 30, shotBytes: Mb);
        var middle = await Seed(TicketPhase.Done, daysAgo: 20, shotBytes: Mb);
        var newest = await Seed(TicketPhase.Done, daysAgo: 1, shotBytes: Mb);

        // 4MB in qa-shots against a 2MB budget: two of the three done tickets go, oldest first.
        var pruned = await Prune(new CaptureRetention.Policy(QaCapturesDays: 0, QaShotsDays: 0, QaCapturesBudgetMb: 0, QaShotsBudgetMb: 2));

        Assert.Equal(new[] { oldest, middle }, pruned.Where(p => p.Store == CaptureRetention.Store.QaShots).Select(p => p.TicketId));
        Assert.All(pruned, p => Assert.Equal("budget", p.Reason));
        Assert.True(Shots(open));
        Assert.True(Shots(newest));
        Assert.NotNull(Shot(oldest).PrunedAt);
        Assert.Null(Shot(newest).PrunedAt);
    }

    [Fact]
    public async Task Zero_days_and_zero_budget_keep_everything()
    {
        var id = await Seed(TicketPhase.Done, daysAgo: 1000);
        Assert.Empty(await Prune(new CaptureRetention.Policy(0, 0, 0, 0)));
        Assert.True(Captures(id));
        Assert.True(Shots(id));
    }

    [Fact]
    public async Task Every_pruned_folder_is_in_the_ledger()
    {
        var id = await Seed(TicketPhase.Done, daysAgo: 91, captureBytes: 7);
        await Prune();

        var lines = File.ReadAllLines(Path.Combine(_paths.GlobalLogsDir, CaptureRetention.LedgerFile));
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("qaCaptures", first.RootElement.GetProperty("store").GetString());
        Assert.Equal(id, first.RootElement.GetProperty("ticketId").GetInt32());
        Assert.Equal("age", first.RootElement.GetProperty("reason").GetString());
        Assert.Equal(7, first.RootElement.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task A_fresh_qa_run_after_a_prune_brings_the_shots_back()
    {
        var id = await Seed(TicketPhase.Done, daysAgo: 91);
        await Prune();
        Assert.NotNull(Shot(id).PrunedAt);

        // Reopened and photographed again: set_qa_shots replaces the rows, and the new ones are kept.
        using (var ctx = NewCtx())
        {
            var t = await ctx.Tickets.SingleAsync(x => x.Id == id);
            t.Phase = TicketPhase.QaTesting;
            await ctx.SaveChangesAsync();
            var svc = new TicketService(ctx);
            await svc.SetQaShotsAsync(id, new[] { ("0.png", "again") });
        }
        Assert.Null(Shot(id).PrunedAt);
    }

    [Theory]
    [InlineData("{}", 7, 90, 2048, 1024)]
    [InlineData("{\"retention\": {\"qaShotsDays\": 30}}", 7, 30, 2048, 1024)]
    [InlineData("{\"retention\": {\"qaCapturesDays\": 0, \"qaShotsBudgetMb\": 500}}", 0, 90, 2048, 500)]
    [InlineData("{\"retention\": {\"qaShotsDays\": \"soon\"}}", 7, 90, 2048, 1024)]
    [InlineData("{\"retention\": 3}", 7, 90, 2048, 1024)]
    public void The_policy_is_read_from_global_config_with_defaults_for_what_is_missing(string json, int capDays, int shotDays, long capMb, long shotMb)
    {
        var global = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        Assert.Equal(new CaptureRetention.Policy(capDays, shotDays, capMb, shotMb), CaptureRetention.Policy.From(global));
    }

    [Fact]
    public void The_placeholder_names_the_date()
    {
        Assert.Contains("removed on 1 Jun 2030", CaptureRetention.RemovedPlaceholderSvg(Now));
    }
}
