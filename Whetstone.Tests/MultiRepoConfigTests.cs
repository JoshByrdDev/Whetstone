using Whetstone.Models;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Step 1 of docs/multi-repo-plan.md: <c>repos</c> is read, a project without it is a set of one,
/// and every way a project's code can be somewhere no worktree can be made of it is refused.
///
/// The set-of-one tests are the ones that matter most. Every existing project is one, and a single
/// field that reads differently through <see cref="ProjectConfig.Repos"/> than it did before would
/// change how the fleet works a project nobody touched.
/// </summary>
public class MultiRepoConfigTests : IDisposable
{
    private readonly string _dataRoot;

    public MultiRepoConfigTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "whetstone-multi-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A disk where the named folders exist and the named ones among them are repos.</summary>
    private static RepoLayout.Probe Disk(string[] dirs, string[] repos) => new(
        d => dirs.Contains(Norm(d), StringComparer.OrdinalIgnoreCase),
        d => repos.Contains(Norm(d), StringComparer.OrdinalIgnoreCase),
        d => dirs.Where(x => string.Equals(Path.GetDirectoryName(x), Norm(d), StringComparison.OrdinalIgnoreCase)).ToList());

    private static string Norm(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private static readonly string Root = Norm(Path.Combine(Path.GetTempPath(), "Alpha"));
    private static string Under(string dir) => Path.Combine(Root, dir);

    private static ProjectConfig Multi(params RepoMember[] members) =>
        new() { Name = "alpha", ProjectPath = Root, ConfiguredRepos = members };

    private static RepoMember Member(string dir, bool readOnly = false) =>
        new(dir, "", Array.Empty<string>(), Array.Empty<string>(), readOnly);

    // ---- the set of one ---------------------------------------------------------------------

    [Fact]
    public void A_project_without_repos_is_a_set_of_one_with_its_own_keys()
    {
        var cfg = Load("""
        {
          "projectPath": "C:/Repos/Alpha",
          "baseBranch": "develop",
          "worktreeSetup": ["npm ci"],
          "worktreeCopyFiles": [".env"]
        }
        """);

        Assert.False(cfg.IsMultiRepo);
        var only = Assert.Single(cfg.Repos);
        Assert.Equal("", only.Dir);
        Assert.Equal("develop", only.BaseBranch);
        Assert.Equal(new[] { "npm ci" }, only.WorktreeSetup);
        Assert.Equal(new[] { ".env" }, only.WorktreeCopyFiles);
        Assert.False(only.ReadOnly);
        Assert.Equal(Path.GetFullPath("C:/Repos/Alpha"), only.HubPath(cfg.ProjectPath));
    }

    [Fact]
    public void An_empty_repos_list_is_the_set_of_one_too()
    {
        var cfg = Load("""{ "projectPath": "C:/Repos/Alpha", "repos": [] }""");
        Assert.False(cfg.IsMultiRepo);
        Assert.Single(cfg.Repos);
    }

    [Fact]
    public void A_set_of_one_keeps_its_steward_mode()
    {
        var cfg = Load("""{ "projectPath": "C:/Repos/Alpha", "mergeInsteadOfPr": true, "stewardMode": "full" }""");
        Assert.Equal(StewardMode.Full, cfg.StewardMode);
    }

    // ---- reading repos ----------------------------------------------------------------------

    [Fact]
    public void Repos_are_read_in_order_with_their_own_keys_and_the_project_base_as_fallback()
    {
        var cfg = Load("""
        {
          "projectPath": "C:/Repos/Alpha",
          "baseBranch": "develop",
          "worktreeSetup": ["ignored for members"],
          "repos": [
            { "dir": "alpha-web", "worktreeSetup": ["pnpm install"], "worktreeCopyFiles": [".env.local"] },
            { "dir": "alpha-api", "baseBranch": "main" },
            { "dir": "alpha-infra", "readOnly": true },
            "alpha-docs"
          ]
        }
        """);

        Assert.True(cfg.IsMultiRepo);
        Assert.Equal(new[] { "alpha-web", "alpha-api", "alpha-infra", "alpha-docs" }, cfg.Repos.Select(r => r.Dir));

        var web = cfg.Repos[0];
        Assert.Equal("develop", web.BaseBranch);
        Assert.Equal(new[] { "pnpm install" }, web.WorktreeSetup);
        Assert.Equal(new[] { ".env.local" }, web.WorktreeCopyFiles);
        Assert.False(web.ReadOnly);

        Assert.Equal("main", cfg.Repos[1].BaseBranch);
        Assert.Empty(cfg.Repos[1].WorktreeSetup);          // the project-level keys are the set of one's
        Assert.True(cfg.Repos[2].ReadOnly);
        Assert.Equal("develop", cfg.Repos[3].BaseBranch);  // a bare name is a member with nothing else

        Assert.Equal(Path.GetFullPath("C:/Repos/Alpha/alpha-api"), cfg.Repos[1].HubPath(cfg.ProjectPath));
    }

    [Fact]
    public void A_local_repos_list_replaces_the_committed_one_whole()
    {
        var cfg = Load(
            """{ "projectPath": "C:/Repos/Alpha", "repos": [ { "dir": "alpha-web" }, { "dir": "alpha-api" } ] }""",
            """{ "repos": [ { "dir": "alpha-api", "worktreeSetup": ["dotnet restore"] } ] }""");

        var only = Assert.Single(cfg.Repos);
        Assert.Equal("alpha-api", only.Dir);
        Assert.Equal(new[] { "dotnet restore" }, only.WorktreeSetup);
    }

    [Fact]
    public void A_malformed_entry_is_kept_so_it_is_refused_rather_than_dropped()
    {
        var cfg = Load("""{ "projectPath": "C:/Repos/Alpha", "repos": [ { "dir": "alpha-web" }, 42 ] }""");
        Assert.Equal(2, cfg.Repos.Count);
        Assert.Equal("", cfg.Repos[1].Dir);
    }

    [Fact]
    public void A_project_of_several_repos_has_no_steward_whatever_it_is_set_to()
    {
        var cfg = Load("""{ "projectPath": "C:/Repos/Alpha", "stewardMode": "manage", "repos": [ "alpha-web" ] }""");
        Assert.Equal(StewardMode.Manage, cfg.StewardModeSetting);
        Assert.Equal(StewardMode.Off, cfg.StewardMode);
    }

    // ---- problems: a single repo ------------------------------------------------------------

    [Fact]
    public void A_single_repo_project_on_a_repo_has_no_problems()
    {
        var cfg = new ProjectConfig { Name = "alpha", ProjectPath = Root };
        Assert.Empty(RepoLayout.Problems(cfg, Disk(new[] { Root }, new[] { Root })));
    }

    [Fact]
    public void A_path_that_is_not_a_repo_is_refused()
    {
        var cfg = new ProjectConfig { Name = "alpha", ProjectPath = Root };
        var problem = Assert.Single(RepoLayout.Problems(cfg, Disk(new[] { Root }, Array.Empty<string>())));
        Assert.Contains("not a git repo", problem);
        Assert.Contains("outside any worktree", problem);
    }

    [Fact]
    public void A_path_with_repos_beneath_it_is_named_as_looking_like_several()
    {
        var cfg = new ProjectConfig { Name = "alpha", ProjectPath = Root };
        var disk = Disk(
            new[] { Root, Under("alpha-web"), Under("alpha-api"), Under("notes") },
            new[] { Under("alpha-web"), Under("alpha-api") });

        var problem = Assert.Single(RepoLayout.Problems(cfg, disk));
        Assert.Contains("several repos", problem);
        Assert.Contains("alpha-web", problem);
        Assert.Contains("alpha-api", problem);
        Assert.DoesNotContain("notes", problem);
    }

    [Fact]
    public void An_unconfigured_or_missing_path_is_not_this_checks_business()
    {
        var disk = Disk(Array.Empty<string>(), Array.Empty<string>());
        Assert.Empty(RepoLayout.Problems(new ProjectConfig { Name = "alpha" }, disk));
        Assert.Empty(RepoLayout.Problems(new ProjectConfig { Name = "alpha", ProjectPath = Root }, disk));
    }

    // ---- problems: several repos ------------------------------------------------------------

    private static RepoLayout.Probe Workspace(params string[] repos) =>
        Disk(new[] { Root }.Concat(repos.Select(Under)).ToArray(), repos.Select(Under).ToArray());

    [Fact]
    public void A_well_formed_project_of_several_repos_has_no_problems()
    {
        var cfg = Multi(Member("alpha-web"), Member("alpha-api"), Member("alpha-infra", readOnly: true));
        Assert.Empty(RepoLayout.Problems(cfg, Workspace("alpha-web", "alpha-api", "alpha-infra")));
    }

    [Fact]
    public void Tfvc_and_a_local_merge_are_refused_for_several_repos()
    {
        var disk = Workspace("alpha-web");
        var tfvc = new ProjectConfig { Name = "alpha", ProjectPath = Root, RepoType = RepoTypes.Tfvc, ConfiguredRepos = new[] { Member("alpha-web") } };
        var merge = new ProjectConfig { Name = "alpha", ProjectPath = Root, MergeInsteadOfPr = true, ConfiguredRepos = new[] { Member("alpha-web") } };

        Assert.Contains(RepoLayout.Problems(tfvc, disk), p => p.Contains("tfvc"));
        Assert.Contains(RepoLayout.Problems(merge, disk), p => p.Contains("mergeInsteadOfPr"));
    }

    [Fact]
    public void Several_repos_need_a_project_path_that_is_not_itself_a_repo()
    {
        var none = new ProjectConfig { Name = "alpha", ConfiguredRepos = new[] { Member("alpha-web") } };
        Assert.Contains(RepoLayout.Problems(none, Workspace("alpha-web")), p => p.Contains("needs projectPath"));

        var disk = Disk(new[] { Root, Under("alpha-web") }, new[] { Root, Under("alpha-web") });
        Assert.Contains(RepoLayout.Problems(Multi(Member("alpha-web")), disk), p => p.Contains("is itself a git repo"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("alpha-web/src")]
    [InlineData(@"alpha-web\src")]
    [InlineData("C:/elsewhere")]
    public void A_dir_must_be_one_folder_directly_under_the_project_path(string dir)
    {
        var problem = Assert.Single(RepoLayout.Problems(Multi(Member("alpha-web"), Member(dir)), Workspace("alpha-web")));
        Assert.Contains("entry 2", problem);
    }

    [Fact]
    public void A_folder_listed_twice_is_refused_whatever_its_case()
    {
        var problem = Assert.Single(RepoLayout.Problems(Multi(Member("alpha-web"), Member("Alpha-Web")), Workspace("alpha-web")));
        Assert.Contains("more than once", problem);
    }

    [Fact]
    public void A_member_that_is_missing_or_not_a_repo_is_refused()
    {
        var disk = Disk(new[] { Root, Under("alpha-web"), Under("alpha-api") }, new[] { Under("alpha-web") });
        var problems = RepoLayout.Problems(Multi(Member("alpha-web"), Member("alpha-api"), Member("alpha-infra")), disk);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("alpha-api") && p.Contains("not a git repo"));
        Assert.Contains(problems, p => p.Contains("alpha-infra") && p.Contains("does not exist"));
    }

    [Fact]
    public void Every_member_read_only_is_refused()
    {
        var cfg = Multi(Member("alpha-web", readOnly: true), Member("alpha-api", readOnly: true));
        var problem = Assert.Single(RepoLayout.Problems(cfg, Workspace("alpha-web", "alpha-api")));
        Assert.Contains("readOnly", problem);
    }

    [Fact]
    public void The_real_disk_finds_a_repo_by_its_git_folder_or_file()
    {
        var root = Path.Combine(_dataRoot, "repos", "alpha");
        Directory.CreateDirectory(Path.Combine(root, "alpha-web", ".git"));
        Directory.CreateDirectory(Path.Combine(root, "alpha-api"));
        File.WriteAllText(Path.Combine(root, "alpha-api", ".git"), "gitdir: elsewhere");   // a worktree's .git is a file

        var cfg = new ProjectConfig { Name = "alpha", ProjectPath = root, ConfiguredRepos = new[] { Member("alpha-web"), Member("alpha-api") } };
        Assert.Empty(RepoLayout.Problems(cfg, RepoLayout.Disk));

        var single = new ProjectConfig { Name = "alpha", ProjectPath = root };
        Assert.Contains("several repos", Assert.Single(RepoLayout.Problems(single, RepoLayout.Disk)));
    }

    // ---- what a claim may take --------------------------------------------------------------

    [Fact]
    public void A_claim_passes_over_a_broken_project_and_one_the_worker_cannot_run_yet()
    {
        var bravoRoot = Norm(Path.Combine(Path.GetTempPath(), "Bravo"));
        var configs = new Dictionary<string, ProjectConfig>
        {
            ["alpha"] = Multi(Member("alpha-web")),                                         // well formed, not runnable yet
            ["bravo"] = new() { Name = "bravo", ProjectPath = bravoRoot },                   // a repo
            ["charlie"] = new() { Name = "charlie", ProjectPath = Root },                     // not a repo
        };
        var disk = Disk(new[] { Root, Under("alpha-web"), bravoRoot }, new[] { Under("alpha-web"), bravoRoot });

        Assert.Equal(new[] { "bravo" }, RepoLayout.Claimable(configs.Keys, k => configs[k], disk));
        Assert.NotNull(RepoLayout.NotYetRunnable(configs["alpha"]));
        Assert.Null(RepoLayout.NotYetRunnable(configs["bravo"]));
    }

    // ---- the Setup check row ----------------------------------------------------------------

    private static SetupCheckService.Probe Machine() => new(
        exe => $@"C:\bin\{exe}.exe", _ => null, (_, key) => key == "user.name" ? "Pat" : "pat@example.com",
        () => null, () => null);

    private static readonly (string, string)[] Roles = { ("implement", "claude") };

    private static SetupItem Row(params (string, IReadOnlyList<string>, string?)[] layouts) =>
        Assert.Single(SetupCheckService.Evaluate(Machine(), Roles, Array.Empty<(string, string)>(), null, layouts),
            i => i.Key == "project-repos");

    [Fact]
    public void The_setup_check_names_each_projects_problem_as_required()
    {
        var row = Row(("alpha", Array.Empty<string>(), null), ("bravo", new[] { "'C:/x' is not a git repo." }, null));
        Assert.Equal("missing", row.State);
        Assert.True(row.Required);
        Assert.Contains("bravo: 'C:/x' is not a git repo.", row.Detail);
        Assert.DoesNotContain("alpha", row.Detail);
        Assert.Contains("data/projects/bravo", row.Fix);
    }

    [Fact]
    public void The_setup_check_says_a_well_formed_project_of_several_repos_is_not_run_yet()
    {
        var row = Row(("alpha", Array.Empty<string>(), "not yet"));
        Assert.Equal("warn", row.State);
        Assert.Contains("alpha: not yet", row.Detail);
    }

    [Fact]
    public void The_setup_check_is_ok_when_every_project_is_on_a_repo()
    {
        Assert.Equal("ok", Row(("alpha", Array.Empty<string>(), null)).State);
    }
}
