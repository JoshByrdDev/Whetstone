using System.Diagnostics;
using System.Text.Json;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services.Tracker;
using Microsoft.EntityFrameworkCore;
using static Whetstone.Services.Walkthrough.WalkthroughDeck;

namespace Whetstone.Services.Walkthrough;

/// <summary>
/// Writes an analysis's walkthrough: gathers what it may point at, has Claude script it, checks every
/// pointer (with one repair round), voices it, and stores the deck on its own row.
/// <para>
/// Runs inside the analysis queue's single slot - straight after a new analysis, or as a
/// walkthrough-only item for a retry. It <b>never fails the analysis</b>: whatever goes wrong here
/// is written to the walkthrough's row, and the analysis it describes is already saved.
/// </para>
/// </summary>
public class WalkthroughService
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(12);
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

    private readonly IServiceScopeFactory _scopes;
    private readonly ProjectRegistry _registry;
    private readonly LinkedIssueResolver _resolver;
    private readonly IssueTrackerFactory _trackers;
    private readonly IssueAttachmentStore _attachments;
    private readonly QaShotStore _shots;
    private readonly TicketGitService _git;
    private readonly AgentProviderFactory _providers;
    private readonly Narrator _narrator;
    private readonly DashboardPaths _paths;
    private readonly ILogger<WalkthroughService> _log;

    public WalkthroughService(IServiceScopeFactory scopes, ProjectRegistry registry, LinkedIssueResolver resolver,
        IssueTrackerFactory trackers, IssueAttachmentStore attachments, QaShotStore shots, TicketGitService git,
        AgentProviderFactory providers, Narrator narrator, DashboardPaths paths, ILogger<WalkthroughService> log)
    {
        _scopes = scopes;
        _registry = registry;
        _resolver = resolver;
        _trackers = trackers;
        _attachments = attachments;
        _shots = shots;
        _git = git;
        _providers = providers;
        _narrator = narrator;
        _paths = paths;
        _log = log;
    }

    public async Task GenerateAsync(int analysisId, IRunProgress progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await SetStatusAsync(analysisId, WalkthroughStatus.Pending, null);
            var error = await WriteAsync(analysisId, progress, sw, ct);
            if (error is not null) await SetStatusAsync(analysisId, WalkthroughStatus.Failed, error);
        }
        catch (OperationCanceledException)
        {
            // The analysis it describes is saved; only this is lost. Recorded rather than rethrown so
            // the queue does not mistake a stopped walkthrough for a stopped analysis.
            await SetStatusAsync(analysisId, WalkthroughStatus.Failed, "Stopped before it finished.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The walkthrough for analysis {AnalysisId} failed", analysisId);
            await SetStatusAsync(analysisId, WalkthroughStatus.Failed, ex.Message);
        }
    }

    /// <returns>Why there is no walkthrough, or null once one is saved.</returns>
    private async Task<string?> WriteAsync(int analysisId, IRunProgress progress, Stopwatch sw, CancellationToken ct)
    {
        TicketAnalysis a;
        Ticket t;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            var found = await db.TicketAnalyses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == analysisId, ct);
            if (found is null) return "the analysis no longer exists";
            a = found;
            var ticket = await db.Tickets.AsNoTracking().Include(x => x.QaShots).FirstOrDefaultAsync(x => x.Id == a.TicketId, ct);
            if (ticket is null) return "the ticket no longer exists";
            t = ticket;
        }

        var doc = AnalysisDocument.Deserialize(a.BlocksJson);
        if (doc is null) return "the analysis could not be read back";
        var cfg = _registry.GetProjectConfig(t.ProjectKey);
        var commit = a.HeadSha ?? a.Branch ?? t.Branch;
        if (string.IsNullOrWhiteSpace(commit)) return "the analysis is not pinned to a commit or branch, so there is no diff to show";

        // ---- the material ----
        progress.Step("Gathering the walkthrough's material");
        await CacheAttachmentsAsync(t);
        var resolved = await _resolver.ResolveAsync(t);
        var description = resolved.Description ?? t.Description ?? "";

        var shots = t.QaShots.OrderBy(s => s.Order)
            .Select(s => (s, path: _shots.DiskPath(t.Id, s.ImageFile)))
            .Where(x => x.path is not null && File.Exists(x.path))
            .Select(x => (x.s, x.path, size: ImageInfo.Size(x.path!)))
            .Where(x => x.size is not null)
            .Select(x => (shot: x.s, path: x.path!, w: x.size!.Value.Width, h: x.size.Value.Height))
            .ToList();

        var ticketDir = _paths.IssueAttachmentTicketDir(t.Id);
        var attachments = _attachments.ListCached(t.Id)
            .Where(f => ImageTypes.Contains(Path.GetExtension(f.FileName)))
            .Select(f => (f, size: ImageInfo.Size(f.LocalPath)))
            .Where(x => x.size is not null)
            .Select(x => (name: x.f.SourceIssue is { } k ? $"{k}/{x.f.FileName}" : x.f.FileName,
                          rel: Path.GetRelativePath(ticketDir, x.f.LocalPath).Replace('\\', '/'),
                          x.f.LocalPath, x.f.SourceIssue, w: x.size!.Value.Width, h: x.size.Value.Height))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();

        var (changed, changedError) = await _git.ChangedFilesAsync(t.ProjectKey, commit, ct);
        if (changedError is not null) return changedError;

        var prompt = WalkthroughPrompt.Build(t, a, doc, resolved.Title, description,
            shots.Select(s => new WalkthroughPrompt.ShotMaterial(s.shot.Order, s.shot.Caption, s.path, s.w, s.h, s.shot.Focus())).ToList(),
            attachments.Select(x => new WalkthroughPrompt.AttachmentMaterial(x.name, x.LocalPath, x.w, x.h, x.SourceIssue)).ToList(),
            changed.Select(c => new WalkthroughPrompt.ChangedFile(c.Path, c.Added, c.Removed)).ToList(),
            commit);

        // Code is cut from the file at the analysed commit and marked from the branch's patch - see
        // DiffSnippet. Cached per file: a deck often shows two ranges of one file.
        var patches = new Dictionary<string, (string? Text, string? Patch, string? Error)>(StringComparer.Ordinal);
        async Task<(DiffSnippet.Result?, string?)> Snippet(string path, int from, int to)
        {
            if (!patches.TryGetValue(path, out var p))
            {
                var diff = await _git.FileDiffAsync(t.ProjectKey, commit, path, ct);
                var (text, textError) = await _git.FileAtAsync(t.ProjectKey, commit, path, ct);
                p = (text, diff.Error is null ? diff.Patch : null, diff.Error ?? textError);
                if (diff.Truncated) p = (text, null, "its diff is too large to show from");
                patches[path] = p;
            }
            if (p.Error is not null) return (null, p.Error);
            return DiffSnippet.Build(p.Text!, p.Patch ?? "", from, to);
        }

        var ctx = new WalkthroughValidator.Context(doc, description,
            shots.Select(s => new WalkthroughValidator.Shot(s.shot.Order, s.shot.Caption, QaShotDto.From(t.Id, s.shot).ImageUrl, s.w, s.h, s.shot.Focus())).ToList(),
            attachments.Select(x => new WalkthroughValidator.Attachment(x.name, AttachmentUrl(t.Id, x.rel), x.w, x.h)).ToList(),
            Snippet);

        // ---- the script, checked, with one repair round ----
        var provider = _providers.ResolveForAnalysis();
        var readDir = AnalysisRunner.ReadDir(t, cfg);
        var scratch = Path.Combine(_paths.RunDir, "walkthrough");
        var tally = new Tally();

        progress.Step("Writing the walkthrough script");
        var (draft, first, runError) = await ScriptAsync(provider, readDir, scratch, prompt, progress, tally, ct);
        if (draft is null) return runError;
        var result = await WalkthroughValidator.ValidateAsync(first!, ctx);

        if (result.Problems.Count > 0)
        {
            progress.Step($"Repairing {result.Problems.Count} reference{(result.Problems.Count == 1 ? "" : "s")} in the script");
            var (_, second, repairError) = await ScriptAsync(provider, readDir, scratch,
                WalkthroughPrompt.Repair(prompt, draft.Value, result.Problems), progress, tally, ct);
            if (second is not null)
            {
                var repaired = await WalkthroughValidator.ValidateAsync(second, ctx);
                if (repaired.Scenes.Count > 0) result = repaired;
            }
            else _log.LogWarning("The walkthrough repair round for analysis {AnalysisId} failed: {Error}", analysisId, repairError);
        }
        if (result.Scenes.Count == 0)
            return "the script had no usable scenes: " + string.Join(" ", result.Problems.Take(3));
        foreach (var p in result.Problems) _log.LogInformation("Walkthrough {AnalysisId} dropped: {Problem}", analysisId, p);

        // ---- the voice ----
        var voice = Narrator.DefaultVoice;
        var say = Pronunciation.ForProject(_paths, t.ProjectKey);
        var beats = result.Scenes.SelectMany(s => s.Beats).ToList();
        var lines = beats.Select(b => new Narrator.Line(Pronunciation.Sentences(b.Say).Select(say.Speak).ToList())).ToList();
        var voiced = await _narrator.VoiceAsync(lines, voice, progress, ct);

        var i = 0;
        var scenes = result.Scenes.Select(s => s with
        {
            Beats = s.Beats.Select(b =>
            {
                var v = voiced[i];
                var spoken = string.Join(" ", lines[i++].Sentences);
                // A line that could not be voiced still gets a slot, timed at a reading pace, so the
                // deck plays through with a caption where the voice would have been.
                var dur = v.Clip is not null ? v.Seconds : Math.Max(2.0, b.Say.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.6);
                return b with { Spoken = spoken, Clip = v.Clip, Dur = Math.Round(dur, 3) };
            }).ToList(),
        }).ToList();

        var unvoiced = voiced.Where(v => v.Error is not null).ToList();
        var warning = unvoiced.Count == 0 ? null
            : $"{unvoiced.Count} of {voiced.Count} lines could not be voiced and play as captions only: {unvoiced[0].Error}";

        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            var row = await db.AnalysisWalkthroughs.FirstAsync(w => w.AnalysisId == analysisId, ct);
            row.Status = WalkthroughStatus.Ready;
            row.Error = warning;
            row.DeckJson = Serialize(new Deck(voice, scenes));
            row.Voice = voice;
            row.Dropped = result.Problems.Count;
            row.AudioSeconds = Math.Round(scenes.SelectMany(s => s.Beats).Sum(b => b.Dur), 1);
            row.InputTokens = tally.Input;
            row.OutputTokens = tally.Output;
            row.CacheReadTokens = tally.CacheRead;
            row.CacheCreationTokens = tally.CacheCreation;
            row.Model = tally.Model;
            row.DurationMs = (int)sw.ElapsedMilliseconds;
            row.GeneratedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return null;
    }

    private sealed class Tally
    {
        public long Input, Output, CacheRead, CacheCreation;
        public string? Model;
    }

    /// <returns>The raw structured output (for a repair prompt), the parsed script, or why neither.</returns>
    private async Task<(JsonElement? Raw, RawScript? Script, string? Error)> ScriptAsync(AgentProvider provider,
        string readDir, string scratch, string prompt, IRunProgress progress, Tally tally, CancellationToken ct)
    {
        var run = await AgentJsonRunner.RunAsync(provider, readDir, scratch, prompt, JsonSchema, progress,
            ScriptTimeout, "walkthrough script", _log, ct);
        if (run.Outcome is not { } o) return (null, null, run.Error);
        tally.Input += o.InputTokens;
        tally.Output += o.OutputTokens;
        tally.CacheRead += o.CacheReadTokens;
        tally.CacheCreation += o.CacheCreationTokens;
        tally.Model ??= o.Model;
        if (o.Error is not null) return (null, null, o.Error);
        if (o.Structured is not { } json) return (null, null, "the run finished without producing a script");
        try
        {
            var script = JsonSerializer.Deserialize<RawScript>(json.GetRawText(), Wire);
            return script is null ? (null, null, "the script was empty") : (json.Clone(), script, null);
        }
        catch (JsonException ex) { return (null, null, $"the script could not be read: {ex.Message}"); }
    }

    /// <summary>
    /// Pull the linked issue's own attachments down if nothing has yet. A pipeline phase caches them
    /// only when it asks for the issue context, and a ticket that never did would otherwise walk
    /// through its requirement without the mockup it came with. A tracker that will not answer costs
    /// the deck its pictures, never the deck.
    /// </summary>
    private async Task CacheAttachmentsAsync(Ticket t)
    {
        if (string.IsNullOrEmpty(t.IssueKey)) return;
        try
        {
            var tracker = _trackers.For(t.ProjectKey);
            if (!tracker.IsConfigured) return;
            if (await tracker.GetIssueAsync(t.IssueKey) is { } issue)
                await _attachments.CacheAsync(t.Id, issue.Attachments, tracker);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not cache the attachments of {IssueKey} for a walkthrough", t.IssueKey);
        }
    }

    public static string AttachmentUrl(int ticketId, string relativePath) =>
        $"/api/tickets/{ticketId}/issue-attachments/{string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString))}";

    private async Task SetStatusAsync(int analysisId, WalkthroughStatus status, string? error)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var row = await db.AnalysisWalkthroughs.FirstOrDefaultAsync(w => w.AnalysisId == analysisId);
        if (row is null)
        {
            if (!await db.TicketAnalyses.AnyAsync(x => x.Id == analysisId)) return;
            row = new AnalysisWalkthrough { AnalysisId = analysisId };
            db.AnalysisWalkthroughs.Add(row);
        }
        // Status is the latest attempt's. The last good deck is not cleared by a retry, so a rewrite
        // in progress - or one that failed - still leaves the previous walkthrough to play.
        row.Status = status;
        row.Error = error;
        if (status == WalkthroughStatus.Pending) row.GeneratedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
