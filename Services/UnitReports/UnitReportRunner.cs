using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services.UnitReports;

/// <summary>Which unit of which plan of which project. A unit's number means nothing without its plan.</summary>
public record UnitSubject(string Project, string Plan, int Unit)
{
    public override string ToString() => $"{Project} {(Plan.Length > 0 ? Plan + " " : "")}unit {Unit}";
}

/// <summary>
/// What the queue is told about a unit whose turn has come.
/// </summary>
/// <param name="Verdict">Whether it may run, leaving the tickets' analyses aside.</param>
/// <param name="Analyse">Tickets whose analysis is missing or describes code that has moved, and
/// that can be studied now. The queue asks for each once; the report waits for them, and goes ahead
/// without any that came back with nothing.</param>
public record UnitGateVerdict(GateVerdict Verdict, IReadOnlyList<int> Analyse);

/// <summary>
/// Writes one unit report: gathers the unit's material, places its range on the base, copies its
/// screenshots, checks out the end of the range, runs the analysis provider over the prompt and
/// stores what comes back. The queue owns when, exactly as for a ticket's analysis.
/// </summary>
public class UnitReportRunner
{
    /// <summary>Longer than a ticket's: the material is several analyses, and the run reads across all of them.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(45);

    private readonly IServiceScopeFactory _scopes;
    private readonly ProjectRegistry _registry;
    private readonly TicketGitService _git;
    private readonly QaShotStore _shots;
    private readonly AgentProviderFactory _providers;
    private readonly ClaudeUsageService _usage;
    private readonly DashboardPaths _paths;
    private readonly ILogger<UnitReportRunner> _log;

    public UnitReportRunner(IServiceScopeFactory scopes, ProjectRegistry registry, TicketGitService git,
        QaShotStore shots, AgentProviderFactory providers, ClaudeUsageService usage, DashboardPaths paths,
        ILogger<UnitReportRunner> log)
    {
        _scopes = scopes;
        _registry = registry;
        _git = git;
        _shots = shots;
        _providers = providers;
        _usage = usage;
        _paths = paths;
        _log = log;
    }

    /// <summary>Everything the gate and the run both need, read once.</summary>
    private record Gathered(
        ProjectConfig Cfg, StewardPlan Plan, PlanUnit Unit, IReadOnlyList<PlanUnit> All,
        UnitMaterial.Result Material, UnitRange Range, IReadOnlyList<StewardOutcome> States,
        IReadOnlyDictionary<int, IReadOnlyList<string>> Bounces);

    private async Task<(Gathered? G, string? Error)> GatherAsync(UnitSubject s, CancellationToken ct)
    {
        var cfg = _registry.GetProjectConfig(s.Project);
        if (cfg.Steward.PlanByKey(s.Plan) is not { Configured: true } plan)
            return (null, $"{cfg.DisplayName} has no plan '{s.Plan}'");

        var all = await _git.PlanOfAsync(s.Project, plan, ct);
        if (all.FirstOrDefault(u => u.Number == s.Unit) is not { } unit)
            return (null, $"plan '{plan.DisplayName}' has no unit {s.Unit}");

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();

        var tickets = await db.Tickets.AsNoTracking().Include(t => t.QaShots)
            .Where(t => t.ProjectKey == s.Project && t.PlanUnit == s.Unit).ToListAsync(ct);
        tickets = tickets.Where(t => (t.PlanKey ?? "") == s.Plan).ToList();
        var ids = tickets.Select(t => t.Id).ToList();

        var analyses = await db.TicketAnalyses.AsNoTracking().Where(a => ids.Contains(a.TicketId)).ToListAsync(ct);
        var states = await db.StewardOutcomes.AsNoTracking()
            .Where(o => o.ProjectKey == s.Project && o.Plan == s.Plan).ToListAsync(ct);
        var entries = await db.StewardEntries.AsNoTracking()
            .Where(e => e.ProjectKey == s.Project && e.Kind == StewardEntryKind.Call && e.TicketId != null && ids.Contains(e.TicketId.Value))
            .ToListAsync(ct);
        var doneAt = (await db.PhaseTransitions.AsNoTracking()
                .Where(p => ids.Contains(p.TicketId) && p.To == TicketPhase.Done)
                .Select(p => new { p.TicketId, p.At }).ToListAsync(ct))
            .GroupBy(p => p.TicketId).ToDictionary(g => g.Key, g => g.Max(p => p.At));
        var codedAt = (await db.PhaseTransitions.AsNoTracking()
                .Where(p => ids.Contains(p.TicketId) && p.From == TicketPhase.Implementing)
                .Select(p => new { p.TicketId, p.At }).ToListAsync(ct))
            .GroupBy(p => p.TicketId).ToDictionary(g => g.Key, g => g.Max(p => p.At));

        // The base before this unit is the last release before it, when the owner said there was one.
        var released = await db.StewardUnits.AsNoTracking()
            .Where(u => u.ProjectKey == s.Project && u.Plan == s.Plan && u.Unit < s.Unit && u.ReleasedSha != null)
            .OrderByDescending(u => u.Unit).Select(u => u.ReleasedSha).FirstOrDefaultAsync(ct);

        var newestHead = analyses.GroupBy(a => a.TicketId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.GeneratedAt).First().HeadSha);
        var (range, rangeError) = await _git.UnitRangeAsync(s.Project,
            tickets.Select(t => new UnitTicketRef(t.Id, t.Branch, t.MergeSha, newestHead.GetValueOrDefault(t.Id))).ToList(),
            released, ct);
        if (range is null) return (null, rangeError);

