using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Two paths to one folder are one folder. Git answers with real paths; a project's config keeps the
/// path a person wrote, which on a Mac is often a link away from it.
/// </summary>
public class PathIdentityTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "path-identity-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void Separators_trailing_slashes_and_case_do_not_make_two_folders()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Repo")).FullName;

        Assert.True(PathIdentity.Same(dir, dir + Path.DirectorySeparatorChar));
        Assert.True(PathIdentity.Same(dir, dir.ToUpperInvariant()));
        Assert.False(PathIdentity.Same(dir, Path.Combine(_root, "Other")));
    }

    [Fact]
    public void A_folder_reached_through_a_link_is_the_same_folder()
    {
        if (OperatingSystem.IsWindows()) return;   // compared as written there, as it always was
        var real = Directory.CreateDirectory(Path.Combine(_root, "real", "worktrees")).FullName;
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "real"));

        Assert.True(PathIdentity.Same(Path.Combine(link, "worktrees"), real));
        Assert.True(PathIdentity.IsUnder(Path.Combine(real, "t-1"), Path.Combine(link, "worktrees")));
    }

    [Fact]
    public void A_path_not_made_yet_is_kept_as_written_below_what_exists()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Directory.CreateDirectory(Path.Combine(_root, "real")).FullName);

        Assert.Equal(
            Path.Combine(PathIdentity.Canonical(Path.Combine(_root, "real")), "not", "yet"),
            PathIdentity.Canonical(Path.Combine(_root, "link", "not", "yet")));
    }

    [Fact]
    public void Under_means_inside_or_the_root_itself_and_never_a_name_that_starts_the_same()
    {
        var root = Path.Combine(_root, "worktrees");

        Assert.True(PathIdentity.IsUnder(Path.Combine(root, "alpha", "t-1"), root));
        Assert.True(PathIdentity.IsUnder(root, root));
        Assert.False(PathIdentity.IsUnder(root + "-old", root));
        Assert.False(PathIdentity.IsUnder(Path.Combine(_root, "x"), ""));
    }
}
