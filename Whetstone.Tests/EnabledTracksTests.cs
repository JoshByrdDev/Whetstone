using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The per-project "enabled tracks" setting: how a list of names is read, and the rule that will
/// not let a track be switched off out from under work still riding on it.
///
/// The rule is worth pinning down precisely because the setting is otherwise inert - nothing in the
/// pipeline consults it - so nothing else in the system would notice it going wrong. A track
/// switched off under live tickets does not fail; it succeeds, and those tickets keep being claimed
/// and walked forward with no lane on the board to show where they are.
/// </summary>
public class EnabledTracksTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly string _dataRoot;

    public EnabledTracksTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();

        _dataRoot = Path.Combine(Path.GetTempPath(), "whetstone-tracks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dataRoot, "projects", "alpha"));
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* temp dir */ }
    }

    private DashboardDbContext NewCtx() => new(_options);

    private ProjectConfig LoadProject(string defaultsJson)
    {
        var paths = new DashboardPaths(_dataRoot);
        File.WriteAllText(paths.ProjectConfigDefaults("alpha"), defaultsJson);
        return new ProjectRegistry(paths).GetProjectConfig("alpha");
    }

    private async Task Add(TicketTrack track, TicketPhase phase, string project = "alpha")
    {
        using var ctx = NewCtx();
        ctx.Tickets.Add(new Ticket
        {
            ProjectKey = project,
            Title = $"{track} {phase}",
            Track = track,
            Phase = phase,
        });
        await ctx.SaveChangesAsync();
    }

    // ---- Reading the setting ------------------------------------------------

    [Fact]
    public void A_project_that_says_nothing_runs_delivery_only()
        => Assert.Equal(new[] { TicketTrack.Delivery }, LoadProject("""{ "displayName": "Alpha" }""").Tracks);

    [Fact]
    public void Both_tracks_read_back_in_enum_order_however_they_were_written()
        => Assert.Equal(
            new[] { TicketTrack.Delivery, TicketTrack.Review },
            LoadProject("""{ "tracks": ["Review", "delivery"] }""").Tracks);

    [Fact]
    public void Review_alone_is_a_legal_setting()
        => Assert.Equal(new[] { TicketTrack.Review }, LoadProject("""{ "tracks": ["Review"] }""").Tracks);

    /// <summary>
    /// Every way of writing nonsense lands on delivery-only rather than on nothing. A project with
    /// no track at all can hold no ticket and draws a board with no lanes, so it is never the
    /// reading to take from a file somebody fat-fingered.
    /// </summary>
    [Theory]
    [InlineData("""{ "tracks": [] }""")]
    [InlineData("""{ "tracks": ["Deliverry", "peer-review"] }""")]
    [InlineData("""{ "tracks": "Delivery" }""")]
    [InlineData("""{ "tracks": [1, 2] }""")]
    public void A_broken_list_reads_as_delivery_only(string json)
        => Assert.Equal(new[] { TicketTrack.Delivery }, LoadProject(json).Tracks);

    [Fact]
    public void Parse_drops_duplicates_and_unknown_names()
        => Assert.Equal(
            new[] { TicketTrack.Delivery, TicketTrack.Review },
            EnabledTracks.Parse(new[] { "Review", "REVIEW", " delivery ", "nonsense", null }));

    /// <summary>
    /// Null and empty are different questions. Null is "the request did not mention tracks", which
    /// leaves the stored list alone; empty is "I want none", which is the thing to refuse.
    /// </summary>
    [Fact]
    public void Nothing_said_and_none_wanted_are_not_the_same_answer()
    {
        Assert.Null(EnabledTracks.Parse(null));
        Assert.Empty(EnabledTracks.Parse(Array.Empty<string>())!);
    }

    // ---- The rule -----------------------------------------------------------

    private static readonly TicketTrack[] Both = { TicketTrack.Delivery, TicketTrack.Review };
    private static readonly TicketTrack[] DeliveryOnly = { TicketTrack.Delivery };
    private static readonly TicketTrack[] ReviewOnly = { TicketTrack.Review };

    private async Task<string?> Refuse(TicketTrack[] current, TicketTrack[]? wanted, string project = "alpha")
    {
        using var ctx = NewCtx();
        return await EnabledTracks.RefusalAsync(project, current, wanted, ctx);
    }

    [Fact]
    public async Task A_request_that_says_nothing_about_tracks_is_allowed()
    {
        await Add(TicketTrack.Review, TicketPhase.PeerReviewing);
        Assert.Null(await Refuse(Both, null));
    }

    [Fact]
    public async Task Turning_a_track_ON_is_never_refused()
    {
        await Add(TicketTrack.Delivery, TicketPhase.Implementing);
        Assert.Null(await Refuse(DeliveryOnly, Both));
    }

    [Fact]
    public async Task An_empty_list_is_refused_even_with_an_empty_board()
        => Assert.Contains("at least one track", await Refuse(Both, Array.Empty<TicketTrack>()));

    [Fact]
    public async Task A_track_with_nothing_on_it_switches_off()
        => Assert.Null(await Refuse(Both, DeliveryOnly));

    [Fact]
    public async Task A_track_carrying_live_work_will_not_switch_off()
    {
        await Add(TicketTrack.Review, TicketPhase.PeerReviewing);
        var why = await Refuse(Both, DeliveryOnly);
        Assert.Contains("1 peer review ticket is", why);   // singular still reads as a sentence
    }

    /// <summary>
    /// Blocked is unfinished. It is the state most likely to be forgotten - nobody is working it and
    /// nothing is moving - which makes it the one worth refusing over rather than the one to excuse.
    /// </summary>
    [Fact]
    public async Task A_blocked_ticket_still_blocks_the_change()
    {
        await Add(TicketTrack.Review, TicketPhase.Blocked);
        Assert.Contains("still open", await Refuse(Both, DeliveryOnly));
    }

    [Fact]
    public async Task Done_tickets_do_not_hold_a_track_open()
    {
        await Add(TicketTrack.Review, TicketPhase.Done);
        await Add(TicketTrack.Review, TicketPhase.Done);
        Assert.Null(await Refuse(Both, DeliveryOnly));
    }

    /// <summary>
    /// The count is per project, not board-wide: one project's open review work must not lock the
    /// checkbox on another. That is the mistake a query missing its project clause makes, and it is
    /// invisible on a single-project machine - which is the only kind this has ever run on.
    /// </summary>
    [Fact]
    public async Task Another_projects_open_work_is_not_this_projects_problem()
    {
        await Add(TicketTrack.Review, TicketPhase.PeerReviewing, project: "beta");
        Assert.Null(await Refuse(Both, DeliveryOnly, project: "alpha"));
        Assert.Contains("still open", await Refuse(Both, DeliveryOnly, project: "beta"));
    }

    [Fact]
    public async Task Delivery_is_refused_on_the_same_terms_as_review()
    {
        await Add(TicketTrack.Delivery, TicketPhase.NeedsQa);
        Assert.Contains("1 delivery ticket is", await Refuse(Both, ReviewOnly));
    }

    [Fact]
    public async Task The_count_in_the_refusal_is_the_number_actually_left()
    {
        await Add(TicketTrack.Review, TicketPhase.NeedsPeerReview);
        await Add(TicketTrack.Review, TicketPhase.PeerReviewing);
        await Add(TicketTrack.Review, TicketPhase.Done);            // finished - not counted
        await Add(TicketTrack.Delivery, TicketPhase.Implementing);  // other track - not counted
        Assert.Contains("2 peer review tickets are", await Refuse(Both, DeliveryOnly));
    }

    // ---- The counts the form greys its checkboxes with ----------------------

    /// <summary>
    /// The form's counts and the refusal have to agree, or a box the form draws as locked is one the
    /// server then lets through. They share <see cref="EnabledTracks.Unfinished"/> for that reason;
    /// this is the test that says so out loud.
    /// </summary>
    [Fact]
    public async Task Open_counts_agree_with_the_refusal()
    {
        await Add(TicketTrack.Review, TicketPhase.PeerReviewing);
        await Add(TicketTrack.Review, TicketPhase.Blocked);
        await Add(TicketTrack.Review, TicketPhase.Done);
        await Add(TicketTrack.Delivery, TicketPhase.Done);
        await Add(TicketTrack.Review, TicketPhase.NeedsPeerReview, project: "beta");

        using var ctx = NewCtx();
        var open = await EnabledTracks.OpenByTrackAsync(ctx);

        Assert.Equal(2, open[EnabledTracks.Key("alpha", TicketTrack.Review)]);
        Assert.Equal(1, open[EnabledTracks.Key("beta", TicketTrack.Review)]);
        // A track with nothing unfinished on it is ABSENT rather than zero; the caller defaults it.
        Assert.False(open.ContainsKey(EnabledTracks.Key("alpha", TicketTrack.Delivery)));
        Assert.Contains("2 peer review tickets are", await Refuse(Both, DeliveryOnly));
    }
}
