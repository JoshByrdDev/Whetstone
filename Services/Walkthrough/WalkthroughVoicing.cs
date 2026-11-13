using System.Collections.Concurrent;
using Whetstone.Models;
using Whetstone.Persistence;
using Microsoft.EntityFrameworkCore;
using static Whetstone.Services.Walkthrough.WalkthroughDeck;

namespace Whetstone.Services.Walkthrough;

/// <summary>
/// Voices a walkthrough that is already written: the Classroom's <b>Generate voice-over</b>, for a
/// deck written on a project that voices on demand (<see cref="ProjectConfig.Narration"/>), or one
/// whose clips are not on this machine. Also the one place a deck's beats are matched to their
/// clips, which writing a deck uses too, so the two can never time a beat differently.
/// <para>
/// Not the analysis queue: voicing needs no agent, and waiting behind a ten-minute analysis to
/// hear a deck that is already written would be a wait for nothing. Kokoro is CPU-bound, so one
/// deck is voiced at a time here, and the rest wait their turn.
/// </para>
/// </summary>
public class WalkthroughVoicing
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Narrator _narrator;
    private readonly DashboardPaths _paths;
    private readonly ILogger<WalkthroughVoicing> _log;
    private readonly CancellationToken _stopping;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly ConcurrentDictionary<int, Job> _jobs = new();

    public WalkthroughVoicing(IServiceScopeFactory scopes, Narrator narrator, DashboardPaths paths,
        IHostApplicationLifetime life, ILogger<WalkthroughVoicing> log)
    {
        _scopes = scopes;
        _narrator = narrator;
        _paths = paths;
        _log = log;
        _stopping = life.ApplicationStopping;
    }

    /// <summary>Where a deck's voicing stands, as the card shows it. Null when nothing has been asked.</summary>
    /// <param name="Running">Waiting its turn or being voiced.</param>
    /// <param name="Activity">The narrator's last word on it ("Voiced 3 of 12").</param>
    /// <param name="Error">Why the last attempt left lines unvoiced.</param>
    public record State(bool Running, string? Activity, string? Error);

    private sealed class Job : IRunProgress
    {
        public volatile bool Running = true;
        public volatile string? Activity = "Waiting for the narrator";
        public volatile string? Error;
        public void Expect(double seconds, int steps) { }
        public void Step(string activity) => Activity = activity;
    }

    public State? StateOf(int walkthroughId) =>
        _jobs.TryGetValue(walkthroughId, out var j) ? new State(j.Running, j.Activity, j.Error) : null;

    public bool ClipExists(string clip) => File.Exists(_narrator.ClipPath(clip));

    /// <summary>Start voicing one walkthrough's missing lines. Returns at once; the card polls <see cref="StateOf"/>.</summary>
    public (bool ok, string message) Start(int walkthroughId)
    {
        if (_narrator.Unavailable() is { } why) return (false, $"Voice-over is not set up on this machine: {why}");
        var job = new Job();
        while (true)
        {
            if (_jobs.TryGetValue(walkthroughId, out var old))
            {
                if (old.Running) return (false, "That walkthrough is already being voiced.");
                if (!_jobs.TryUpdate(walkthroughId, job, old)) continue;
            }
            else if (!_jobs.TryAdd(walkthroughId, job)) continue;
            break;
        }
        _ = Task.Run(() => RunAsync(walkthroughId, job));
        return (true, "Voicing.");
    }

    private async Task RunAsync(int id, Job job)
    {
        try
        {
            await _one.WaitAsync(_stopping);
            try { job.Error = await VoiceRowAsync(id, job, _stopping); }
            finally { _one.Release(); }
        }
        catch (OperationCanceledException) { job.Error = "Stopped before it finished."; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Voicing walkthrough {Id} failed", id);
            job.Error = ex.Message;
        }
        finally { job.Running = false; }
    }

    /// <returns>Why lines were left unvoiced, or null when every line has its clip.</returns>
    private async Task<string?> VoiceRowAsync(int id, IRunProgress progress, CancellationToken ct)
    {
        string before, projectKey;
        Deck deck;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            var row = await db.AnalysisWalkthroughs.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (row is null) return "the walkthrough no longer exists";
            if (Deserialize(row.DeckJson) is not { } d) return "there is no written walkthrough to voice";
            var key = row.AnalysisId is int a
                ? await db.TicketAnalyses.AsNoTracking().Where(x => x.Id == a)
                    .Join(db.Tickets, x => x.TicketId, t => t.Id, (_, t) => t.ProjectKey).FirstOrDefaultAsync(ct)
                : await db.UnitReports.AsNoTracking().Where(r => r.Id == row.UnitReportId).Select(r => r.ProjectKey).FirstOrDefaultAsync(ct);
            if (key is null) return "what the walkthrough narrates no longer exists";
            (before, projectKey, deck) = (row.DeckJson, key, d);
        }

        var voice = string.IsNullOrEmpty(deck.Voice) ? Narrator.DefaultVoice : deck.Voice;
        var (scenes, warning) = await VoiceScenesAsync(_narrator, _paths, projectKey, voice, deck.Scenes, generate: true, progress, ct);

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            var row = await db.AnalysisWalkthroughs.FirstOrDefaultAsync(w => w.Id == id, ct);
            if (row is null) return "the walkthrough no longer exists";
            // A rewrite that landed while this was voicing is the newer deck; its lines were voiced
            // here, by content, so the next press finds them on disk and only times its beats.
            if (row.DeckJson != before) return "the walkthrough was rewritten while it was being voiced - press Generate again";
            row.DeckJson = Serialize(new Deck(voice, scenes));
            row.Voice = voice;
            row.AudioSeconds = AudioSeconds(scenes);
            if (row.Status == WalkthroughStatus.Ready) row.Error = warning;
            await db.SaveChangesAsync(ct);
        }
        return warning;
    }

    public static double AudioSeconds(IEnumerable<Scene> scenes) =>
        Math.Round(scenes.SelectMany(s => s.Beats).Sum(b => b.Dur), 1);

    /// <summary>
    /// Match a deck's beats to their clips. With <paramref name="generate"/>, every line not on disk is
    /// voiced first; without it nothing runs and only lines already voiced - by content, anywhere -
    /// get a clip. A beat without one is timed at a reading pace and plays as a caption.
    /// </summary>
    /// <returns>The scenes, and why some lines are unvoiced when they were meant to be (null on demand).</returns>
    public static async Task<(IReadOnlyList<Scene> Scenes, string? Warning)> VoiceScenesAsync(Narrator narrator,
        DashboardPaths paths, string projectKey, string voice, IReadOnlyList<Scene> scenes, bool generate,
        IRunProgress progress, CancellationToken ct)
    {
        var say = Pronunciation.ForProject(paths, projectKey);
        var lines = scenes.SelectMany(s => s.Beats)
            .Select(b => new Narrator.Line(Pronunciation.Sentences(b.Say).Select(say.Speak).ToList())).ToList();
        var voiced = generate ? await narrator.VoiceAsync(lines, voice, progress, ct) : narrator.Existing(lines, voice);

        var i = 0;
        var result = scenes.Select(s => s with
        {
            Beats = s.Beats.Select(b =>
            {
                var v = voiced[i];
                var spoken = string.Join(" ", lines[i++].Sentences);
                // A line that is not voiced still gets a slot, timed at a reading pace, so the deck
                // plays through with a caption where the voice would have been.
                var dur = v.Clip is not null ? v.Seconds : ReadingSeconds(b.Say);
                return b with { Spoken = spoken, Clip = v.Clip, Dur = Math.Round(dur, 3) };
            }).ToList(),
        }).ToList();

        var unvoiced = voiced.Where(v => v.Error is not null).ToList();
        var warning = !generate || unvoiced.Count == 0 ? null
            : $"{unvoiced.Count} of {voiced.Count} lines could not be voiced and play as captions only: {unvoiced[0].Error}";
        return (result, warning);
    }

    public static double ReadingSeconds(string say) =>
        Math.Max(2.0, say.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.6);
}
