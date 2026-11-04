using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Carried-over QA verdicts, and refinement's audit of them. After a rework every pass expires; QA
/// may keep one only by ruling, with the diff named, that the rework cannot reach it. The ruling
/// counts for the hand-off, keeps the build that was actually driven, can be ruled out by the owner
/// in advance, and can be overturned by refinement - which then cannot hand the ticket on.
/// </summary>
public class QaCarryOverTests : IDisposable
{
    private const string Sha = "abc1234def5678900000000000000000000000ff";
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;

    public QaCarryOverTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext NewCtx() => new(_options);

    /// <summary>
    /// A ticket in <paramref name="phase"/> whose one QA item was driven on <see cref="Sha"/> an hour
    /// ago and has since been bounced past - the expired pass every case here starts from. Shots are
    /// in place so only the checklist decides the QA hand-off.
    /// </summary>
    private async Task<(int TicketId, int ItemId)> SeedExpired(TicketPhase phase, Action<Ticket>? setup = null)
    {
        await using var ctx = NewCtx();
        var driven = DateTime.UtcNow.AddHours(-1);
        var t = new Ticket
        {
            ProjectKey = "alpha", Title = "t", Phase = phase, Branch = "ticket/1",
            LearningBrief = "brief", LearningBriefAt = DateTime.UtcNow,
        };
        t.QaItems.Add(new QaItem { Text = "the other screen still loads", Status = QaStatus.Passed, VerifiedAt = driven, VerifiedOn = Sha });
        setup?.Invoke(t);
        ctx.Tickets.Add(t);
        await ctx.SaveChangesAsync();
        // Real shots count at any age, so a later bounce does not hold the QA hand-off on them.
        await using (var shots = NewCtx())
            await new TicketService(shots).SetQaShotsAsync(t.Id, new[] { ("0.png", "The finished screen.") });
        await Bounced(t.Id, DateTime.UtcNow.AddMinutes(-30));
        return (t.Id, t.QaItems[0].Id);
    }

    private async Task Bounced(int ticketId, DateTime at)
    {
        await using var ctx = NewCtx();
        ctx.PhaseTransitions.Add(new PhaseTransition
        {
            TicketId = ticketId, ProjectKey = "alpha",
            From = TicketPhase.Refining, To = TicketPhase.NeedsImplementation,
            Actor = TransitionActor.Worker, Backward = true, At = at,
        });
        await ctx.SaveChangesAsync();
    }

    private const string GoodNote = "git diff abc1234..HEAD changes only WizardStep3.tsx (the results grid height); "
                                  + "this item is on the settings screen, which imports nothing from it.";

    // ---- The gate counts a carry-over as verified ----

