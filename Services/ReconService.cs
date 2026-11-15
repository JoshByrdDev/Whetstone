using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// The Recon tab's reader and its one write (docs/recon-tab-plan.md, "Routes"). Every number is the
/// marts' (<see cref="AnalyticsService"/>), read for one project at a time and never aggregated here.
/// What this class adds is the two filters that have to be live rather than wait for a refresh:
/// a suggestion leaves the page while an open discovery carries its key (<see cref="Ticket.ReconFrom"/>),
/// and while the owner has set it aside (<see cref="ReconDismissalEvent"/>).
/// <para>
/// Setting one aside and taking that back are owner actions: REST routes with no MCP tool behind
/// them, like the verdict. Nothing here reaches past the data folder - the marts are the copy's own
/// and the one table written is local - so none of it belongs in <see cref="QaIsolation"/>'s table.
/// </para>
/// </summary>
public sealed class ReconService
{
    private readonly DashboardDbContext _db;
    private readonly AnalyticsService _marts;
    private readonly ProjectRegistry _registry;

    public ReconService(DashboardDbContext db, AnalyticsService marts, ProjectRegistry registry) =>
        (_db, _marts, _registry) = (db, marts, registry);

    /// <summary>A refusal the route hands straight back to the owner.</summary>
    public sealed class Refused(string message, bool notFound = false) : Exception(message)
    {
        public bool NotFound { get; } = notFound;
    }

    /// <summary>The columns that hold a JSON list or object, handed on parsed rather than as text.</summary>
    private static readonly HashSet<string> JsonColumns = ["cells", "reasons", "tickets", "ticketsSince", "repos"];

    // ---- projects -------------------------------------------------------------------------

    public sealed record AreaView(string Name, string[] Pages, string[] Code);

    /// <summary>
    /// One project the tab can show: what its config says now, and what the marts were last built
    /// from. <see cref="Built"/> false is "Recon has not been built yet"; <see cref="BuiltAreas"/>
    /// differing from the config's count is a refresh behind an edit to the areas.
    /// <see cref="CannotFile"/> says why Scout this cannot file a discovery here, or is null when it can.
    /// </summary>
    public sealed record ProjectView(
        string Key, string DisplayName, IReadOnlyList<AreaView> Areas, IReadOnlyList<string> Roles,
        bool Built, int? BuiltAreas, int? BuiltRoles, object? BuiltAt, object? InputsAt, bool? HistoryOk, object? Repos, string? CannotFile);

    /// <summary>Every enabled project with a <c>discovery</c> block, in the board's order. Empty hides the tab.</summary>
    public IReadOnlyList<ProjectView> Projects()
    {
        var list = new List<ProjectView>();
        foreach (var key in _registry.Projects)
        {
            var cfg = _registry.GetProjectConfig(key);
            if (!cfg.Discovery.Present) continue;
            var built = Plain(_marts.ProjectMart("mart_recon_projects", key)).FirstOrDefault();
            list.Add(new ProjectView(
                key, string.IsNullOrWhiteSpace(cfg.DisplayName) ? key : cfg.DisplayName,
                cfg.Discovery.Areas.Select(a => new AreaView(a.Name, a.Pages, a.Code)).ToList(),
                cfg.Discovery.Roles,
                built is not null,
                built?.GetValueOrDefault("areaCount") is { } ac ? Convert.ToInt32(ac) : null,
                built?.GetValueOrDefault("roleCount") is { } rc ? Convert.ToInt32(rc) : null,
                built?.GetValueOrDefault("builtAt"), built?.GetValueOrDefault("inputsAt"),
                built?.GetValueOrDefault("historyOk") as bool?, built?.GetValueOrDefault("repos"),
                CannotFile(cfg)));
        }
        return list;
    }

    /// <summary>
    /// Why a discovery cannot be filed on this project from the tab - the create route's own
    /// refusals, said before the owner fills the form in rather than after.
    /// </summary>
    public static string? CannotFile(ProjectConfig cfg) =>
        !cfg.Tracks.Contains(TicketTrack.Discovery)
            ? "Discovery is not one of this project's tracks: add it to \"tracks\" in its project.defaults.json to file a discovery from here."
        : !cfg.Discovery.CanScout
            ? "This project's discovery block has no siteUrl, so a scout would have no product to use."
        : null;

    /// <summary>The project, if the tab can show it; refused as not found otherwise.</summary>
    private ProjectConfig Project(string project)
    {
        if (!_registry.IsEnabled(project)) throw new Refused($"No enabled project named {project}.", notFound: true);
        var cfg = _registry.GetProjectConfig(project);
        if (!cfg.Discovery.Present) throw new Refused($"{project} has no discovery block, so it has no Recon.", notFound: true);
        return cfg;
    }

    // ---- suggestions and their live filters -----------------------------------------------

    /// <summary>An open discovery filed from a suggestion: what takes that suggestion off the page.</summary>
    public sealed record InFlight(string Key, int TicketId, string? Title, string Phase);

