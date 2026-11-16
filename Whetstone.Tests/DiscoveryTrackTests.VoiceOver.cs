using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A recording's voice-over: what a run says (<c>say</c>) is stamped by the board like a chapter,
/// placed in the video when the recording is adopted, and voiced in place of the run's log - which
/// is read aloud instead only for a recording whose run said nothing.
/// </summary>
public partial class DiscoveryTrackTests
{
    [Fact]
    public async Task A_line_is_said_only_while_the_product_is_driven_and_only_in_a_line()
    {
        var id = await DiscoveryIn(TicketPhase.Sifting);
        var (_, _, d) = New();
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.SayAsync(id, "I want to put the class on.", 0));
        await SetPhase(id, TicketPhase.Scouting);
        var n = await d.SayAsync(id, "  I want to put the class on. Events looks like the way in.  ", 0);
        Assert.Equal("I want to put the class on. Events looks like the way in.", n.Text);
        Assert.Equal(0, n.Job);
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.SayAsync(id, " ", null));
        // Too long is refused, not cut: the agent says it shorter rather than being voiced mid-sentence.
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.SayAsync(id, new string('a', DiscoveryService.MaxSay + 1), null));
        await SetPhase(id, TicketPhase.Rechecking);
        Assert.NotNull(await d.SayAsync(id, "This one was meant to let me take sign-ups.", null));
    }

    [Fact]
    public async Task What_was_said_during_a_recording_is_placed_in_it()
    {
        var id = await DiscoveryIn(TicketPhase.NeedsSifting);
        var start = new DateTime(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc);
        RunVideo(id, start, 60);
        await using (var db = new DashboardDbContext(_options))
        {
            db.DiscoveryNarrations.Add(new DiscoveryNarration { TicketId = id, Text = "During.", At = start.AddSeconds(8.25) });
            db.DiscoveryNarrations.Add(new DiscoveryNarration { TicketId = id, Text = "Long after.", At = start.AddMinutes(5) });
            await db.SaveChangesAsync();
            Assert.Equal(1, await DiscoveryMedia.AdoptAsync(db, _paths, id, ffmpeg: null, start.AddMinutes(10)));
        }
        await using (var db = new DashboardDbContext(_options))
        {
            var said = await db.DiscoveryNarrations.AsNoTracking().OrderBy(n => n.Id).ToListAsync();
            Assert.NotNull(said[0].RecordingId);
            Assert.Equal(8.25, said[0].VideoAt);
            Assert.Null(said[1].RecordingId);
            Assert.Null(said[1].VideoAt);
        }
    }

    private static DiscoveryService.View ViewOf(IEnumerable<DiscoveryObservation> obs, IEnumerable<DiscoveryChapter> chapters, IEnumerable<DiscoveryNarration> said) =>
        new(null, obs.ToList(), [], [], [], [], 0, [], 0, [], chapters.ToList(), said.ToList());

    [Fact]
    public void A_run_that_narrated_is_heard_in_its_own_words_and_not_over_its_log()
    {
        var v = ViewOf(
            [new DiscoveryObservation { Id = 1, Title = "Found", Kind = ObservationKind.Wall, RecordingId = 7, VideoAt = 5 }],
            [new DiscoveryChapter { Id = 1, Title = "Job 1", RecordingId = 7, VideoAt = 1 }],
            [new DiscoveryNarration { Id = 2, Text = "Second.", RecordingId = 7, VideoAt = 9 },
             new DiscoveryNarration { Id = 1, Text = "First.", RecordingId = 7, VideoAt = 2 },
             new DiscoveryNarration { Id = 3, Text = "Another run's.", RecordingId = 8, VideoAt = 3 },
             new DiscoveryNarration { Id = 4, Text = "Not placed.", RecordingId = null, VideoAt = null }]);
        var lines = DiscoveryVoiceOver.LinesOf(v, 7);
        Assert.Equal(new[] { "First.", "Second." }, lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(DiscoveryVoiceOver.Source.Say, l.From));
    }

    [Fact]
    public void A_run_that_said_nothing_has_its_chapters_and_findings_read_aloud()
    {
        var v = ViewOf(
            [new DiscoveryObservation { Id = 2, Title = "Found later", Kind = ObservationKind.Bug, RecordingId = 7, VideoAt = 30 },
             new DiscoveryObservation { Id = 1, Title = "Found at once", Kind = ObservationKind.Wall, RecordingId = 7, VideoAt = 10 }],
            [new DiscoveryChapter { Id = 1, Title = "Job 1", RecordingId = 7, VideoAt = 10 }],
            [new DiscoveryNarration { Id = 1, Text = "Another run's.", RecordingId = 8, VideoAt = 3 }]);
        var lines = DiscoveryVoiceOver.LinesOf(v, 7);
        // A chapter before what was found in it, when both fall on one second.
        Assert.Equal(new[] { "Job 1", "Found at once", "Found later" }, lines.Select(l => l.Text));
        Assert.Equal("Bug", lines[2].Kind);
        Assert.Equal(new[] { "c1", "o1", "o2" }, lines.Select(l => l.Key));
    }
}
