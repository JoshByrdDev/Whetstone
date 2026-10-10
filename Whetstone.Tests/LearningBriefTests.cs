using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The learning brief — the reading material refinement leaves for the human before they sign a
/// ticket off. Covers the two things that would silently be wrong: the migration that adds the
/// columns (the rest of the suite uses EnsureCreated, which never runs one), and the rewrite
/// semantics that keep the brief describing the work that actually landed.
/// </summary>
public class LearningBriefTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public LearningBriefTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private async Task<int> Seed()
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = "alpha", Title = "t", Phase = TicketPhase.Refining };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    /// <summary>
    /// The whole migration chain applies to an empty database. The columns were added by hand
    /// (the dashboard was running and holding its own build output), so this is the check that
    /// the hand-written migration and the model snapshot actually agree with the entity.
    /// </summary>
    [Fact]
    public async Task Migrations_apply_and_create_the_brief_columns()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(conn).Options;
        await using var ctx = new DashboardDbContext(options);

        await ctx.Database.MigrateAsync();

        // A round-trip through the migrated schema is the real proof the columns exist and are typed
        // as the entity expects — a PRAGMA check would pass even if EF disagreed about the mapping.
        var t = new Ticket
        {
            ProjectKey = "alpha",
            Title = "t",
            LearningBrief = "## The area\nAllocation tiers.",
            LearningBriefAt = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var read = await ctx.Tickets.SingleAsync();
        Assert.Equal("## The area\nAllocation tiers.", read.LearningBrief);
        Assert.NotNull(read.LearningBriefAt);
    }

    [Fact]
    public async Task Setting_the_brief_stamps_when_it_was_written()
    {
        var id = await Seed();
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        var before = DateTime.UtcNow;
        var t = await svc.SetLearningBriefAsync(id, "  ## The area\nTier resolution.  ");

        Assert.NotNull(t);
        Assert.Equal("## The area\nTier resolution.", t!.LearningBrief);
        Assert.NotNull(t.LearningBriefAt);
        Assert.True(t.LearningBriefAt >= before);
    }

    /// <summary>
    /// Rewrite, never accrete: a ticket that bounces back to implementation and returns gets a
    /// brief for the work that landed, not the previous attempt's with a new one bolted on.
    /// </summary>
    [Fact]
    public async Task Rewriting_the_brief_replaces_it_wholesale()
    {
        var id = await Seed();
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        await svc.SetLearningBriefAsync(id, "first pass");
        var t = await svc.SetLearningBriefAsync(id, "second pass");

        Assert.Equal("second pass", t!.LearningBrief);
    }

    /// <summary>Clearing it leaves no orphan timestamp for the UI to render a brief-less date from.</summary>
    [Fact]
    public async Task Clearing_the_brief_clears_its_timestamp()
    {
        var id = await Seed();
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);

        await svc.SetLearningBriefAsync(id, "something");
        var t = await svc.SetLearningBriefAsync(id, "   ");

        Assert.Null(t!.LearningBrief);
        Assert.Null(t.LearningBriefAt);
    }

    /// <summary>The heading is per-project so "Learn Alpha" never has to exist in this codebase.</summary>
    [Fact]
    public void Learning_label_defaults_to_learn_plus_display_name()
    {
        Assert.Equal("Learn Alpha", new ProjectConfig { Name = "alpha", DisplayName = "Alpha" }.ResolvedLearningLabel);
        Assert.Equal("Domain notes",
            new ProjectConfig { Name = "alpha", DisplayName = "Alpha", LearningLabel = " Domain notes " }.ResolvedLearningLabel);
    }
}
