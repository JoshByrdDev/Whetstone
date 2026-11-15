using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Step 3, video (docs/discovery-track-plan.md): a run's recording is found and stamped by the board
/// rather than reported by the agent, chapters bound the clips, a clip is cut around what a proposal
/// cites, and Complete removes what the verdict no longer needs.
/// </summary>
public partial class DiscoveryTrackTests
{
    // ---- the clip's span --------------------------------------------------------------------

    [Fact]
    public void A_clip_starts_at_the_chapter_the_moment_is_in()
    {
        var s = DiscoveryMedia.ClipSpans(120, new[] { 10.0, 30.0, 60.0 }, new[] { 42.0 });
        Assert.Equal(new[] { (30.0, 46.0) }, s);
    }

    [Fact]
    public void A_long_chapter_is_cut_to_the_lead_and_a_near_one_to_the_minimum()
    {
        Assert.Equal(new[] { (30.0, 54.0) }, DiscoveryMedia.ClipSpans(120, new[] { 5.0 }, new[] { 50.0 }));
        // A chapter marked a second before the moment would show nothing of what led to it.
        Assert.Equal(new[] { (45.0, 54.0) }, DiscoveryMedia.ClipSpans(120, new[] { 49.0 }, new[] { 50.0 }));
    }

    [Fact]
    public void A_clip_ends_at_the_next_chapter_but_never_on_the_moment_itself()
    {
        Assert.Equal(new[] { (30.0, 52.0) }, DiscoveryMedia.ClipSpans(120, new[] { 30.0, 52.0 }, new[] { 50.0 }));
        Assert.Equal(new[] { (30.0, 51.5) }, DiscoveryMedia.ClipSpans(120, new[] { 30.0, 50.5 }, new[] { 50.0 }));
    }

    [Fact]
    public void Moments_close_together_are_one_span_and_far_apart_are_two()
    {
        var s = DiscoveryMedia.ClipSpans(300, Array.Empty<double>(), new[] { 50.0, 60.0, 200.0 });
        Assert.Equal(new[] { (30.0, 64.0), (180.0, 204.0) }, s);
    }

    [Fact]
    public void A_clip_is_capped_and_never_runs_past_the_recording()
    {
        var s = DiscoveryMedia.ClipSpans(1000, Array.Empty<double>(), new[] { 100.0, 300.0, 500.0, 700.0 });
        Assert.Equal(DiscoveryMedia.MaxClip, s.Sum(x => x.To - x.From), 3);
        Assert.Equal(new[] { (0.0, 11.0) }, DiscoveryMedia.ClipSpans(11, Array.Empty<double>(), new[] { 27.0 }));
        Assert.Empty(DiscoveryMedia.ClipSpans(0, Array.Empty<double>(), new[] { 1.0 }));
    }

    [Fact]
    public void A_recording_starts_when_its_name_says()
    {
        Assert.Equal(new DateTime(2026, 11, 15, 5, 32, 17, 887, DateTimeKind.Utc), DiscoveryMedia.StartFromName("video-2026-11-15T05-32-17-887Z.webm"));
        Assert.Null(DiscoveryMedia.StartFromName("my-run.webm"));
    }

    // ---- chapters and moments ---------------------------------------------------------------