    /// <summary>A suggestion the owner set aside, hidden until <see cref="Until"/> or until its area's code moves.</summary>
    public sealed record SetAside(string Key, string? Kind, string? Area, string? Direction, DateTime? Until, DateTime At);

    public sealed record SuggestionsRead(
        IReadOnlyList<Dictionary<string, object?>> Suggestions, IReadOnlyList<SetAside> SetAside, IReadOnlyList<InFlight> InFlight);

    public async Task<SuggestionsRead> SuggestionsAsync(string project, DateTime now, CancellationToken ct = default)
    {
        Project(project);
        var rows = Plain(_marts.ProjectMart("mart_recon_suggestions", project, orderBy: "rank"));
        var open = await OpenAsync(project, ct);
        var events = await _db.ReconDismissalEvents.AsNoTracking().Where(e => e.ProjectKey == project).ToListAsync(ct);
        return Filter(rows, open, events, now);
    }

    private async Task<List<InFlight>> OpenAsync(string project, CancellationToken ct) =>
        (await _db.Tickets.AsNoTracking()
            .Where(t => t.ProjectKey == project && t.Track == TicketTrack.Discovery && t.Phase != TicketPhase.Done && t.ReconFrom != null)
            .OrderBy(t => t.Id)
            .Select(t => new { t.ReconFrom, t.Id, t.Title, t.Phase })
            .ToListAsync(ct))
        .Select(t => new InFlight(t.ReconFrom!, t.Id, t.Title, t.Phase.ToString()))
        .ToList();

    /// <summary>
    /// The live filters, on the mart's rows in rank order (docs/recon-tab-plan.md, "Scout this" and
    /// "Not now"). A row goes while an open discovery carries its key; a collapsed gap row also loses
    /// each cell an open discovery was filed from by its own key, and goes when none is left. A row
    /// is set aside while its latest event is Dismissed, the date given (if any) is still ahead, and
    /// the area's latest code commit is still the one recorded when it was pressed - so new code in
    /// the area brings it back. Filtering only: nothing is counted or re-ranked.
    /// </summary>
    public static SuggestionsRead Filter(
        IReadOnlyList<Dictionary<string, object?>> rows, IReadOnlyList<InFlight> open,
        IReadOnlyList<ReconDismissalEvent> events, DateTime now)
    {
        var openKeys = open.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var latest = events
            .GroupBy(e => e.SuggestionKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).First(), StringComparer.Ordinal);
        var shown = new List<Dictionary<string, object?>>();
        var aside = new List<SetAside>();
        var inFlight = new List<InFlight>();

