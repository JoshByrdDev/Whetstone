using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// What a project says about its steward, and above all what switches one on.
/// <para>
/// The mode is the one setting here whose wrong value does damage rather than nothing: a steward
/// that believes it may merge when it may not. So the reading is pinned from the careful side -
/// anything that is not plainly a mode is no steward at all, and "full" is only full where a merge
/// stays on this machine.
/// </para>
/// </summary>
public class StewardConfigTests : IDisposable
{
    private readonly string _dataRoot;

    public StewardConfigTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "whetstone-steward-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dataRoot, "projects", "alpha"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* temp dir */ }
    }

    private ProjectConfig Load(string defaultsJson, string? localJson = null)
    {
        var paths = new DashboardPaths(_dataRoot);
        File.WriteAllText(paths.ProjectConfigDefaults("alpha"), defaultsJson);
        if (localJson is not null) File.WriteAllText(paths.ProjectConfig("alpha"), localJson);
        return new ProjectRegistry(paths).GetProjectConfig("alpha");
    }

    [Fact]
    public void A_project_that_says_nothing_has_no_steward()
    {
        var cfg = Load("""{ "displayName": "Alpha" }""");
        Assert.Equal(StewardMode.Off, cfg.StewardMode);
        Assert.False(cfg.Steward.Plan.Configured);
    }

    [Theory]
    [InlineData("propose", StewardMode.Propose)]
    [InlineData("Manage", StewardMode.Manage)]
    [InlineData(" FULL ", StewardMode.Full)]
    [InlineData("on", StewardMode.Off)]       // not a mode: a typo must not switch one on
    [InlineData("3", StewardMode.Off)]        // nor a number that happens to be an enum value
    [InlineData("", StewardMode.Off)]
    public void The_mode_is_read_carefully(string stored, StewardMode expected)
    {
        var cfg = Load("""{ "mergeInsteadOfPr": true }""", $$"""{ "stewardMode": "{{stored}}" }""");
        Assert.Equal(expected, cfg.StewardMode);
    }

    [Fact]
    public void A_plan_may_name_the_document_its_units_are_cut_from()
    {
        Assert.Equal("", Load("""{ "steward": { "plan": { "units": "docs/units" } } }""").Steward.Plan.Master);
        var plan = Load("""{ "steward": { "plan": { "units": "docs/units", "master": "/docs\\PLAN.md" } } }""").Steward.Plan;
        Assert.Equal("docs/PLAN.md", plan.Master);
        Assert.Equal("docs/units", plan.UnitsDir);
    }

    [Fact]
    public void Full_is_only_full_where_a_merge_stays_on_this_machine()
    {
        // By pull request the last step is a push; by TFVC the one writer is the human.
        Assert.Equal(StewardMode.Manage, Load("""{ }""", """{ "stewardMode": "full" }""").StewardMode);
        Assert.Equal(StewardMode.Manage,
            Load("""{ "mergeInsteadOfPr": true, "repoType": "tfvc" }""", """{ "stewardMode": "full" }""").StewardMode);
        Assert.Equal(StewardMode.Full,
            Load("""{ "mergeInsteadOfPr": true }""", """{ "stewardMode": "full" }""").StewardMode);
    }

    [Fact]
    public void Every_steward_key_is_actually_read()
    {
        var s = Load("""
        {
          "steward": {
            "limits": { "createdPerDay": 100, "openAtOnce": 9, "sendBacksPerTicket": 3 },
            "memoMaxChars": 5000,
            "syncFrom": "main",
            "decisionsFile": "docs\\plan\\decisions.md",
            "plan": { "units": "docs/plan/units/", "outcome": "^## ([A-Z]\\d+)" },
            "neverMerge": ["infra/", "deploy.config.ts"],
            "mergeChecks": [
              { "run": "npm run check" },
              { "name": "database tests", "run": "npm run test:db", "whenChanged": ["packages/db/"],
                "machineFault": "database is not running", "timeoutMinutes": 20 }
            ]
          }
        }
        """).Steward;

        Assert.Equal(100, s.CreatedPerDay);
        Assert.Equal(9, s.OpenAtOnce);
        Assert.Equal(3, s.SendBacksPerTicket);
        Assert.Equal(5000, s.MemoMaxChars);
        Assert.Equal("main", s.SyncFrom);
        Assert.Equal("docs/plan/decisions.md", s.DecisionsFile);
        Assert.Equal("docs/plan/units", s.Plan.UnitsDir);
        Assert.Equal(@"^## ([A-Z]\d+)", s.Plan.Outcome);
        Assert.Equal(StewardPlan.DefaultAgreed, s.Plan.Agreed);   // unsaid keeps the default
        Assert.Equal(2, s.MergeChecks.Length);
        Assert.Equal("npm run check", s.MergeChecks[0].Name);     // unnamed: the command is its name
        Assert.Equal(15, s.MergeChecks[0].TimeoutMinutes);
        Assert.Equal(new[] { "packages/db/" }, s.MergeChecks[1].WhenChanged);
        Assert.Equal("database is not running", s.MergeChecks[1].MachineFault);
    }

    [Fact]
    public void A_pattern_that_will_not_compile_falls_back_rather_than_breaking_every_run()
    {
        var s = Load("""{ "steward": { "plan": { "units": "docs", "agreed": "([unclosed" } } }""").Steward;
        Assert.Equal(StewardPlan.DefaultAgreed, s.Plan.Agreed);
    }

    [Theory]
    [InlineData("infra/stack.ts", true)]
    [InlineData("infra", false)]                 // the folder rule names what is under it
    [InlineData("deploy.config.ts", true)]
    [InlineData("src/deploy.config.ts", false)]  // a file rule is that one file
    [InlineData("INFRA\\stack.ts", true)]
    public void Never_merged_paths_are_folders_or_single_files(string path, bool expected)
    {
        var s = Load("""{ "steward": { "neverMerge": ["infra/", "deploy.config.ts"] } }""").Steward;
        Assert.Equal(expected, s.IsNeverMerged(path));
    }

    /// <summary>A family of files, so the next script added to the folder is held like the rest.</summary>
    [Theory]
    [InlineData("scripts/deploy.ps1", true)]
    [InlineData("scripts/new-one.ps1", true)]
    [InlineData("scripts/deploy.tests.ps1", true)]
    [InlineData("scripts/sub/deploy.ps1", false)]  // one * stays in its folder
    [InlineData("scripts/deploy.psm1", false)]
    [InlineData("scripts/review-prompt.md", true)]
    [InlineData("scripts/README.md", false)]
    [InlineData("templates/qa/skill.md", true)]    // ** crosses folders
    [InlineData("templates/skill.md", true)]
    [InlineData("other/templates/skill.md", false)]
    [InlineData("SCRIPTS\\Deploy.PS1", true)]
    public void A_never_merged_rule_may_be_a_glob(string path, bool expected)
    {
        var s = Load("""{ "steward": { "neverMerge": ["scripts/*.ps1", "scripts/*-prompt.md", "templates/**"] } }""").Steward;
        Assert.Equal(expected, s.IsNeverMerged(path));
    }
}