    [Fact]
    public async Task Chapters_are_marked_only_while_the_product_is_driven()
    {
        var id = await DiscoveryIn(TicketPhase.Sifting);
        var (_, _, d) = New();
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.MarkChapterAsync(id, "Plan the weekend", 0));
        await SetPhase(id, TicketPhase.Rechecking);
        var c = await d.MarkChapterAsync(id, "Plan the weekend", 0);
        Assert.Equal(0, c.Job);
        await SetPhase(id, TicketPhase.Scouting);
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.MarkChapterAsync(id, " ", null));
    }

    [Fact]
    public void An_observation_is_seen_when_its_screenshot_was_taken()
    {
        var shot = Path.Combine(_root, "seen.png");
        File.WriteAllBytes(shot, new byte[] { 1 });
        var logged = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(shot, logged.AddSeconds(-7));
        Assert.Equal(logged.AddSeconds(-7), DiscoveryService.ShotTaken(shot, logged));
        // A shot from long ago, or none, is no clock: the moment is when it was logged.
        File.SetLastWriteTimeUtc(shot, logged.AddHours(-2));
        Assert.Equal(logged, DiscoveryService.ShotTaken(shot, logged));
        Assert.Equal(logged, DiscoveryService.ShotTaken(null, logged));
    }

    // ---- adopting a recording ---------------------------------------------------------------

    /// <summary>A video the browser left in a run folder, named for when it started, last written <paramref name="seconds"/> later.</summary>
    private string RunVideo(int ticketId, DateTime start, double seconds, string? content = null)
    {
        var dir = Path.Combine(_paths.DiscoveryTicketDir(ticketId), DiscoveryMedia.RunsFolder, "20261115-050000");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"video-{start:yyyy-MM-dd}T{start:HH-mm-ss}-{start:fff}Z.webm");
        if (content is not null) File.Copy(content, file, overwrite: true);
        else File.WriteAllBytes(file, new byte[2048]);
        File.SetLastWriteTimeUtc(file, start.AddSeconds(seconds));
        return file;
    }

    private async Task<int> Observed(int ticketId, DateTime at, int round = 0, string? shot = null)
    {
        await using var db = new DashboardDbContext(_options);
        var o = new DiscoveryObservation { TicketId = ticketId, Kind = ObservationKind.Wall, Title = "t", Saw = "s", ObservedAt = at, CreatedAt = at.AddSeconds(5), Round = round, RunId = 41, ShotPath = shot };
        db.DiscoveryObservations.Add(o);
        await db.SaveChangesAsync();
        if (shot is not null)
        {
            Directory.CreateDirectory(_paths.DiscoveryTicketDir(ticketId));
            File.WriteAllBytes(Path.Combine(_paths.DiscoveryTicketDir(ticketId), shot), new byte[100]);
        }
        return o.Id;
    }

    [Fact]
    public async Task A_finished_recording_is_adopted_and_what_was_logged_during_it_is_stamped()
    {
        var id = await DiscoveryIn(TicketPhase.NeedsSifting);
        var start = new DateTime(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc);
        var file = RunVideo(id, start, 60);
        var during = await Observed(id, start.AddSeconds(12.5));
        var after = await Observed(id, start.AddMinutes(5));
        await using (var db = new DashboardDbContext(_options))
        {
            db.DiscoveryChapters.Add(new DiscoveryChapter { TicketId = id, Title = "Job 1", Job = 0, At = start.AddSeconds(3) });
            await db.SaveChangesAsync();
        }

        await using (var db = new DashboardDbContext(_options))
        {
            // Without ffmpeg the length is read from the file's own times.
            Assert.Equal(1, await DiscoveryMedia.AdoptAsync(db, _paths, id, ffmpeg: null, start.AddMinutes(10)));
            Assert.Equal(0, await DiscoveryMedia.AdoptAsync(db, _paths, id, ffmpeg: null, start.AddMinutes(10)));
        }

        await using var check = new DashboardDbContext(_options);
        var rec = await check.DiscoveryRecordings.SingleAsync(r => r.TicketId == id);
        Assert.Equal(start, rec.StartedAt);
        Assert.Equal(60, rec.Seconds, 1);
        Assert.Equal(41, rec.RunId);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(_paths.DiscoveryTicketDir(id), rec.File)));
        var o = await check.DiscoveryObservations.SingleAsync(x => x.Id == during);
        Assert.Equal(rec.Id, o.RecordingId);
        Assert.Equal(12.5, o.VideoAt!.Value, 2);
        Assert.Null((await check.DiscoveryObservations.SingleAsync(x => x.Id == after)).VideoAt);
        Assert.Equal(3, (await check.DiscoveryChapters.SingleAsync(c => c.TicketId == id)).VideoAt!.Value, 2);
    }

    [Fact]
    public async Task A_recording_still_being_written_is_left_for_the_next_look()
    {
        var id = await DiscoveryIn(TicketPhase.Scouting);
        var start = DateTime.UtcNow.AddSeconds(-30);
        RunVideo(id, start, 29);
        await using var db = new DashboardDbContext(_options);
        Assert.Equal(0, await DiscoveryMedia.AdoptAsync(db, _paths, id, ffmpeg: null, DateTime.UtcNow));
    }

    // ---- a real clip ------------------------------------------------------------------------

    private static string? FfmpegHere() => Ffmpeg.Find(null);

    /// <summary>A short test video, VP8 in WebM as the browser records.</summary>
    private string MakeVideo(string ffmpeg, double seconds)
    {
        var file = Path.Combine(_root, $"src-{Guid.NewGuid():N}.webm");
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=10:duration={seconds}",
                     "-c:v", "libvpx", "-b:v", "200k", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return file;
    }

    [Fact]
    public async Task A_proposal_gets_a_clip_cut_around_what_it_cites()
    {
        if (FfmpegHere() is not { } ffmpeg) return; // the drawer's "not set up" path is covered below
        var id = await DiscoveryIn(TicketPhase.NeedsVerdict);
        var start = DateTime.UtcNow.AddMinutes(-5);
        RunVideo(id, start, 40, MakeVideo(ffmpeg, 40));
        var o1 = await Observed(id, start.AddSeconds(20));
        int pid;
        await using (var db = new DashboardDbContext(_options))
        {
            db.DiscoveryChapters.Add(new DiscoveryChapter { TicketId = id, Title = "Job 2", Job = 1, At = start.AddSeconds(12) });
            var p = new DiscoveryProposal { TicketId = id, Title = "A month view", Body = "b", ObservationIds = new() { o1 } };
            db.DiscoveryProposals.Add(p);
            await db.SaveChangesAsync();
            pid = p.Id;
            await DiscoveryMedia.AdoptAsync(db, _paths, id, ffmpeg, DateTime.UtcNow);
        }

        await using (var db = new DashboardDbContext(_options))
        {
            var rec = await db.DiscoveryRecordings.SingleAsync(r => r.TicketId == id);
            Assert.Equal(40, rec.Seconds, 0);
            var (path, state, error) = await DiscoveryMedia.EnsureClipAsync(db, _paths, pid, ffmpeg);
            Assert.True(path is not null, error);
            Assert.Equal(DiscoveryMedia.ClipState.Ready, state);
            // From the chapter at 12s to four seconds after the moment at 20s.
            Assert.Equal(12, (await Ffmpeg.SecondsAsync(ffmpeg, path!))!.Value, 0);
            Assert.Equal($"p{pid}.mp4", (await db.DiscoveryProposals.SingleAsync(p => p.Id == pid)).ClipFile);
        }
    }

    [Fact]
    public async Task Without_ffmpeg_a_recorded_proposal_says_so_rather_than_failing()
    {
        var id = await DiscoveryIn(TicketPhase.NeedsVerdict);
        var start = new DateTime(2026, 11, 15, 5, 0, 0, DateTimeKind.Utc);
        RunVideo(id, start, 30);
        var o = await Observed(id, start.AddSeconds(10));
        await using var db = new DashboardDbContext(_options);
        await DiscoveryMedia.AdoptAsync(db, _paths, id, null, start.AddHours(1));
        var p = new DiscoveryProposal { TicketId = id, Title = "x", Body = "b", ObservationIds = new() { o } };
        var obs = await db.DiscoveryObservations.Where(x => x.TicketId == id).ToListAsync();
        var recs = await db.DiscoveryRecordings.Where(x => x.TicketId == id).ToListAsync();
        var dir = _paths.DiscoveryTicketDir(id);
        Assert.Equal(DiscoveryMedia.ClipState.NoFfmpeg, DiscoveryMedia.StateOf(p, obs, recs, dir, ffmpeg: false));
        Assert.Equal(DiscoveryMedia.ClipState.Cuttable, DiscoveryMedia.StateOf(p, obs, recs, dir, ffmpeg: true));
        Assert.Equal(DiscoveryMedia.ClipState.None, DiscoveryMedia.StateOf(new DiscoveryProposal { ObservationIds = new() { 999 } }, obs, recs, dir, true));
    }

    // ---- what Complete removes --------------------------------------------------------------

    [Fact]
    public async Task Complete_drops_a_rejected_clip_and_keeps_one_still_and_the_approved_evidence()
    {
        var (id, first) = await Sifting();
        var dir = _paths.DiscoveryTicketDir(id);
        var a = await Observed(id, DateTime.UtcNow, shot: "oa.png");
        var b = await Observed(id, DateTime.UtcNow, shot: "ob.png");
        var c = await Observed(id, DateTime.UtcNow, shot: "oc.png");
        var unused = await Observed(id, DateTime.UtcNow, shot: "ou.png");
        var (_, _, d) = New();
        var keep = await d.ProposeAsync(id, new("Ticket", "Keep", null, "b", null, null, null, null, new[] { a }));
        var drop = await d.ProposeAsync(id, new("Ticket", "Drop", null, "b", null, null, null, null, new[] { b, c }));
        await SetPhase(id, TicketPhase.NeedsVerdict);
        File.WriteAllBytes(Path.Combine(dir, "p-keep.mp4"), new byte[10]);
        File.WriteAllBytes(Path.Combine(dir, "p-drop.mp4"), new byte[10]);
        Directory.CreateDirectory(Path.Combine(dir, DiscoveryMedia.RunsFolder, "x"));
        await using (var db = new DashboardDbContext(_options))
        {
            await db.DiscoveryProposals.Where(p => p.Id == keep.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ClipFile, "p-keep.mp4"));
            await db.DiscoveryProposals.Where(p => p.Id == drop.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ClipFile, "p-drop.mp4"));
            db.DiscoveryRecordings.Add(new DiscoveryRecording { TicketId = id, File = "rec1.webm", Source = "runs/x/v.webm" });
            await db.SaveChangesAsync();
        }

        var (_, _, v) = New();
        await v.DecideAsync(keep.Id, new(Status: "Approved"));
        await v.DecideAsync(drop.Id, new(Status: "Rejected", RejectReason: "Generic"));
        await v.CompleteAsync(id);

        await using var check = new DashboardDbContext(_options);
        var props = await check.DiscoveryProposals.Where(p => p.TicketId == id).ToDictionaryAsync(p => p.Id);
        Assert.Null(props[keep.Id].ClipPrunedAt);
        Assert.True(File.Exists(Path.Combine(dir, "p-keep.mp4")));
        Assert.NotNull(props[drop.Id].ClipPrunedAt);
        Assert.Null(props[drop.Id].ClipFile);
        Assert.False(File.Exists(Path.Combine(dir, "p-drop.mp4")));
        var obs = await check.DiscoveryObservations.Where(o => o.TicketId == id).ToDictionaryAsync(o => o.Id);
        Assert.Null(obs[a].ShotPrunedAt);
        Assert.Null(obs[b].ShotPrunedAt);          // the rejected proposal's still
        Assert.NotNull(obs[c].ShotPrunedAt);       // the rest of its evidence
        Assert.NotNull(obs[unused].ShotPrunedAt);  // no proposal used it
        Assert.True(File.Exists(Path.Combine(dir, "ob.png")));
        Assert.False(File.Exists(Path.Combine(dir, "oc.png")));
        Assert.NotNull((await check.DiscoveryRecordings.SingleAsync(r => r.TicketId == id)).VerdictAt);
        Assert.False(Directory.Exists(Path.Combine(dir, DiscoveryMedia.RunsFolder)));
        Assert.True(File.Exists(Path.Combine(_paths.GlobalLogsDir, CaptureRetention.LedgerFile)));
        _ = first;
    }

    [Fact]
    public async Task A_pinned_rejected_proposal_keeps_its_clip_at_complete()
    {
        var (id, obs) = await Sifting();
        var (_, _, d) = New();
        var p = await d.ProposeAsync(id, new("Ticket", "Pinned", null, "b", null, null, null, null, new[] { obs }));
        await SetPhase(id, TicketPhase.NeedsVerdict);
        var (_, _, v) = New();
        await v.PinAsync(p.Id, true);
        await v.DecideAsync(p.Id, new(Status: "Rejected", RejectReason: "NotNow"));
        await v.CompleteAsync(id);
        await using var check = new DashboardDbContext(_options);
        Assert.Null((await check.DiscoveryProposals.SingleAsync(x => x.Id == p.Id)).ClipPrunedAt);
    }

    [Fact]
    public async Task A_dropped_line_cannot_be_pinned()
    {
        var (id, _) = await Sifting();
        var (_, _, d) = New();
        var dropped = await d.DropIdeaAsync(id, "Add search", "generic", null);
        await Assert.ThrowsAsync<DiscoveryService.Refused>(() => d.PinAsync(dropped.Id, true));
    }
}
