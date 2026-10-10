using System.Text.Json;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Classroom's two load-bearing rules — which tickets can be studied, and whether a stored
/// analysis still describes the ticket — plus the parser that stands between a model's output and
/// the page. All three fail silently if they are wrong: an over-permissive gate produces an analysis
/// with nothing to analyse, a broken staleness check presents last week's reading as current, and a
/// sloppy parser puts empty blocks on screen.
/// </summary>
public class TicketAnalysisTests
{
    private static Ticket T(TicketPhase phase, string? branch = "feature/x",
        string? summary = null, TicketPhase? beforeBlock = null) => new()
    {
        Id = 1,
        ProjectKey = "alpha",
        Phase = phase,
        Branch = branch,
        ImplementationSummary = summary,
        PhaseBeforeBlock = beforeBlock,
    };

    // ---- Eligibility --------------------------------------------------------

    [Fact]
    public void A_ticket_with_no_branch_cannot_be_studied()
    {
        var (eligible, reason) = TicketAnalysisService.Eligibility(T(TicketPhase.NeedsQa, branch: null));
        Assert.False(eligible);
        Assert.Contains("branch", reason);
    }

    [Theory]
    [InlineData(TicketPhase.Backlog)]
    [InlineData(TicketPhase.NeedsSpeculation)]
    [InlineData(TicketPhase.NeedsApproval)]
    [InlineData(TicketPhase.NeedsImplementation)]
    [InlineData(TicketPhase.Implementing)]
    public void Before_implementation_lands_there_is_nothing_to_teach(TicketPhase phase)
    {
        var (eligible, reason) = TicketAnalysisService.Eligibility(T(phase));
        Assert.False(eligible);
        Assert.Contains("implementation", reason);
    }

    [Theory]
    [InlineData(TicketPhase.NeedsImplementationReview)]
    [InlineData(TicketPhase.NeedsQa)]
    [InlineData(TicketPhase.QaTesting)]
    [InlineData(TicketPhase.NeedsRefinement)]
    [InlineData(TicketPhase.NeedsReview)]
    [InlineData(TicketPhase.Done)]
    public void Everything_past_implementation_can_be_studied(TicketPhase phase) =>
        Assert.True(TicketAnalysisService.Eligibility(T(phase)).eligible);

    /// <summary>
    /// The reason the gate is "has implementation landed" rather than a phase ordinal. QA bounces a
    /// ticket back to NeedsImplementation all the time, and that ticket's branch is full of real work
    /// worth reading — an ordinal check would call it ineligible and hide the most interesting case.
    /// </summary>
    [Fact]
    public void A_ticket_bounced_back_for_rework_is_still_studiable() =>
        Assert.True(TicketAnalysisService
            .Eligibility(T(TicketPhase.NeedsImplementation, summary: "Added the retry."))
            .eligible);

    /// <summary>
    /// Blocked sorts LAST in the phase enum, so an ordinal comparison would read every blocked ticket
    /// as "past everything". It is judged on where it actually stopped instead.
    /// </summary>
    [Fact]
    public void A_blocked_ticket_is_judged_on_where_it_stopped()
    {
        Assert.False(TicketAnalysisService
            .Eligibility(T(TicketPhase.Blocked, beforeBlock: TicketPhase.Speculating)).eligible);

        Assert.True(TicketAnalysisService
            .Eligibility(T(TicketPhase.Blocked, beforeBlock: TicketPhase.NeedsReview)).eligible);
    }

    // ---- Staleness ----------------------------------------------------------

    private static TicketAnalysis A(TicketPhase phase, string? sha) =>
        new() { PhaseAtGeneration = phase, HeadSha = sha };

    [Fact]
    public void An_analysis_of_the_current_phase_and_commit_is_fresh() =>
        Assert.False(TicketAnalysisService.IsStale(
            A(TicketPhase.NeedsReview, "abc123"), T(TicketPhase.NeedsReview), "abc123"));

    [Fact]
    public void A_moved_phase_makes_it_stale() =>
        Assert.True(TicketAnalysisService.IsStale(
            A(TicketPhase.NeedsQa, "abc123"), T(TicketPhase.NeedsReview), "abc123"));

