using System.Collections.Concurrent;
using Whetstone.Models;
using Whetstone.Services.Walkthrough;

namespace Whetstone.Services;

/// <summary>
/// A discovery recording's voice-over: what is said over the video, and when.
/// <para>
/// A run that narrated itself (<c>say</c>, <see cref="DiscoveryNarration"/>) is voiced in its own
/// words. One that did not - every run recorded before <c>say</c> existed, or one that forgot -
/// still gets a voice from what it did leave: each chapter's title and each observation's title,
/// at their places in the video. Which of the two is decided per recording, never mixed, so a run
/// that did narrate is not talked over by its own log.
/// </para>
/// <para>
/// Nothing about the voicing is stored. A line's clip is found by its content, the way the
/// Classroom's are (<see cref="Narrator.ClipId"/>), in the same folder and through the same route,
/// so a line voiced once is never voiced again and the board shows a line as voiced exactly when
/// its clip is on disk.
/// </para>
/// </summary>
public static class DiscoveryVoiceOver
{
    /// <summary>Where a line came from: the run's own voice, or its log read aloud in its place.</summary>
    public enum Source { Say, Chapter, Observation }

    /// <summary>One line, at its place in one recording. <paramref name="Key"/> is stable across reads, for the client's list.</summary>
    public sealed record Line(string Key, Source From, string Text, double At, string? Kind);

    /// <summary>The recording's lines, in the order they are heard.</summary>
    public static IReadOnlyList<Line> LinesOf(DiscoveryService.View v, int recordingId)
    {
        var said = v.Narration.Where(n => n.RecordingId == recordingId && n.VideoAt is not null)
            .Select(n => new Line($"s{n.Id}", Source.Say, n.Text, n.VideoAt!.Value, null)).ToList();
        if (said.Count > 0) return said.OrderBy(l => l.At).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();

        // The log, read aloud: a chapter before what was found in it when both fall on one second.
        var chapters = v.Chapters.Where(c => c.RecordingId == recordingId && c.VideoAt is not null)
            .Select(c => new Line($"c{c.Id}", Source.Chapter, c.Title, c.VideoAt!.Value, null));
        var found = v.Observations.Where(o => o.RecordingId == recordingId && o.VideoAt is not null)
            .Select(o => new Line($"o{o.Id}", Source.Observation, o.Title, o.VideoAt!.Value, o.Kind.ToString()));
        return chapters.Concat(found).OrderBy(l => l.At).ThenBy(l => l.From).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>What the narrator is handed for a line: its sentences, in spoken form.</summary>
    public static Narrator.Line Spoken(Pronunciation say, Line l) =>
        new(Pronunciation.Sentences(l.Text).Select(say.Speak).ToList());
}

/// <summary>
/// Voices a discovery's recordings on demand - the drawer's <b>Generate voice-over</b> - the way
/// <see cref="WalkthroughVoicing"/> voices a Classroom deck: no agent, one discovery at a time, the
/// drawer polling <see cref="StateOf"/>. Every line of every live recording is voiced; a line
/// already on disk, by content, costs nothing.
/// </summary>
public class DiscoveryVoicing
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Narrator _narrator;
    private readonly DashboardPaths _paths;
    private readonly ILogger<DiscoveryVoicing> _log;
    private readonly CancellationToken _stopping;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly ConcurrentDictionary<int, Job> _jobs = new();

    public DiscoveryVoicing(IServiceScopeFactory scopes, Narrator narrator, DashboardPaths paths,
        IHostApplicationLifetime life, ILogger<DiscoveryVoicing> log)
    {
        _scopes = scopes;
        _narrator = narrator;
        _paths = paths;
        _log = log;
        _stopping = life.ApplicationStopping;
    }

    public sealed record State(bool Running, string? Activity, string? Error);

    private sealed class Job : IRunProgress
    {
        public volatile bool Running = true;
        public volatile string? Activity = "Waiting for the narrator";
        public volatile string? Error;
        public void Expect(double seconds, int steps) { }
        public void Step(string activity) => Activity = activity;
    }

    public State? StateOf(int ticketId) =>
        _jobs.TryGetValue(ticketId, out var j) ? new State(j.Running, j.Activity, j.Error) : null;

