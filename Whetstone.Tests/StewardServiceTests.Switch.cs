using System.Text.Json;
using Whetstone.Models;
using Whetstone.Services;

namespace Whetstone.Tests;

/// <summary>
/// docs/slice-branches.md, piece 8: the owner moves a project that already exists to slice
/// branches. It refuses until the slices in flight are finished, and then retires what is over,
/// keeps what the owner keeps as conventions, and lets the plan's drafts go on as slices.
/// </summary>
public partial class StewardServiceTests
{
    private const string OldShape = """
        "plan": { "key": "p2", "name": "Plan two", "units": "docs/units" },
        "pastPlans": [ { "key": "p1", "name": "Plan one", "units": "docs/old" } ],
        "syncFrom": "main"
        """;

    private static JsonElement Data(StewardReply reply) =>
        JsonDocument.Parse(JsonSerializer.Serialize(reply.Data)).RootElement.Clone();

    /// <summary>Unit 1 of p2 built, a decision of it, one of p1's, and three of the whole project's.</summary>
    private async Task OldProjectAsync()
    {
        Configure("manage", OldShape);
        await Built(1, "A1", planKey: "p2");
        await Built(1, "A2", planKey: "p2");
        await DoneAsync(1, "A1", "A2");
        await using var ctx = NewCtx();
        ctx.StewardDecisions.AddRange(
            new StewardDecision { ProjectKey = "alpha", Number = 60, Rule = "Unit one's.", PlanKey = "p2", PlanUnit = 1 },
            new StewardDecision { ProjectKey = "alpha", Number = 61, Rule = "Plan one's.", PlanKey = "p1", PlanUnit = 3 },
            new StewardDecision { ProjectKey = "alpha", Number = 62, Rule = "Money is whole cents." },
            new StewardDecision { ProjectKey = "alpha", Number = 63, Rule = "Money is whole cents!" },
            new StewardDecision { ProjectKey = "alpha", Number = 64, Rule = "Never text after nine." });
        await ctx.SaveChangesAsync();
    }

    private async Task DoneAsync(int unit, params string[] outcomes)
    {
        await using var ctx = NewCtx();
        foreach (var o in outcomes)
            ctx.StewardOutcomes.Add(new StewardOutcome { ProjectKey = "alpha", Plan = "p2", Unit = unit, Outcome = o, State = StewardOutcomeState.Done });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task The_view_says_what_the_switch_does_and_drafts_the_conventions_once_each()
    {
        await OldProjectAsync();
        var view = await In(s => s.SwitchViewAsync("alpha"));
        Assert.True(view.Answer == "ready", view.Why);
        var d = Data(view);
        Assert.Equal("main", d.GetProperty("base").GetString());
        Assert.Equal("docs/conventions.md", d.GetProperty("conventions").GetString());
        var released = d.GetProperty("released").EnumerateArray().Select(r => (r.GetProperty("plan").GetString(), r.GetProperty("unit").GetInt32())).ToList();
        Assert.Contains(("p2", 1), released);
        Assert.Contains(("p1", 3), released);
        Assert.Equal(new[] { 62, 63, 64 }, d.GetProperty("projectWide").EnumerateArray().Select(x => x.GetProperty("number").GetInt32()).Order());
        var draft = d.GetProperty("draft").GetString()!;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(draft, "whole cents"));
        Assert.Contains("Never text after nine.", draft);
        Assert.Empty(_git.Switched);
    }

    [Fact]
    public async Task It_refuses_while_a_slice_is_in_flight_or_agreed_and_unbuilt_or_git_says_no()
    {
        Configure("manage", OldShape);
        var unbuilt = await In(s => s.SwitchViewAsync("alpha"));
        Assert.Equal("not-yet", unbuilt.Answer);
        Assert.Contains("Slice 1 is agreed and not built", unbuilt.Why);

        await Built(1, "A1", planKey: "p2");
        var open = await Built(1, "A2", planKey: "p2", phase: TicketPhase.NeedsImplementation);
        var inFlight = await In(s => s.OwnerSwitchesAsync("alpha", new StewardSwitch(null, null, null, null)));
        Assert.Equal("not-yet", inFlight.Answer);
        Assert.Contains($"#{open}", inFlight.Why);
        Assert.Empty(_git.Switched);

        await using (var ctx = NewCtx())
        {
            ctx.Tickets.Single(t => t.Id == open).Phase = TicketPhase.Done;
            await ctx.SaveChangesAsync();
        }
        await DoneAsync(1, "A1", "A2");
        _git.SwitchRefusal = "Merge develop into main first, by hand.";
        Assert.Contains("by hand", (await In(s => s.SwitchViewAsync("alpha"))).Why);
        _git.SwitchRefusal = null;
        Assert.Equal("ready", (await In(s => s.SwitchViewAsync("alpha"))).Answer);
    }

    [Fact]
    public async Task A_project_that_has_switched_or_cannot_is_refused()
    {
        Configure("manage");
        SwitchToSliceBranches();
        Assert.Contains("switched already", (await In(s => s.SwitchViewAsync("alpha"))).Why);

        Configure("manage");
        var f = new DashboardPaths(_dataRoot).ProjectConfigDefaults("alpha");
        File.WriteAllText(f, File.ReadAllText(f).Replace("\"mergeInsteadOfPr\": true", "\"mergeInsteadOfPr\": false"));
        Assert.Contains("pull request", (await In(s => s.SwitchViewAsync("alpha"))).Why);
    }

