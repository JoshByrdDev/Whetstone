using Whetstone.Models;
using Whetstone.Services;
using Whetstone.Services.UnitReports;
using Xunit;
using Doc = Whetstone.Services.AnalysisDocument;

namespace Whetstone.Tests;

public class UnitMaterialTests
{
    private static readonly PlanUnit One = new(1, "01-first.md", true, false, new[] { "A1", "A2" }, "");
    private static readonly IReadOnlyList<PlanGroup> Groups = new[]
    {
        new PlanGroup("A customer", new[] { new PlanOutcomeText("A1", "can save an address"), new PlanOutcomeText("A2", "can pick a default") }),
    };

    private static Ticket T(int id, string plan = "p1", int unit = 1, TicketPhase phase = TicketPhase.Done,
        string outcomes = "A1", DateTime? merged = null, params int[] shots) => new()
    {
        Id = id, ProjectKey = "alpha", Title = $"Ticket {id}", Phase = phase,
        PlanKey = plan, PlanUnit = unit, PlanOutcomes = outcomes, MergedAt = merged,
        QaShots = shots.Select(o => new QaShot { TicketId = id, Order = o, Caption = $"#{id} shot {o}", ImageFile = $"{o}.png" }).ToList(),
    };

    private static TicketAnalysis A(int ticket, string tree, DateTime at, AnalysisLens lens = AnalysisLens.Full, int? flowShot = null) => new()
    {
        TicketId = ticket, TreeSha = tree, HeadSha = "h-" + tree, GeneratedAt = at, Lens = lens,
        BlocksJson = Doc.Serialize(new Doc.Doc("headline", new[]
        {
            new Doc.Section("How it works", new[]
            {
                new Doc.Block("userFlow")
                {
                    Nodes = new[] { new Doc.FlowNode("n1", "screen", "Addresses", null, null, "new", null, flowShot) },
                    Edges = Array.Empty<Doc.FlowEdge>(),
                },
            }),
        })),
    };

    private static UnitMaterial.Result Build(IReadOnlyList<Ticket> tickets, IReadOnlyList<TicketAnalysis>? analyses = null,
        Dictionary<int, UnitMaterial.BranchTip>? tips = null, Dictionary<int, DateTime>? doneAt = null,
        IReadOnlyList<StewardEntry>? entries = null, Dictionary<int, DateTime>? codedAt = null) =>
        UnitMaterial.Build(new UnitMaterial.Input("p1", One, Groups, tickets, Array.Empty<StewardOutcome>(),
            entries ?? Array.Empty<StewardEntry>(), analyses ?? Array.Empty<TicketAnalysis>(),
            tips ?? new(), doneAt ?? new(), codedAt));

    [Fact]
    public void A_unit_is_its_plans_unit_and_no_other_plans_unit_of_the_same_number()
    {
        var r = Build(new[] { T(1), T(2, plan: "p0"), T(3, unit: 2) });
        Assert.Equal(new[] { 1 }, r.Tickets.Select(t => t.Ticket.Id));
    }

    [Fact]
    public void Outcomes_are_the_lines_ruled_on_in_the_specs_words_under_their_heading()
    {
        var r = Build(Array.Empty<Ticket>());
        Assert.Equal(new[] { "A1", "A2" }, r.Outcomes.Select(o => o.Id));
        Assert.Equal("can pick a default", r.Outcomes[1].Text);
        Assert.Equal("A customer", r.Outcomes[1].Origin);
    }

    [Fact]
    public void The_pool_puts_the_last_ticket_to_land_first_and_one_still_open_last()
    {
        var early = new DateTime(2026, 10, 1);
        var r = Build(new[]
        {
            T(1, merged: early, shots: new[] { 1, 2 }),
            T(2, phase: TicketPhase.NeedsReview, shots: new[] { 1 }),
            T(3, shots: new[] { 1 }), // Done before merges were stamped: lands by its move to Done
        }, doneAt: new() { [3] = early.AddDays(2) });

        Assert.Equal(new[] { (3, 1), (1, 1), (1, 2), (2, 1) }, r.Shots.Select(s => (s.TicketId, s.TicketOrder)));
        Assert.Equal(new[] { 1, 2, 3, 4 }, r.Shots.Select(s => s.N));
        Assert.Contains(r.Missing, m => m.TicketId == 2 && m.Kind == UnitMaterial.MissingKind.Open);
    }

