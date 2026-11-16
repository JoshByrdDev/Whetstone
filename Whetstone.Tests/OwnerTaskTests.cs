using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The owner's to-do list (docs/owner-todo-plan.md): one list of what only the owner can do, that
/// agents fill and cannot empty, and that refuses a duplicate an agent did not rule out.
/// </summary>
public class OwnerTaskTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public OwnerTaskTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    private async Task<T> With<T>(Func<OwnerTaskService, Task<T>> act)
    {
        await using var ctx = NewCtx();
        return await act(new OwnerTaskService(ctx));
    }

    private async Task<int> Ticket(TicketPhase phase = TicketPhase.Implementing, string project = "alpha")
    {
        await using var ctx = NewCtx();
        var t = new Ticket { ProjectKey = project, Title = "t", Phase = phase };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t.Id;
    }

    private const string Body =
        "**Why:** payments cannot be tested end to end without it.\n**Where:** the vendor's dashboard, Developers > API keys.\n"
        + "**Steps:** create a restricted key.\n**Done when:** STRIPE_API_KEY is set in the stack's .env.\n**Hand back:** .env, STRIPE_API_KEY.";

    private static OwnerTaskDraft Draft(string title = "Get a Stripe test API key", string? key = null, int? ticket = null,
        string body = Body, int[]? notDup = null, string kind = "secret") =>
        new("alpha", title, body, kind, key, ticket, null, false, notDup);

    private static int IdOf(OwnerTaskReply r) => (int)r.Data!.GetType().GetProperty("Id")!.GetValue(r.Data)!;

    [Fact]
    public async Task An_agent_adds_an_item_and_reads_it_back_by_its_ref()
    {
        var ticket = await Ticket();
        var r = await With(s => s.AddAsync(Draft(ticket: ticket), "worker"));
        Assert.Equal("added", r.Answer);
        var id = IdOf(r);

        var t = await With(s => s.GetAsync(OwnerTaskService.ParseRef($"OT-{id}")!.Value));
        Assert.NotNull(t);
        Assert.Equal(OwnerTaskKind.Secret, t!.Kind);
        Assert.Equal(ticket, t.TicketId);
        Assert.Equal("worker", t.CreatedBy);
        Assert.Contains($"OT-{id}", OwnerTaskService.Markdown(t, "http://localhost:5338"));
        Assert.Contains("/done", OwnerTaskService.Markdown(t, "http://localhost:5338"));
    }

    [Theory]
    [InlineData("14", 14)]
    [InlineData("OT-14", 14)]
    [InlineData("ot14", 14)]
    [InlineData("OT-x", null)]
    [InlineData("", null)]
    public void A_ref_is_read_however_it_is_said(string said, int? id) => Assert.Equal(id, OwnerTaskService.ParseRef(said));

    [Fact]
    public async Task An_agent_must_write_a_body_a_fresh_session_can_carry_out()
    {
        var r = await With(s => s.AddAsync(Draft(body: "get the key"), "worker"));
        Assert.Equal("bad-request", r.Answer);
        // The owner writing their own reminder needs no essay.
        Assert.Equal("added", (await With(s => s.AddAsync(Draft(body: ""), "owner"))).Answer);
    }

    [Fact]
    public async Task The_same_key_lands_on_the_existing_item_as_another_asker()
    {
        var a = await Ticket();
        var b = await Ticket();
        var first = await With(s => s.AddAsync(Draft(key: "secret:STRIPE_API_KEY", ticket: a), "worker"));
        var second = await With(s => s.AddAsync(Draft(title: "Need the payments key", key: "secret:stripe_api_key", ticket: b), "worker"));

        Assert.Equal("merged", second.Answer);
        Assert.Equal(IdOf(first), IdOf(second));
        await using var ctx = NewCtx();
        Assert.Equal(1, await ctx.OwnerTasks.CountAsync());
        Assert.Contains(await ctx.OwnerTaskNotes.ToListAsync(), n => n.Kind == OwnerTaskNoteKind.AlsoAsked && n.TicketId == b);
    }

    [Fact]
    public async Task Something_that_looks_like_an_item_is_refused_until_each_candidate_is_ruled_out()
    {
        var first = IdOf(await With(s => s.AddAsync(Draft(), "worker")));

        var again = await With(s => s.AddAsync(Draft(title: "Stripe test API key for checkout"), "worker"));
        Assert.Equal("possible-duplicate", again.Answer);
        Assert.Contains($"OT-{first}", System.Text.Json.JsonSerializer.Serialize(again.Data));

        var ruledOut = await With(s => s.AddAsync(Draft(title: "Stripe test API key for checkout", notDup: new[] { first }), "worker"));
        Assert.Equal("added", ruledOut.Answer);
    }

    [Fact]
    public async Task A_shared_identifier_is_the_same_thing_whatever_the_title_says()
    {
        IdOf(await With(s => s.AddAsync(Draft(), "worker")));
        var r = await With(s => s.AddAsync(Draft(title: "Payments credentials",
            body: "Put the value in STRIPE_API_KEY so the checkout tests stop skipping. Ask the vendor for a restricted key."), "worker"));
        Assert.Equal("possible-duplicate", r.Answer);
    }

    [Fact]
    public async Task Unrelated_items_do_not_collide()
    {
        await With(s => s.AddAsync(Draft(), "worker"));
        var r = await With(s => s.AddAsync(Draft(title: "Create the uploads S3 bucket in us-east-1", kind: "infra",
            body: "**Why:** attachments need somewhere to go.\n**Where:** the cloud console.\n**Done when:** the bucket exists."), "worker"));
        Assert.Equal("added", r.Answer);
    }

    [Fact]
    public async Task Find_returns_the_item_by_the_name_of_its_thing()
    {
        var id = IdOf(await With(s => s.AddAsync(Draft(), "worker")));
        var hits = await With(s => s.FindAsync("alpha", "STRIPE_API_KEY"));
        Assert.Equal(id, hits.First().Task.Id);
    }

    [Fact]
    public async Task A_suggestion_does_not_close_an_item()
    {
        var id = IdOf(await With(s => s.AddAsync(Draft(), "worker")));
        Assert.Equal("recorded", (await With(s => s.SuggestAsync(id, "done", "STRIPE_API_KEY is present in .env now", "worker"))).Answer);
        var t = await With(s => s.GetAsync(id));
        Assert.Equal(OwnerTaskStatus.Open, t!.Status);
        Assert.Equal("done", t.Suggestion);
    }

    [Fact]
    public async Task Done_takes_no_note_when_the_owner_gives_none()
    {
        var id = IdOf(await With(s => s.AddAsync(Draft(), "worker")));
        Assert.Equal("recorded", (await With(s => s.DoneAsync(id, " "))).Answer);

        await using var ctx = NewCtx();
        var t = await ctx.OwnerTasks.Include(x => x.Notes).SingleAsync();
        Assert.Equal(OwnerTaskStatus.Done, t.Status);
        Assert.Null(t.DoneNote);
        Assert.Contains(t.Notes, n => n.Text == "Done.");
    }

    [Fact]
    public async Task Done_tells_the_ticket_and_the_steward()
    {
        var ticket = await Ticket(TicketPhase.Blocked);
        var finished = await Ticket(TicketPhase.Done);
        var id = IdOf(await With(s => s.AddAsync(Draft(ticket: ticket), "worker")));
        await With(s => s.AppendAsync(id, "The done ticket needed it too.", finished, "worker"));

        Assert.Equal("recorded", (await With(s => s.DoneAsync(id, "Key is in .env as STRIPE_API_KEY.", "session"))).Answer);

        await using var ctx = NewCtx();
        var t = await ctx.OwnerTasks.SingleAsync();
        Assert.Equal(OwnerTaskStatus.Done, t.Status);
        Assert.Equal("Key is in .env as STRIPE_API_KEY.", t.DoneNote);
        // An owner note on the open ticket, so its next run reads what was done; none on a finished one.
        var notes = await ctx.Comments.ToListAsync();
        Assert.Single(notes);
        Assert.Equal(ticket, notes[0].TicketId);
        Assert.Equal(AuthorKind.Human, notes[0].Author);
        Assert.Contains($"OT-{id}", notes[0].Body);
        Assert.Contains($"OT-{id}", (await ctx.StewardStates.FindAsync("alpha"))!.DueReason);
    }

    [Fact]
    public async Task Waiting_dropping_and_reopening_are_the_owners_words()
    {
        var id = IdOf(await With(s => s.AddAsync(Draft(), "worker")));
        Assert.Equal("bad-request", (await With(s => s.WaitAsync(id, "", null))).Answer);
        Assert.Equal("recorded", (await With(s => s.WaitAsync(id, "the vendor's approval", DateTime.UtcNow.AddDays(3)))).Answer);
        Assert.Equal(OwnerTaskStatus.Waiting, (await With(s => s.GetAsync(id)))!.Status);
        // Waiting until a later day is not counted on the badge; open is.
        Assert.Equal(0, await With(s => s.OpenCountAsync()));

        Assert.Equal("recorded", (await With(s => s.DropAsync(id, "The feature was cut."))).Answer);
        Assert.Empty(await With(s => s.ListAsync("alpha")));
        Assert.Single(await With(s => s.ListAsync("alpha", "dropped")));

        Assert.Equal("recorded", (await With(s => s.ReopenAsync(id, "It is back in."))).Answer);
        Assert.Equal(1, await With(s => s.OpenCountAsync()));
    }

    [Fact]
    public async Task An_owners_own_item_has_no_project_and_shows_on_every_projects_list()
    {
        var r = await With(s => s.AddAsync(new OwnerTaskDraft(null, "Renew the domain", "", "account"), "owner"));
        Assert.Equal("added", r.Answer);
        Assert.Single(await With(s => s.ListAsync("alpha")));
        Assert.Single(await With(s => s.ListAsync("none")));
    }

    [Fact]
    public async Task What_holds_a_release_is_read_first()
    {
        var a = IdOf(await With(s => s.AddAsync(new OwnerTaskDraft("alpha", "Plain thing", "", "action"), "owner")));
        var b = IdOf(await With(s => s.AddAsync(new OwnerTaskDraft("alpha", "Gate thing", "", "config", HoldsRelease: true), "owner")));
        var list = await With(s => s.ListAsync());
        Assert.Equal(new[] { b, a }, list.Select(t => t.Id));
        Assert.Contains("Holds a release", OwnerTaskService.ListMarkdown(list, "http://localhost:5338"));
    }

    [Theory]
    [InlineData("Read the new trial-pricing words (#12, ruling 3).")]
    [InlineData("Check two pieces of wording a member's customers still see")]
    [InlineData("A line for docs/PLAN.md (slice 4, D1), as the worker wrote it")]
    [InlineData("Add a CLAUDE.md line (Database conventions) from #20")]
    [InlineData("Update slice 9's wording for the portal tabs")]
    [InlineData("Two lines for your docs, from #21")]
    [InlineData("Set `WarehouseHashKey` before the push")]
    public async Task Wording_doc_lines_and_unreadable_titles_are_not_jobs(string title)
    {
        var r = await With(s => s.AddAsync(Draft(title: title), "steward"));
        Assert.Equal("not-a-job", r.Answer);
        // The owner may still write whatever they like on their own list.
        Assert.NotEqual("not-a-job", (await With(s => s.AddAsync(Draft(title: title, notDup: new[] { 1, 2, 3 }), "owner"))).Answer);
    }

    [Theory]
    [InlineData("Open the analytics account so the warehouse has somewhere to load")]
    [InlineData("Set the warehouse hash secret before the next deploy, or it fails")]
    [InlineData("Paste the new importer prompt into the scheduled task")]
    public void Plain_jobs_pass(string title) => Assert.Null(OwnerTaskService.NotAJob(title));

    [Fact]
    public async Task An_item_that_is_reading_moves_to_the_release_notes()
    {
        var ticket = await Ticket(TicketPhase.Done);
        var id = IdOf(await With(s => s.AddAsync(new OwnerTaskDraft("alpha", "Customer words on the plans table", "They say None at $0.", "action", TicketId: ticket), "owner")));
        Assert.Equal("bad-request", (await With(s => s.ToReleaseNoteAsync(id, "action"))).Answer);
        Assert.Equal("recorded", (await With(s => s.ToReleaseNoteAsync(id, "words"))).Answer);

        await using var ctx = NewCtx();
        var t = await ctx.OwnerTasks.SingleAsync();
        Assert.Equal(OwnerTaskStatus.Dropped, t.Status);
        var note = await ctx.StewardEntries.SingleAsync();
        Assert.Equal(StewardReleaseKind.Words, note.Release);
        Assert.Equal(ticket, note.TicketId);
        Assert.Contains("None at $0", note.Text);
        Assert.Empty(await ctx.Comments.ToListAsync());
    }
}
