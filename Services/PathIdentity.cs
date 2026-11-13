namespace Whetstone.Services;

/// <summary>
/// When two paths name the same folder. Git answers with real paths - every symlink on the way
/// resolved - while a project's config, a worktree root and a ticket's recorded worktree are kept as
/// they were written. On a Mac the two differ more often than not: the temp folder is
/// <c>/var/...</c>, a link to <c>/private/var/...</c>, and a folder reached through a link of a
/// person's own is the same folder too. Compared as text, a worktree the fleet made reads as
/// "outside this project's worktree root, so it may be a person's", and is refused.
/// <para>
/// So off Windows a path is compared by where it leads. On Windows it is compared exactly as it
/// always was - full path, either separator, any case - because that is what every board there has
/// been running on.
/// </para>
/// </summary>
public static class PathIdentity
{
    /// <summary>
    /// The path in the form two paths are compared in: full, no trailing separator, and off Windows
    /// with every link in the part of it that exists resolved. A part that does not exist yet is kept
    /// as written below the last part that does.
    /// </summary>
    public static string Canonical(string path) => Canonical(path, depth: 0);

    // A link's target can itself run through a link (a link to /var/... is a link to /private/var/...),
    // so a target is made canonical in turn. Links that loop stop at the depth limit, as written.
    private const int MaxLinkDepth = 32;

    private static string Canonical(string path, int depth)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Replace('/', Path.DirectorySeparatorChar)));
        if (OperatingSystem.IsWindows()) return full;

        var current = Path.GetPathRoot(full) ?? "/";
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, part);
            try
            {
                var info = new FileInfo(next);
                if (depth < MaxLinkDepth && info.LinkTarget is not null
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    next = Canonical(target.FullName, depth + 1);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* kept as written */ }
            current = next;
        }
        return current;
    }

    /// <summary>Whether the two name the same folder. Case is ignored, as it always has been here.</summary>
    public static bool Same(string a, string b) =>
        string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var p = Canonical(path) + Path.DirectorySeparatorChar;
        var r = Canonical(root) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
