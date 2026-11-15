using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Whetstone.Api;
using Whetstone.Mcp;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The Recon tab's record (docs/recon-tab-plan.md, R1): the project's roles, what get_discovery
/// hands Chart and the scout, and the key a discovery carries from the suggestion it was filed
/// from - which only the owner's create route sets.
/// </summary>
public class ReconTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly DashboardPaths _paths;
    private readonly ProjectRegistry _registry;

    public ReconTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-recon-" + Guid.NewGuid().ToString("N")[..8]);
        var data = Path.Combine(_root, "data");
        var repo = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(repo);
        var project = Path.Combine(data, "projects", "alpha");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "project.defaults.json"), $$"""
            { "displayName": "Alpha", "projectPath": {{JsonSerializer.Serialize(repo)}}, "baseBranch": "main",
              "discovery": { "siteUrl": "http://localhost:53590/",
                "areas": [ { "name": "Events", "pages": ["/events/**"], "code": ["src/events"] },
                           { "name": "Billing", "pages": ["/billing"] } ],
                "roles": ["signed out", " member ", "", "Member", "admin"] } }
            """);
        // bravo is two repos with a discovery block and no roles; charlie has no discovery block.
        var bravo = Path.Combine(_root, "bravo");
        Directory.CreateDirectory(Path.Combine(bravo, "bravo-api"));
        Directory.CreateDirectory(Path.Combine(bravo, "bravo-web"));
        Directory.CreateDirectory(Path.Combine(data, "projects", "bravo"));
        File.WriteAllText(Path.Combine(data, "projects", "bravo", "project.defaults.json"), $$"""
            { "displayName": "Bravo", "projectPath": {{JsonSerializer.Serialize(bravo)}}, "baseBranch": "main",
              "repos": [ { "dir": "bravo-api" }, { "dir": "bravo-web", "baseBranch": "develop", "readOnly": true } ],
              "discovery": { "areas": [ { "name": "Help", "pages": ["/help"], "code": ["bravo-web/src/help"] } ] } }
            """);
        Directory.CreateDirectory(Path.Combine(data, "projects", "charlie"));
        File.WriteAllText(Path.Combine(data, "projects", "charlie", "project.defaults.json"),
            $$"""{ "displayName": "Charlie", "projectPath": {{JsonSerializer.Serialize(repo)}}, "baseBranch": "main" }""");
        File.WriteAllText(Path.Combine(data, "config.defaults.json"), """{ "baseBranch": "main" }""");
        _paths = new DashboardPaths(data);
        _registry = new ProjectRegistry(_paths);

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

    private static DiscoveryConfig Parse(string json) =>
        DiscoveryConfig.From(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);

    // ---- roles ------------------------------------------------------------------------------

    [Fact]
    public void Roles_parse_trimmed_without_blanks_or_repeats()
    {
        var d = _registry.GetProjectConfig("alpha").Discovery;
        // " member " and "Member" are one role; the first spelling is kept.
        Assert.Equal(["signed out", "member", "admin"], d.Roles);
        Assert.Equal(["Events", "Billing"], d.Areas.Select(a => a.Name));
        Assert.True(d.Present);
        Assert.False(_registry.GetProjectConfig("charlie").Discovery.Present);
    }

    [Fact]
    public void Roles_are_empty_when_the_project_names_none()
    {
        Assert.Empty(Parse("""{ "discovery": { "siteUrl": "http://localhost:1" } }""").Roles);
        Assert.Empty(Parse("""{ "discovery": { "roles": "admin" } }""").Roles);
        Assert.Equal(["admin"], Parse("""{ "discovery": { "roles": ["admin", 3, null] } }""").Roles);
        Assert.Empty(Parse("{}").Roles);
    }

    [Fact]
    public async Task Get_discovery_names_the_projects_areas_and_roles()
    {
        var db = new DashboardDbContext(_options);
        var tickets = new TicketService(db, registry: _registry);
        var dsc = new DiscoveryService(db, tickets, _registry, _paths);
        var t = await tickets.CreateAsync("alpha", "d", "Plan a weekend", null, null, TicketPhase.Charting, track: TicketTrack.Discovery);

        var got = JsonSerializer.SerializeToElement(await DiscoveryTools.GetDiscovery(dsc, new DiscoveryTaste(db, _registry, _paths),
            tickets, _paths, _registry, t.Id));
        var project = got.GetProperty("project");
        Assert.Equal(["Events", "Billing"], project.GetProperty("areas").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["signed out", "member", "admin"], project.GetProperty("roles").EnumerateArray().Select(x => x.GetString()));
        // Names only: the patterns are the board's to apply at refresh time.
        Assert.DoesNotContain("/events", project.GetRawText());
    }

    // ---- what the refresh is handed ------------------------------------------------------------

    [Fact]
    public async Task The_inputs_name_every_project_with_a_discovery_block_and_each_repos_base()
    {
        var asked = new List<(string Repo, string Branch)>();
        var inputs = await new ReconInputs(_registry, (repo, _, branch, _) =>
        {
            asked.Add((Path.GetFileName(repo), branch));
            return Task.FromResult<string?>(branch == "develop" ? null : "origin/" + branch);
        }).BuildAsync();

        Assert.Equal(["alpha", "bravo"], inputs.Projects.Select(p => p.Key));
        var alpha = inputs.Projects[0];
        Assert.Equal(["signed out", "member", "admin"], alpha.Roles);
        Assert.Equal(["/events/**"], alpha.Areas[0].Pages);
        Assert.Equal(["src/events"], alpha.Areas[0].Code);
        var one = Assert.Single(alpha.Repos);
        Assert.Equal(("", "origin/main"), (one.Dir, one.BaseRef));

        // A project of several repos: each with its own base, a missing one left null for the
        // refresh to report rather than guessed.
        var bravo = inputs.Projects[1];
        Assert.Empty(bravo.Roles);
        Assert.Equal(["bravo-api", "bravo-web"], bravo.Repos.Select(r => r.Dir));
        Assert.Equal(["origin/main", null], bravo.Repos.Select(r => r.BaseRef));
        Assert.True(bravo.Repos[1].ReadOnly);
        Assert.Contains(("bravo-web", "develop"), asked);
    }

    [Fact]
    public async Task The_inputs_file_is_written_where_the_refresh_reads_it()
    {
        var out_ = Path.Combine(_root, "data", "analytics");
        var analytics = new AnalyticsService(_paths, new QaIsolation(false), Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsService>.Instance,
            _ => null, new ReconInputs(_registry, (_, _, b, _) => Task.FromResult<string?>(b)));
        await analytics.WriteReconInputsAsync();

        Assert.Equal(Path.Combine(out_, "recon-inputs.json"), analytics.ReconInputsFile);
        using var doc = JsonDocument.Parse(File.ReadAllText(analytics.ReconInputsFile));
        var projects = doc.RootElement.GetProperty("projects");
        Assert.Equal(2, projects.GetArrayLength());
        // The names refresh.py reads.
        var p = projects[0];
        Assert.Equal("alpha", p.GetProperty("key").GetString());
        Assert.Equal("Events", p.GetProperty("areas")[0].GetProperty("name").GetString());
        Assert.Equal("/events/**", p.GetProperty("areas")[0].GetProperty("pages")[0].GetString());
        Assert.Equal("main", p.GetProperty("repos")[0].GetProperty("baseRef").GetString());
        Assert.True(p.GetProperty("repos")[0].TryGetProperty("path", out _));
        Assert.True(p.GetProperty("repos")[0].TryGetProperty("dir", out _));
        Assert.True(doc.RootElement.TryGetProperty("writtenAt", out _));
    }

    [Fact]
    public async Task Inputs_that_cannot_be_written_leave_no_stale_file()
    {
        var analytics = new AnalyticsService(_paths, new QaIsolation(false), Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsService>.Instance,
            _ => null, new ReconInputs(_registry, (_, _, _, _) => throw new InvalidOperationException("git is gone")));
        Directory.CreateDirectory(analytics.OutDir);
        File.WriteAllText(analytics.ReconInputsFile, "{ \"projects\": [] }");
        // A resolver that throws is a null base for that repo, not a failed write.
        await analytics.WriteReconInputsAsync();
        using (var doc = JsonDocument.Parse(File.ReadAllText(analytics.ReconInputsFile)))
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("projects")[0].GetProperty("repos")[0].GetProperty("baseRef").ValueKind);

        // A write that fails outright removes the old file, so the marts build empty rather than old.
        var blocked = new AnalyticsService(new DashboardPaths(Path.Combine(_root, "data")), new QaIsolation(false),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalyticsService>.Instance, _ => null,
            new ReconInputs(_registry, (_, _, _, _) => Task.FromResult<string?>("main")));
        Directory.CreateDirectory(analytics.ReconInputsFile + ".tmp");
        await blocked.WriteReconInputsAsync();
        Assert.False(File.Exists(analytics.ReconInputsFile));
    }

    [Fact]
    public void Recons_marts_are_published_and_read_as_a_pair()
    {
        foreach (var m in new[] { "mart_recon_projects", "mart_recon_coverage", "mart_recon_patterns", "mart_recon_yield", "mart_recon_suggestions" })
            Assert.Contains(m, AnalyticsService.Marts);
    }

    [Fact]
    public void The_phone_split_is_the_boards_own()
    {
        // Written twice and the two must agree (docs/recon-tab-plan.md): the dbt width bucket and NARROW_PX.
        string? Find(string rel)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Whetstone.csproj"))) return Path.Combine(dir.FullName, rel);
            return null;
        }
        var macro = File.ReadAllText(Find(Path.Combine("analytics", "macros", "recon.sql"))!);
        var hook = File.ReadAllText(Find(Path.Combine("ClientApp", "src", "hooks", "useNarrow.ts"))!);
        var px = System.Text.RegularExpressions.Regex.Match(hook, @"NARROW_PX\s*=\s*(\d+)").Groups[1].Value;
        Assert.Equal("640", px);
        Assert.Contains($"< {px} then 'phone'", macro);
    }

    // ---- ReconFrom ----------------------------------------------------------------------------

    [Theory]
    [InlineData("gap:Billing", true)]
    [InlineData("gap:Billing:admin:phone", true)]
    [InlineData("gap:Billing:signed out:desk", true)]
    [InlineData("changed:Events", true)]
    [InlineData("rerun:42", true)]
    [InlineData("rerun:x", false)]
    [InlineData("gap:Billing:admin", false)]
    [InlineData("gap:", false)]
    [InlineData("scout:anything", false)]
    [InlineData("changed:a:b", false)]
    public void A_recon_key_has_one_of_three_shapes(string key, bool ok) =>
        Assert.Equal(ok, ReconKey.Problem(key) is null);

    [Fact]
    public void A_rerun_key_names_its_discovery()
    {
        Assert.Equal(42, ReconKey.RerunOf("rerun:42"));
        Assert.Null(ReconKey.RerunOf("gap:Billing"));
        Assert.Null(ReconKey.Clean("  "));
        Assert.Equal("gap:Billing", ReconKey.Clean(" gap:Billing "));
        Assert.NotNull(ReconKey.Problem("gap:" + new string('a', ReconKey.MaxLength)));
    }

    [Fact]
    public async Task A_discovery_keeps_the_key_it_was_filed_from_and_no_other_ticket_does()
    {
        var db = new DashboardDbContext(_options);
        var tickets = new TicketService(db, registry: _registry);
        var d = await tickets.CreateAsync("alpha", "d", "Plan a weekend", null, null, TicketPhase.NeedsCharting,
            track: TicketTrack.Discovery, reconFrom: "gap:Billing");
        var t = await tickets.CreateAsync("alpha", "t", "Fix it", null, null, TicketPhase.Backlog, reconFrom: "gap:Billing");

        var fresh = new DashboardDbContext(_options);
        Assert.Equal("gap:Billing", (await fresh.Tickets.FindAsync(d.Id))!.ReconFrom);
        Assert.Null((await fresh.Tickets.FindAsync(t.Id))!.ReconFrom);
    }

    [Fact]
    public void Only_the_create_route_takes_a_recon_key()
    {
        Assert.NotNull(typeof(CreateTicketRequest).GetProperty("ReconFrom"));
        Assert.Null(typeof(UpdateTicketRequest).GetProperty("ReconFrom"));
    }

    /// <summary>
    /// Held by absence, like the steward's limits: no MCP tool on either door has a parameter for the
    /// key, takes a type that carries one, or names Recon - and none returns a ticket's detail with it,
    /// because the DTOs a tool hands back do not carry it.
    /// </summary>
    [Fact]
    public void No_tool_can_set_or_read_a_recon_key()
    {
        var tools = typeof(ReconKey).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>())))
            .Where(x => x.Tool is not null)
            .ToList();
        Assert.NotEmpty(tools);

        static bool Carries(Type type) =>
            type.GetProperties().Any(p => p.Name.Contains("Recon", StringComparison.OrdinalIgnoreCase));

        foreach (var (method, tool) in tools)
        {
            Assert.DoesNotMatch("(?i)recon", tool!.Name ?? method.Name);
            foreach (var p in method.GetParameters())
            {
                Assert.DoesNotMatch("(?i)recon", p.Name ?? "");
                var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                if (type.IsArray) type = type.GetElementType()!;
                Assert.False(Carries(type), $"{method.Name}({p.Name}) takes {type.Name}, which carries a Recon key");
            }
        }

        foreach (var dto in typeof(TicketDetailDto).Assembly.GetTypes().Where(t => t.Namespace == "Whetstone.Api" && t.Name.StartsWith("Ticket")))
            Assert.False(Carries(dto), $"{dto.Name} carries a Recon key, and tools hand ticket DTOs back");
    }
}
