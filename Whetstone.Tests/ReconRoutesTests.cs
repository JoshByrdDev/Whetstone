using System.Reflection;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Recon tab's routes (docs/recon-tab-plan.md, R3): a reader over the Recon marts for one
/// project at a time, the two filters that are live rather than wait for a refresh - an open
/// discovery carrying a suggestion's key, and the owner's Not now - and the Not now itself, an
/// owner action with no tool behind it. The numbers are dbt's and tested there; these cover what
/// the board does with them.
/// </summary>
public sealed class ReconRoutesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly ProjectRegistry _registry;
    private readonly AnalyticsService _marts;

    public ReconRoutesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-recon-routes-" + Guid.NewGuid().ToString("N")[..8]);
        var data = Path.Combine(_root, "data");
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        void Project(string key, string json)
        {
            Directory.CreateDirectory(Path.Combine(data, "projects", key));
            File.WriteAllText(Path.Combine(data, "projects", key, "project.defaults.json"), json);
        }
        var path = JsonSerializer.Serialize(repo);
        Project("alpha", $$"""
            { "displayName": "Alpha", "projectPath": {{path}}, "baseBranch": "main",
              "discovery": { "siteUrl": "http://localhost:53590/",
                "areas": [ { "name": "Events", "pages": ["/events/**"], "code": ["src/events"] },
                           { "name": "Billing", "pages": ["/billing"], "code": ["src/billing"] } ],
                "roles": ["member", "admin"] } }
            """);
        Project("delta", $$"""{ "displayName": "Delta", "projectPath": {{path}}, "baseBranch": "main", "tracks": ["Delivery", "Discovery"], "discovery": { "siteUrl": "http://localhost:53590/" } }""");
        Project("echo", $$"""{ "displayName": "Echo", "projectPath": {{path}}, "baseBranch": "main", "tracks": ["Delivery", "Discovery"], "discovery": { } }""");
        Project("bravo", $$"""{ "displayName": "Bravo", "projectPath": {{path}}, "baseBranch": "main", "discovery": { } }""");
        Project("charlie", $$"""{ "displayName": "Charlie", "projectPath": {{path}}, "baseBranch": "main" }""");
        File.WriteAllText(Path.Combine(data, "config.defaults.json"), """{ "baseBranch": "main" }""");
        var paths = new DashboardPaths(data);
        _registry = new ProjectRegistry(paths);
        _marts = new AnalyticsService(paths, new QaIsolation(false), NullLogger<AnalyticsService>.Instance, _ => null);

        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private DashboardDbContext Db() => new(_options);
    private ReconService Service(DashboardDbContext db) => new(db, _marts, _registry);

    private void Publish(string mart, string select)
    {
        Directory.CreateDirectory(_marts.MartsDir);
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"COPY ({select}) TO '{_marts.MartPath(mart).Replace('\\', '/')}' (FORMAT parquet)";
        cmd.ExecuteNonQuery();
    }

    /// <summary>alpha's suggestions in rank order: a rerun, a collapsed gap of two cells, a changed area; and one of bravo's.</summary>
    private void PublishSuggestions() => Publish("mart_recon_suggestions", """
        SELECT * FROM (VALUES
            ('alpha', 'rerun:7', 'rerun', 1, 1, 'Events', '[]', 'Plan a night out', 'h-events', '["scouted 3 Oct on a1b2c3d"]'),
            ('alpha', 'gap:Billing', 'gap', 3, 2, 'Billing', '[{"role":"member","width":"phone"},{"role":"admin","width":"desk"}]',
             'Nobody has used Billing as a member on a phone, or as an admin at a desk.', 'h-billing', '["never scouted"]'),
            ('alpha', 'changed:Events', 'changed', 4, 3, 'Events', '[]', 'Events has changed.', 'h-events', '[]'),
            ('bravo', 'gap:Help', 'gap', 3, 1, 'Help', '[{"role":"any role","width":"phone"}]', 'Nobody has used Help.', NULL, '[]'))
        AS t(project_key, suggestion_key, kind, rule, rank, area, cells, direction, area_head_commit, reasons)
        """);

    private static async Task Open(DashboardDbContext db, string project, string key, TicketPhase phase = TicketPhase.Scouting)
    {
        db.Tickets.Add(new Ticket { ProjectKey = project, Title = "scout " + key, Track = TicketTrack.Discovery, Phase = phase, ReconFrom = key });
        await db.SaveChangesAsync();
    }

    private static List<string> Keys(ReconService.SuggestionsRead r) =>
        r.Suggestions.Select(s => (string)s["suggestionKey"]!).ToList();

    // ---- projects --------------------------------------------------------------------------

    [Fact]
    public void Projects_are_the_ones_with_a_discovery_block_and_say_whether_Recon_is_built()
    {
        using var db = Db();
        var r = Service(db);

        var before = r.Projects();
        Assert.Equal(["alpha", "bravo", "delta", "echo"], before.Select(p => p.Key));
        Assert.All(before, p => Assert.False(p.Built));
        Assert.Equal(["Events", "Billing"], before[0].Areas.Select(a => a.Name));
        Assert.Equal(["member", "admin"], before[0].Roles);
        Assert.Empty(before[1].Areas);
        // alpha can scout but has not switched the Discovery track on; bravo has no siteUrl either.
        Assert.Contains("tracks", before[0].CannotFile);
        Assert.Contains("tracks", before[1].CannotFile);
        Assert.Null(before[2].CannotFile);
        Assert.Contains("siteUrl", before[3].CannotFile);

        Publish("mart_recon_projects", """
            SELECT 'alpha' AS project_key, 0 AS project_ordinal, 1 AS area_count, 2 AS role_count,
                   '[{"repo":"","base_ref":"main","tip":"abc","commits":4,"error":null}]' AS repos, true AS history_ok,
                   TIMESTAMP '2026-11-15 11:00:00' AS built_at
            """);
        var after = r.Projects();
        Assert.True(after[0].Built);
        // Built from one area while the config now has two: a refresh is behind the edit.
        Assert.Equal(1, after[0].BuiltAreas);
        Assert.True(after[0].HistoryOk);
        Assert.Equal(JsonValueKind.Array, Assert.IsType<JsonElement>(after[0].Repos).ValueKind);
        Assert.False(after[1].Built);
    }

    [Fact]
    public async Task A_project_without_a_discovery_block_or_not_enabled_is_not_found()
    {
        using var db = Db();
        var r = Service(db);
        Assert.True(Assert.Throws<ReconService.Refused>(() => r.Coverage("charlie")).NotFound);
        Assert.True(Assert.Throws<ReconService.Refused>(() => r.Coverage("nobody")).NotFound);
        Assert.True((await Assert.ThrowsAsync<ReconService.Refused>(() => r.SuggestionsAsync("charlie", Now))).NotFound);
    }

    // ---- suggestions, live ------------------------------------------------------------------

    [Fact]
    public async Task Suggestions_are_one_projects_in_rank_order_with_their_lists_parsed()
    {
        PublishSuggestions();
        using var db = Db();
        var read = await Service(db).SuggestionsAsync("alpha", Now);

        Assert.Equal(["rerun:7", "gap:Billing", "changed:Events"], Keys(read));
        var gap = read.Suggestions[1];
        Assert.Equal(2, Assert.IsType<JsonElement>(gap["cells"]).GetArrayLength());
        Assert.Equal("never scouted", Assert.IsType<JsonElement>(gap["reasons"])[0].GetString());
        Assert.Empty(read.SetAside);
        Assert.Empty(read.InFlight);
    }

    [Fact]
    public async Task An_open_discovery_takes_its_suggestion_off_the_page_and_a_done_one_does_not()
    {
        PublishSuggestions();
        using var db = Db();
        await Open(db, "alpha", "rerun:7");
        await Open(db, "alpha", "changed:Events", TicketPhase.Done);
        // Another project's discovery with the same key is not this project's.
        await Open(db, "bravo", "gap:Billing");

        var read = await Service(db).SuggestionsAsync("alpha", Now);

        Assert.Equal(["gap:Billing", "changed:Events"], Keys(read));
        var flight = Assert.Single(read.InFlight);
        Assert.Equal("rerun:7", flight.Key);
        Assert.Equal("Scouting", flight.Phase);
    }

    [Fact]
    public async Task A_discovery_filed_from_one_cell_takes_that_cell_out_of_its_gap_row()
    {
        PublishSuggestions();
        using var db = Db();
        await Open(db, "alpha", "gap:Billing:member:phone");
        var r = Service(db);

        var one = await r.SuggestionsAsync("alpha", Now);
        var cells = Assert.IsType<JsonElement>(one.Suggestions.Single(s => (string)s["suggestionKey"]! == "gap:Billing")["cells"]);
        Assert.Equal("admin", Assert.Single(cells.EnumerateArray()).GetProperty("role").GetString());
        Assert.Equal("gap:Billing:member:phone", Assert.Single(one.InFlight).Key);

        await Open(db, "alpha", "gap:Billing:admin:desk");
        var both = await r.SuggestionsAsync("alpha", Now);
        Assert.DoesNotContain("gap:Billing", Keys(both));
        Assert.Equal(2, both.InFlight.Count);
    }

    // ---- Not now ----------------------------------------------------------------------------

    [Fact]
    public async Task Not_now_hides_a_suggestion_without_a_refresh_and_undo_brings_it_back()
    {
        PublishSuggestions();
        using var db = Db();
        var r = Service(db);

        var e = await r.DismissAsync("alpha", " gap:Billing ", null, Now);
        Assert.Equal("gap:Billing", e.SuggestionKey);
        Assert.Equal("h-billing", e.AreaHeadCommit);
        Assert.Equal(ReconDismissalKind.Dismissed, e.Kind);

        var hidden = await r.SuggestionsAsync("alpha", Now.AddDays(400));
        Assert.DoesNotContain("gap:Billing", Keys(hidden));
        var aside = Assert.Single(hidden.SetAside);
        Assert.Equal("Billing", aside.Area);
        Assert.Equal("gap", aside.Kind);

        await r.UndismissAsync("alpha", "gap:Billing", Now.AddMinutes(1));
        var back = await r.SuggestionsAsync("alpha", Now.AddMinutes(2));
        Assert.Contains("gap:Billing", Keys(back));
        Assert.Empty(back.SetAside);
        // Append only: both presses are on the record.
        Assert.Equal([ReconDismissalKind.Dismissed, ReconDismissalKind.Undone],
            db.ReconDismissalEvents.OrderBy(x => x.Id).Select(x => x.Kind).ToList());
    }

    [Fact]
    public async Task Not_now_until_a_date_lapses_on_that_date()
    {
        PublishSuggestions();
        using var db = Db();
        var r = Service(db);
        await r.DismissAsync("alpha", "rerun:7", Now.AddDays(3), Now);

        Assert.DoesNotContain("rerun:7", Keys(await r.SuggestionsAsync("alpha", Now.AddDays(2))));
        Assert.Contains("rerun:7", Keys(await r.SuggestionsAsync("alpha", Now.AddDays(3))));
    }

    [Fact]
    public void New_code_in_the_area_brings_a_set_aside_suggestion_back()
    {
        var row = new Dictionary<string, object?>
        {
            ["suggestionKey"] = "changed:Events", ["kind"] = "changed", ["area"] = "Events", ["areaHeadCommit"] = "h2",
        };
        var pressedOnOldHead = new ReconDismissalEvent
        {
            Id = 1, ProjectKey = "alpha", SuggestionKey = "changed:Events", Kind = ReconDismissalKind.Dismissed, AreaHeadCommit = "h1", At = Now,
        };
        var pressedOnThisHead = new ReconDismissalEvent
        {
            Id = 2, ProjectKey = "alpha", SuggestionKey = "changed:Events", Kind = ReconDismissalKind.Dismissed, AreaHeadCommit = "h2", At = Now,
        };

        Assert.Single(ReconService.Filter([row], [], [pressedOnOldHead], Now).Suggestions);
        Assert.Empty(ReconService.Filter([row], [], [pressedOnOldHead, pressedOnThisHead], Now).Suggestions);

        // An area with no code paths has no head: set aside, it stays aside until undone or its date.
        var noCode = new Dictionary<string, object?> { ["suggestionKey"] = "gap:Help", ["kind"] = "gap", ["area"] = "Help", ["areaHeadCommit"] = null };
        var pressed = new ReconDismissalEvent { Id = 3, SuggestionKey = "gap:Help", Kind = ReconDismissalKind.Dismissed, AreaHeadCommit = null, At = Now };
        Assert.Empty(ReconService.Filter([noCode], [], [pressed], Now.AddYears(1)).Suggestions);
    }

    [Fact]
    public async Task Not_now_refuses_what_is_not_a_suggestion_on_the_page()
    {
        PublishSuggestions();
        using var db = Db();
        var r = Service(db);

        Assert.Contains("not a Recon", (await Assert.ThrowsAsync<ReconService.Refused>(() => r.DismissAsync("alpha", "billing", null, Now))).Message);
        Assert.Contains("key is needed", (await Assert.ThrowsAsync<ReconService.Refused>(() => r.DismissAsync("alpha", "  ", null, Now))).Message);
        // Well formed, but not one of alpha's suggestions - bravo's gap is bravo's.
        Assert.Contains("Refresh", (await Assert.ThrowsAsync<ReconService.Refused>(() => r.DismissAsync("alpha", "gap:Help", null, Now))).Message);
        Assert.Contains("future", (await Assert.ThrowsAsync<ReconService.Refused>(() => r.DismissAsync("alpha", "gap:Billing", Now.AddDays(-1), Now))).Message);
        Assert.Contains("not set aside", (await Assert.ThrowsAsync<ReconService.Refused>(() => r.UndismissAsync("alpha", "gap:Billing", Now))).Message);
        Assert.True((await Assert.ThrowsAsync<ReconService.Refused>(() => r.DismissAsync("charlie", "gap:Billing", null, Now))).NotFound);
        Assert.Empty(db.ReconDismissalEvents);
    }

    // ---- coverage, the drill-down, yield ----------------------------------------------------

    [Fact]
    public void The_drill_down_is_one_areas_and_yield_one_window()
    {
        Publish("mart_recon_coverage", """
            SELECT * FROM (VALUES
                ('alpha', 'Events', 0, 'admin', 1, 'desk', 1, 'gap', '[]'),
                ('alpha', 'Events', 0, 'member', 0, 'phone', 0, 'changed', '[30,31]'),
                ('alpha', 'Billing', 1, 'member', 0, 'phone', 0, 'gap', '[]'))
            AS t(project_key, area, area_ordinal, role, role_ordinal, width, width_ordinal, state, tickets_since)
            """);
        Publish("mart_recon_patterns", """
            SELECT * FROM (VALUES ('alpha', 'Events', '/events/[id]', 'member', 'phone', 3, 90),
                                  ('alpha', 'Billing', '/billing', 'member', 'desk', 1, 5))
            AS t(project_key, area, route_pattern, role, width, visits, seconds)
            """);
        Publish("mart_recon_area_discoveries", """
            SELECT * FROM (VALUES ('alpha', 'Events', 7, 'Plan a night out', TIMESTAMP '2026-10-03 10:00:00'),
                                  ('alpha', 'Events', 9, 'Again', TIMESTAMP '2026-11-03 10:00:00'))
            AS t(project_key, area, discovery_id, direction, created_at)
            """);
        Publish("mart_recon_area_commits", """
            SELECT * FROM (VALUES ('alpha', 'Events', '', 'e1', 1, 'later', 'after'), ('alpha', 'Events', '', 'e0', 0, 'tip', 'after'))
            AS t(project_key, area, repo, sha, fp_index, subject, since)
            """);
        Publish("mart_recon_yield", """
            SELECT *, 4 AS shown, 1 AS new_to_me, TIMESTAMP '2026-11-03 10:00:00' AS latest_at FROM (VALUES
                (0, 'alpha', 'area', 'Events', 'Events', 2, 3), (30, 'alpha', 'area', 'Events', 'Events', 1, 1),
                (0, 'alpha', 'direction', '7', 'Plan a night out', 2, 3), (0, 'bravo', 'area', 'Help', 'Help', 1, 0))
            AS t(window_days, project_key, cut, cut_key, label, discoveries, approved)
            """);
        using var db = Db();
        var r = Service(db);

        var cov = r.Coverage("alpha");
        Assert.Equal(["Events/member/phone", "Events/admin/desk", "Billing/member/phone"], cov.Select(c => $"{c["area"]}/{c["role"]}/{c["width"]}"));
        Assert.Equal(2, Assert.IsType<JsonElement>(cov[0]["ticketsSince"]).GetArrayLength());

        var area = r.Area("alpha", "Events");
        Assert.Equal(["src/events"], area.Config!.Code);
        Assert.Equal(2, area.Cells.Count);
        Assert.Equal("/events/[id]", Assert.Single(area.Patterns)["routePattern"]);
        Assert.Equal([9L, 7L], area.Discoveries.Select(d => Convert.ToInt64(d["discoveryId"])));
        Assert.Equal(["e0", "e1"], area.Commits.Select(c => (string)c["sha"]!));
        Assert.Null(r.Area("alpha", "(unmapped)").Config);

        var all = r.Yield("alpha", null);
        Assert.Equal(0, all.Days);
        Assert.Equal(2, Convert.ToInt32(Assert.Single(all.Areas)["discoveries"]));
        Assert.Equal("7", Assert.Single(all.Directions)["cutKey"]);
        Assert.Equal(1, Convert.ToInt32(Assert.Single(r.Yield("alpha", 30).Areas)["discoveries"]));
        // A window the marts are not cut by falls back to all of history rather than nothing.
        Assert.Equal(0, r.Yield("alpha", 12).Days);
    }

    [Fact]
    public void Unpublished_marts_read_empty_rather_than_failing()
    {
        using var db = Db();
        var r = Service(db);
        Assert.Empty(r.Coverage("alpha"));
        var area = r.Area("alpha", "Events");
        Assert.Empty(area.Patterns);
        Assert.Empty(area.Commits);
        Assert.Empty(r.Yield("alpha", 30).Areas);
    }

    // ---- owner only -------------------------------------------------------------------------

    [Fact]
    public void No_tool_reaches_the_recon_service()
    {
        var toolTypes = typeof(ReconService).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .ToList();
        Assert.NotEmpty(toolTypes);
        foreach (var t in toolTypes)
        {
            foreach (var ctor in t.GetConstructors())
                Assert.DoesNotContain(ctor.GetParameters(), p => p.ParameterType == typeof(ReconService));
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(ReconService) || p.ParameterType == typeof(ReconDismissalEvent));
        }
    }
}
