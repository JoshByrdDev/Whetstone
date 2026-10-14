using Whetstone.Models;
using Whetstone.Services;
using Whetstone.Services.UnitReports;
using Xunit;
using Doc = Whetstone.Services.AnalysisDocument;

namespace Whetstone.Tests;

public class UnitReportPromptTests
{
    private static readonly PlanUnit One = new(1, "01-first.md", true, false, new[] { "A1", "A2" },
        "# Unit 1: Addresses\n\nStatus: agreed\n\n- A1 can save an address\n- A2 can pick a default");

    private static UnitMaterial.Result Material(bool missing = false)
    {
        var landed = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var tickets = new[]
        {
            new Ticket
            {
                Id = 11, ProjectKey = "alpha", Title = "Save an address", Phase = TicketPhase.Done,
                PlanKey = "p1", PlanUnit = 1, PlanOutcomes = "A1", MergedAt = landed.AddDays(-1),
                QaShots = { new QaShot { TicketId = 11, Order = 1, Caption = "the address list", ImageFile = "1.png" } },
            },
            new Ticket
            {
                Id = 12, ProjectKey = "alpha", Title = "Pick a default", Phase = missing ? TicketPhase.QaTesting : TicketPhase.Done,
                PlanKey = "p1", PlanUnit = 1, PlanOutcomes = "A1,A2", MergedAt = missing ? null : landed,
                QaShots = { new QaShot { TicketId = 12, Order = 1, Caption = "the list with a default marked", ImageFile = "1.png" } },
            },
        };
        TicketAnalysis Analysis(int id, int ticket) => new()
        {
            Id = id, TicketId = ticket, Lens = AnalysisLens.Full, HeadSha = "abc", GeneratedAt = landed,
            BlocksJson = Doc.Serialize(new Doc.Doc("headline", Array.Empty<Doc.Section>())),
        };
        var analyses = missing ? new[] { Analysis(5, 11) } : new[] { Analysis(5, 11), Analysis(6, 12) };
        var calls = new[] { new StewardEntry { Kind = StewardEntryKind.Call, TicketId = 12, Text = "kept the default per customer, not per visit", At = landed } };
        return UnitMaterial.Build(new UnitMaterial.Input("p1", One,
            new[] { new PlanGroup("A customer", new[] { new PlanOutcomeText("A1", "can save an address"), new PlanOutcomeText("A2", "can pick a default") }) },
            tickets, Array.Empty<StewardOutcome>(), calls, analyses, new Dictionary<int, UnitMaterial.BranchTip>(), new Dictionary<int, DateTime>()));
    }

    private static string Build(UnitMaterial.Result m, UnitRange? range = null) =>
        UnitReportPrompt.Build(new UnitReportPrompt.Context(
            "Alpha", "", "First plan", One, "Addresses", One.Text, m,
            new[] { new StewardOutcome { Plan = "p1", Unit = 1, Outcome = "A1", State = StewardOutcomeState.Done } },
            range ?? new UnitRange("main", "1111111111aa", "2222222222bb", 5, 2, new Dictionary<int, UnitMaterial.BranchTip>()),
            "C:/Repos/Alpha", new Dictionary<int, string> { [1] = "C:/data/run/unit-report/shots/1.png" },
            new Dictionary<int, IReadOnlyList<string>> { [12] = new[] { "QaTesting -> NeedsImplementation: the default was lost on refresh" } },
            "unit 2, Visits (B1, B2)",
            new Dictionary<int, string> { [11] = "C:/data/run/unit-report/analyses/11.json" }));

    [Fact]
    public void Every_outcome_is_named_with_its_state_and_the_tickets_that_carry_it()
    {
        var p = Build(Material());
        Assert.Contains("A1  [Done] (A customer) can save an address   - carried by #11, #12", p);
        Assert.Contains("A2  [NotStarted] (A customer) can pick a default   - carried by #12", p);
    }

