using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Leases: exclusivity, all-or-nothing acquisition, expiry, and the rule that losing the race for a
/// busy resource must cost the passed-over ticket nothing. Also covers the configuration shape that
/// turns a resource into an N-worker cap, since that is the reason the capacity exists at all.
/// </summary>
public class ResourceLeaseTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public ResourceLeaseTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);
    private static bool AllSkillsExist(string project, string skill) => true;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(150);

    private static async Task<Ticket> Seed(DashboardDbContext ctx, TicketPhase phase, DateTime? updated = null,
        string project = "zeta")
    {
        var t = new Ticket
        {
            ProjectKey = project,
            Title = "t",
            Phase = phase,
            UpdatedAt = updated ?? DateTime.UtcNow,
        };
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        return t;
    }

    // ---- Exclusivity ----------------------------------------------------------------

    [Fact]
    public async Task A_resource_is_held_by_one_ticket_at_a_time()
    {
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);

        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 1, "qa-test", Ttl));
        Assert.Null(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 2, "qa-test", Ttl));

        await leases.ReleaseForTicketAsync(1, "done");
        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 2, "qa-test", Ttl));
    }

    [Fact]
    public async Task Acquiring_is_all_or_nothing()
    {
        // A worker holding the database but not the stack has taken something away from a worker
        // that could have used it, and still cannot run itself.
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);

        await leases.TryAcquireAllAsync(new[] { "zeta:db" }, 1, "qa-test", Ttl);
        Assert.Null(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack", "zeta:db" }, 2, "qa-test", Ttl));

        // The one that WAS free must still be free - the failed attempt left nothing behind.
        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 3, "qa-test", Ttl));
    }

    [Fact]
    public async Task Different_projects_do_not_contend()
    {
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);
        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 1, "qa-test", Ttl));
        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "other:app-stack" }, 2, "qa-test", Ttl));
    }

    [Fact]
    public async Task An_abandoned_lease_expires_rather_than_jamming_the_resource()
    {
        // A worker killed at the timeout never releases. Without expiry the QA stack would stay held
        // until a person noticed, which on an unattended loop means overnight.
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);
        await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 1, "qa-test", TimeSpan.FromMinutes(-1));

        Assert.Equal(1, await leases.SweepExpiredAsync());
        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 2, "qa-test", Ttl));

        var reclaimed = await ctx.ResourceLeases.FirstAsync(l => l.TicketId == 1);
        Assert.Contains("expired", reclaimed.ReleaseReason);
    }

    [Fact]
    public async Task Releasing_twice_is_harmless()
    {
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);
        await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 1, "qa-test", Ttl);
        Assert.Equal(1, await leases.ReleaseForTicketAsync(1));
        Assert.Equal(0, await leases.ReleaseForTicketAsync(1));
    }

    [Fact]
    public async Task Needing_nothing_always_succeeds()
    {
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);
        var got = await leases.TryAcquireAllAsync(Array.Empty<string>(), 1, "implement", Ttl);
        Assert.NotNull(got);
        Assert.Empty(got!);
    }

    // ---- The claim ------------------------------------------------------------------

    private static string[] QaNeedsTheStack(string project, string skill) =>
        skill == "qa-test" ? new[] { $"{project}:app-stack" } : Array.Empty<string>();

    [Fact]
    public async Task The_claim_passes_over_a_ticket_whose_resource_is_busy()
    {
        var old = DateTime.UtcNow.AddHours(-2);
        int firstQa, secondQa;
        await using (var ctx = NewCtx())
        {
            firstQa = (await Seed(ctx, TicketPhase.NeedsQa, old)).Id;
            secondQa = (await Seed(ctx, TicketPhase.NeedsQa, old.AddMinutes(1))).Id;
        }

        await using var ctx2 = NewCtx();
        var leases = new ResourceLeaseService(ctx2);
        var svc = new TicketService(ctx2, leases: leases);

        var a = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
        Assert.Equal(firstQa, a!.Ticket.Id);
        Assert.Equal(new[] { $"ticket:{firstQa}", "zeta:app-stack" }, a.Leases);

        // The second QA ticket is next in line and perfectly workable - but the stack is taken, so
        // the whole claim comes back empty rather than starting work that would collide.
        Assert.Null(await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl));
        Assert.DoesNotContain(secondQa, (await leases.ActiveAsync()).Select(l => l.TicketId));

        // Releasing hands the stack straight back to the SAME ticket, not to the queued one: the
        // first is now QaTesting and the second only NeedsQa, and the loop always finishes the
        // most-progressed work first. Contention changes who waits, never the pickup order.
        await leases.ReleaseForTicketAsync(firstQa, "cycle finished");
        var b = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
        Assert.Equal(firstQa, b!.Ticket.Id);
        Assert.True(b.Resumed);
        Assert.Equal(new[] { $"ticket:{firstQa}", "zeta:app-stack" }, b.Leases);
    }

    /// <summary>
    /// The cross-project half of the test above, and the one that matters the day a second project
    /// arrives: two QA tickets in DIFFERENT projects must both be claimable at once. Their stacks
    /// are separate things, so scoping the bare <c>app-stack</c> name per project is what keeps one
    /// project's QA run from parking the other's behind it — a queue that would look exactly like
    /// the pipeline being slow, with nothing anywhere saying why.
    /// </summary>
    [Fact]
    public async Task Two_projects_can_each_hold_their_own_stack_at_the_same_time()
    {
        var old = DateTime.UtcNow.AddHours(-2);
        int zetaQa, otherQa;
        await using (var ctx = NewCtx())
        {
            zetaQa = (await Seed(ctx, TicketPhase.NeedsQa, old)).Id;
            otherQa = (await Seed(ctx, TicketPhase.NeedsQa, old.AddMinutes(1), project: "other")).Id;
        }

        await using var ctx2 = NewCtx();
        var svc = new TicketService(ctx2, leases: new ResourceLeaseService(ctx2));

        var a = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
        Assert.Equal(zetaQa, a!.Ticket.Id);
        Assert.Equal(new[] { $"ticket:{zetaQa}", "zeta:app-stack" }.Order(), a.Leases.Order());

        // Same phase, same skill, same bare resource name - and it runs anyway, because the name
        // resolved against a different project.
        var b = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
        Assert.Equal(otherQa, b!.Ticket.Id);
        Assert.Equal(new[] { $"ticket:{otherQa}", "other:app-stack" }.Order(), b.Leases.Order());
    }

    [Fact]
    public async Task A_ticket_needing_a_different_resource_still_gets_picked_up()
    {
        // The point of leasing rather than a global lock: implement does not touch the QA stack, so
        // it must keep running while QA holds it.
        var old = DateTime.UtcNow.AddHours(-2);
        int implId;
        await using (var ctx = NewCtx())
        {
            await Seed(ctx, TicketPhase.NeedsQa, old);
            implId = (await Seed(ctx, TicketPhase.NeedsImplementation, old)).Id;
        }

        await using var ctx2 = NewCtx();
        var svc = new TicketService(ctx2, leases: new ResourceLeaseService(ctx2));

        await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);   // QA takes the stack
        var next = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
        Assert.Equal(implId, next!.Ticket.Id);
        // Nothing shared - the only thing this run holds is the ticket it is working.
        Assert.Equal(new[] { $"ticket:{implId}" }, next.Leases);
    }

    /// <summary>
    /// A code-review-only ticket reads the diff and never starts the app, so QA takes no stack for
    /// it — and, more to the point, does not queue behind whoever holds it. Without this the flag
    /// would still save the browser but leave the ticket waiting on a resource it never touches.
    /// </summary>
    [Fact]
    public async Task Qa_takes_no_stack_for_a_code_review_only_ticket()
    {
        var old = DateTime.UtcNow.AddHours(-2);
        int reviewOnly;
        await using (var ctx = NewCtx())
        {
            var t = await Seed(ctx, TicketPhase.NeedsQa, old);
            t.SkipQaTesting = true;
            await ctx.SaveChangesAsync();
            reviewOnly = t.Id;
        }

        await using var ctx2 = NewCtx();
        var svc = new TicketService(ctx2, leases: new ResourceLeaseService(ctx2));

        var claim = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);

        Assert.Equal(reviewOnly, claim!.Ticket.Id);
        Assert.Equal("qa-test", claim.Skill);
        Assert.Equal(new[] { $"ticket:{reviewOnly}" }, claim.Leases);
    }

    /// <summary>And the stack it declined stays free for a ticket that will actually use it.</summary>
    [Fact]
    public async Task The_stack_a_code_review_only_ticket_declined_is_still_there_for_the_next_one()
    {
        var old = DateTime.UtcNow.AddHours(-2);
        int reviewOnly, realQa;
        await using (var ctx = NewCtx())
        {
            var a = await Seed(ctx, TicketPhase.NeedsQa, old);
            a.SkipQaTesting = true;
            realQa = (await Seed(ctx, TicketPhase.NeedsQa, old.AddMinutes(1))).Id;
            await ctx.SaveChangesAsync();
            reviewOnly = a.Id;
        }

        await using var ctx2 = NewCtx();
        var svc = new TicketService(ctx2, leases: new ResourceLeaseService(ctx2));

        var first = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
        Assert.Equal(reviewOnly, first!.Ticket.Id);

        // Before the flag this second claim came back empty: the stack was held by a run that was
        // only ever going to read a diff.
        var second = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
        Assert.Equal(realQa, second!.Ticket.Id);
        Assert.Equal(new[] { $"ticket:{realQa}", "zeta:app-stack" }, second.Leases);
    }

    // ---- One agent per ticket -------------------------------------------------------

    [Fact]
    public async Task Filling_three_agent_slots_hands_out_three_different_tickets()
    {
        // The whole point of the fleet: the orchestrator fills its slots by calling the claim in a
        // row, with nothing released in between. Before the ticket lease, the second call saw the
        // first ticket sitting in its "-ing" phase, read that as a resume, and sent a second agent
        // into the same worktree.
        var old = DateTime.UtcNow.AddHours(-3);
        await using (var ctx = NewCtx())
        {
            await Seed(ctx, TicketPhase.NeedsImplementation, old);
            await Seed(ctx, TicketPhase.NeedsImplementation, old.AddMinutes(1));
            await Seed(ctx, TicketPhase.NeedsImplementation, old.AddMinutes(2));
        }

        await using var ctx2 = NewCtx();
        var svc = new TicketService(ctx2, leases: new ResourceLeaseService(ctx2));

        var claimed = new List<int>();
        for (var slot = 0; slot < 3; slot++)
        {
            var c = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
            Assert.NotNull(c);
            claimed.Add(c!.Ticket.Id);
        }

        Assert.Equal(3, claimed.Distinct().Count());
        // A fourth agent has nothing left to take rather than doubling up on one of the three.
        Assert.Null(await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl));
    }

    [Fact]
    public async Task A_ticket_is_claimable_again_once_its_agent_finishes()
    {
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.NeedsImplementation)).Id;

        await using var ctx2 = NewCtx();
        var leases = new ResourceLeaseService(ctx2);
        var svc = new TicketService(ctx2, leases: leases);

        Assert.Equal(id, (await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl))!.Ticket.Id);
        Assert.Null(await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl));

        // The agent's own release at cycle end is what puts a still-unfinished ticket back in play.
        await leases.ReleaseForTicketAsync(id, "cycle finished");
        var again = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
        Assert.Equal(id, again!.Ticket.Id);
        Assert.True(again.Resumed);
    }

    [Fact]
    public async Task A_restarted_orchestrator_takes_back_everything_the_killed_run_held()
    {
        // A hard stop kills the agents where they stand, so their leases outlive them. The restart
        // is the one moment we KNOW nothing is running, and without this sweep every in-flight
        // ticket would sit behind its own lease until the TTL expired - hours of a queue that looks
        // stuck for no visible reason.
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.NeedsQa)).Id;

        await using var ctx2 = NewCtx();
        var leases = new ResourceLeaseService(ctx2);
        var svc = new TicketService(ctx2, leases: leases);

        Assert.NotNull(await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl));
        Assert.Null(await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl));   // agent still on it

        Assert.Equal(2, await leases.ReleaseAllAsync("orchestrator restarted"));   // ticket + app-stack
        Assert.Equal(id, (await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl))!.Ticket.Id);
    }

    [Fact]
    public async Task A_restarted_orchestrator_leaves_a_surviving_agents_lease_alone()
    {
        // The sweep above assumes a restart means nothing is running, and an agent that outlived the
        // orchestrator that launched it is the case where that is false. Its ticket must come back
        // out of the reset untouched: releasing it would hand a half-written worktree to a second
        // agent, and the next phase refuses to start on a dirty tree, so the ticket would not merely
        // be worked twice - it would end up Blocked waiting for a human.
        int survivor, abandoned;
        await using (var ctx = NewCtx())
        {
            survivor  = (await Seed(ctx, TicketPhase.NeedsQa)).Id;
            abandoned = (await Seed(ctx, TicketPhase.NeedsImplementation)).Id;
        }

        await using var ctx2 = NewCtx();
        var leases = new ResourceLeaseService(ctx2);
        var svc = new TicketService(ctx2, leases: leases);

        // QA outranks implementation, so the survivor is claimed first and takes the stack with it.
        Assert.Equal(survivor,  (await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl))!.Ticket.Id);
        Assert.Equal(abandoned, (await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl))!.Ticket.Id);

        // Only the abandoned ticket's lease goes. The survivor keeps BOTH of its leases - sparing the
        // ticket but reclaiming the QA stack underneath it would be the same collision by other means.
        Assert.Equal(1, await leases.ReleaseAllAsync("orchestrator restarted", new[] { survivor }));

        var stillHeld = (await leases.ActiveAsync()).Where(l => l.TicketId == survivor).Select(l => l.ResourceKey);
        Assert.Equal(
            new[] { "zeta:app-stack", ResourceLeaseService.TicketKey(survivor) }.OrderBy(k => k),
            stillHeld.OrderBy(k => k));

        // And the freed one is workable again while the survivor stays out of reach.
        Assert.Equal(abandoned, (await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl))!.Ticket.Id);
        Assert.Null(await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl));
    }

    [Fact]
    public async Task Being_passed_over_costs_the_ticket_nothing()
    {
        // This is the rule that makes contention safe to retry: a ticket skipped because a resource
        // was busy must not burn an attempt, move phase, or gain a comment. It simply was not its
        // turn, and the wedged-ticket breaker must never fire because of traffic.
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.NeedsQa)).Id;

        await using var ctx2 = NewCtx();
        var leases = new ResourceLeaseService(ctx2);
        var svc = new TicketService(ctx2, leases: leases);

        // One real cycle, so the ticket carries a genuine progress mark and one used attempt.
        Assert.NotNull(await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl));
        await leases.ReleaseForTicketAsync(id, "cycle finished");

        var before = await ctx2.Comments.CountAsync(c => c.TicketId == id);
        // Now somebody else is holding the stack when the claim comes round again.
        await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 999, "qa-test", Ttl);
        Assert.Null(await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl));

        await using var read = NewCtx();
        var after = await read.Tickets.FirstAsync(t => t.Id == id);
        Assert.Equal(1, after.HeartbeatAttempts);                 // attempt not consumed by traffic
        Assert.Equal(TicketPhase.QaTesting, after.Phase);         // left exactly where it was
        Assert.Equal(before, await read.Comments.CountAsync(c => c.TicketId == id));   // and silent
    }

    [Fact]
    public async Task A_ticket_blocked_by_the_attempt_cap_never_takes_the_resource()
    {
        // Order matters: reserving first would let a ticket that is about to be blocked hold the QA
        // stack on its way out.
        //
        // The no-progress state is produced by actually running the claim repeatedly rather than by
        // writing a made-up progress mark - the mark is a hash of the ticket's own contents, so a
        // fabricated one reads as "something changed" and the breaker never fires.
        int id;
        await using (var ctx = NewCtx()) id = (await Seed(ctx, TicketPhase.NeedsQa)).Id;

        await using var ctx2 = NewCtx();
        var leases = new ResourceLeaseService(ctx2);
        var svc = new TicketService(ctx2, leases: leases);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var claim = await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl);
            Assert.NotNull(claim);                       // claimed, then the "worker" achieves nothing
            await leases.ReleaseForTicketAsync(id, "cycle finished");
        }

        // Fourth time round the cap is reached: the ticket is blocked instead of resumed...
        Assert.Null(await svc.ClaimNextAsync(null, AllSkillsExist, 3, QaNeedsTheStack, Ttl));

        await using var read = NewCtx();
        Assert.Equal(TicketPhase.Blocked, (await read.Tickets.FirstAsync(t => t.Id == id)).Phase);
        // ...and it took nothing with it on the way out.
        Assert.Empty(await read.ResourceLeases.Where(l => l.ReleasedAt == null).ToListAsync());
    }

    // ---- Configuration --------------------------------------------------------------

    [Fact]
    public void A_bare_resource_name_is_scoped_to_its_project_and_a_qualified_one_is_not()
    {
        var cfg = new ProjectConfig
        {
            Name = "zeta",
            PhaseResources = new() { ["qa-test"] = new[] { "app-stack", "docker:daemon" } },
        };

        // The bare name cannot collide with another project's stack; the qualified one is how a
        // genuinely machine-wide resource is shared ACROSS projects.
        Assert.Equal(new[] { "zeta:app-stack", "docker:daemon" }, cfg.ResourcesFor("qa-test"));
        Assert.Empty(cfg.ResourcesFor("implement"));
    }

    [Fact]
    public void QA_takes_the_project_stack_unless_a_project_says_otherwise()
    {
        Assert.Equal(new[] { "zeta:app-stack" }, new ProjectConfig { Name = "zeta" }.ResourcesFor("qa-test"));
        Assert.Empty(new ProjectConfig { Name = "zeta" }.ResourcesFor("speculate"));

        // review-pr rebuilds the same app tier on the same ports, so it contends for the same lease -
        // including in a project that configured qa-test alone, since the fallback is per skill.
        Assert.Equal(new[] { "zeta:app-stack" }, new ProjectConfig { Name = "zeta" }.ResourcesFor("review-pr"));
        var qaOnly = new ProjectConfig { Name = "zeta", PhaseResources = new() { ["qa-test"] = new[] { "app-stack" } } };
        Assert.Equal(new[] { "zeta:app-stack" }, qaOnly.ResourcesFor("review-pr"));

        // An explicit empty list is a project saying "QA shares nothing here", not a missing value.
        var noneNeeded = new ProjectConfig { Name = "zeta", PhaseResources = new() { ["qa-test"] = Array.Empty<string>() } };
        Assert.Empty(noneNeeded.ResourcesFor("qa-test"));
    }

    // ---- The human as a holder ------------------------------------------------------

    [Fact]
    public void The_stack_control_defaults_to_the_same_resource_the_QA_phase_takes()
    {
        var cfg = new ProjectConfig { Name = "zeta" };

        // If these two ever diverged, the dashboard's Start button would bind the same ports as a QA
        // agent while both believed they held the thing exclusively.
        Assert.Equal("zeta:app-stack", cfg.StackResourceKey);
        Assert.Contains(cfg.StackResourceKey, cfg.ResourcesFor("qa-test"));

        // Same scoping rule as everywhere else: bare names are this project's, qualified ones are not.
        Assert.Equal("docker:daemon",
            new ProjectConfig { Name = "zeta", Stack = new StackConfig { Resource = "docker:daemon" } }.StackResourceKey);
    }

    [Fact]
    public async Task A_person_holding_the_stack_blocks_an_agent_and_vice_versa()
    {
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);

        Assert.NotNull(await leases.TryAcquireAllAsync(
            new[] { "zeta:app-stack" }, 0, "manual", Ttl, holderLabel: ProjectStackService.HumanHolder));

        // The whole point: an agent cannot claim QA while someone is using the stack by hand.
        Assert.Null(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 7, "qa-test", Ttl));

        Assert.True(await leases.ReleaseHumanLeaseAsync("zeta:app-stack", "stopped"));
        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 7, "qa-test", Ttl));

        // ...and the other direction, which is what the Stop button checks before it downs anything.
        var holder = await leases.HolderOfAsync("zeta:app-stack");
        Assert.NotNull(holder);
        Assert.Null(holder!.HolderLabel);
        Assert.Equal(7, holder.TicketId);
    }

    [Fact]
    public async Task Releasing_a_human_lease_never_touches_an_agents()
    {
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);

        await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 9, "qa-test", Ttl);

        // Stop must be inert against an agent's hold, or a mistimed click takes the stack down
        // under a running QA agent - the exact collision the lease exists to prevent.
        Assert.False(await leases.ReleaseHumanLeaseAsync("zeta:app-stack", "stopped"));
        Assert.False(await leases.ExtendHumanLeaseAsync("zeta:app-stack", Ttl));
        Assert.NotNull(await leases.HolderOfAsync("zeta:app-stack"));
    }

    [Fact]
    public async Task An_orchestrator_restart_leaves_the_humans_hold_alone()
    {
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);

        await leases.TryAcquireAllAsync(
            new[] { "zeta:app-stack" }, 0, "manual", Ttl, holderLabel: ProjectStackService.HumanHolder);
        await leases.TryAcquireAllAsync(new[] { "zeta:db" }, 4, "qa-test", Ttl);

        // "A fresh orchestrator has no workers" is an argument about workers. The person's stack is
        // still up and still theirs, so only the agent's lease is swept.
        Assert.Equal(1, await leases.ReleaseAllAsync("orchestrator restarted"));

        var still = await leases.ActiveAsync();
        Assert.Equal("zeta:app-stack", Assert.Single(still).ResourceKey);
    }

    [Fact]
    public async Task A_human_lease_lapses_when_nothing_renews_it()
    {
        await using var ctx = NewCtx();
        var leases = new ResourceLeaseService(ctx);

        await leases.TryAcquireAllAsync(
            new[] { "zeta:app-stack" }, 0, "manual", TimeSpan.FromMinutes(-1), holderLabel: ProjectStackService.HumanHolder);

        // The dashboard pushes this out every few seconds while the stack answers, so a short TTL
        // only bounds how long a DEAD dashboard can hold the queue hostage.
        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "zeta:app-stack" }, 3, "qa-test", Ttl));
    }
}
