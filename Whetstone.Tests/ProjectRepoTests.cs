using System.Diagnostics;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

public class ProjectRepoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "project-repo-" + Guid.NewGuid().ToString("N"), "alpha");

    public ProjectRepoTests()
    {
        // A project folder as the scaffolder leaves it: config with a secret in it, and skills.
        Directory.CreateDirectory(Path.Combine(_dir, "skills", "implement"));
        File.WriteAllText(Path.Combine(_dir, "project.json"), """{ "tracker": { "token": "secret" } }""");
        File.WriteAllText(Path.Combine(_dir, "project.defaults.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "skills", "implement", "skill.md"), "# implement");
        File.WriteAllText(Path.Combine(_dir, "improvement-suggestions.md"), "runtime");
    }

    public void Dispose()
    {
        try
        {
            // git marks its object files read-only, which Directory.Delete refuses on Windows.
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(_dir)!, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path.GetDirectoryName(_dir)!, true);
        }
        catch { }
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    [Fact]
    public void A_new_folder_becomes_a_repo_with_a_baseline_that_leaves_the_secrets_out()
    {
        var result = ProjectRepo.Initialize(_dir);

        Assert.True(result.Created);
        Assert.Null(result.Error);
        var committed = Git("ls-files").Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains(".gitignore", committed);
        Assert.Contains("project.defaults.json", committed);
        Assert.Contains("skills/implement/skill.md", committed);
        Assert.DoesNotContain("project.json", committed);
        Assert.DoesNotContain("improvement-suggestions.md", committed);
        Assert.Equal("", Git("status", "--porcelain")); // everything else is either committed or ignored
    }

    [Fact]
    public void A_folder_that_is_already_a_repo_is_left_alone()
    {
        Git("init", "--quiet");
        var result = ProjectRepo.Initialize(_dir);

        Assert.False(result.Created);
        Assert.Null(result.Error);
        Assert.False(File.Exists(Path.Combine(_dir, ".gitignore")));
    }

    [Fact]
    public void An_ignore_file_that_misses_project_json_stops_the_commit()
    {
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "logs/\n");
        var result = ProjectRepo.Initialize(_dir);

        Assert.True(result.Created);
        Assert.Contains("project.json", result.Error);
        Assert.Equal("", Git("log", "--oneline", "-1"));        // nothing in history
        Assert.Equal("", Git("diff", "--cached", "--name-only")); // and nothing left staged
    }

    [Fact]
    public void Read_reports_the_folder_and_its_remote()
    {
        Assert.Equal(new ProjectRepo.State(false, null), ProjectRepo.Read(_dir));

        ProjectRepo.Initialize(_dir);
        Assert.Equal(new ProjectRepo.State(true, null), ProjectRepo.Read(_dir));

        Git("remote", "add", "upstream", "https://example.com/other.git");
        Assert.Equal(new ProjectRepo.State(true, null), ProjectRepo.Read(_dir)); // only origin backs it up

        Git("remote", "add", "origin", "https://example.com/alpha-project.git");
        Assert.Equal(new ProjectRepo.State(true, "https://example.com/alpha-project.git"), ProjectRepo.Read(_dir));
    }
}
