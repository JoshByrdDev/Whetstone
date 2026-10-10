using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The owner's filing tags. Three things are worth pinning down here and none of them is the CRUD:
/// that two spellings of the same word are one label, that a label stops existing when the last
/// ticket takes it off, and that a rename onto a name already in use merges instead of failing.
/// </summary>
public class TicketLabelTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public TicketLabelTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);
    private TicketLabelService NewSvc(DashboardDbContext ctx) => new(ctx);

    private static async Task<Ticket> Seed(DashboardDbContext ctx, string title = "a ticket")
    {
        var t = new Ticket { ProjectKey = "alpha", Title = title, Phase = TicketPhase.Backlog };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    // ---- Normalising what was typed ----

    [Theory]
    [InlineData("  ui  ", "ui")]
    [InlineData("tech  debt", "tech debt")]
    [InlineData("needs\tdesign", "needs design")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_tidies_or_rejects(string? raw, string? expected) =>
        Assert.Equal(expected, TicketLabelService.Normalize(raw));

    [Fact]
    public void Normalize_caps_at_the_column_width()
    {
        var long_ = new string('x', Label.MaxLength + 20);
        Assert.Equal(Label.MaxLength, TicketLabelService.Normalize(long_)!.Length);
    }

    // ---- Assigning ----

    [Fact]
    public async Task Assign_creates_the_label_the_first_time_and_reuses_it_after()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var a = await Seed(ctx);
        var b = await Seed(ctx, "another");

        await svc.AssignAsync(a.Id, "ui");
        await svc.AssignAsync(b.Id, "ui");

        Assert.Single(ctx.Labels);
        var usage = Assert.Single(await svc.ListAsync());
        Assert.Equal("ui", usage.Name);
        Assert.Equal(2, usage.Count);
    }

    /// <summary>
    /// The point of the NOCASE collation: typing a word you already use in a different case must
    /// find the label you have, not make a second one that looks identical in the picker.
    /// </summary>
    [Fact]
    public async Task Assign_matches_an_existing_label_regardless_of_case()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var a = await Seed(ctx);
        var b = await Seed(ctx, "another");

        await svc.AssignAsync(a.Id, "ui");
        await svc.AssignAsync(b.Id, "UI");

        Assert.Single(ctx.Labels);
        // The first spelling wins - the second ticket wears the label that already existed.
        Assert.Equal("ui", (await svc.ForTicketAsync(b.Id)).Single().Name);
    }

    [Fact]
    public async Task Assign_is_idempotent()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var t = await Seed(ctx);

        await svc.AssignAsync(t.Id, "ui");
        var after = await svc.AssignAsync(t.Id, "ui");

        Assert.Single(after!);
        Assert.Equal(1, await ctx.TicketLabels.CountAsync());
    }

    [Fact]
    public async Task Assign_refuses_an_unknown_ticket_without_creating_the_label()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);

        Assert.Null(await svc.AssignAsync(4242, "ui"));
        Assert.Empty(ctx.Labels);
    }

    // ---- Unassigning, and the garbage collection that keeps the picker honest ----

    [Fact]
    public async Task Unassign_deletes_a_label_nobody_is_wearing_any_more()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var t = await Seed(ctx);

        await svc.AssignAsync(t.Id, "typoo");
        var labelId = (await svc.ForTicketAsync(t.Id)).Single().Id;
        var after = await svc.UnassignAsync(t.Id, labelId);

        Assert.Empty(after!);
        Assert.Empty(ctx.Labels);
    }

    [Fact]
    public async Task Unassign_keeps_a_label_another_ticket_still_wears()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var a = await Seed(ctx);
        var b = await Seed(ctx, "another");

        await svc.AssignAsync(a.Id, "ui");
        await svc.AssignAsync(b.Id, "ui");
        var labelId = (await svc.ForTicketAsync(a.Id)).Single().Id;
        await svc.UnassignAsync(a.Id, labelId);

        Assert.Single(ctx.Labels);
        Assert.Single(await svc.ForTicketAsync(b.Id));
    }

    [Fact]
    public async Task Unassign_reports_a_label_the_ticket_was_not_wearing()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var t = await Seed(ctx);

        Assert.Null(await svc.UnassignAsync(t.Id, 99));
    }

    [Fact]
    public async Task Deleting_a_ticket_takes_its_assignments_with_it()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var t = await Seed(ctx);
        await svc.AssignAsync(t.Id, "ui");

        ctx.Tickets.Remove(await ctx.Tickets.FindAsync(t.Id) ?? throw new InvalidOperationException());
        await ctx.SaveChangesAsync();

        Assert.Empty(ctx.TicketLabels);
        // The label itself survives an orphaning it never got the chance to notice. Harmless: it is
        // offered by the picker, and the first ticket to wear it again puts it back in use.
        Assert.Single(ctx.Labels);
    }

    // ---- Renaming ----

    [Fact]
    public async Task Rename_changes_the_word_everywhere_at_once()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var a = await Seed(ctx);
        var b = await Seed(ctx, "another");

        await svc.AssignAsync(a.Id, "gui");
        await svc.AssignAsync(b.Id, "gui");
        var labelId = (await svc.ForTicketAsync(a.Id)).Single().Id;

        var renamed = await svc.RenameAsync(labelId, "ui");

        Assert.Equal("ui", renamed!.Name);
        Assert.Equal(2, renamed.Count);
        Assert.Equal("ui", (await svc.ForTicketAsync(b.Id)).Single().Name);
    }

    /// <summary>
    /// Renaming onto a name already in use is what someone does to clean up a near-duplicate, so it
    /// merges: every ticket ends up wearing the survivor exactly once, and the old label is gone.
    /// </summary>
    [Fact]
    public async Task Rename_onto_an_existing_name_merges_the_two()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var both = await Seed(ctx);
        var onlyOld = await Seed(ctx, "another");

        await svc.AssignAsync(both.Id, "ui");
        await svc.AssignAsync(both.Id, "gui");
        await svc.AssignAsync(onlyOld.Id, "gui");
        var oldId = (await svc.ListAsync()).Single(l => l.Name == "gui").Id;

        var survivor = await svc.RenameAsync(oldId, "ui");

        Assert.Equal("ui", survivor!.Name);
        Assert.Equal(2, survivor.Count);
        Assert.Single(ctx.Labels);
        // The ticket that wore both keeps one chip, not two.
        Assert.Single(await svc.ForTicketAsync(both.Id));
        Assert.Equal("ui", (await svc.ForTicketAsync(onlyOld.Id)).Single().Name);
    }

    [Fact]
    public async Task Rename_refuses_an_empty_name()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var t = await Seed(ctx);
        await svc.AssignAsync(t.Id, "ui");
        var labelId = (await svc.ForTicketAsync(t.Id)).Single().Id;

        Assert.Null(await svc.RenameAsync(labelId, "   "));
        Assert.Equal("ui", (await svc.ForTicketAsync(t.Id)).Single().Name);
    }

    // ---- Retiring a label from the whole board ----

    [Fact]
    public async Task Delete_strips_the_label_from_every_ticket_wearing_it()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var a = await Seed(ctx);
        var b = await Seed(ctx, "another");

        await svc.AssignAsync(a.Id, "ui");
        await svc.AssignAsync(b.Id, "ui");
        var labelId = (await svc.ForTicketAsync(a.Id)).Single().Id;

        Assert.True(await svc.DeleteAsync(labelId));
        Assert.Empty(await svc.ForTicketAsync(a.Id));
        Assert.Empty(await svc.ForTicketAsync(b.Id));
        Assert.Empty(ctx.Labels);
    }

    // ---- What the board reads ----

    [Fact]
    public async Task Assignments_reports_only_the_tickets_wearing_something()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var labelled = await Seed(ctx);
        var bare = await Seed(ctx, "unlabelled");

        await svc.AssignAsync(labelled.Id, "ui");
        await svc.AssignAsync(labelled.Id, "api");

        var map = await svc.AssignmentsAsync();

        Assert.False(map.ContainsKey(bare.Id));
        // Alphabetical, so a row's chips do not reshuffle between polls.
        Assert.Equal(new[] { "api", "ui" }, map[labelled.Id].Select(l => l.Name));
    }

    /// <summary>
    /// The picker leads with what is actually in use, so the labels already doing work are one click
    /// away and the one-offs sink to the bottom.
    /// </summary>
    [Fact]
    public async Task List_orders_by_usage_then_name()
    {
        using var ctx = NewCtx();
        var svc = NewSvc(ctx);
        var a = await Seed(ctx);
        var b = await Seed(ctx, "another");

        await svc.AssignAsync(a.Id, "zebra");
        await svc.AssignAsync(b.Id, "zebra");
        await svc.AssignAsync(a.Id, "alpha");
        await svc.AssignAsync(a.Id, "beta");

        Assert.Equal(new[] { "zebra", "alpha", "beta" }, (await svc.ListAsync()).Select(l => l.Name));
    }
}
