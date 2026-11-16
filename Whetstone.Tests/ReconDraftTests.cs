using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Whetstone.Mcp;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Drafted directions and the areas they are drafted from (docs/recon-tab-plan.md, "Drafted
/// directions"): an area's own <c>about</c> and <c>roles</c>, what the drafter is told and what of
/// its answer is kept, and how a filed discovery says where its direction came from. The run itself
/// is an agent's and is not exercised here; everything around it is.
/// </summary>
public sealed class ReconDraftTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly DashboardPaths _paths;
    private readonly ProjectRegistry _registry;
    private readonly AnalyticsService _marts;

    public ReconDraftTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-recon-draft-" + Guid.NewGuid().ToString("N")[..8]);
        var data = Path.Combine(_root, "data");
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(Path.Combine(data, "projects", "alpha"));
        File.WriteAllText(Path.Combine(data, "projects", "alpha", "project.defaults.json"), $$"""
            { "displayName": "Alpha", "projectPath": {{JsonSerializer.Serialize(repo)}}, "baseBranch": "main",
              "tracks": ["Delivery", "Discovery"],
              "discovery": { "siteUrl": "http://localhost:53590/",
                "areas": [
                  { "name": "Portal", "pages": ["/portal/**"], "code": ["src/portal"],
                    "about": "Where an owner runs their listing and their staff check the day's bookings.",
                    "roles": ["Owner", "staff", "nobody we know"] },
                  { "name": "Events", "pages": ["/events/**"], "code": ["src/events"] } ],
                "roles": ["signed out", "owner", "staff"] } }
            """);
        File.WriteAllText(Path.Combine(data, "config.defaults.json"), """{ "baseBranch": "main" }""");
        _paths = new DashboardPaths(data);
        _registry = new ProjectRegistry(_paths);
        _marts = new AnalyticsService(_paths, new QaIsolation(false), NullLogger<AnalyticsService>.Instance, _ => null);

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

    private void Publish(string mart, string select)
    {
        Directory.CreateDirectory(_marts.MartsDir);
        using var con = new DuckDBConnection("DataSource=:memory:");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"COPY ({select}) TO '{_marts.MartPath(mart).Replace('\\', '/')}' (FORMAT parquet)";
        cmd.ExecuteNonQuery();
    }

    private ReconDrafter Drafter(DashboardDbContext db) =>
        new(db, new ReconService(db, _marts, _registry), _registry, _marts, new DiscoveryTaste(db, _registry, _paths),
            new AgentProviderFactory(_registry), null!, _paths, new QaIsolation(false), NullLogger<ReconDrafter>.Instance);

    // ---- an area's own words and roles --------------------------------------------------------

    [Fact]
    public void An_area_keeps_its_about_and_the_roles_it_is_used_as_in_the_projects_spelling()
    {
        var d = _registry.GetProjectConfig("alpha").Discovery;
        var portal = d.Areas[0];
        Assert.StartsWith("Where an owner runs", portal.About);
        // "Owner" is the project's "owner"; a role the project does not list is dropped.
        Assert.Equal(["owner", "staff"], portal.Roles);
        Assert.Equal(["owner", "staff"], d.RolesOf(portal));
        // An area naming none of its own is used as every role.
        Assert.Empty(d.Areas[1].Roles);
        Assert.Equal(["signed out", "owner", "staff"], d.RolesOf(d.Areas[1]));
        Assert.Equal("", d.Areas[1].About);
    }

    [Fact]
    public async Task The_refresh_is_handed_each_areas_roles()
    {
        var inputs = await new ReconInputs(_registry, (_, _, b, _) => Task.FromResult<string?>("origin/" + b)).BuildAsync();
        var alpha = Assert.Single(inputs.Projects);
        Assert.Equal(["owner", "staff"], alpha.Areas[0].Roles);
        Assert.Equal(["signed out", "owner", "staff"], alpha.Areas[1].Roles);
    }

    [Fact]
    public async Task Get_discovery_hands_chart_the_owners_word_on_each_area()
    {
        var db = Db();
        var tickets = new TicketService(db, registry: _registry);
        var dsc = new DiscoveryService(db, tickets, _registry, _paths);
        var t = await tickets.CreateAsync("alpha", "d", "Plan a weekend", null, null, TicketPhase.Charting, track: TicketTrack.Discovery);

        var got = JsonSerializer.SerializeToElement(await DiscoveryTools.GetDiscovery(dsc, new DiscoveryTaste(db, _registry, _paths),
            tickets, _paths, _registry, t.Id));
        var guide = Assert.Single(got.GetProperty("project").GetProperty("areaGuide").EnumerateArray());
        Assert.Equal("Portal", guide.GetProperty("name").GetString());
        Assert.StartsWith("Where an owner", guide.GetProperty("about").GetString());
        Assert.Equal(["owner", "staff"], guide.GetProperty("usedAs").EnumerateArray().Select(x => x.GetString()));
        // Still the board's alone: no patterns or code paths.
        Assert.DoesNotContain("/portal", got.GetProperty("project").GetRawText());
    }

    // ---- which keys, and what it is told ------------------------------------------------------

    [Theory]
    [InlineData("gap:Portal", true)]
    [InlineData("gap:Portal:owner:phone", true)]
    [InlineData("changed:Events", true)]
    [InlineData("rerun:7", false)]
    [InlineData("", false)]
    public void A_gap_or_a_changed_area_is_drafted_for_and_a_rerun_is_not(string key, bool ok)
    {
        if (ok) Assert.Equal(key, ReconDrafter.DraftableKey(key));
        else Assert.Throws<ReconService.Refused>(() => ReconDrafter.DraftableKey(key));
    }

    [Theory]
    [InlineData("gap:Portal", "Portal")]
    [InlineData("gap:Portal:owner:phone", "Portal")]
    [InlineData("gap:Portal:any role:desk", "Portal")]
    [InlineData("changed:Events", "Events")]
    [InlineData("rerun:7", null)]
    [InlineData(null, null)]
    public void A_keys_area_is_read_from_it(string? key, string? area) => Assert.Equal(area, ReconDrafter.AreaOf(key));

    [Fact]
    public async Task The_brief_is_the_areas_words_its_empty_cells_and_the_record_never_its_code()
    {
        Publish("mart_recon_suggestions", """
            SELECT * FROM (VALUES
                ('alpha', 'gap:Portal', 'gap', 3, 1, 'Portal',
                 '[{"role":"owner","width":"phone"},{"role":"staff","width":"desk"}]',
                 'Nobody has used Portal yet, as owner or staff.', 'h', '["never scouted"]'))
            AS t(project_key, suggestion_key, kind, rule, rank, area, cells, direction, area_head_commit, reasons)
            """);
        var db = Db();
        db.Tickets.Add(new Ticket { ProjectKey = "alpha", Title = "out", Track = TicketTrack.Discovery, Phase = TicketPhase.Scouting,
            Description = "Book a groom for Saturday as a signed-out visitor" });
        await db.SaveChangesAsync();

        var brief = await Drafter(db).BriefAsync("alpha", "gap:Portal");
        Assert.Equal(["as owner on a phone", "as staff at a desk"], brief.Gaps);
        Assert.Equal(["owner", "staff"], brief.Roles);
        Assert.True(brief.RolesAreTheAreas);
        Assert.Equal(["never scouted"], brief.Reasons);
        Assert.Equal(["Book a groom for Saturday as a signed-out visitor"], brief.InFlight);

        var cell = await Drafter(db).BriefAsync("alpha", "gap:Portal:staff:desk");
        Assert.Equal(["as staff at a desk"], cell.Gaps);

        var prompt = ReconDrafter.Prompt(brief);
        Assert.Contains("Where an owner runs their listing", prompt);
        Assert.Contains("\"owner\", \"staff\"", prompt);
        Assert.Contains("as owner on a phone; as staff at a desk", prompt);
        Assert.Contains("do not repeat these", prompt);
        // Its pages, yes; its code, never - a direction drafted from code is written as a feature.
        Assert.Contains("/portal/**", prompt);
        Assert.DoesNotContain("src/portal", prompt);
    }

    [Fact]
    public async Task An_area_with_no_about_is_drafted_from_its_name_and_asks_for_one()
    {
        var brief = await Drafter(Db()).BriefAsync("alpha", "changed:Events");
        Assert.Equal("changed", brief.Kind);
        var prompt = ReconDrafter.Prompt(brief);
        Assert.Contains("has not described this area yet", prompt);
        Assert.Contains("the area names none of its own", prompt);
    }

    [Fact]
    public async Task A_project_that_cannot_file_a_discovery_is_not_drafted_for()
    {
        Directory.CreateDirectory(Path.Combine(_paths.DataRoot, "projects", "bravo"));
        File.WriteAllText(Path.Combine(_paths.DataRoot, "projects", "bravo", "project.defaults.json"),
            $$"""{ "displayName": "Bravo", "projectPath": {{JsonSerializer.Serialize(Path.Combine(_root, "repo"))}}, "baseBranch": "main", "discovery": { "areas": [ { "name": "Help" } ] } }""");
        var registry = new ProjectRegistry(_paths);
        var db = Db();
        var d = new ReconDrafter(db, new ReconService(db, _marts, registry), registry, _marts, new DiscoveryTaste(db, registry, _paths),
            new AgentProviderFactory(registry), null!, _paths, new QaIsolation(false), NullLogger<ReconDrafter>.Instance);
        await Assert.ThrowsAsync<ReconService.Refused>(() => d.BriefAsync("bravo", "gap:Help"));
        await Assert.ThrowsAsync<ReconService.Refused>(() => Drafter(Db()).BriefAsync("alpha", "gap:Nowhere"));
    }

    [Fact]
    public async Task A_qa_copy_refuses_to_draft_before_anything_runs()
    {
        var db = Db();
        var d = new ReconDrafter(db, new ReconService(db, _marts, _registry), _registry, _marts, new DiscoveryTaste(db, _registry, _paths),
            new AgentProviderFactory(_registry), null!, _paths, new QaIsolation(true), NullLogger<ReconDrafter>.Instance);
        var e = await Assert.ThrowsAsync<ReconService.Refused>(() => d.DraftAsync("alpha", "gap:Portal"));
        Assert.Contains(QaIsolation.EnvVar, e.Message);
        Assert.NotNull(QaIsolation.RefusedRoute("POST", "/api/recon/alpha/drafts"));
        Assert.Null(QaIsolation.RefusedRoute("GET", "/api/recon/alpha/drafts"));
    }

    // ---- what of the answer is kept -----------------------------------------------------------

    private static ReconDrafter.Brief Brief(IReadOnlyList<string>? roles = null) => new(
        "Alpha", "Portal", "", [], roles ?? ["owner", "staff"], true, "gap", [], [],
        [new ReconDrafter.Past("Plan your week of bookings as an owner.", 1, 2, 1, 1, 0, 0, 0)],
        ["Check today's walk-ins as staff"], "");

    private static JsonElement Answer(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_candidate_is_kept_signed_in_as_the_areas_role_in_its_spelling()
    {
        var (kept, question, error) = ReconDrafter.Check(Answer("""
            { "candidates": [
                { "direction": "Sort out a no-show before lunch", "signedInAs": "OWNER", "width": "phone", "why": "nobody has handled a no-show" },
                { "direction": "Look around", "signedInAs": "admin", "width": "desk", "why": "x" },
                { "direction": "plan your week of bookings as an owner", "signedInAs": "owner", "width": "desk", "why": "again" },
                { "direction": "Check today's walk-ins as staff.", "signedInAs": "staff", "width": "desk", "why": "open" },
                { "direction": "Cover a colleague's shift", "signedInAs": "staff", "width": "tablet", "why": "a second person" } ],
              "question": "  " }
            """), Brief());
        Assert.Null(error);
        Assert.Null(question);
        Assert.NotNull(kept);
        // admin is not one of the area's; the past and the open directions are not offered again,
        // whatever their case or closing stop; a width it does not know reads as both.
        Assert.Equal(["Sort out a no-show before lunch", "Cover a colleague's shift"], kept!.Select(c => c.Direction));
        Assert.Equal("owner", kept[0].SignedInAs);
        Assert.Equal("both", kept[1].Width);
    }

    [Fact]
    public void A_draft_with_nothing_left_is_refused_and_keeps_its_question()
    {
        var (kept, question, error) = ReconDrafter.Check(Answer("""
            { "candidates": [ { "direction": "Look around", "signedInAs": "admin", "width": "desk", "why": "x" } ],
              "question": "Who uses the portal on a phone?" }
            """), Brief());
        Assert.Null(kept);
        Assert.Equal("Who uses the portal on a phone?", question);
        Assert.Contains("role this area does not list", error);
        Assert.Contains("no candidates", ReconDrafter.Check(Answer("""{ "candidates": [], "question": null }"""), Brief()).Error);
    }

    [Fact]
    public void A_project_with_no_roles_takes_the_role_as_written()
    {
        var (kept, _, _) = ReconDrafter.Check(Answer("""
            { "candidates": [ { "direction": "Find a vet open on Sunday", "signedInAs": "a visitor", "width": "phone", "why": "x" } ], "question": null }
            """), Brief([]));
        Assert.Equal("a visitor", Assert.Single(kept!).SignedInAs);
    }

    // ---- where a filed direction came from ----------------------------------------------------

    [Fact]
    public void A_filed_direction_says_whether_it_was_drafted_edited_rerun_or_written()
    {
        IReadOnlyList<ReconDrafter.Candidate> draft = [new("Sort out a no-show before lunch", "owner", "phone", "why")];
        Assert.Equal("draft", ReconDrafter.SourceOf("  sort out a no-show   before lunch. ", draft, null));
        Assert.Equal("draft-edited", ReconDrafter.SourceOf("Sort out two no-shows before lunch", draft, null));
        Assert.Equal("rerun", ReconDrafter.SourceOf("Plan a night out", null, "Plan a night out."));
        Assert.Equal("owner", ReconDrafter.SourceOf("Plan a night in", null, "Plan a night out"));
        Assert.Equal("owner", ReconDrafter.SourceOf("Anything", null, null));
    }

    [Fact]
    public async Task A_discovery_keeps_its_draft_and_source_and_no_other_ticket_does()
    {
        var db = Db();
        var tickets = new TicketService(db, registry: _registry);
        var d = await tickets.CreateAsync("alpha", "d", "Sort out a no-show", null, null, TicketPhase.NeedsCharting,
            track: TicketTrack.Discovery, reconFrom: "gap:Portal:owner:phone", reconDraftId: 3, directionSource: "draft");
        var t = await tickets.CreateAsync("alpha", "t", "Fix it", null, null, TicketPhase.Backlog, reconDraftId: 3, directionSource: "draft");

        var fresh = Db();
        var got = (await fresh.Tickets.FindAsync(d.Id))!;
        Assert.Equal((3, "draft"), (got.ReconDraftId, got.DirectionSource));
        var other = (await fresh.Tickets.FindAsync(t.Id))!;
        Assert.Equal((null, null), (other.ReconDraftId, other.DirectionSource));
    }

    [Fact]
    public async Task The_form_reopens_on_the_last_draft_for_its_key()
    {
        var db = Db();
        db.ReconDrafts.AddRange(
            new ReconDraft { ProjectKey = "alpha", SuggestionKey = "gap:Portal", Area = "Portal", CreatedAt = new DateTime(2026, 11, 1),
                CandidatesJson = """[{"direction":"old","signedInAs":"owner","width":"phone","why":"w"}]""" },
            new ReconDraft { ProjectKey = "alpha", SuggestionKey = "gap:Portal", Area = "Portal", CreatedAt = new DateTime(2026, 11, 2),
                CandidatesJson = """[{"direction":"new","signedInAs":"staff","width":"desk","why":"w"}]""", Question = "q" },
            new ReconDraft { ProjectKey = "alpha", SuggestionKey = "gap:Events", Area = "Events", CreatedAt = new DateTime(2026, 11, 3) });
        await db.SaveChangesAsync();

        var last = await Drafter(Db()).LatestAsync("alpha", "gap:Portal");
        Assert.Equal("new", Assert.Single(last!.Candidates).Direction);
        Assert.Equal("q", last.Question);
        Assert.Null(await Drafter(Db()).LatestAsync("alpha", "gap:Portal:owner:phone"));
    }
}
