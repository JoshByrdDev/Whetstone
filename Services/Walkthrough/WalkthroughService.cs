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

    /// <summary>What a deck narrates, and so which row it is stored on. Exactly one is set.</summary>
    private readonly record struct Subject(int? AnalysisId, int? ReportId)
    {
        public override string ToString() => AnalysisId is int a ? $"analysis {a}" : $"unit report {ReportId}";
    }

    /// <summary>What a script is written from, whichever kind of document it narrates.</summary>
    private record Material(string ProjectKey, string ReadDir, string Prompt, WalkthroughValidator.Context Ctx);

    public Task GenerateAsync(int analysisId, IRunProgress progress, CancellationToken ct) =>
        GenerateAsync(new Subject(analysisId, null), progress, ct);

    /// <summary>
    /// The walkthrough of a unit report: the same script, checks and voice, over the unit's material -
    /// its spec to quote, its pooled screenshots, and the code across its range on the base.
    /// </summary>
    public Task GenerateForUnitAsync(int reportId, IRunProgress progress, CancellationToken ct) =>
        GenerateAsync(new Subject(null, reportId), progress, ct);

    private async Task GenerateAsync(Subject subject, IRunProgress progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await SetStatusAsync(subject, WalkthroughStatus.Pending, null);
            var error = await WriteAsync(subject, progress, sw, ct);
            if (error is not null) await SetStatusAsync(subject, WalkthroughStatus.Failed, error);
        }
        catch (OperationCanceledException)
        {
            // The document it describes is saved; only this is lost. Recorded rather than rethrown so
            // the queue does not mistake a stopped walkthrough for a stopped analysis.
            await SetStatusAsync(subject, WalkthroughStatus.Failed, "Stopped before it finished.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The walkthrough for {Subject} failed", subject);
            await SetStatusAsync(subject, WalkthroughStatus.Failed, ex.Message);
        }
    }

    /// <returns>Why there is no walkthrough, or null once one is saved.</returns>
    private async Task<string?> WriteAsync(Subject subject, IRunProgress progress, Stopwatch sw, CancellationToken ct)
    {
        progress.Step("Gathering the walkthrough's material");
        var (m, materialError) = subject.AnalysisId is int analysisId
            ? await TicketMaterialAsync(analysisId, ct)
            : await UnitMaterialAsync(subject.ReportId!.Value, ct);
        if (m is null) return materialError;
        var (prompt, ctx) = (m.Prompt, m.Ctx);

        // ---- the script, checked, with one repair round ----
        var provider = _providers.ResolveForAnalysis();
        var readDir = m.ReadDir;
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
            else _log.LogWarning("The walkthrough repair round for {Subject} failed: {Error}", subject, repairError);
        }
        if (result.Scenes.Count == 0)
            return "the script had no usable scenes: " + string.Join(" ", result.Problems.Take(3));
        foreach (var p in result.Problems) _log.LogInformation("Walkthrough of {Subject} dropped: {Problem}", subject, p);

        // ---- the voice ----
        var voice = Narrator.DefaultVoice;
        var say = Pronunciation.ForProject(_paths, m.ProjectKey);
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
            var row = (await RowAsync(db, subject, ct))!;
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

    /// <summary>An analysis's material: its ticket's description, QA shots, attachments, and branch.</summary>
    private async Task<(Material?, string?)> TicketMaterialAsync(int analysisId, CancellationToken ct)
    {
        TicketAnalysis a;
        Ticket t;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            var found = await db.TicketAnalyses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == analysisId, ct);
            if (found is null) return (null, "the analysis no longer exists");
            a = found;
            var ticket = await db.Tickets.AsNoTracking().Include(x => x.QaShots).FirstOrDefaultAsync(x => x.Id == a.TicketId, ct);
            if (ticket is null) return (null, "the ticket no longer exists");
            t = ticket;
        }

        var doc = AnalysisDocument.Deserialize(a.BlocksJson);
        if (doc is null) return (null, "the analysis could not be read back");
        var cfg = _registry.GetProjectConfig(t.ProjectKey);
        var commit = a.HeadSha ?? a.Branch ?? t.Branch;
        if (string.IsNullOrWhiteSpace(commit)) return (null, "the analysis is not pinned to a commit or branch, so there is no diff to show");

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
        if (changedError is not null) return (null, changedError);

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

        return (new Material(t.ProjectKey, AnalysisRunner.ReadDir(t, cfg), prompt, ctx), null);
    }

    /// <summary>
    /// A unit report's material. The "ask" it quotes is the unit's spec, the shots are the report's own
    /// copies, and the code is the range the report read - from the base before the unit to the base
    /// it was written at. Read from the hub: the range's end is a commit on the base, not a branch.
    /// </summary>
    private async Task<(Material?, string?)> UnitMaterialAsync(int reportId, CancellationToken ct)
    {
        UnitReport? r;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            r = await db.UnitReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reportId, ct);
        }
        if (r is null) return (null, "the report no longer exists");
        var doc = AnalysisDocument.Deserialize(r.BlocksJson);
        if (doc is null) return (null, "the report could not be read back");

        var cfg = _registry.GetProjectConfig(r.ProjectKey);
        var plan = cfg.Steward.PlanByKey(r.Plan);
        var unit = plan is { Configured: true } ? (await _git.PlanOfAsync(r.ProjectKey, plan, ct)).FirstOrDefault(u => u.Number == r.Unit) : null;
        var spec = unit?.Text ?? "";

        var dir = _paths.UnitReportShotsDir(r.Id);
        var shots = UnitReports.UnitReportRunner.ShotsOf(r)
            .Select(s => (s, path: Path.Combine(dir, s.File)))
            .Where(x => File.Exists(x.path))
            .Select(x => (x.s, x.path, size: ImageInfo.Size(x.path)))
            .Where(x => x.size is not null)
            .Select(x => (shot: x.s, x.path, w: x.size!.Value.Width, h: x.size.Value.Height))
            .ToList();

        var (changed, changedError) = await _git.ChangedFilesAsync(r.ProjectKey, r.ToSha, ct, fromRef: r.FromSha);
        if (changedError is not null) return (null, changedError);

        var title = unit is null ? $"Unit {r.Unit}" : $"Unit {r.Unit}: {TicketGitService.UnitTitle(unit)}";
        var prompt = WalkthroughPrompt.BuildForUnit(title, plan?.DisplayName ?? r.Plan, doc, spec,
            shots.Select(s => new WalkthroughPrompt.ShotMaterial(s.shot.N, s.shot.Caption, s.path, s.w, s.h, s.shot.Focus)).ToList(),
            changed.Select(c => new WalkthroughPrompt.ChangedFile(c.Path, c.Added, c.Removed)).ToList(),
            r.FromSha, r.ToSha);

        var patches = new Dictionary<string, (string? Text, string? Patch, string? Error)>(StringComparer.Ordinal);
        async Task<(DiffSnippet.Result?, string?)> Snippet(string path, int from, int to)
        {
            if (!patches.TryGetValue(path, out var p))
            {
                var diff = await _git.FileDiffAsync(r.ProjectKey, r.ToSha, path, ct, fromRef: r.FromSha);
                var (text, textError) = await _git.FileAtAsync(r.ProjectKey, r.ToSha, path, ct);
                p = (text, diff.Error is null ? diff.Patch : null, diff.Error ?? textError);
                if (diff.Truncated) p = (text, null, "its diff is too large to show from");
                patches[path] = p;
            }
            if (p.Error is not null) return (null, p.Error);
            return DiffSnippet.Build(p.Text!, p.Patch ?? "", from, to);
        }

        var ctx = new WalkthroughValidator.Context(doc, spec,
            shots.Select(s => new WalkthroughValidator.Shot(s.shot.N, s.shot.Caption, UnitShotUrl(r.Id, s.shot.N), s.w, s.h, s.shot.Focus)).ToList(),
            Array.Empty<WalkthroughValidator.Attachment>(),
            Snippet);

        return (new Material(r.ProjectKey, cfg.ProjectPath, prompt, ctx), null);
    }

    public static string UnitShotUrl(int reportId, int n) => $"/api/unit-reports/{reportId}/shots/{n}";

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

    private static Task<AnalysisWalkthrough?> RowAsync(DashboardDbContext db, Subject subject, CancellationToken ct = default) =>
        subject.AnalysisId is int a
            ? db.AnalysisWalkthroughs.FirstOrDefaultAsync(w => w.AnalysisId == a, ct)
            : db.AnalysisWalkthroughs.FirstOrDefaultAsync(w => w.UnitReportId == subject.ReportId, ct);

    private async Task SetStatusAsync(Subject subject, WalkthroughStatus status, string? error)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var row = await RowAsync(db, subject);
        if (row is null)
        {
            var exists = subject.AnalysisId is int a
                ? await db.TicketAnalyses.AnyAsync(x => x.Id == a)
                : await db.UnitReports.AnyAsync(x => x.Id == subject.ReportId);
            if (!exists) return;
            row = new AnalysisWalkthrough { AnalysisId = subject.AnalysisId, UnitReportId = subject.ReportId };
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
