using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The agent's thinking time: where a recording stands still is measured once, and a player jumps
/// over it - never across a moment that matters. A proposal's clip carries the part of the
/// voice-over and the skips that fall in it, in its own seconds.
/// </summary>
public partial class DiscoveryTrackTests
{
    [Fact]
    public void Freezedetect_lines_become_stills_joined_across_a_flicker()
    {
        var output = """
            [freezedetect @ 0x1] lavfi.freezedetect.freeze_start: 0
            [freezedetect @ 0x1] lavfi.freezedetect.freeze_end: 2.52
            [freezedetect @ 0x1] lavfi.freezedetect.freeze_start: 2.6
            [freezedetect @ 0x1] lavfi.freezedetect.freeze_end: 9.16
            [freezedetect @ 0x1] lavfi.freezedetect.freeze_start: 40.5
            """;
        Assert.Equal(new[] { (0.0, 9.16), (40.5, 60.0) }, Ffmpeg.ParseStills(output, 60));
    }

    [Fact]
    public void A_still_is_skipped_less_its_ends_and_every_window_it_must_keep()
    {
        // A 20s still with an observation at 10: the page settles, then a jump to just before the
        // moment, a beat of it, then a jump to just before the picture moves again.
        var s = DiscoveryMedia.Skips(new[] { (0.0, 20.0) }, DiscoveryMedia.KeepWindows([], [10.0], []));
        Assert.Equal(new[] { (0.4, 9.0), (11.5, 19.6) }, s);
        // A piece shorter than the minimum is not worth the jump.
        Assert.Empty(DiscoveryMedia.Skips(new[] { (5.0, 6.5) }, []));
    }

    [Fact]
    public void A_skip_never_crosses_a_line_or_a_chapter()
    {
        var s = DiscoveryMedia.Skips(new[] { (0.0, 30.0) }, DiscoveryMedia.KeepWindows([5.0], [], [15.0]));
        foreach (var m in new[] { 5.0, 15.0 })
            Assert.DoesNotContain(s, k => k.From <= m && m < k.To);
        Assert.Equal(new[] { (0.4, 4.7), (7.0, 14.5), (16.0, 29.6) }, s);
    }

    [Fact]
    public void A_moment_of_the_recording_is_found_in_the_clip_cut_from_it()
    {
        var spans = new[] { (30.0, 46.0), (100.0, 110.0) };
        Assert.Equal(12.0, DiscoveryMedia.ToClip(spans, 42));
        Assert.Equal(21.0, DiscoveryMedia.ToClip(spans, 105));
        Assert.Null(DiscoveryMedia.ToClip(spans, 60));
        // A line said just before a span opens it - the scout says what it is about to do.
        Assert.Equal(16.0, DiscoveryMedia.ToClip(spans, 98, DiscoveryMedia.LineLeadIn));
        Assert.Null(DiscoveryMedia.ToClip(spans, 98));
        Assert.Equal(new[] { (0.0, 10.0), (16.0, 20.0) },
            DiscoveryMedia.ClipSkips(spans, new[] { (20.0, 40.0), (90.0, 104.0), (108.0, 108.5) }));
    }

    /// <summary>Four seconds moving, six standing still, four moving: a scout acting, thinking, acting.</summary>
    private string MakeThinkingVideo(string ffmpeg)
    {
        var file = Path.Combine(_root, $"think-{Guid.NewGuid():N}.webm");
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i",
                     "testsrc=d=4:s=320x240:r=10,format=yuv420p[a];color=c=gray:s=320x240:r=10:d=6,format=yuv420p[b];testsrc=d=4:s=320x240:r=10,format=yuv420p[c];[a][b][c]concat=n=3[out0]",
                     "-c:v", "libvpx", "-b:v", "200k", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return file;
    }

    [Fact]
    public async Task A_recording_is_measured_once_where_it_stands_still()
    {
        if (FfmpegHere() is not { } ffmpeg) return;
        var id = await DiscoveryIn(TicketPhase.NeedsSifting);
        var start = new DateTime(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc);
        RunVideo(id, start, 14, MakeThinkingVideo(ffmpeg));
        await using (var db = new DashboardDbContext(_options))
        {
            await DiscoveryMedia.AdoptAsync(db, _paths, id, ffmpeg, DateTime.UtcNow);
            await DiscoveryMedia.MeasureStillsAsync(db, _paths, id, ffmpeg);
        }
        await using (var db = new DashboardDbContext(_options))
        {
            var rec = await db.DiscoveryRecordings.AsNoTracking().SingleAsync(r => r.TicketId == id);
            var still = Assert.Single(DiscoveryMedia.StillsOf(rec)!);
            Assert.InRange(still.From, 3.5, 4.5);
            Assert.InRange(still.To, 9.5, 10.5);
        }
        // Measured is measured: a second read runs nothing.
        await using (var db = new DashboardDbContext(_options))
        {
            var rec = await db.DiscoveryRecordings.SingleAsync(r => r.TicketId == id);
            rec.Stills = "[]";
            await db.SaveChangesAsync();
            await DiscoveryMedia.MeasureStillsAsync(db, _paths, id, ffmpeg);
            Assert.Equal("[]", (await db.DiscoveryRecordings.AsNoTracking().SingleAsync(r => r.TicketId == id)).Stills);
        }
    }
}