    [Fact]
    public async Task Switching_releases_what_is_over_keeps_the_chosen_conventions_and_retires_the_rest()
    {
        await OldProjectAsync();
        await using (var ctx = NewCtx())
        {
            ctx.StewardUnits.Add(new StewardUnit { ProjectKey = "alpha", Plan = "p2", Unit = 2 });
            ctx.StewardOutcomes.Add(new StewardOutcome { ProjectKey = "alpha", Plan = "p2", Unit = 2, Outcome = "A1" });
            await ctx.SaveChangesAsync();
        }
        Assert.Equal("bad-request", (await In(s => s.OwnerSwitchesAsync("alpha", new StewardSwitch(null, "../x.md", "x", null)))).Answer);
        Assert.Equal("bad-request", (await In(s => s.OwnerSwitchesAsync("alpha", new StewardSwitch(null, null, "x", new[] { 60 })))).Answer);

        var text = "# Conventions\n\n- Money is whole cents.\n";
        var reply = await In(s => s.OwnerSwitchesAsync("alpha", new StewardSwitch(null, null, text, new[] { 62, 64 })));
        Assert.Equal("switched", reply.Answer);
        Assert.Equal(("main", "docs/conventions.md", text), _git.Switched.Single());
        var d = Data(reply);
        Assert.Equal(2, d.GetProperty("conventionsKept").GetInt32());
        Assert.Equal(1, d.GetProperty("retired").GetInt32());

        await using var check = NewCtx();
        var decisions = check.StewardDecisions.ToDictionary(x => x.Number);
        Assert.True(decisions[62].Convention);
        Assert.True(decisions[64].Convention);
        Assert.NotNull(decisions[63].RetiredAt);
        Assert.False(decisions[63].Convention);
        // The plan being worked goes on under no key: its draft is a slice now, its unit 1 released.
        Assert.Equal("", decisions[60].PlanKey);
        Assert.Equal("p1", decisions[61].PlanKey);
        Assert.DoesNotContain(check.StewardUnits, u => u.Plan == "p2");
        Assert.DoesNotContain(check.StewardOutcomes, o => o.Plan == "p2");
        Assert.NotNull(check.StewardUnits.Single(u => u.Plan == "" && u.Unit == 1).ReleasedAt);
        Assert.Null(check.StewardUnits.Single(u => u.Plan == "" && u.Unit == 2).ReleasedAt);
        Assert.NotNull(check.StewardUnits.Single(u => u.Plan == "p1" && u.Unit == 3).ReleasedAt);
        Assert.NotNull(check.StewardStates.Single().RegisterBuiltAt);
        // Tickets keep the key they were filed under: history.
        Assert.All(check.Tickets, t => Assert.Equal("p2", t.PlanKey));
    }

    [Fact]
    public async Task Once_switched_only_conventions_and_open_slices_decisions_are_in_force()
    {
        await OldProjectAsync();
        Assert.Equal("switched", (await In(s => s.OwnerSwitchesAsync("alpha", new StewardSwitch(null, null, null, new[] { 62 })))).Answer);
        Assert.Null(_git.Switched.Single().File);
        SwitchToSliceBranches();
        var now = Statuses(await Decisions());
        Assert.Equal(new[] { 62 }, now.Keys.Where(n => n >= 60));
        var all = Statuses(await Decisions(history: true));
        Assert.Equal("retired", all[60]);
        Assert.Equal("retired", all[61]);
        Assert.Equal("retired", all[63]);
        Assert.Equal("retired", all[64]);
    }

    [Fact]
    public async Task The_settings_file_switches_and_drops_the_plan_around_the_slices()
    {
        var dir = Path.Combine(_dataRoot, "switch-files");
        Directory.CreateDirectory(dir);
        var defaults = Path.Combine(dir, "project.defaults.json");
        var local = Path.Combine(dir, "project.json");
        File.WriteAllText(defaults, """
            { "displayName": "Alpha", "baseBranch": "develop", "mergeInsteadOfPr": true,
              "steward": { "plan": { "key": "p2", "name": "Plan two", "units": "docs/units", "charter": "c.md", "master": "m.md" },
                           "pastPlans": [ { "key": "p1" } ], "syncFrom": "main", "neverMerge": ["infra/"] } }
            """);
        File.WriteAllText(local, """{ "stewardMode": "manage", "sliceBranches": false }""");

        Assert.Null(await StewardPlanFile.SwitchAsync(defaults, local, "main", "docs/conventions.md"));

        using var d = JsonDocument.Parse(File.ReadAllText(defaults));
        var root = d.RootElement;
        Assert.Equal("main", root.GetProperty("baseBranch").GetString());
        Assert.True(root.GetProperty("sliceBranches").GetBoolean());
        var steward = root.GetProperty("steward");
        Assert.False(steward.TryGetProperty("pastPlans", out _));
        Assert.False(steward.TryGetProperty("syncFrom", out _));
        Assert.Equal("docs/conventions.md", steward.GetProperty("conventions").GetString());
        Assert.Equal("infra/", steward.GetProperty("neverMerge")[0].GetString());
        var plan = steward.GetProperty("plan");
        Assert.Equal("docs/units", plan.GetProperty("units").GetString());
        foreach (var gone in new[] { "key", "name", "charter", "master" }) Assert.False(plan.TryGetProperty(gone, out _));

        using var l = JsonDocument.Parse(File.ReadAllText(local));
        Assert.False(l.RootElement.TryGetProperty("sliceBranches", out _));
        Assert.Equal("manage", l.RootElement.GetProperty("stewardMode").GetString());
    }
}
