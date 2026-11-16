using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Where every ticket came from (docs/intake-plan.md, section 6): one derivation, used by every
/// filing path, written once, and a backfill that gives every ticket from before it an honest
/// answer. Named under Intake so the intake filter runs it: attribution is what makes "how many
/// bugs came from production" answerable at all.
/// </summary>
public class IntakeOriginTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public IntakeOriginTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext Ctx() => new(_options);

    [Theory]
    [InlineData(null, null, null, null, TicketOriginKind.Owner)]
    [InlineData(TransitionActor.Steward, null, null, null, TicketOriginKind.Steward)]
    [InlineData(TransitionActor.System, null, null, null, TicketOriginKind.Steward)]
    [InlineData(TransitionActor.Steward, 4, null, null, TicketOriginKind.FoundInRun)]
    [InlineData(null, 4, 9, null, TicketOriginKind.Regression)]
    [InlineData(null, null, 9, "sentry", TicketOriginKind.Intake)]
    [InlineData(null, null, null, "cloudwatch-alarm", TicketOriginKind.Intake)]
    public void The_origin_is_the_most_specific_fact_about_the_filing(TransitionActor? by, int? foundOn, int? regressionOf,
        string? channel, TicketOriginKind expected)
    {
        var t = new Ticket { CreatedBy = by, FoundOn = foundOn, RegressionOf = regressionOf, IntakeChannel = channel };
        Assert.Equal(expected, TicketOrigin.Of(t));
    }

    [Fact]
    public void Its_name_is_the_plans_spelling()
    {
        Assert.Equal("found-in-run", TicketOrigin.Name(TicketOriginKind.FoundInRun));
        Assert.Equal("intake", TicketOrigin.Name(TicketOriginKind.Intake));
        Assert.Equal("owner", TicketOrigin.Name(TicketOriginKind.Owner));
    }

    [Fact]
    public async Task Every_filing_path_reaches_the_same_derivation()
    {
        await using (var ctx = Ctx()) await ctx.Database.EnsureCreatedAsync();
        await using var db = Ctx();
        var svc = new TicketService(db);

        var owner = await svc.CreateAsync("alpha", "by hand", null, null, null, TicketPhase.Backlog);
        var steward = await svc.CreateAsync("alpha", "from a slice", null, null, null, TicketPhase.Backlog,
            filing: new TicketFiling(CreatedBy: TransitionActor.Steward));
        var found = await svc.CreateAsync("alpha", "seen in passing", null, null, null, TicketPhase.Backlog,
            filing: new TicketFiling(FoundOn: owner.Id));
        var regression = await svc.CreateAsync("alpha", "came back", null, null, null, TicketPhase.Backlog,
            filing: new TicketFiling(RegressionOf: owner.Id));
        var intake = await svc.CreateAsync("alpha", "[sentry] boom", null, null, null, TicketPhase.Backlog,
            filing: new TicketFiling(RegressionOf: owner.Id, IntakeChannel: IntakeChannels.Sentry, IntakeIssueId: 3));

        Assert.Equal(TicketOriginKind.Owner, owner.Origin);
        Assert.Null(owner.CreatedBy);
        Assert.Equal(TicketOriginKind.Steward, steward.Origin);
        Assert.Equal(TransitionActor.Steward, steward.CreatedBy);
        Assert.Equal(TicketOriginKind.FoundInRun, found.Origin);
        Assert.Equal(owner.Id, found.FoundOn);
        Assert.Equal(TicketOriginKind.Regression, regression.Origin);
        Assert.Equal(TicketOriginKind.Intake, intake.Origin);
        Assert.Equal(owner.Id, intake.RegressionOf);
        Assert.Equal("sentry", intake.IntakeChannel);
        Assert.Equal(3, intake.IntakeIssueId);
    }

    [Fact]
    public async Task An_intake_issue_id_without_a_channel_is_not_an_intake_ticket()
    {
        await using (var ctx = Ctx()) await ctx.Database.EnsureCreatedAsync();
        await using var db = Ctx();
        var t = await new TicketService(db).CreateAsync("alpha", "t", null, null, null, TicketPhase.Backlog,
            filing: new TicketFiling(IntakeIssueId: 5));
        Assert.Equal(TicketOriginKind.Owner, t.Origin);
        Assert.Null(t.IntakeIssueId);
    }

    /// <summary>
    /// The backfill, run as the real migration runs it: the database at the migration before, rows
    /// written as the board wrote them then, and the migration on top.
    /// </summary>
    [Fact]
    public async Task The_migration_gives_every_existing_ticket_its_origin()
    {
        await using (var ctx = Ctx())
            await ctx.GetService<IMigrator>().MigrateAsync("20261116201003_OwnerTodoList");

        // Every other column that must have a value gets a plain one, so the test needs no edit when
        // a later migration adds one.
        var required = new List<(string Name, string Type)>();
        using (var info = _conn.CreateCommand())
        {
            info.CommandText = "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('Tickets')";
            using var r = info.ExecuteReader();
            while (r.Read())
                if (r.GetInt64(2) == 1 && r.IsDBNull(3) && r.GetInt64(4) == 0) required.Add((r.GetString(0), r.GetString(1)));
        }
        var given = new HashSet<string> { "ProjectKey", "Title", "Description", "Phase", "CreatedBy", "DiscoveryId", "CreatedAt", "UpdatedAt" };
        var extra = required.Where(c => !given.Contains(c.Name)).ToList();
        var extraCols = string.Concat(extra.Select(c => $", \"{c.Name}\""));
        var extraVals = string.Concat(extra.Select(c => c.Type.Equals("TEXT", StringComparison.OrdinalIgnoreCase) ? ", ''" : ", 0"));

        void Insert(int id, string? createdBy, int? discoveryId, string? description)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO Tickets (Id, ProjectKey, Title, Description, Phase, CreatedBy, DiscoveryId, CreatedAt, UpdatedAt{extraCols}) "
                + $"SELECT $id, 'alpha', 't', $d, 'Backlog', $by, $disc, '2026-01-01 00:00:00', '2026-01-01 00:00:00'{extraVals}";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$d", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$by", (object?)createdBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$disc", (object?)discoveryId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        try { Insert(1, null, null, "a person's"); }
        catch (SqliteException e) { Assert.Fail("The insert no longer matches the table before the migration: " + e.Message); }
        Insert(2, "Steward", null, "from slice 3");
        Insert(3, "System", null, "a walk-through");
        Insert(4, null, 1, "a scout's proposal");
        Insert(5, null, null, "A null guard.\n\n---\n\nFiled by the heartbeat's reflection after a run on ticket #42, which noticed this in passing.");
        Insert(6, "Steward", null, null);

        await using (var ctx = Ctx()) await ctx.Database.MigrateAsync();

        await using var read = Ctx();
        var rows = await read.Tickets.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(
            new[] { TicketOriginKind.Owner, TicketOriginKind.Steward, TicketOriginKind.Steward,
                    TicketOriginKind.FoundInRun, TicketOriginKind.FoundInRun, TicketOriginKind.Steward },
            rows.Select(t => t.Origin).ToArray());
        Assert.Equal(1, rows[3].FoundOn);
        Assert.Equal(42, rows[4].FoundOn);
        Assert.All(rows, t => Assert.Null(t.IntakeChannel));
    }
}