    [Fact]
    public void The_shots_are_listed_by_pool_number_newest_landed_first_with_where_to_open_them()
    {
        var p = Build(Material());
        var first = p.IndexOf("- 1: #12", StringComparison.Ordinal);
        var second = p.IndexOf("- 2: #11", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first);
        Assert.Contains("in C:/data/run/unit-report/shots;", p);
    }

    [Fact]
    public void Commits_in_the_range_that_are_not_the_units_are_named_so_they_are_not_credited_to_it()
    {
        Assert.Contains("The other 3 are not this unit's work", Build(Material()));
    }

    [Fact]
    public void What_the_report_is_written_without_is_said_and_only_when_there_is_something()
    {
        Assert.DoesNotContain("What this report is written without", Build(Material()));
        var p = Build(Material(missing: true));
        Assert.Contains("What this report is written without", p);
        Assert.Contains("#12 is in QaTesting, not Done", p);
        Assert.Contains("#12 has no Classroom analysis", p);
    }

    [Fact]
    public void The_stewards_calls_bounces_and_next_unit_reach_the_run()
    {
        var p = Build(Material());
        Assert.Contains("kept the default per customer, not per visit", p);
        Assert.Contains("the default was lost on refresh", p);
        Assert.Contains("unit 2, Visits (B1, B2)", p);
    }

    [Fact]
    public void A_ticket_reaches_the_run_as_a_digest_with_the_way_to_its_whole_analysis()
    {
        var p = Build(Material());
        Assert.Contains("### #11 Save an address", p);
        Assert.Contains("Carries A1; landed 2026-10-01", p);
        Assert.Contains("Whole analysis: C:/data/run/unit-report/analyses/11.json", p);
        Assert.Contains("sent back 1x: QaTesting -> NeedsImplementation: the default was lost on refresh", p);
    }

    [Fact]
    public void A_digest_carries_the_flow_the_gaps_and_the_risks_and_not_the_rest()
    {
        var doc = new Doc.Doc("Saves an address", new[]
        {
            new Doc.Section("s", new[]
            {
                new Doc.Block("userFlow")
                {
                    Nodes = new[]
                    {
                        new Doc.FlowNode("a", "screen", "Home", null, null, "unchanged", null, null),
                        new Doc.FlowNode("b", "screen", "Address form", null, null, "new", null, 4),
                    },
                    Edges = Array.Empty<Doc.FlowEdge>(),
                },
                new Doc.Block("callout") { Tone = "risk", Title = "Two defaults", Text = "nothing stops it" },
                new Doc.Block("prose") { Text = "a long explanation the digest leaves out" },
            }),
        });
        var part = new UnitMaterial.TicketPart(new Ticket { Id = 3, Title = "t" }, new TicketAnalysis(), doc, null, new[] { "A1" });

        var d = UnitReportPrompt.Digest(part, null, null);
        Assert.Contains("Flow: Home [unchanged] > Address form [new, shot 4]", d);
        Assert.Contains("Risk: Two defaults - nothing stops it", d);
        Assert.DoesNotContain("long explanation", d);
    }

    [Fact]
    public void A_shot_the_pool_does_not_hold_is_dropped_from_the_document()
    {
        var doc = new Doc.Doc("h", new[]
        {
            new Doc.Section("s", new[]
            {
                new Doc.Block("userFlow")
                {
                    Nodes = new[]
                    {
                        new Doc.FlowNode("a", "screen", "A", null, null, "new", null, 2),
                        new Doc.FlowNode("b", "screen", "B", null, null, "new", null, 9),
                    },
                    Edges = Array.Empty<Doc.FlowEdge>(),
                },
            }),
        });

        var kept = UnitReportRunner.KeepPoolShots(doc, new HashSet<int> { 1, 2 });
        Assert.Equal(new int?[] { 2, null }, kept.Sections[0].Blocks[0].Nodes!.Select(n => n.Shot));
    }
}