    [Fact]
    public async Task An_expired_pass_holds_the_qa_hand_off_until_it_is_carried_over()
    {
        var (id, item) = await SeedExpired(TicketPhase.QaTesting);

        await using (var ctx = NewCtx())
            await Assert.ThrowsAsync<HandoffRequirementException>(() =>
                new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));

        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            var (carried, error) = await svc.CarryOverQaItemAsync(item, GoodNote);
            Assert.Null(error);
            Assert.NotNull(carried!.CarriedOverAt);
        }

        await using (var ctx = NewCtx())
        {
            var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);
            Assert.Equal(TicketPhase.NeedsRefinement, t!.Phase);
        }
    }

    [Fact]
    public async Task A_carry_over_shows_as_its_own_verdict()
    {
        var (id, item) = await SeedExpired(TicketPhase.QaTesting);
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.CarryOverQaItemAsync(item, GoodNote);

        var t = (await svc.GetAsync(id))!;
        var dto = TicketMapper.ToDetail(t, lastBackwardAt: await svc.LastBackwardAtAsync(id)).QaItems.Single();
        Assert.True(dto.Verified);
        Assert.Equal("CarriedOver", dto.Verdict);
        Assert.Equal(GoodNote, dto.CarryOverNote);
        Assert.Equal(Sha, dto.VerifiedOn);
        Assert.False(dto.PassedByInspection);
    }

    /// <summary>A carry-over is dated by its ruling, so the next rework expires it like any pass.</summary>
    [Fact]
    public async Task A_carry_over_expires_at_the_next_rework()
    {
        var (id, item) = await SeedExpired(TicketPhase.QaTesting);
        await using (var ctx = NewCtx())
            await new TicketService(ctx).CarryOverQaItemAsync(item, GoodNote);

        await Bounced(id, DateTime.UtcNow.AddSeconds(1));

        await using (var ctx = NewCtx())
        {
            var q = await ctx.QaItems.SingleAsync(x => x.Id == item);
            Assert.False(HandoffRequirements.IsVerified(q, await new TicketService(ctx).LastBackwardAtAsync(id)));
        }
    }

    // ---- The note is required, and must name the build ----

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("The rework only touched the wizard, nothing here.")]   // names no commit
    public async Task A_carry_over_without_a_note_naming_the_build_is_refused(string? note)
    {
        var (_, item) = await SeedExpired(TicketPhase.QaTesting);
        await using var ctx = NewCtx();
        var (carried, error) = await new TicketService(ctx).CarryOverQaItemAsync(item, note);

        Assert.Null(carried);
        Assert.NotNull(error);
        Assert.Null((await ctx.QaItems.AsNoTracking().SingleAsync(q => q.Id == item)).CarriedOverAt);
    }

    [Fact]
    public async Task An_item_with_no_recorded_build_cannot_be_carried()
    {
        var (_, item) = await SeedExpired(TicketPhase.QaTesting, t => t.QaItems[0].VerifiedOn = null);
        await using var ctx = NewCtx();
        var (carried, error) = await new TicketService(ctx).CarryOverQaItemAsync(item, GoodNote);
        Assert.Null(carried);
        Assert.Contains("no record of the build", error);
    }

    [Fact]
    public async Task Only_an_expired_pass_in_qa_can_be_carried()
    {
        // Not Passed: nothing to keep.
        var (_, failed) = await SeedExpired(TicketPhase.QaTesting, t => t.QaItems[0].Status = QaStatus.Failed);
        await using (var ctx = NewCtx())
            Assert.Null((await new TicketService(ctx).CarryOverQaItemAsync(failed, GoodNote)).item);

        // Not in QA: carrying is the QA run's ruling.
        var (_, refining) = await SeedExpired(TicketPhase.Refining);
        await using (var ctx = NewCtx())
            Assert.Null((await new TicketService(ctx).CarryOverQaItemAsync(refining, GoodNote)).item);

        // Already current: nothing to carry.
        var (_, current) = await SeedExpired(TicketPhase.QaTesting, t => t.QaItems[0].VerifiedAt = DateTime.UtcNow);
        await using (var ctx = NewCtx())
            Assert.Null((await new TicketService(ctx).CarryOverQaItemAsync(current, GoodNote)).item);
    }

    /// <summary>A project of several repos: the note names each repo's build.</summary>
    [Fact]
    public async Task On_several_repos_the_note_must_name_every_build()
    {
        const string other = "fedcba9876543210000000000000000000000000";
        var (_, item) = await SeedExpired(TicketPhase.QaTesting,
            t => t.QaItems[0].VerifiedOn = QaCommits.Format(new[] { ("alpha-api", Sha), ("alpha-web", other) }));

        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        Assert.Null((await svc.CarryOverQaItemAsync(item, GoodNote)).item);   // names alpha-api's only
        var (carried, error) = await svc.CarryOverQaItemAsync(item, GoodNote + " alpha-web: git diff fedcba9..HEAD is empty.");
        Assert.Null(error);
        Assert.NotNull(carried);
    }

    [Fact]
    public void Verified_on_round_trips_one_repo_and_several()
    {
        Assert.Equal(Sha, QaCommits.Format(new[] { ("", Sha) }));
        Assert.Equal(new[] { ("", Sha) }, QaCommits.Parse(Sha));

        var several = QaCommits.Format(new[] { ("alpha-api", "111"), ("alpha-web", "222") });
        Assert.Equal("alpha-api@111, alpha-web@222", several);
        Assert.Equal(new[] { ("alpha-api", "111"), ("alpha-web", "222") }, QaCommits.Parse(several));
        Assert.Null(QaCommits.Format(Array.Empty<(string, string)>()));
    }

    // ---- The verified-on build survives ----

    [Fact]
    public async Task A_carry_over_keeps_the_build_that_was_actually_driven()
    {
        var (id, item) = await SeedExpired(TicketPhase.QaTesting);
        DateTime? drivenAt;
        await using (var ctx = NewCtx())
            drivenAt = (await ctx.QaItems.AsNoTracking().SingleAsync(q => q.Id == item)).VerifiedAt;

        await using (var ctx = NewCtx())
            await new TicketService(ctx).CarryOverQaItemAsync(item, GoodNote);

        // A second rework, and a second carry: still measured from the first build.
        await Bounced(id, DateTime.UtcNow.AddSeconds(1));
        await using (var ctx = NewCtx())
        {
            var (carried, error) = await new TicketService(ctx).CarryOverQaItemAsync(item, GoodNote);
            Assert.Null(error);
            Assert.Equal(Sha, carried!.VerifiedOn);
            Assert.Equal(drivenAt, carried.VerifiedAt);
        }
    }

    [Fact]
    public async Task Driving_an_item_again_records_the_new_build_and_ends_the_carry()
    {
        var (_, item) = await SeedExpired(TicketPhase.QaTesting);
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.CarryOverQaItemAsync(item, GoodNote);

        var qi = await svc.UpdateQaItemAsync(item, null, QaStatus.Passed, null, null, verifiedOn: "0123456789");
        Assert.Equal("0123456789", qi!.VerifiedOn);
        Assert.Null(qi.CarriedOverAt);
        Assert.Null(qi.CarryOverNote);

        var started = await svc.StartQaItemAsync(item);
        Assert.Null(started!.VerifiedOn);
    }

    // ---- The owner's full re-run ----

    [Fact]
    public async Task The_owner_can_send_a_ticket_back_for_a_full_qa_rerun()
    {
        var (id, _) = await SeedExpired(TicketPhase.NeedsReview);
        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).RequestChangesAsync(id, "The grid is too short.", null, shortLoop: false, fullQa: true);

        Assert.Equal(TicketPhase.NeedsImplementation, t!.Phase);
        Assert.True(t.FullQaRequired);
    }

    [Fact]
    public async Task A_full_rerun_cannot_also_skip_qa()
    {
        var (id, _) = await SeedExpired(TicketPhase.NeedsReview);
        await using var ctx = NewCtx();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TicketService(ctx).RequestChangesAsync(id, "Tweak.", null, shortLoop: true, fullQa: true));
    }

    [Fact]
    public async Task Under_a_full_rerun_nothing_can_be_carried_and_a_carry_does_not_count()
    {
        var (id, item) = await SeedExpired(TicketPhase.QaTesting, t => t.FullQaRequired = true);

        await using (var ctx = NewCtx())
        {
            var (carried, error) = await new TicketService(ctx).CarryOverQaItemAsync(item, GoodNote);
            Assert.Null(carried);
            Assert.Contains("full QA re-run", error);
        }

        // A carry made before the owner switched it on stops counting too.
        await using (var ctx = NewCtx())
        {
            var q = await ctx.QaItems.SingleAsync(x => x.Id == item);
            q.CarriedOverAt = DateTime.UtcNow;
            q.CarryOverNote = GoodNote;
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = NewCtx())
        {
            var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
                new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));
            Assert.Contains("full re-run", ex.Message);
        }
    }

    [Fact]
    public async Task A_full_rerun_is_spent_when_qa_hands_off_clean()
    {
        var (id, item) = await SeedExpired(TicketPhase.QaTesting, t => t.FullQaRequired = true);
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.UpdateQaItemAsync(item, null, QaStatus.Passed, null, null, verifiedOn: Sha);

        var t = await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);
        Assert.False(t!.FullQaRequired);
    }

    [Fact]
    public async Task A_full_rerun_survives_qa_sending_the_work_back()
    {
        var (id, _) = await SeedExpired(TicketPhase.QaTesting, t => t.FullQaRequired = true);
        await using var ctx = NewCtx();
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsImplementation, null, TransitionActor.Worker);
        Assert.True(t!.FullQaRequired);
    }

    // ---- Refinement's expiry ----

    /// <summary>
    /// A carried item in refinement: refinement reads the diff, finds it does reach the item, and
    /// expires it. The ticket then cannot go to review - only back to QA.
    /// </summary>
    private async Task<(int TicketId, int ItemId)> SeedCarriedInRefinement()
    {
        var (id, item) = await SeedExpired(TicketPhase.QaTesting);
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        Assert.Null((await svc.CarryOverQaItemAsync(item, GoodNote)).error);
        await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);
        await svc.TransitionAsync(id, TicketPhase.Refining, null, TransitionActor.System);
        return (id, item);
    }

    [Fact]
    public async Task Refinement_can_hand_off_a_carry_it_does_not_expire()
    {
        var (id, _) = await SeedCarriedInRefinement();
        await using var ctx = NewCtx();
        // The brief must postdate the bounce; it does (seeded now, bounce half an hour ago).
        var t = await new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker);
        Assert.Equal(TicketPhase.NeedsReview, t!.Phase);
    }

    [Fact]
    public async Task An_item_refinement_expired_blocks_the_hand_off_to_review()
    {
        var (id, item) = await SeedCarriedInRefinement();
        await using (var ctx = NewCtx())
        {
            var (expired, error) = await new TicketService(ctx).ExpireQaItemAsync(item,
                "The diff changes the shared grid component the settings screen also renders.");
            Assert.Null(error);
            Assert.NotNull(expired!.ExpiredAt);
        }

        await using (var ctx = NewCtx())
        {
            var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
                new TicketService(ctx).TransitionAsync(id, TicketPhase.NeedsReview, null, TransitionActor.Worker));
            Assert.Contains("NeedsQa", ex.Message);
            Assert.Equal(TicketPhase.NeedsQa, ex.RouteInstead);
        }

        // NeedsQa is the route it is meant to take, and the guide says so before the move.
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            var guide = TicketMapper.ToDetail((await svc.GetAsync(id))!).NextPhaseGuide
                .Single(g => g.Phase == nameof(TicketPhase.NeedsReview));
            Assert.Contains("expired", guide.Requirement);

            var t = await svc.TransitionAsync(id, TicketPhase.NeedsQa, "expired one item", TransitionActor.Worker);
            Assert.Equal(TicketPhase.NeedsQa, t!.Phase);
        }
    }

    [Fact]
    public async Task An_expired_item_must_be_driven_again_and_cannot_be_carried()
    {
        var (id, item) = await SeedCarriedInRefinement();
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            await svc.ExpireQaItemAsync(item, "The diff reaches it through the shared grid.");
            await svc.TransitionAsync(id, TicketPhase.NeedsQa, "expired", TransitionActor.Worker);
            await svc.TransitionAsync(id, TicketPhase.QaTesting, null, TransitionActor.System);
        }

        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            var (carried, error) = await svc.CarryOverQaItemAsync(item, GoodNote);
            Assert.Null(carried);
            Assert.Contains("expired", error);

            var dto = TicketMapper.ToDetail((await svc.GetAsync(id))!, lastBackwardAt: await svc.LastBackwardAtAsync(id)).QaItems.Single();
            Assert.Equal("Expired", dto.Verdict);
            Assert.False(dto.Verified);

            var ex = await Assert.ThrowsAsync<HandoffRequirementException>(() =>
                svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker));
            Assert.Contains("expired by refinement", ex.Message);
        }

        // Driving it clears the expiry, and the ticket moves on.
        await using (var ctx = NewCtx())
        {
            var svc = new TicketService(ctx);
            var qi = await svc.UpdateQaItemAsync(item, null, QaStatus.Passed, null, null, verifiedOn: "0123456789");
            Assert.Null(qi!.ExpiredAt);
            var t = await svc.TransitionAsync(id, TicketPhase.NeedsRefinement, null, TransitionActor.Worker);
            Assert.Equal(TicketPhase.NeedsRefinement, t!.Phase);
        }
    }

    [Fact]
    public async Task Expiry_is_refinement_s_needs_a_reason_and_a_pass()
    {
        var (_, inQa) = await SeedExpired(TicketPhase.QaTesting);
        await using (var ctx = NewCtx())
            Assert.Null((await new TicketService(ctx).ExpireQaItemAsync(inQa, "reaches it")).item);

        var (_, item) = await SeedCarriedInRefinement();
        await using (var ctx = NewCtx())
            Assert.Null((await new TicketService(ctx).ExpireQaItemAsync(item, "  ")).item);

        var (_, failed) = await SeedExpired(TicketPhase.Refining, t => t.QaItems[0].Status = QaStatus.Failed);
        await using (var ctx = NewCtx())
            Assert.Null((await new TicketService(ctx).ExpireQaItemAsync(failed, "reaches it")).item);
    }

    /// <summary>What refinement is pointed at: the carried and the by-inspection, not the driven.</summary>
    [Fact]
    public async Task The_audit_list_is_the_carried_and_the_inspected()
    {
        var (id, item) = await SeedExpired(TicketPhase.QaTesting, t =>
        {
            t.QaItems.Add(new QaItem { Text = "driven", Status = QaStatus.Passed, VerifiedAt = DateTime.UtcNow, VerifiedOn = Sha, Order = 1 });
            t.QaItems.Add(new QaItem { Text = "read", Status = QaStatus.Passed, VerifiedAt = DateTime.UtcNow, PassedByInspection = true, Order = 2 });
        });
        await using var ctx = NewCtx();
        var svc = new TicketService(ctx);
        await svc.CarryOverQaItemAsync(item, GoodNote);

        var t = (await svc.GetAsync(id))!;
        var audit = HandoffRequirements.ToAudit(t, await svc.LastBackwardAtAsync(id)).Select(q => q.Text).ToList();
        Assert.Equal(new[] { "the other screen still loads", "read" }, audit);
    }
}
