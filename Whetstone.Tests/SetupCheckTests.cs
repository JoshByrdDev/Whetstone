using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

public class SetupCheckTests
{
    private static readonly (string, string)[] AllClaude = { ("implement", "claude"), ("qa-test", "claude"), ("analysis", "claude") };

    /// <summary>A machine with everything, unless a test says otherwise.</summary>
    private static SetupCheckService.Probe Machine(
        string[]? missing = null, string[]? installedLater = null,
        Func<string?, string, string?>? git = null, string? narrator = null, string? refused = null)
    {
        missing ??= Array.Empty<string>();
        installedLater ??= Array.Empty<string>();
        return new SetupCheckService.Probe(
            exe => missing.Contains(exe) || installedLater.Contains(exe) ? null : $@"C:\bin\{exe}.exe",
            exe => installedLater.Contains(exe) ? $@"C:\new\{exe}.exe" : null,
            git ?? ((_, key) => key == "user.name" ? "Pat" : "pat@example.com"),
            () => narrator,
            () => refused);
    }

    private static SetupItem Item(IReadOnlyList<SetupItem> items, string key) => Assert.Single(items, i => i.Key == key);

    [Fact]
    public void A_complete_machine_is_all_ok()
    {
        var items = SetupCheckService.Evaluate(Machine(), AllClaude, new[] { ("alpha", @"C:\repos\alpha") });
        Assert.All(items, i => Assert.Equal("ok", i.State));
        Assert.DoesNotContain(items, i => i.Key == "heartbeat");
        Assert.DoesNotContain(items, i => i.Key == "npx"); // only listed when Node is there without it
    }

    [Fact]
    public void A_missing_cli_names_the_roles_that_run_on_it()
    {
        var roles = new[] { ("implement", "codex"), ("qa-test", "claude"), ("refine", "codex") };
        var items = SetupCheckService.Evaluate(Machine(missing: new[] { "codex" }), roles, Array.Empty<(string, string)>());

        var codex = Item(items, "codex");
        Assert.Equal("missing", codex.State);
        Assert.Contains("implement, refine", codex.Detail);
        Assert.NotNull(codex.Fix);
        Assert.Equal("ok", Item(items, "claude").State);
        Assert.DoesNotContain(items, i => i.Key == "gemini"); // no role runs on it
    }

    [Fact]
    public void A_tool_installed_after_start_asks_for_a_restart_not_an_install()
    {
        var items = SetupCheckService.Evaluate(Machine(installedLater: new[] { "node" }), AllClaude, Array.Empty<(string, string)>());
        var node = Item(items, "node");
        Assert.Equal("warn", node.State);
        Assert.Contains("Restart", node.Fix);
    }

    [Fact]
    public void Git_identity_is_checked_in_each_project_repo()
    {
        // bravo has no identity of its own and none it inherits.
        string? Git(string? repo, string key) => repo == @"C:\repos\bravo" ? null : key == "user.name" ? "Pat" : "pat@example.com";
        var items = SetupCheckService.Evaluate(Machine(git: Git), AllClaude,
            new[] { ("alpha", @"C:\repos\alpha"), ("bravo", @"C:\repos\bravo") });

        var identity = Item(items, "git-identity");
        Assert.Equal("missing", identity.State);
        Assert.Contains("bravo", identity.Detail);
        Assert.DoesNotContain("alpha", identity.Detail);
    }

    [Fact]
    public void With_no_projects_the_global_identity_is_what_counts()
    {
        var asked = new List<string?>();
        string? Git(string? repo, string key) { asked.Add(repo); return null; }
        var items = SetupCheckService.Evaluate(Machine(git: Git), AllClaude, Array.Empty<(string, string)>());

        Assert.Equal("missing", Item(items, "git-identity").State);
        Assert.All(asked, r => Assert.Null(r));
    }

    [Fact]
    public void No_git_means_no_identity_row()
    {
        var items = SetupCheckService.Evaluate(Machine(missing: new[] { "git" }), AllClaude, Array.Empty<(string, string)>());
        Assert.Equal("missing", Item(items, "git").State);
        Assert.DoesNotContain(items, i => i.Key == "git-identity");
    }

    [Fact]
    public void Node_without_npx_is_called_out()
    {
        var items = SetupCheckService.Evaluate(Machine(missing: new[] { "npx" }), AllClaude, Array.Empty<(string, string)>());
        Assert.Equal("missing", Item(items, "npx").State);
    }

    [Fact]
    public void The_narrator_is_optional()
    {
        var items = SetupCheckService.Evaluate(Machine(narrator: "the narrator is not installed"), AllClaude, Array.Empty<(string, string)>());
        var narrator = Item(items, "narrator");
        Assert.Equal("warn", narrator.State);
        Assert.False(narrator.Required);
        Assert.Contains("npm install", narrator.Fix);
    }

    [Fact]
    public void The_heartbeat_refusal_comes_first()
    {
        var items = SetupCheckService.Evaluate(Machine(refused: "'codex' command not found in PATH"), AllClaude, Array.Empty<(string, string)>());
        Assert.Equal("heartbeat", items[0].Key);
        Assert.Contains("'codex' command not found", items[0].Detail);
    }

    [Fact]
    public void Project_folders_without_a_repo_or_a_remote_are_flagged()
    {
        var folders = new[]
        {
            ("alpha", new ProjectRepo.State(true, "https://example.com/alpha.git")),
            ("bravo", new ProjectRepo.State(true, null)),
        };
        var item = Item(SetupCheckService.Evaluate(Machine(), AllClaude, Array.Empty<(string, string)>(), folders), "project-folders");
        Assert.Equal("warn", item.State);
        Assert.False(item.Required);
        Assert.Contains("bravo", item.Detail);
        Assert.DoesNotContain("alpha", item.Detail);
        Assert.Contains("data/projects/bravo", item.Fix);

        var unversioned = Item(SetupCheckService.Evaluate(Machine(), AllClaude, Array.Empty<(string, string)>(),
            new[] { ("alpha", new ProjectRepo.State(false, null)) }), "project-folders");
        Assert.Contains("No git history", unversioned.Detail);

        var fine = Item(SetupCheckService.Evaluate(Machine(), AllClaude, Array.Empty<(string, string)>(),
            new[] { ("alpha", new ProjectRepo.State(true, "https://example.com/alpha.git")) }), "project-folders");
        Assert.Equal("ok", fine.State);
    }

    [Fact]
    public void FindOnPath_follows_path_order_and_pathext()
    {
        var root = Path.Combine(Path.GetTempPath(), "setup-check-" + Guid.NewGuid().ToString("N"));
        var first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(second, "tool.exe"), "");
            File.WriteAllText(Path.Combine(first, "tool.cmd"), "");
            File.WriteAllText(Path.Combine(second, "shim.ps1"), "");
            var path = string.Join(Path.PathSeparator, first, second);

            Assert.Equal(Path.Combine(first, "tool.cmd"), SetupCheckService.FindOnPath("tool", path, ".EXE;.CMD"), ignoreCase: true);
            Assert.Equal(Path.Combine(second, "shim.ps1"), SetupCheckService.FindOnPath("shim", path, ".EXE;.CMD"), ignoreCase: true);
            Assert.Null(SetupCheckService.FindOnPath("absent", path, ".EXE;.CMD"));
        }
        finally { Directory.Delete(root, true); }
    }
}