    [Fact]
    public void A_tickets_document_cites_shots_by_their_number_in_the_pool()
    {
        var r = Build(new[] { T(1, merged: DateTime.UtcNow.AddDays(-1), shots: new[] { 1 }), T(2, merged: DateTime.UtcNow, shots: new[] { 1, 2 }) },
            new[] { A(1, "t1", DateTime.UtcNow, flowShot: 1), A(2, "t2", DateTime.UtcNow, flowShot: 2) });

        int? Shot(int ticket) => r.Tickets.Single(t => t.Ticket.Id == ticket).Doc!.Sections[0].Blocks[0].Nodes![0].Shot;
        Assert.Equal(3, Shot(1)); // ticket 2 landed later, so its two shots come first
        Assert.Equal(2, Shot(2));
    }

    [Fact]
    public void A_shot_the_ticket_has_since_replaced_is_cited_as_none()
    {
        var r = Build(new[] { T(1, shots: new[] { 1 }) }, new[] { A(1, "t1", DateTime.UtcNow, flowShot: 5) });
        Assert.Null(r.Tickets[0].Doc!.Sections[0].Blocks[0].Nodes![0].Shot);
    }

    [Fact]
    public void A_done_ticket_whose_code_has_not_moved_keeps_the_analysis_written_at_review()
    {
        // TicketAnalysisService.IsStale would call this stale: the phase moved. The code did not.
        var r = Build(new[] { T(1) }, new[] { A(1, "t1", DateTime.UtcNow) }, tips: new() { [1] = new("h-other", "t1") });
        Assert.NotNull(r.Tickets[0].Analysis);
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void An_analysis_of_code_the_branch_has_moved_past_is_missing_not_material()
    {
        var r = Build(new[] { T(1) }, new[] { A(1, "old", DateTime.UtcNow) }, tips: new() { [1] = new("h-new", "new") });
        Assert.Null(r.Tickets[0].Analysis);
        Assert.Equal(UnitMaterial.MissingKind.StaleAnalysis, Assert.Single(r.Missing).Kind);
    }

    [Fact]
    public void A_rebase_before_the_merge_does_not_make_the_analysis_stale()
    {
        // The rebase brought other tickets' work into the branch's tree; its own change did not move.
        var analysed = new DateTime(2026, 10, 5, 12, 0, 0);
        var r = Build(new[] { T(1) }, new[] { A(1, "before-rebase", analysed) },
            tips: new() { [1] = new("h-after", "after-rebase") },
            codedAt: new() { [1] = analysed.AddHours(-2) });
        Assert.NotNull(r.Tickets[0].Analysis);
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void A_pass_through_implementation_after_the_analysis_makes_it_stale()
    {
        var analysed = new DateTime(2026, 10, 5, 12, 0, 0);
        var r = Build(new[] { T(1) }, new[] { A(1, "old", analysed) },
            tips: new() { [1] = new("h-new", "new") },
            codedAt: new() { [1] = analysed.AddHours(3) });
        Assert.Equal(UnitMaterial.MissingKind.StaleAnalysis, Assert.Single(r.Missing).Kind);
    }

    [Fact]
    public void Full_is_read_over_a_newer_narrower_lens()
    {
        var now = DateTime.UtcNow;
        var r = Build(new[] { T(1) }, new[] { A(1, "t1", now.AddHours(-1)), A(1, "t1", now, AnalysisLens.Code) });
        Assert.Equal(AnalysisLens.Full, r.Tickets[0].Analysis!.Lens);
    }

    [Fact]
    public void A_ticket_never_studied_is_missing()
    {
        var r = Build(new[] { T(1) });
        Assert.Equal(UnitMaterial.MissingKind.NoAnalysis, Assert.Single(r.Missing).Kind);
    }

    [Fact]
    public void Only_the_stewards_calls_on_this_units_tickets_are_its_decisions()
    {
        var r = Build(new[] { T(1) }, entries: new[]
        {
            new StewardEntry { Kind = StewardEntryKind.Call, TicketId = 1, Text = "kept it", At = DateTime.UtcNow },
            new StewardEntry { Kind = StewardEntryKind.Call, TicketId = 9, Text = "another unit's" },
            new StewardEntry { Kind = StewardEntryKind.BeforeRelease, TicketId = 1, Text = "a note" },
        });
        Assert.Equal("kept it", Assert.Single(r.Calls).Text);
    }
}
