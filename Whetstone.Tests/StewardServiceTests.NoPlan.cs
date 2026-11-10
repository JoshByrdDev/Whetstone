using System.Text.Json;
using Whetstone.Services;

namespace Whetstone.Tests;

/// <summary>
/// docs/slice-branches.md, piece 7: on a project with slice branches the plan goes. Its key is never
/// read for counting, past plans, the one-plan lock and starting a plan are gone, and so are the
/// register's import and its pairs. Open slices are capped at slicesInFlight.
/// </summary>
public partial class StewardServiceTests
{
    private const string KeyedPlan = """
        "plan": { "key": "p2", "name": "Plan two", "units": "docs/units", "charter": "docs/charter.md", "master": "docs/master.md" },
        "pastPlans": [ { "key": "p1", "name": "Plan one", "units": "docs/old" } ],
        "syncFrom": "develop"
        """;

    private ProjectConfig Cfg() => new ProjectRegistry(new DashboardPaths(_dataRoot)).GetProjectConfig("alpha");

    [Fact]
    public void A_switched_project_reads_slices_with_no_plan_around_them_and_keeps_what_was_written()
    {
        Configure("manage", KeyedPlan);
        var before = Cfg().Steward;
        Assert.Equal("p2", before.Plan.Key);
        Assert.Single(before.PastPlans);
        Assert.Equal("develop", before.SyncFrom);

        SwitchToSliceBranches();
        var cfg = Cfg();
        var s = cfg.Steward;
        Assert.Equal("", s.Plan.Key);
        Assert.Equal("", s.Plan.Name);
        Assert.Equal("", s.Plan.Charter);
        Assert.Equal("", s.Plan.Master);
        Assert.Empty(s.PastPlans);
        Assert.Equal("", s.SyncFrom);
        Assert.Equal("docs/units", s.Plan.UnitsDir);
        Assert.Null(s.PlanByKey("p1"));
        Assert.Equal(3, s.SlicesInFlight);
        // The migration still reads what the file says.
        Assert.Equal("p2", cfg.StewardAsWritten.Plan.Key);
        Assert.Equal("develop", cfg.StewardAsWritten.SyncFrom);
    }

    [Fact]
    public void Slices_name_the_folder_and_how_many_may_be_open()
    {
        Configure("manage", """ "slices": "docs/slices", "slicesInFlight": 2 """);
        SwitchToSliceBranches();
        Assert.Equal("docs/slices", Cfg().Steward.Plan.UnitsDir);
        Assert.Equal(2, Cfg().Steward.SlicesInFlight);
    }

    [Fact]
    public async Task A_ticket_that_carries_an_old_plans_key_is_not_counted_against_a_slice()
    {
        Configure("manage", KeyedPlan);
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        // Unit 1 of a plan before the switch, still open: history, not slice 1's.
        await Built(1, "A1", planKey: "p2", phase: Whetstone.Models.TicketPhase.NeedsImplementation);
        var reply = await In(s => s.OwnerAbandonsUnitAsync("alpha", 1, null));
        Assert.NotEqual("tickets-open", reply.Answer);
        Assert.Equal("abandoned", reply.Answer);

        // One filed with no key is the slice's.
        _git.Second = AgreedSecond;
        await OpenSlice(2, "slice/02-second");
        await Built(2, "A1", phase: Whetstone.Models.TicketPhase.NeedsImplementation);
        Assert.Equal("tickets-open", (await In(s => s.OwnerAbandonsUnitAsync("alpha", 2, null))).Answer);
    }

    [Fact]
    public async Task There_is_no_plan_to_start_and_no_import_or_pairs()
    {
        Configure("manage", """ "plan": { "units": "docs/units" } """);
        SwitchToSliceBranches();
        var start = await In(s => s.OwnerStartsPlanAsync("alpha", "next", "The next one", null, "charter", "idea",
            new[] { new StewardNewIdea("Cards", "A person can save a card.") }));
        Assert.Equal("slice-branches", start.Answer);
        Assert.Empty(_git.Committed);

        Assert.Equal("slice-branches", (await In(s => s.OwnerBuildsRegisterAsync("alpha"))).Answer);
        Assert.Equal("slice-branches", (await In(s => s.OwnerSettlesConflictAsync("alpha", 1, true, "x"))).Answer);

        // Reading the register imports nothing: it starts as it is.
        var view = await In(s => s.RegisterViewAsync("alpha"));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(view.Data));
        Assert.Equal(0, doc.RootElement.GetProperty("decisions").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("mayConflict").GetArrayLength());
    }

    [Fact]
    public async Task No_more_slices_are_agreed_than_the_project_builds_at_once()
    {
        Configure("manage", """ "plan": { "units": "docs/units" }, "slicesInFlight": 1 """);
        SwitchToSliceBranches();
        await OpenSlice(1, "slice/01-first");
        _git.Second = "> **Status: draft, awaiting the owner.**\n\n- **A1.** One.\n";
        var full = await In(s => s.OwnerAgreesUnitAsync("alpha", 2));
        Assert.Equal("in-flight", full.Answer);
        Assert.Contains("slicesInFlight 1", full.Why);
        Assert.Empty(_git.Committed);
        Assert.Contains("as many as", JsonSerializer.Serialize((await In(s => s.PlanViewAsync("alpha"))).Data));

        await using (var ctx = NewCtx())
        {
            ctx.StewardUnits.Single(u => u.Unit == 1).ReleasedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        Assert.Equal("agreed", (await In(s => s.OwnerAgreesUnitAsync("alpha", 2))).Answer);
    }
}