        var material = UnitMaterial.Build(new UnitMaterial.Input(s.Plan, unit,
            TicketGitService.UnitGroups(plan, unit), tickets, states, entries, analyses, range.Tips, doneAt, codedAt));

        var bounces = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var id in ids)
        {
            var list = await TicketJourney.BouncesAsync(db, id);
            if (list.Count > 0)
                bounces[id] = list.Select(b => $"{b.From} -> {b.To}: {b.Reason ?? b.Why.FirstOrDefault()?.Body ?? "no reason recorded"}").ToList();
        }

        return (new Gathered(cfg, plan, unit, all, material, range, states, bounces), null);
    }

    /// <summary>
    /// Whether a unit may be written up now. Asked by the queue at the item's turn, never at the click.
    /// <para>
    /// A report asked for by a person runs over an unfinished unit and says what is unfinished: they
    /// asked, and an answer about part of a unit is still an answer. One asked for by the board itself
    /// waits until every ticket is Done, because a report it wrote early would be the one waiting for
    /// the owner when they came to release.
    /// </para>
    /// </summary>
    public async Task<UnitGateVerdict> CheckAsync(UnitSubject s, bool manual, CancellationToken ct = default)
    {
        static UnitGateVerdict V(GateVerdict v) => new(v, Array.Empty<int>());

        using (var scope = _scopes.CreateScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<ResourceLeaseService>();
            if (await leases.HolderOfAsync(StewardService.LeaseKey(s.Project)) is not null)
                return V(GateVerdict.Wait("the steward is at work on this project"));
        }

        var (g, error) = await GatherAsync(s, ct);
        if (g is null) return V(GateVerdict.Stop(error ?? "the unit could not be read"));
        if (!g.Unit.Agreed) return V(GateVerdict.Stop("the unit is not agreed, so nothing has been built against it"));
        if (g.Material.Tickets.Count == 0) return V(GateVerdict.Stop("no ticket has been filed against this unit"));

        var analyse = g.Material.Missing
            .Where(m => m.Kind is UnitMaterial.MissingKind.NoAnalysis or UnitMaterial.MissingKind.StaleAnalysis)
            .Select(m => g.Material.Tickets.First(t => t.Ticket.Id == m.TicketId).Ticket)
            .Where(t => TicketAnalysisService.Eligibility(t).eligible)
            .Select(t => t.Id).ToList();

        var open = g.Material.Missing.Where(m => m.Kind == UnitMaterial.MissingKind.Open).ToList();
        return new UnitGateVerdict(
            !manual && open.Count > 0 ? GateVerdict.Wait(open[0].Why) : GateVerdict.Go,
            analyse);
    }

    /// <summary>Read the unit and write its report. The stored report's id, or why there isn't one.</summary>
    public async Task<(int? reportId, string? error)> RunAsync(UnitSubject s, IRunProgress progress, CancellationToken ct)
    {
        if (await _usage.EnsureSignedInAsync() is { } authError) return (null, authError);

        progress.Step("Gathering the unit's tickets and analyses");
        var (g, error) = await GatherAsync(s, ct);
        if (g is null) return (null, error);

        // The pictures, copied before anything else: a ticket re-QA'd while this runs would
        // otherwise delete a file the prompt has already named.
        var staging = Path.Combine(_paths.RunDir, "unit-report", "shots");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var shotPaths = new Dictionary<int, string>();
        foreach (var shot in g.Material.Shots)
            if (_shots.DiskPath(shot.TicketId, shot.ImageFile) is { } from && File.Exists(from))
            {
                var to = Path.Combine(staging, $"{shot.N}{Path.GetExtension(from)}");
                File.Copy(from, to, overwrite: true);
                shotPaths[shot.N] = to;
            }

        var scratch = Path.Combine(_paths.RunDir, "unit-report");

        // Each ticket's whole analysis, as a file the run opens when a digest leaves a question.
        var analysesDir = Path.Combine(scratch, "analyses");
        if (Directory.Exists(analysesDir)) Directory.Delete(analysesDir, recursive: true);
        Directory.CreateDirectory(analysesDir);
        var analysisPaths = new Dictionary<int, string>();
        foreach (var t in g.Material.Tickets.Where(t => t.Doc is not null))
        {
            var path = Path.Combine(analysesDir, $"{t.Ticket.Id}.json");
            await File.WriteAllTextAsync(path, AnalysisDocument.Serialize(t.Doc!), ct);
            analysisPaths[t.Ticket.Id] = path;
        }

        var (dir, release, checkoutError) = await _git.CheckoutForReadingAsync(s.Project, g.Range.ToSha, scratch, ct);
        if (dir is null) return (null, checkoutError);

        try
        {
            var next = g.All.Where(u => u.Number > g.Unit.Number).OrderBy(u => u.Number).FirstOrDefault();
            var prompt = UnitReportPrompt.Build(new UnitReportPrompt.Context(
                g.Cfg.DisplayName, g.Cfg.LearningLabel ?? "", g.Plan.DisplayName, g.Unit, TicketGitService.UnitTitle(g.Unit),
                g.Unit.Text, g.Material, g.States, g.Range, dir, shotPaths, g.Bounces,
                next is null ? null : $"unit {next.Number}, {TicketGitService.UnitTitle(next)} ({string.Join(", ", next.Outcomes)})",
                analysisPaths));

            var provider = _providers.ResolveForAnalysis();
            var run = await AgentJsonRunner.RunAsync(provider, dir, Path.Combine(scratch, "run"), prompt,
                AnalysisDocument.JsonSchema, progress, Timeout, "unit report", _log, ct);
            if (run.Outcome is not { } outcome) return (null, run.Error);
            if (outcome.Error != null) return (null, outcome.Error);
            if (outcome.Structured is not { } structured) return (null, "the run finished without producing a document");

            var (doc, parseError) = AnalysisDocument.Parse(structured, g.Material.Outcomes);
            if (doc is null) return (null, parseError ?? "the document was empty");
            doc = KeepPoolShots(doc, shotPaths.Keys.ToHashSet());

            var row = new UnitReport
            {
                ProjectKey = s.Project,
                Plan = s.Plan,
                Unit = s.Unit,
                BaseRef = g.Range.BaseRef,
                FromSha = g.Range.FromSha,
                ToSha = g.Range.ToSha,
                RangeCommits = g.Range.RangeCommits,
                OwnCommits = g.Range.OwnCommits,
                ReadJson = JsonSerializer.Serialize(g.Material.Tickets.Select(t => new { ticketId = t.Ticket.Id, analysisId = t.Analysis?.Id })),
                ShotsJson = JsonSerializer.Serialize(g.Material.Shots.Where(x => shotPaths.ContainsKey(x.N)).Select(x =>
                    new UnitReportShot(x.N, x.TicketId, x.TicketOrder, x.Caption, Path.GetFileName(shotPaths[x.N]), x.Focus, x.LandedAt)).ToList()),
                GapsJson = JsonSerializer.Serialize(g.Material.Missing.Select(m => new { ticketId = m.TicketId, kind = m.Kind.ToString(), why = m.Why })),
                Headline = doc.Headline,
                BlocksJson = AnalysisDocument.Serialize(doc),
                InputTokens = outcome.InputTokens,
                OutputTokens = outcome.OutputTokens,
                CacheReadTokens = outcome.CacheReadTokens,
                CacheCreationTokens = outcome.CacheCreationTokens,
                Model = outcome.Model,
                DurationMs = run.DurationMs,
                StepCount = run.Steps,
            };

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            db.UnitReports.Add(row);
            await db.SaveChangesAsync(CancellationToken.None);

            // The pictures move under the report they belong to, now that it has an id.
            var home = _paths.UnitReportShotsDir(row.Id);
            Directory.CreateDirectory(home);
            foreach (var path in shotPaths.Values)
                File.Move(path, Path.Combine(home, Path.GetFileName(path)), overwrite: true);

            return (row.Id, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "The report on {Unit} failed", s);
            return (null, ex.Message);
        }
        finally
        {
            await release();
        }
    }

    /// <summary>The shots a stored report kept. Empty for a row that cannot be read.</summary>
    public static IReadOnlyList<UnitReportShot> ShotsOf(UnitReport r)
    {
        try { return JsonSerializer.Deserialize<List<UnitReportShot>>(r.ShotsJson) ?? new(); }
        catch (JsonException) { return Array.Empty<UnitReportShot>(); }
    }

    /// <summary>
    /// The document with every shot it cites checked against the pool. A number the pool does not
    /// hold is dropped rather than drawn: a stop showing no picture is honest, and one showing the
    /// wrong picture teaches a screen that does not exist.
    /// </summary>
    public static AnalysisDocument.Doc KeepPoolShots(AnalysisDocument.Doc doc, IReadOnlySet<int> pool)
    {
        int? Keep(int? n) => n is int x && pool.Contains(x) ? x : null;
        return doc with
        {
            Sections = doc.Sections.Select(s => s with
            {
                Blocks = s.Blocks.Select(b => b with
                {
                    Nodes = b.Nodes?.Select(n => n with { Shot = Keep(n.Shot) }).ToList(),
                    Segments = b.Segments?.Select(x => x with { Shot = Keep(x.Shot) }).ToList(),
                }).ToList(),
            }).ToList(),
        };
    }
}
