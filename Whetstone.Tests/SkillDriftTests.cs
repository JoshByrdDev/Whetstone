using System.Diagnostics;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A project's copy of a skill template, and whether it has fallen behind the template. The data
/// root is a git repo of its own here, so the tests that need the template's history have one.
/// </summary>
public class SkillDriftTests : IDisposable
{
    private const string Rel = "skills/implement/skill.md";
    private readonly string _root;
    private readonly DashboardPaths _paths;
    private readonly SkillDriftService _drift;

    private const string V1 = "# {{PROJECT_NAME}} implement\n\nOne.\n\nTwo.\n\nThree.\n\nFour.\n\nFive.\n";

    public SkillDriftTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "whetstone-drift-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_paths.ProjectDir("alpha")));
        File.WriteAllText(_paths.ProjectConfigDefaults("alpha"), "{}");
        _drift = new SkillDriftService(_paths, key => key == "alpha" ? "Alpha" : key);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@localhost");
        // The project folders are not the platform's to track - the same as the real .gitignore.
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "data/projects/\n");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.autocrlf=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o.Trim();
    }

    private void WriteTemplate(string text, string? commit = null)
    {
        var path = _paths.SkillTemplate("implement");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        if (commit is null) return;
        Git("add", "-A");
        Git("commit", "-q", "-m", commit);
    }

    private string Copy => _paths.SkillFile("alpha", "implement");
    private void WriteCopy(string text) { Directory.CreateDirectory(Path.GetDirectoryName(Copy)!); File.WriteAllText(Copy, text); }
    private SkillDriftItem Item() => Assert.Single(_drift.Items("alpha"), i => i.File == Rel);

    [Fact]
    public void A_scaffolded_copy_is_current_and_a_template_change_puts_it_behind()
    {
        WriteTemplate(V1, "First");
        ProjectScaffolder.ScaffoldMissing(_paths, "alpha", "Alpha");
        Assert.Equal("current", Item().Status);
        Assert.Equal("scaffolded", Item().How);

        WriteTemplate(V1.Replace("Three.", "Three, now with a rule."), "Add the rule");
        var item = Item();
        Assert.Equal("behind", item.Status);
        Assert.Contains("+Three, now with a rule.", item.TemplateDiff);
        Assert.Contains("-Three.", item.TemplateDiff);
        Assert.Equal("Add the rule", Assert.Single(item.Commits).Subject);
        Assert.Equal(0, item.Conflicts);
    }

    [Fact]
    public void Editing_the_copy_does_not_hide_a_template_change()
    {
        WriteTemplate(V1, "First");
        ProjectScaffolder.ScaffoldMissing(_paths, "alpha", "Alpha");
        WriteTemplate(V1.Replace("Five.", "Five, changed."), "Template change");
        // Edited after the template moved - a date comparison would call this copy current.
        WriteCopy(File.ReadAllText(Copy).Replace("One.", "One, our way."));
        Assert.Equal("behind", Item().Status);
    }

    [Fact]
    public void Apply_merges_the_template_change_and_keeps_the_projects_own_edit()
    {
        WriteTemplate(V1, "First");
        ProjectScaffolder.ScaffoldMissing(_paths, "alpha", "Alpha");
        WriteCopy(File.ReadAllText(Copy).Replace("One.", "One, our way."));
        WriteTemplate(V1.Replace("Five.", "Five, changed."), "Template change");

        var result = _drift.Apply("alpha", Rel);
        Assert.True(result.Ok);
        var text = File.ReadAllText(Copy);
        Assert.Contains("One, our way.", text);
        Assert.Contains("Five, changed.", text);
        Assert.StartsWith("# Alpha implement", text);   // tokens filled on both sides of the merge
        Assert.Equal("current", Item().Status);
        Assert.Equal("merged", Item().How);
    }

    [Fact]
    public void A_conflicting_merge_writes_nothing_and_hands_back_the_markers()
    {
        WriteTemplate(V1, "First");
        ProjectScaffolder.ScaffoldMissing(_paths, "alpha", "Alpha");
        WriteCopy(File.ReadAllText(Copy).Replace("Three.", "Three, ours."));
        var before = File.ReadAllText(Copy);
        WriteTemplate(V1.Replace("Three.", "Three, theirs."), "Template change");

        Assert.Equal(1, Item().Conflicts);
        var result = _drift.Apply("alpha", Rel);
        Assert.False(result.Ok);
        Assert.Equal(1, result.Conflicts);
        Assert.Contains("<<<<<<< this project's copy", result.Preview);
        Assert.Equal(before, File.ReadAllText(Copy));
        Assert.Equal("behind", Item().Status);
    }

    [Fact]
    public void Apply_keeps_a_crlf_copy_crlf()
    {
        WriteTemplate(V1, "First");
        ProjectScaffolder.ScaffoldMissing(_paths, "alpha", "Alpha");
        WriteCopy(File.ReadAllText(Copy).Replace("\n", "\r\n"));
        WriteTemplate(V1.Replace("Five.", "Five, changed."), "Template change");

        Assert.True(_drift.Apply("alpha", Rel).Ok);
        var text = File.ReadAllText(Copy);
        Assert.Contains("Five, changed.\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void Marking_it_synced_moves_the_baseline_without_touching_the_copy()
    {
        WriteTemplate(V1, "First");
        ProjectScaffolder.ScaffoldMissing(_paths, "alpha", "Alpha");
        var before = File.ReadAllText(Copy);
        WriteTemplate(V1.Replace("Five.", "Five, changed."), "Template change");

        Assert.Null(_drift.MarkSynced("alpha", Rel, "skipped"));
        Assert.Equal(before, File.ReadAllText(Copy));
        Assert.Equal("current", Item().Status);
        Assert.Equal("skipped", Item().How);

        // ...and the next change raises it again.
        WriteTemplate(V1.Replace("Five.", "Five, changed twice."), "Another");
        Assert.Equal("behind", Item().Status);
    }

    [Fact]
    public void A_copy_from_before_baselines_existed_finds_its_own_template_version()
    {
        WriteTemplate(V1, "First");
        WriteCopy(SkillDriftService.Fill(V1, "alpha", "Alpha"));       // scaffolded long ago, no record
        WriteTemplate(V1.Replace("Two.", "Two, newer."), "Second");

        var item = Item();
        Assert.Equal("behind", item.Status);
        Assert.Equal("matched", item.How);
        Assert.Equal("Second", Assert.Single(item.Commits).Subject);
        Assert.Contains("+Two, newer.", item.TemplateDiff);
    }

    [Fact]
    public void A_copy_that_is_already_the_template_is_simply_current()
    {
        WriteTemplate(V1);   // not even committed
        WriteCopy(SkillDriftService.Fill(V1, "alpha", "Alpha"));
        Assert.Equal("current", Item().Status);
    }

    [Fact]
    public void A_copy_that_matches_no_version_is_untracked_until_marked()
    {
        WriteTemplate(V1, "First");
        WriteCopy("# Something of our own\n");

        var item = Item();
        Assert.Equal("untracked", item.Status);
        Assert.Contains("+# Alpha implement", item.CopyDiff);
        Assert.Equal(1, Assert.Single(_drift.Summary(), s => s.Project == "alpha").Untracked);

        Assert.Null(_drift.MarkSynced("alpha", Rel, "ported"));
        Assert.Equal("current", Item().Status);
    }

    [Fact]
    public void The_summary_counts_what_is_behind()
    {
        WriteTemplate(V1, "First");
        ProjectScaffolder.ScaffoldMissing(_paths, "alpha", "Alpha");
        WriteTemplate(V1 + "\nSix.\n", "More");
        var s = Assert.Single(_drift.Summary(), x => x.Project == "alpha");
        Assert.Equal(1, s.Behind);
        Assert.Equal(0, s.Untracked);
    }

    [Fact]
    public void A_file_outside_the_tracked_set_is_refused()
    {
        Assert.NotNull(_drift.MarkSynced("alpha", "../../secrets.md", "ported"));
        Assert.False(_drift.Apply("alpha", "project.json").Ok);
    }
}
