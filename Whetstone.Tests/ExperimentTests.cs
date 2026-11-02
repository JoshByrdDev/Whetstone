using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A/B experiments (docs/bugs-and-experiments-plan.md, section 2): sticky, balanced assignment at the
/// claim, the per-ticket measures, the auto-stop rule, the stages a verdict waits through, and that
/// only a person can start, stop or declare one.
/// </summary>
public class ExperimentTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private static readonly string[] Providers = { "claude", "codex" };

    public ExperimentTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext Ctx() => new(_options);

    private static bool AnyVariant(string skill, string name) => name == "terse";

    private async Task<int> Start(DashboardDbContext ctx, int min = 3, int wait = 21, string skill = "implement")
    {
        var exp = await new ExperimentService(ctx, () => 0).CreateAsync(new ExperimentService.CreateSpec(
            "alpha", skill, "terse implement", "fewer turns", min, wait,
            new ExperimentService.ArmSpec("As is", null, null, null, null),
            new ExperimentService.ArmSpec("Terse", "terse", null, "high", null)), AnyVariant, Providers);
        return exp.Id;
    }

    private static async Task<Ticket> NewTicket(DashboardDbContext ctx, TicketPhase phase = TicketPhase.Implementing)
    {
        var t = new Ticket { ProjectKey = "alpha", Title = "work", Phase = phase };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    // ---- assignment ------------------------------------------------------------------------

    [Fact]
    public async Task A_ticket_keeps_its_arm_on_every_later_pass()
    {
        await using var ctx = Ctx();
        await Start(ctx);
        var svc = new ExperimentService(ctx, () => 1);
        var t = await NewTicket(ctx);

        var first = await svc.ForClaimAsync(t, "implement", AnyVariant);
        Assert.NotNull(first);
        Assert.Equal("variant", first!.Arm);
        Assert.True(first.Applied);
        Assert.Equal("terse", first.SkillVariant);
        Assert.Equal("high", first.Effort);

        // A run happened; QA sent it back; the next implement claim is the same ticket again.
        ctx.CycleRuns.Add(new CycleRun { TicketId = t.Id, ProjectKey = "alpha", Skill = "implement", StartedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        var again = await new ExperimentService(ctx, () => 0).ForClaimAsync(t, "implement", AnyVariant);
        Assert.Equal("variant", again!.Arm);
        Assert.Equal(1, await ctx.ExperimentAssignments.CountAsync());
    }

    [Fact]
    public void Pairs_stay_balanced_at_every_count()
    {
        var taken = new List<string>();
        var rng = new Random(7);
        for (var i = 0; i < 40; i++)
        {
            taken.Add(ExperimentService.NextArm(taken, () => rng.Next(2)));
            var c = taken.Count(k => k == "control");
            Assert.True(Math.Abs(c - (taken.Count - c)) <= 1, $"unbalanced at {taken.Count}");
        }
    }

    [Fact]
    public async Task Enrolment_closes_at_the_minimum_and_skips_other_skills()
    {
        await using var ctx = Ctx();
        await Start(ctx, min: 2);
        var svc = new ExperimentService(ctx);
        for (var i = 0; i < 4; i++) Assert.NotNull(await svc.ForClaimAsync(await NewTicket(ctx), "implement", AnyVariant));
        Assert.Null(await svc.ForClaimAsync(await NewTicket(ctx), "implement", AnyVariant));
        Assert.Null(await svc.ForClaimAsync(await NewTicket(ctx), "qa-test", AnyVariant));
        var arms = await ctx.ExperimentAssignments.Select(a => a.ArmKey).ToListAsync();
        Assert.Equal(2, arms.Count(a => a == "control"));
        Assert.Equal(2, arms.Count(a => a == "variant"));
    }

    [Fact]
    public async Task A_ticket_that_already_ran_the_skill_is_not_enrolled_halfway()
    {
        await using var ctx = Ctx();
        var t = await NewTicket(ctx);
        ctx.CycleRuns.Add(new CycleRun { TicketId = t.Id, ProjectKey = "alpha", Skill = "implement", StartedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        await Start(ctx);
        Assert.Null(await new ExperimentService(ctx).ForClaimAsync(t, "implement", AnyVariant));
    }

    [Fact]
    public async Task After_a_stop_every_enrolled_ticket_runs_as_configured()
    {
        await using var ctx = Ctx();
        var id = await Start(ctx);
        var svc = new ExperimentService(ctx, () => 1);
        var t = await NewTicket(ctx);
        Assert.True((await svc.ForClaimAsync(t, "implement", AnyVariant))!.Applied);

        await svc.StopAsync(id, "enough");

        var after = await svc.ForClaimAsync(t, "implement", AnyVariant);
        Assert.Equal("variant", after!.Arm);
        Assert.False(after.Applied);
        Assert.Null(after.SkillVariant);
        Assert.Null(await svc.ForClaimAsync(await NewTicket(ctx), "implement", AnyVariant));
        Assert.Contains(await ctx.Comments.Where(c => c.TicketId == t.Id).Select(c => c.Body).ToListAsync(), b => b.Contains("stopped"));
    }

    [Fact]
    public async Task A_missing_variant_file_reports_the_arm_unapplied()
    {
        await using var ctx = Ctx();
        await Start(ctx);
        var t = await NewTicket(ctx);
        var arm = await new ExperimentService(ctx, () => 1).ForClaimAsync(t, "implement", (_, _) => false);
        Assert.Equal("variant", arm!.Arm);
        Assert.False(arm.Applied);
    }

    [Fact]
    public async Task Creating_refuses_a_second_running_one_and_bad_overrides()
    {
        await using var ctx = Ctx();
        await Start(ctx);
        var svc = new ExperimentService(ctx);
        ExperimentService.CreateSpec Spec(ExperimentService.ArmSpec variant, string skill = "implement") => new(
            "alpha", skill, "x", null, null, null, new ExperimentService.ArmSpec(null, null, null, null, null), variant);

        await Assert.ThrowsAsync<ExperimentService.Refused>(() => svc.CreateAsync(Spec(new(null, null, null, "low", null)), AnyVariant, Providers));
        await Assert.ThrowsAsync<ExperimentService.Refused>(() => svc.CreateAsync(Spec(new(null, "missing", null, null, null), "qa-test"), AnyVariant, Providers));
        await Assert.ThrowsAsync<ExperimentService.Refused>(() => svc.CreateAsync(Spec(new(null, null, null, null, "gemini"), "qa-test"), AnyVariant, Providers));
        await Assert.ThrowsAsync<ExperimentService.Refused>(() => svc.CreateAsync(Spec(new(null, null, null, "huge", null), "qa-test"), AnyVariant, Providers));
        await Assert.ThrowsAsync<ExperimentService.Refused>(() => svc.CreateAsync(Spec(new(null, null, null, null, null), "qa-test"), AnyVariant, Providers));
        await Assert.ThrowsAsync<ExperimentService.Refused>(() => svc.CreateAsync(Spec(new(null, null, null, "low", null), "deploy"), AnyVariant, Providers));
        Assert.NotNull(await svc.CreateAsync(Spec(new(null, null, null, "low", null), "qa-test"), AnyVariant, Providers));
    }

    // ---- the arithmetic and the stop rule ---------------------------------------------------

    [Theory]
    [InlineData(5, 6, 0, 6, 6.0 / 792)]       // C(6,5)C(6,0)/C(12,5)
    [InlineData(4, 6, 0, 6, 15.0 / 495)]      // C(6,4)/C(12,4)
    [InlineData(0, 6, 0, 6, 1.0)]
    [InlineData(1, 1, 0, 1, 0.5)]
    public void Fisher_matches_the_hypergeometric_tail(int badV, int nV, int badC, int nC, double want) =>
        Assert.Equal(want, ExperimentService.FisherOneSided(badV, nV, badC, nC), 6);

    [Fact]
    public void A_guardrail_breaks_only_when_clearly_worse()
    {
        Assert.True(ExperimentService.Check("qa-failure", new(6, 0), new(6, 5)).Breaks);
        Assert.False(ExperimentService.Check("qa-failure", new(6, 0), new(6, 4)).Breaks);   // p ~ 0.03
        Assert.False(ExperimentService.Check("qa-failure", new(3, 0), new(3, 3)).Breaks);   // too few
        Assert.False(ExperimentService.Check("qa-failure", new(6, 6), new(6, 0)).Breaks);   // better is never a stop
        Assert.True(ExperimentService.Check("qa-failure", new(6, 0), new(6, 0)).Passes);
    }

    /// <summary>Enrol <paramref name="n"/> tickets per arm and take each to Done, the variant's with a QA failure on <paramref name="variantFails"/> of them.</summary>
    private async Task<int> Landed(DashboardDbContext ctx, int n, int variantFails, int doneDaysAgo = 1, int wait = 21)
    {
        var id = await Start(ctx, min: n, wait: wait);
        var svc = new ExperimentService(ctx);
        var failed = 0;
        for (var i = 0; i < n * 2; i++)
        {
            var t = await NewTicket(ctx);
            var arm = (await svc.ForClaimAsync(t, "implement", AnyVariant))!.Arm;
            var a = await ctx.ExperimentAssignments.SingleAsync(x => x.TicketId == t.Id);
            a.AssignedAt = DateTime.UtcNow.AddDays(-doneDaysAgo - 2);
            ctx.CycleRuns.Add(new CycleRun
            {
                TicketId = t.Id, ProjectKey = "alpha", Skill = "implement", StartedAt = DateTime.UtcNow.AddDays(-doneDaysAgo - 1),
                DurationSeconds = arm == "variant" ? 600 : 1200, InputTokens = arm == "variant" ? 1000 : 2000,
                ExperimentId = id, ExperimentArm = arm, ExperimentApplied = true,
            });
            if (arm == "variant" && failed < variantFails)
            {
                failed++;
                ctx.PhaseTransitions.Add(new PhaseTransition { TicketId = t.Id, ProjectKey = "alpha", From = TicketPhase.QaTesting,
                    To = TicketPhase.NeedsImplementation, Backward = true, At = DateTime.UtcNow.AddDays(-doneDaysAgo - 1) });
            }
            t.Phase = TicketPhase.Done;
            ctx.PhaseTransitions.Add(new PhaseTransition { TicketId = t.Id, ProjectKey = "alpha", From = TicketPhase.NeedsReview,
                To = TicketPhase.Done, At = DateTime.UtcNow.AddDays(-doneDaysAgo) });
            await ctx.SaveChangesAsync();
        }
        return id;
    }

    [Fact]
    public async Task The_board_stops_a_variant_that_clearly_breaks_qa()
    {
        await using var ctx = Ctx();
        var id = await Landed(ctx, 6, variantFails: 5);
        var stopped = await new ExperimentService(ctx).CheckGuardrailsAsync("alpha");
        var exp = Assert.Single(stopped);
        Assert.Equal(ExperimentStatus.Stopped, exp.Status);
        Assert.Equal(ExperimentStopKind.Guardrail, exp.StopKind);
        Assert.Contains("qa-failure", exp.StopReason);
        Assert.Equal(id, exp.Id);
    }

    [Fact]
    public async Task One_unlucky_ticket_more_does_not_stop_it()
    {
        await using var ctx = Ctx();
        await Landed(ctx, 6, variantFails: 4);
        Assert.Empty(await new ExperimentService(ctx).CheckGuardrailsAsync("alpha"));
    }

    [Fact]
    public async Task A_verdict_waits_for_escaped_bugs_then_suggests_the_cheaper_arm()
    {
        await using var ctx = Ctx();
        var id = await Landed(ctx, 3, variantFails: 0, doneDaysAgo: 5, wait: 21);
        var svc = new ExperimentService(ctx);

        var waiting = (await svc.ViewAsync(id))!;
        Assert.Equal(ExperimentService.Stages.Waiting, waiting.Stage);
        await Assert.ThrowsAsync<ExperimentService.Refused>(() => svc.DeclareAsync(id, "variant", null));

        var later = (await svc.ViewAsync(id, DateTime.UtcNow.AddDays(30)))!;
        Assert.Equal(ExperimentService.Stages.Ready, later.Stage);
        Assert.Equal("variant", later.Suggestion);
        var v = later.Arms.Single(a => a.Key == "variant");
        Assert.Equal(10, v.MedianMinutes);
        Assert.Equal(1000, v.MedianFreshTokens);

        var declared = await svc.DeclareAsync(id, "variant", "adopt it", DateTime.UtcNow.AddDays(30));
        Assert.Equal(ExperimentStatus.Concluded, declared!.Status);
        Assert.Equal("variant", declared.Winner);
    }

    [Fact]
    public async Task A_stopped_experiment_can_be_declared_at_once()
    {
        await using var ctx = Ctx();
        var id = await Start(ctx);
        var svc = new ExperimentService(ctx);
        Assert.Equal(ExperimentService.Stages.Enrolling, (await svc.ViewAsync(id))!.Stage);
        await svc.StopAsync(id, null);
        Assert.Equal("none", (await svc.DeclareAsync(id, "none", null))!.Winner);
    }

    // ---- who controls it ----------------------------------------------------------------------

    /// <summary>
    /// The human-only limits, held by absence the way the steward's are: no MCP tool, on either door,
    /// takes a service that can confirm a bug link or start, stop or declare an experiment, or is
    /// named for one. A read-only tool, when one is wanted, goes on this allowlist by name.
    /// </summary>
    [Fact]
    public void No_tool_can_confirm_a_bug_link_or_steer_an_experiment()
    {
        string[] readOnlyAllowed = [];
        var forbidden = new[] { typeof(BugTraceService), typeof(ExperimentService), typeof(BugTraceQueue) };
        var tools = typeof(ExperimentService).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Select(m => (Type: t, Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>())))
            .Where(x => x.Tool is not null)
            .ToList();
        Assert.NotEmpty(tools);

        foreach (var (type, method, tool) in tools)
        {
            var name = tool!.Name ?? method.Name;
            Assert.DoesNotContain(method.GetParameters(), p => forbidden.Contains(p.ParameterType));
            if (readOnlyAllowed.Contains(name)) continue;
            Assert.DoesNotMatch("(?i)bug|experiment|arm_|variant", name);
        }
        foreach (var type in tools.Select(x => x.Type).Distinct())
            foreach (var ctor in type.GetConstructors())
                Assert.DoesNotContain(ctor.GetParameters(), p => forbidden.Contains(p.ParameterType));
    }

    [Fact]
    public void Variant_files_travel_to_worker_hosts_in_the_project_kit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccd-kit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "skills", "implement", "variants"));
            File.WriteAllText(Path.Combine(dir, "skills", "implement", "skill.md"), "skill");
            File.WriteAllText(Path.Combine(dir, "skills", "implement", "variants", "terse.md"), "terse");
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(ProjectKit.Build(dir, null)));
            Assert.Contains(zip.Entries, e => e.FullName == "skills/implement/variants/terse.md");
        }
        finally { Directory.Delete(dir, true); }
    }
}