    /// <summary>The sharper half: a ticket can sit in one phase across four more commits.</summary>
    [Fact]
    public void New_commits_in_the_same_phase_make_it_stale() =>
        Assert.True(TicketAnalysisService.IsStale(
            A(TicketPhase.NeedsReview, "abc123"), T(TicketPhase.NeedsReview), "def456"));

    /// <summary>
    /// A missing sha on either side means we cannot tell. Cannot-tell is reported as fresh rather
    /// than as a warning the reader has no way to act on.
    /// </summary>
    [Fact]
    public void An_unknown_commit_is_not_reported_as_stale()
    {
        Assert.False(TicketAnalysisService.IsStale(
            A(TicketPhase.NeedsReview, null), T(TicketPhase.NeedsReview), "abc123"));
        Assert.False(TicketAnalysisService.IsStale(
            A(TicketPhase.NeedsReview, "abc123"), T(TicketPhase.NeedsReview), null));
    }

    // ---- The document -------------------------------------------------------

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// The schema is handed to the CLI as one command-line argument after a compaction pass that
    /// parses it. Malformed, and every run fails at the point of spawning the process.
    /// </summary>
    [Fact]
    public void The_schema_handed_to_the_cli_is_valid_json()
    {
        var doc = JsonDocument.Parse(AnalysisDocument.JsonSchema);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("properties", out _));
    }

    [Fact]
    public void A_complete_document_survives_the_round_trip()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        {
          "headline": "The retry now backs off",
          "sections": [{
            "title": "The area",
            "blocks": [
              { "kind": "prose", "text": "It lives in the **dispatcher**." },
              { "kind": "readingPath", "steps": [
                  { "location": "Services/Dispatch.cs:88", "what": "the retry loop", "why": "start here" }
              ]},
              { "kind": "callout", "tone": "gotcha", "text": "The timer is wall-clock, not monotonic." },
              { "kind": "question", "text": "What happens on a clock change mid-retry?" }
            ]
          }]
        }
        """));

        Assert.Null(error);
        Assert.NotNull(doc);
        Assert.Equal("The retry now backs off", doc!.Headline);
        Assert.Equal(4, doc.Sections[0].Blocks.Count);

        // And back out again, since this is what actually gets stored and re-read.
        var reread = AnalysisDocument.Deserialize(AnalysisDocument.Serialize(doc));
        Assert.NotNull(reread);
        Assert.Equal("gotcha", reread!.Sections[0].Blocks[2].Tone);
        Assert.Equal("Services/Dispatch.cs:88", reread.Sections[0].Blocks[1].Steps![0].Location);
    }

    /// <summary>
    /// A block with nothing in it is dropped rather than fatal. The schema already stops the output
    /// being nonsense; what is left is the model filling six fields and leaving the seventh empty,
    /// and losing a three-minute run over that would be the wrong trade.
    /// </summary>
    [Fact]
    public void Empty_and_unknown_blocks_are_dropped_not_fatal()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{
            "title": "S",
            "blocks": [
              { "kind": "prose", "text": "" },
              { "kind": "prose" },
              { "kind": "keyValue", "items": [] },
              { "kind": "readingPath", "steps": [{ "location": "", "what": "x" }] },
              { "kind": "sonnet", "text": "not a block kind" },
              { "kind": "prose", "text": "the only real one" }
            ]
          }]
        }
        """));

        Assert.Null(error);
        Assert.Single(doc!.Sections[0].Blocks);
        Assert.Equal("the only real one", doc.Sections[0].Blocks[0].Text);
    }

    /// <summary>A titled section with nothing under it is the filler this design exists to avoid.</summary>
    [Fact]
    public void A_section_whose_blocks_all_drop_disappears_with_them()
    {
        var (doc, _) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [
            { "title": "Empty", "blocks": [{ "kind": "prose", "text": "" }] },
            { "title": "Real", "blocks": [{ "kind": "prose", "text": "something" }] }
          ]
        }
        """));

        Assert.Single(doc!.Sections);
        Assert.Equal("Real", doc.Sections[0].Title);
    }

    [Fact]
    public void A_document_with_nothing_usable_in_it_is_an_error()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        { "headline": "h", "sections": [{ "title": "S", "blocks": [{ "kind": "prose" }] }] }
        """));

        Assert.Null(doc);
        Assert.NotNull(error);
    }

    /// <summary>
    /// A short row is padded and a long one is trimmed. A table that renders one cell short is still
    /// readable; a dropped row is a rejected approach the reader never learns about.
    /// </summary>
    [Fact]
    public void Comparison_rows_are_squared_off_against_the_header()
    {
        var (doc, _) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "Why this way", "blocks": [{
            "kind": "comparison",
            "columns": ["Route", "Cost", "Ruled out by"],
            "rows": [["Poll", "cheap"], ["Webhook", "a", "b", "c"]]
          }]}]
        }
        """));

        var rows = doc!.Sections[0].Blocks[0].Rows!;
        Assert.All(rows, r => Assert.Equal(3, r.Count));
        Assert.Equal("", rows[0][2]);
        Assert.Equal("b", rows[1][2]);
    }

    /// <summary>
    /// Indentation survives. Every other field is trimmed, and applying that here would left-align
    /// every line of a snippet — destroying the one thing about quoted code that must be exact.
    /// </summary>
    [Fact]
    public void A_code_snippet_keeps_its_indentation()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "What changed", "blocks": [{
            "kind": "code",
            "path": "Services/Dispatch.cs",
            "startLine": 88,
            "language": "csharp",
            "text": "if (feeChargeIds.Any())\n{\n    Update(feeChargeIds);\n}\n\n",
            "note": "The `else` is gone."
          }]}]
        }
        """));

        Assert.Null(error);
        var block = doc!.Sections[0].Blocks[0];
        Assert.Equal("code", block.Kind);
        Assert.Equal("Services/Dispatch.cs", block.Path);
        Assert.Equal(88, block.StartLine);
        Assert.Equal("csharp", block.Language);
        // Leading whitespace kept, trailing blank lines dropped.
        Assert.Contains("\n    Update(feeChargeIds);", block.Text);
        Assert.EndsWith("}", block.Text);

        // And it survives storage, which is where a naive re-serialize would flatten it.
        var reread = AnalysisDocument.Deserialize(AnalysisDocument.Serialize(doc));
        Assert.Equal(block.Text, reread!.Sections[0].Blocks[0].Text);
    }

    [Fact]
    public void A_code_block_with_no_snippet_is_dropped()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "S", "blocks": [
            { "kind": "code", "path": "A.cs", "startLine": 3, "text": "   \n  " },
            { "kind": "prose", "text": "kept" }
          ]}]
        }
        """));

        Assert.Null(error);
        Assert.Single(doc!.Sections[0].Blocks);
        Assert.Equal("prose", doc.Sections[0].Blocks[0].Kind);
    }

    /// <summary>A snippet with no location still reads; it just cannot be opened.</summary>
    [Fact]
    public void A_code_snippet_without_a_path_is_kept()
    {
        var (doc, _) = AnalysisDocument.Parse(El("""
        { "headline": "h", "sections": [{ "title": "S", "blocks": [
          { "kind": "code", "text": "SELECT 1", "startLine": 0 }
        ]}]}
        """));

        var block = doc!.Sections[0].Blocks[0];
        Assert.Equal("SELECT 1", block.Text);
        Assert.Null(block.Path);
        Assert.Null(block.StartLine);   // 0 is not a line number
    }

    /// <summary>
    /// The "how it worked before" snippet: code that was changed or deleted has no line in today's
    /// file, so it carries a title and no startLine, and the renderer draws no line numbers. Getting
    /// this wrong points the reader's editor at whatever happens to occupy those lines now.
    /// </summary>
    [Fact]
    public void A_historical_snippet_keeps_its_label_and_stays_unnumbered()
    {
        var (doc, _) = AnalysisDocument.Parse(El("""
        { "headline": "h", "sections": [{ "title": "How it worked before", "blocks": [{
          "kind": "code",
          "title": "Before, on main",
          "path": "Services/TradeBasketDataService.cs",
          "language": "csharp",
          "text": "else if (feeChargeIds.Any())"
        }]}]}
        """));

        var block = doc!.Sections[0].Blocks[0];
        Assert.Equal("Before, on main", block.Title);
        Assert.Equal("Services/TradeBasketDataService.cs", block.Path);
        Assert.Null(block.StartLine);
    }

    [Fact]
    public void A_comparison_with_no_rows_is_dropped()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "S", "blocks": [
            { "kind": "comparison", "columns": ["A", "B"], "rows": [] },
            { "kind": "prose", "text": "kept" }
          ]}]
        }
        """));

        Assert.Null(error);
        Assert.Single(doc!.Sections[0].Blocks);
    }

    // ---- User flow ----------------------------------------------------------

    /// <summary>
    /// The graph the renderer computes every coordinate from, kept whole: ids, kinds, the change
    /// marking that makes it a diagram of a change rather than documentation, the actor, and the
    /// citation that turns a stop into a click.
    /// </summary>
    [Fact]
    public void A_user_flow_keeps_its_graph()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "S", "blocks": [{
            "kind": "userFlow",
            "title": "Assigning an advisor",
            "note": "The bulk import still bypasses this.",
            "nodes": [
              { "id": "a", "kind": "screen", "label": "Enroller roster", "change": "unchanged" },
              { "id": "b", "kind": "step", "label": "Assign advisor", "change": "new",
                "actor": "Enrolment admin", "explain": "Writes the join row and clears any existing advisor.",
                "refs": ["src/Roster.tsx:142", "Api/Enrollers.cs:88", "src/Picker.tsx:12", "src/Extra.tsx:1"] },
              { "id": "c", "kind": "decision", "label": "At caseload cap?", "change": "new" }
            ],
            "edges": [
              { "from": "a", "to": "b", "change": "new" },
              { "from": "b", "to": "c", "label": "save", "change": "new" }
            ]
          }]}]
        }
        """));

        Assert.Null(error);
        var block = doc!.Sections[0].Blocks[0];
        Assert.Equal("userFlow", block.Kind);
        Assert.Equal(3, block.Nodes!.Count);
        Assert.Equal(2, block.Edges!.Count);

        var step = block.Nodes[1];
        Assert.Equal("step", step.Kind);
        Assert.Equal("new", step.Change);
        Assert.Equal("Enrolment admin", step.Actor);
        Assert.Equal("Writes the join row and clears any existing advisor.", step.Explain);
        // Capped at three. A stop that cites four files is really two stops, and the fourth diff
        // would sit below the fold of a panel nobody scrolls.
        Assert.Equal(3, step.Refs!.Count);
        Assert.Equal("src/Roster.tsx:142", step.Refs[0]);
        Assert.Equal("save", block.Edges[1].Label);
        Assert.Equal("The bulk import still bypasses this.", block.Note);
    }

    /// <summary>
    /// A stop keeps the QA screenshot it names, and only the first stop to name one keeps it.
    /// <para>
    /// One screenshot is a picture of one place. A run that hangs the same shot on two stops has
    /// guessed on at least one of them, and the same picture appearing twice down a route is a
    /// route the reader can no longer trust.
    /// </para>
    /// </summary>
    [Fact]
    public void A_flow_stop_keeps_one_screenshot_and_no_shot_is_used_twice()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "S", "blocks": [{
            "kind": "userFlow",
            "nodes": [
              { "id": "a", "kind": "screen", "label": "Enroller roster", "change": "unchanged", "shot": 2 },
              { "id": "b", "kind": "screen", "label": "Advisor picker", "change": "new", "shot": 2 },
              { "id": "c", "kind": "end", "label": "Advisor shown", "change": "new", "shot": -1 },
              { "id": "d", "kind": "step", "label": "Save", "change": "new" }
            ],
            "edges": [
              { "from": "a", "to": "b", "change": "new" },
              { "from": "b", "to": "c", "change": "new" },
              { "from": "c", "to": "d", "change": "new" }
            ]
          }]}]
        }
        """));

        Assert.Null(error);
        var nodes = doc!.Sections[0].Blocks[0].Nodes!;
        Assert.Equal(2, nodes[0].Shot);
        Assert.Null(nodes[1].Shot);   // the second claim on shot 2
        Assert.Null(nodes[2].Shot);   // not an order any ticket has
        Assert.Null(nodes[3].Shot);
    }

    /// <summary>
    /// An edge naming a node that was never declared is a line to nowhere — and the layout ranks
    /// the graph by walking those edges, so it is not a cosmetic problem.
    /// </summary>
    [Fact]
    public void Edges_pointing_at_nodes_that_do_not_exist_are_dropped()
    {
        var (doc, _) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "S", "blocks": [{
            "kind": "userFlow",
            "nodes": [
              { "id": "a", "kind": "screen", "label": "One", "change": "unchanged" },
              { "id": "b", "kind": "step", "label": "Two", "change": "new" }
            ],
            "edges": [
              { "from": "a", "to": "b" },
              { "from": "b", "to": "ghost" },
              { "from": "a", "to": "a" },
              { "from": "a", "to": "b" }
            ]
          }]}]
        }
        """));

        var block = doc!.Sections[0].Blocks[0];
        // The self-loop and the duplicate go with it: neither can be drawn as a line between stops.
        var edge = Assert.Single(block.Edges!);
        Assert.Equal("unchanged", edge.Change);
    }

    /// <summary>A stop nothing reaches would read as a gap in the route, not as a mistake.</summary>
    [Fact]
    public void A_node_no_edge_touches_is_dropped()
    {
        var (doc, _) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "S", "blocks": [{
            "kind": "userFlow",
            "nodes": [
              { "id": "a", "kind": "screen", "label": "One", "change": "unchanged" },
              { "id": "b", "kind": "step", "label": "Two", "change": "new" },
              { "id": "orphan", "kind": "end", "label": "Nowhere", "change": "new" }
            ],
            "edges": [{ "from": "a", "to": "b" }]
          }]}]
        }
        """));

        var block = doc!.Sections[0].Blocks[0];
        Assert.Equal(2, block.Nodes!.Count);
        Assert.DoesNotContain(block.Nodes, n => n.Id == "orphan");
    }

    /// <summary>One box and no line is a label, not a flow.</summary>
    [Fact]
    public void A_flow_with_nothing_to_join_is_dropped()
    {
        var (doc, error) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "S", "blocks": [
            {
              "kind": "userFlow",
              "nodes": [{ "id": "a", "kind": "screen", "label": "Alone", "change": "new" }],
              "edges": []
            },
            { "kind": "prose", "text": "kept" }
          ]}]
        }
        """));

        Assert.Null(error);
        Assert.Single(doc!.Sections[0].Blocks);
        Assert.Equal("prose", doc.Sections[0].Blocks[0].Kind);
    }

    /// <summary>
    /// An analysis that drew no flow says so on the page, so a run skipping the section on every
    /// ticket is distinguishable from one correctly skipping it on this ticket.
    /// </summary>
    [Fact]
    public void A_stored_document_reports_whether_it_drew_a_flow()
    {
        var (withFlow, _) = AnalysisDocument.Parse(El("""
        {
          "headline": "h",
          "sections": [{ "title": "S", "blocks": [{
            "kind": "userFlow",
            "nodes": [
              { "id": "a", "kind": "screen", "label": "One", "change": "unchanged" },
              { "id": "b", "kind": "end", "label": "Two", "change": "new" }
            ],
            "edges": [{ "from": "a", "to": "b" }]
          }]}]
        }
        """));

        var (without, _) = AnalysisDocument.Parse(El("""
        { "headline": "h", "sections": [{ "title": "S", "blocks": [{ "kind": "prose", "text": "x" }] }] }
        """));

        Assert.True(AnalysisDocument.HasKind(AnalysisDocument.Serialize(withFlow!), "userFlow"));
        Assert.False(AnalysisDocument.HasKind(AnalysisDocument.Serialize(without!), "userFlow"));
        Assert.False(AnalysisDocument.HasKind("not json at all", "userFlow"));
    }

    // ---- Schema -------------------------------------------------------------

    /// <summary>
    /// The whole migration chain applies to an empty database and leaves the analyses table behind.
    /// The rest of the suite uses EnsureCreated, which never runs a migration, so nothing else here
    /// would notice a broken one.
    /// </summary>
    [Fact]
    public async Task The_migration_chain_creates_the_analyses_table()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(conn).Options;

        await using var ctx = new DashboardDbContext(options);
        await ctx.Database.MigrateAsync();

        var ticket = new Ticket { ProjectKey = "alpha", Title = "t", Phase = TicketPhase.NeedsReview };
        ctx.Tickets.Add(ticket);
        await ctx.SaveChangesAsync();

        ctx.TicketAnalyses.Add(new TicketAnalysis
        {
            TicketId = ticket.Id,
            ProjectKey = "alpha",
            PhaseAtGeneration = TicketPhase.NeedsReview,
            Lens = AnalysisLens.Decisions,
            Headline = "h",
            BlocksJson = "{}",
        });
        await ctx.SaveChangesAsync();

        var stored = await ctx.TicketAnalyses.SingleAsync();
        Assert.Equal(AnalysisLens.Decisions, stored.Lens);
        Assert.Equal(TicketPhase.NeedsReview, stored.PhaseAtGeneration);
    }

    /// <summary>
    /// Timestamps come back out of SQLite marked UTC.
    /// <para>
    /// SQLite has no date type, so without the converter every read is
    /// <c>DateTimeKind.Unspecified</c> — which serializes to JSON with no <c>Z</c>, and a browser
    /// reads a zoneless ISO string as local time. On a UTC-5 machine that put every timestamp five
    /// hours in the future, and anything recent rendered as "just now" forever.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Timestamps_come_back_from_sqlite_as_utc()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(conn).Options;

        var written = DateTime.UtcNow;
        await using (var ctx = new DashboardDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            var t = new Ticket { ProjectKey = "alpha", Title = "t" };
            ctx.Tickets.Add(t);
            await ctx.SaveChangesAsync();
            ctx.TicketAnalyses.Add(new TicketAnalysis
            {
                TicketId = t.Id, ProjectKey = "alpha", PhaseAtGeneration = TicketPhase.NeedsReview,
                Headline = "h", BlocksJson = "{}", GeneratedAt = written,
            });
            await ctx.SaveChangesAsync();
        }

        // A fresh context, so the value is genuinely read from storage rather than served out of the
        // change tracker — which keeps the original Kind and would hide the bug entirely.
        await using var reader = new DashboardDbContext(options);
        var stored = await reader.TicketAnalyses.AsNoTracking().SingleAsync();

        Assert.Equal(DateTimeKind.Utc, stored.GeneratedAt.Kind);
        Assert.Equal(written, stored.GeneratedAt, TimeSpan.FromSeconds(1));

        // The whole point: serialized, it carries a zone the browser will honour.
        Assert.EndsWith("Z\"", System.Text.Json.JsonSerializer.Serialize(stored.GeneratedAt));

        // And the nullable path, which needs a converter of its own.
        var ticket = await reader.Tickets.AsNoTracking().SingleAsync();
        Assert.Equal(DateTimeKind.Utc, ticket.CreatedAt.Kind);
    }

    /// <summary>
    /// Enums are stored as their names, not their ordinals. Reordering either enum would otherwise
    /// silently reinterpret every row already on disk.
    /// </summary>
    [Fact]
    public async Task Enums_are_stored_as_names()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(conn).Options;

        await using var ctx = new DashboardDbContext(options);
        await ctx.Database.MigrateAsync();
        var ticket = new Ticket { ProjectKey = "alpha", Title = "t" };
        ctx.Tickets.Add(ticket);
        await ctx.SaveChangesAsync();
        ctx.TicketAnalyses.Add(new TicketAnalysis
        {
            TicketId = ticket.Id, ProjectKey = "alpha",
            PhaseAtGeneration = TicketPhase.QaTesting, Lens = AnalysisLens.Testing,
            Headline = "h", BlocksJson = "{}",
        });
        await ctx.SaveChangesAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Lens, PhaseAtGeneration FROM TicketAnalyses";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Testing", reader.GetString(0));
        Assert.Equal("QaTesting", reader.GetString(1));
    }
}