    /// <summary>
    /// Each line of a live recording with its clip, when it is voiced, and how long it plays: its
    /// clip's length, or a reading pace for a line not voiced yet, which the drawer shows as a caption.
    /// Reads the disk; runs nothing.
    /// </summary>
    public IReadOnlyList<(DiscoveryVoiceOver.Line Line, string? Clip, double Seconds)> Timed(DiscoveryService.View v, int recordingId, string projectKey)
    {
        var lines = DiscoveryVoiceOver.LinesOf(v, recordingId);
        if (lines.Count == 0) return Array.Empty<(DiscoveryVoiceOver.Line, string?, double)>();
        var say = Pronunciation.ForProject(_paths, projectKey);
        var voiced = _narrator.Existing(lines.Select(l => DiscoveryVoiceOver.Spoken(say, l)).ToList(), Narrator.DefaultVoice);
        return lines.Select((l, i) => (l, voiced[i].Clip,
            Math.Round(voiced[i].Clip is not null ? voiced[i].Seconds : WalkthroughVoicing.ReadingSeconds(l.Text), 3))).ToList();
    }

    /// <summary>
    /// Voice a discovery's lines without being asked, the first time anyone reads it with lines not
    /// voiced yet. A discovery's recording is watched to be followed, and a player whose voice waits
    /// on a button nobody saw is a silent player - so unlike a Classroom deck, it is not on demand.
    /// Runs nothing while a voicing is under way, or after one that failed (the drawer shows why,
    /// and Generate tries again), or on a machine without the narrator.
    /// </summary>
    /// <returns>Whether a voicing was started.</returns>
    public bool EnsureVoiced(int ticketId, bool anyUnvoiced)
    {
        if (!anyUnvoiced || _narrator.Unavailable() is not null) return false;
        if (_jobs.TryGetValue(ticketId, out var j) && (j.Running || j.Error is not null)) return false;
        return Start(ticketId).ok;
    }

    /// <summary>Start voicing one discovery's unvoiced lines. Returns at once.</summary>
    public (bool ok, string message) Start(int ticketId)
    {
        if (_narrator.Unavailable() is { } why) return (false, $"Voice-over is not set up on this machine: {why}");
        var job = new Job();
        while (true)
        {
            if (_jobs.TryGetValue(ticketId, out var old))
            {
                if (old.Running) return (false, "That discovery is already being voiced.");
                if (!_jobs.TryUpdate(ticketId, job, old)) continue;
            }
            else if (!_jobs.TryAdd(ticketId, job)) continue;
            break;
        }
        _ = Task.Run(() => RunAsync(ticketId, job));
        return (true, "Voicing.");
    }

    private async Task RunAsync(int ticketId, Job job)
    {
        try
        {
            await _one.WaitAsync(_stopping);
            try { job.Error = await VoiceAsync(ticketId, job, _stopping); }
            finally { _one.Release(); }
        }
        catch (OperationCanceledException) { job.Error = "Stopped before it finished."; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Voicing discovery {Id} failed", ticketId);
            job.Error = ex.Message;
        }
        finally { job.Running = false; }
    }

    private async Task<string?> VoiceAsync(int ticketId, IRunProgress progress, CancellationToken ct)
    {
        List<Narrator.Line> lines;
        using (var scope = _scopes.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<DiscoveryService>();
            var v = await svc.GetAsync(ticketId);
            if (v is null) return "the discovery no longer exists";
            var say = Pronunciation.ForProject(_paths, await svc.ProjectOfAsync(ticketId));
            lines = v.Recordings.Where(r => r.PrunedAt is null)
                .SelectMany(r => DiscoveryVoiceOver.LinesOf(v, r.Id))
                .Select(l => DiscoveryVoiceOver.Spoken(say, l)).ToList();
        }
        if (lines.Count == 0) return "there is nothing to voice: no recording of this discovery has a line placed in it";
        var voiced = await _narrator.VoiceAsync(lines, Narrator.DefaultVoice, progress, ct);
        var failed = voiced.Where(x => x.Error is not null).ToList();
        return failed.Count == 0 ? null
            : $"{failed.Count} of {voiced.Count} lines could not be voiced and play as captions only: {failed[0].Error}";
    }
}