        foreach (var source in rows)
        {
            var key = source.GetValueOrDefault("suggestionKey") as string ?? "";
            var row = new Dictionary<string, object?>(source);
            if (openKeys.Contains(key))
            {
                inFlight.AddRange(open.Where(o => o.Key == key));
                continue;
            }
            if (row.GetValueOrDefault("kind") as string == "gap" && row.GetValueOrDefault("cells") is JsonElement { ValueKind: JsonValueKind.Array } cells)
            {
                var area = row.GetValueOrDefault("area") as string ?? "";
                var kept = new List<JsonElement>();
                foreach (var cell in cells.EnumerateArray())
                {
                    var cellKey = $"gap:{area}:{Str(cell, "role")}:{Str(cell, "width")}";
                    if (openKeys.Contains(cellKey)) inFlight.AddRange(open.Where(o => o.Key == cellKey));
                    else kept.Add(cell);
                }
                if (kept.Count == 0) continue;
                if (kept.Count != cells.GetArrayLength()) row["cells"] = JsonSerializer.SerializeToElement(kept);
            }
            if (latest.TryGetValue(key, out var e) && Hides(e, row.GetValueOrDefault("areaHeadCommit") as string, now))
            {
                aside.Add(new SetAside(key, row.GetValueOrDefault("kind") as string, row.GetValueOrDefault("area") as string,
                    row.GetValueOrDefault("direction") as string, e.Until, e.At));
                continue;
            }
            shown.Add(row);
        }
        return new SuggestionsRead(shown, aside, inFlight);
    }

    private static bool Hides(ReconDismissalEvent e, string? areaHead, DateTime now) =>
        e.Kind == ReconDismissalKind.Dismissed
        && (e.Until is null || e.Until > now)
        && string.Equals(e.AreaHeadCommit ?? "", areaHead ?? "", StringComparison.Ordinal);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- Not now --------------------------------------------------------------------------

    /// <summary>
    /// The owner's Not now on a suggestion on the page. The key has to be one of the project's
    /// suggestions as the marts have them, because what is recorded with it is that suggestion's
    /// area head - the commit whose successor brings it back. A date, when given, has to be ahead.
    /// </summary>
    public async Task<ReconDismissalEvent> DismissAsync(string project, string? key, DateTime? until, DateTime now, CancellationToken ct = default)
    {
        Project(project);
        var k = Key(key);
        var row = Plain(_marts.ProjectMart("mart_recon_suggestions", project, new Dictionary<string, object?> { ["suggestion_key"] = k }))
            .FirstOrDefault()
            ?? throw new Refused($"'{k}' is not one of {project}'s suggestions as Recon was last built. Refresh, then set it aside from the page.");
        DateTime? untilUtc = until is { } u ? (u.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(u, DateTimeKind.Utc) : u.ToUniversalTime()) : null;
        if (untilUtc is { } t && t <= now) throw new Refused("A Not now date has to be in the future.");
        var e = new ReconDismissalEvent
        {
            ProjectKey = project, SuggestionKey = k, Kind = ReconDismissalKind.Dismissed,
            AreaHeadCommit = row.GetValueOrDefault("areaHeadCommit") as string, Until = untilUtc, At = now,
        };
        _db.ReconDismissalEvents.Add(e);
        await _db.SaveChangesAsync(ct);
        return e;
    }

    /// <summary>The owner taking a Not now back. Refused when the suggestion is not set aside.</summary>
    public async Task<ReconDismissalEvent> UndismissAsync(string project, string? key, DateTime now, CancellationToken ct = default)
    {
        Project(project);
        var k = Key(key);
        var last = await _db.ReconDismissalEvents.AsNoTracking()
            .Where(e => e.ProjectKey == project && e.SuggestionKey == k)
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(ct);
        if (last is not { Kind: ReconDismissalKind.Dismissed })
            throw new Refused($"'{k}' is not set aside, so there is nothing to undo.");
        var e = new ReconDismissalEvent { ProjectKey = project, SuggestionKey = k, Kind = ReconDismissalKind.Undone, At = now };
        _db.ReconDismissalEvents.Add(e);
        await _db.SaveChangesAsync(ct);
        return e;
    }

    private static string Key(string? key)
    {
        var k = ReconKey.Clean(key) ?? throw new Refused("Which suggestion? A key is needed.");
        if (ReconKey.Problem(k) is { } why) throw new Refused(why);
        return k;
    }

    // ---- coverage, the drill-down, yield ---------------------------------------------------

    public IReadOnlyList<Dictionary<string, object?>> Coverage(string project)
    {
        Project(project);
        return Plain(_marts.ProjectMart("mart_recon_coverage", project,
            orderBy: "area_ordinal, area, role_ordinal, role, width_ordinal, width"));
    }

    public sealed record AreaRead(
        string Area, AreaView? Config,
        IReadOnlyList<Dictionary<string, object?>> Cells, IReadOnlyList<Dictionary<string, object?>> Patterns,
        IReadOnlyList<Dictionary<string, object?>> Discoveries, IReadOnlyList<Dictionary<string, object?>> Commits);

    /// <summary>
    /// One area's drill-down: its cells, the route patterns visited in it by role and width, the
    /// discoveries that went there, and the commits in its code since it was last scouted. An area
    /// the config no longer names, or <c>(unmapped)</c>, still reads whatever the marts hold for it.
    /// </summary>
    public AreaRead Area(string project, string area)
    {
        var cfg = Project(project);
        var w = new Dictionary<string, object?> { ["area"] = area };
        var conf = cfg.Discovery.Areas.FirstOrDefault(a => a.Name == area);
        return new AreaRead(area, conf is null ? null : new AreaView(conf.Name, conf.Pages, conf.Code),
            Plain(_marts.ProjectMart("mart_recon_coverage", project, w, "role_ordinal, role, width_ordinal, width")),
            Plain(_marts.ProjectMart("mart_recon_patterns", project, w, "route_pattern, role, width")),
            Plain(_marts.ProjectMart("mart_recon_area_discoveries", project, w, "created_at DESC, discovery_id DESC")),
            Plain(_marts.ProjectMart("mart_recon_area_commits", project, w, "repo, fp_index LIMIT 200")));
    }

    public sealed record YieldRead(int Days, IReadOnlyList<Dictionary<string, object?>> Areas, IReadOnlyList<Dictionary<string, object?>> Directions);

    /// <summary>Yield by area and by direction lineage, in one window (7, 30, 90, or 0 for all; all by default).</summary>
    public YieldRead Yield(string project, int? days)
    {
        Project(project);
        var d = days is { } x && AnalyticsService.Windows.Contains(x) ? x : 0;
        IReadOnlyList<Dictionary<string, object?>> Cut(string cut, string order) =>
            Plain(_marts.ProjectMart("mart_recon_yield", project,
                new Dictionary<string, object?> { ["window_days"] = d, ["cut"] = cut }, order));
        return new YieldRead(d,
            Cut("area", "approved DESC, shown DESC, discoveries DESC, label"),
            Cut("direction", "approved DESC, new_to_me DESC, latest_at DESC NULLS LAST"));
    }

    /// <summary>The marts' rows with their JSON columns parsed, so the page reads a list as a list.</summary>
    private static List<Dictionary<string, object?>> Plain(List<Dictionary<string, object?>> rows)
    {
        foreach (var row in rows)
            foreach (var col in JsonColumns)
                if (row.GetValueOrDefault(col) is string s)
                {
                    try { row[col] = JsonDocument.Parse(s).RootElement.Clone(); }
                    catch (JsonException) { /* left as the text it was */ }
                }
        return rows;
    }
}
