using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Every per-project key survives the trip from <c>project.defaults.json</c> into
/// <see cref="ProjectConfig"/>.
///
/// This exists because the failure it catches is silent. A key documented on the config class but
/// never read in <c>GetProjectConfig</c> does not throw, does not warn, and does not appear in the
/// UI - the project simply gets the default for ever, and the person who set it has no way to tell.
/// That is exactly what happened to <c>ideUrl</c>, and it is a per-project setting, so it could only
/// ever have been noticed by a second project wanting a different value from the first.
///
/// Paths here use forward slashes deliberately: Windows accepts them, and it keeps the JSON in
/// these literals free of escapes, which is its own class of test bug.
/// </summary>
public class ProjectConfigReadTests : IDisposable
{
    private readonly string _dataRoot;

    public ProjectConfigReadTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "whetstone-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dataRoot, "projects", "alpha"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* temp dir */ }
    }

    private ProjectConfig Load(string defaultsJson)
    {
        var paths = new DashboardPaths(_dataRoot);
        File.WriteAllText(paths.ProjectConfigDefaults("alpha"), defaultsJson);
        return new ProjectRegistry(paths).GetProjectConfig("alpha");
    }

    [Fact]
    public void Every_documented_per_project_key_is_actually_read()
    {
        var cfg = Load("""
        {
          "displayName": "Alpha",
          "projectPath": "C:/Repos/Alpha",
          "baseBranch": "develop",
          "repoType": "TFVC",
          "tfvcFolders": ["External", "Internal"],
          "branchPrefix": "jb/",
          "worktreeRoot": "C:/wt/alpha",
          "worktreeSetup": ["npm ci"],
          "worktreeCopyFiles": [".env"],
          "diffNoisePaths": ["src/generated/**"],
          "memoryDir": ".notes",
          "learningLabel": "Study Alpha",
          "ideUrl": "jetbrains://rider/navigate/reference?path={path}:{line}",
          "phaseResources": { "qa-test": ["app-stack", "docker:daemon"] },
          "jira": { "siteUrl": "https://alpha.atlassian.net", "email": "a@b.c", "apiToken": "tok" },
          "stack": { "up": "docker compose up -d", "healthUrl": "http://localhost:9000/" }
        }
        """);

        Assert.Equal("alpha", cfg.Name);
        Assert.Equal("Alpha", cfg.DisplayName);
        Assert.Equal("C:/Repos/Alpha", cfg.ProjectPath);
        Assert.Equal("develop", cfg.BaseBranch);
        Assert.Equal("tfvc", cfg.RepoType);                   // normalised - case never matters
        Assert.True(cfg.IsTfvc);
        Assert.Equal(new[] { "External", "Internal" }, cfg.TfvcFolders);
        Assert.Equal("jb/", cfg.BranchPrefix);
        Assert.Equal("C:/wt/alpha", cfg.WorktreeRoot);
        Assert.Equal(new[] { "npm ci" }, cfg.WorktreeSetup);
        Assert.Equal(new[] { ".env" }, cfg.WorktreeCopyFiles);
        Assert.Equal(new[] { "src/generated/**" }, cfg.DiffNoisePaths);
        Assert.Equal(".notes", cfg.MemoryDir);
        Assert.Equal("Study Alpha", cfg.ResolvedLearningLabel);
        Assert.Equal("jetbrains://rider/navigate/reference?path={path}:{line}", cfg.ResolvedIdeUrl);
        Assert.Equal(new[] { "alpha:app-stack", "docker:daemon" }, cfg.ResourcesFor("qa-test"));
        // The legacy "jira" block still reads, and still means what it always did.
        Assert.Equal(Whetstone.Services.Tracker.TrackerKind.Jira, cfg.Tracker.Kind);
        Assert.Equal("https://alpha.atlassian.net", cfg.Tracker.BaseUrl);
        Assert.True(cfg.Stack.IsConfigured);
    }

    /// <summary>
    /// The same keys, absent: each falls back to the documented default rather than to whatever the
    /// first project happened to set. A second project inherits nothing from the first.
    /// </summary>
    [Fact]
    public void Omitted_keys_fall_back_to_their_documented_defaults()
    {
        var cfg = Load("""{ "projectPath": "C:/Repos/Alpha" }""");

        Assert.Equal("alpha", cfg.DisplayName);               // the key itself
        Assert.Equal("Learn alpha", cfg.ResolvedLearningLabel);
        Assert.Equal("vscode://file/{path}:{line}", cfg.ResolvedIdeUrl);
        Assert.Equal(Path.GetFullPath("C:/Repos/Alpha/.claude/memory"), cfg.ResolvedMemoryDir);
        Assert.Equal(new[] { "alpha:app-stack" }, cfg.ResourcesFor("qa-test"));
        Assert.False(cfg.Stack.IsConfigured);
        Assert.Equal("", cfg.BaseBranch);                     // empty = inherit the global
        Assert.Equal("git", cfg.RepoType);                    // every project before the setting existed
        Assert.False(cfg.IsTfvc);
    }

    /// <summary>
    /// A value that is not tfvc reads as git, and which folders TFVC holds is decided by prefix, so a
    /// sibling whose name merely starts the same way is not swept in.
    /// </summary>
    [Fact]
    public void Repo_type_falls_back_to_git_and_tfvc_folders_match_whole_segments()
    {
        Assert.Equal("git", Load("""{ "repoType": "tfs" }""").RepoType);
        Assert.Equal("git", Load("""{ "repoType": 7 }""").RepoType);

        var cfg = Load("""{ "repoType": "tfvc", "tfvcFolders": ["External", "Internal/"] }""");
        Assert.True(cfg.InTfvc("External/Core/A.cs"));
        Assert.True(cfg.InTfvc(@"Internal\Logic\B.cs"));
        Assert.False(cfg.InTfvc("ExternalTools/C.cs"));
        Assert.False(cfg.InTfvc("CLAUDE.md"));
        Assert.False(cfg.InTfvc(".claude/settings.json"));

        // No folders listed: the whole repo is mapped.
        Assert.True(Load("""{ "repoType": "tfvc" }""").InTfvc("anything/at/all.cs"));
    }

    /// <summary>
    /// The local override file wins over the committed defaults, key by key - which is how a repo
    /// path and a Jira token stay out of git while the rest of the project's config stays in it.
    /// </summary>
    [Fact]
    public void The_local_override_file_wins_key_by_key()
    {
        var paths = new DashboardPaths(_dataRoot);
        File.WriteAllText(paths.ProjectConfigDefaults("alpha"),
            """{ "displayName": "Alpha", "baseBranch": "develop" }""");
        File.WriteAllText(paths.ProjectConfig("alpha"),
            """{ "projectPath": "C:/Repos/Alpha", "baseBranch": "main" }""");

        var cfg = new ProjectRegistry(paths).GetProjectConfig("alpha");
        Assert.Equal("Alpha", cfg.DisplayName);               // only in defaults - survives
        Assert.Equal("main", cfg.BaseBranch);                 // in both - the override wins
        Assert.Equal("C:/Repos/Alpha", cfg.ProjectPath);      // only in the override
    }

    /// <summary>
    /// The gate every project-scoped request goes through, now that there is no active project to
    /// fall back on. Nothing, an unknown key and an unregistered project all have to answer the same
    /// way: no. An unregistered project is the one worth pinning down - its folder and config are
    /// still on disk, so it reads as perfectly valid right up until this check.
    /// </summary>
    [Fact]
    public void Only_an_enabled_project_can_be_named()
    {
        var paths = new DashboardPaths(_dataRoot);
        File.WriteAllText(paths.ProjectConfigDefaults("alpha"), """{ "displayName": "Alpha" }""");
        var registry = new ProjectRegistry(paths);

        Assert.True(registry.IsEnabled("alpha"));
        Assert.False(registry.IsEnabled(null));
        Assert.False(registry.IsEnabled(""));
        Assert.False(registry.IsEnabled("   "));
        Assert.False(registry.IsEnabled("beta"));

        File.WriteAllText(paths.DisabledMarker("alpha"), "unregistered");
        Assert.False(registry.IsEnabled("alpha"));
        Assert.Contains("alpha", registry.AllProjects);   // still there to re-register
    }

    /// <summary>
    /// A malformed config file must not take the registry down with it: a project whose JSON the
    /// user has just broken by hand should degrade to defaults, not stop the other project's
    /// tickets being claimed.
    /// </summary>
    [Fact]
    public void A_malformed_config_degrades_to_defaults_rather_than_throwing()
    {
        var cfg = Load("{ not json");
        Assert.Equal("alpha", cfg.DisplayName);
        Assert.Equal("", cfg.ProjectPath);
    }
}
