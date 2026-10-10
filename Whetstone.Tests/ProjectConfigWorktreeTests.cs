using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Where a project's agent worktrees live. The derived default has to land beside the repo rather
/// than inside it: a worktree nested under the repo would surface as untracked files in the human's
/// own checkout, which is the exact interference the worktree model exists to remove.
/// </summary>
public class ProjectConfigWorktreeTests
{
    private static string Root(string projectPath, string configured = "", string name = "alpha") =>
        new ProjectConfig { Name = name, ProjectPath = projectPath, WorktreeRoot = configured }
            .ResolvedWorktreeRoot;

    [Fact]
    public void Derives_a_sibling_of_the_repo_keyed_by_project()
    {
        var expected = Path.Combine(Path.GetFullPath(@"C:\Projects"), ".worktrees", "alpha");
        Assert.Equal(expected, Root(@"C:\Projects\Alpha"));
    }

    [Fact]
    public void Derived_root_is_never_inside_the_repo()
    {
        var repo = Path.GetFullPath(@"C:\Projects\Alpha");
        Assert.DoesNotContain(repo, Root(@"C:\Projects\Alpha"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Projects\Alpha\")]
    [InlineData(@"C:\Projects\Alpha/")]
    public void Trailing_separators_do_not_shift_the_root_a_level_down(string projectPath)
    {
        // Without trimming, GetDirectoryName returns the repo itself and every worktree lands inside it.
        Assert.Equal(Root(@"C:\Projects\Alpha"), Root(projectPath));
    }

    [Fact]
    public void An_explicit_root_wins_over_the_derived_one()
        => Assert.Equal(Path.GetFullPath(@"D:\wt"), Root(@"C:\Projects\Alpha", @"D:\wt"));

    [Fact]
    public void Two_projects_in_one_folder_do_not_share_a_root()
        => Assert.NotEqual(Root(@"C:\Projects\Alpha", name: "alpha"),
                           Root(@"C:\Projects\Other", name: "other"));

    [Fact]
    public void Unconfigured_project_yields_no_root_rather_than_a_guess()
        => Assert.Equal("", Root(""));
}
