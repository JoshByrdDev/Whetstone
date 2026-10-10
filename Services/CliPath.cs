using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>
/// Turns an agent CLI's bare name into something <see cref="System.Diagnostics.Process"/> can start
/// without a shell.
/// <para>
/// With <c>UseShellExecute = false</c> on Windows, a bare <c>claude</c> is only ever looked up as
/// <c>claude.exe</c>. An npm install puts no such file on PATH - only a <c>claude.cmd</c> shim that
/// calls the real executable inside <c>node_modules</c> - so the start fails with "file not found"
/// before the CLI ever runs, and every caller that swallows the exception looks like a button that
/// does nothing. Running the shim itself would mean going through <c>cmd.exe</c>, which re-parses
/// every argument and mangles the quotes in a JSON schema, so the shim is read for the path it
/// would have run instead.
/// </para>
/// <para>
/// Anything this cannot resolve comes back unchanged, which is exactly the behaviour before it existed.
/// </para>
/// </summary>
public static class CliPath
{
    /// <summary>The line an npm shim ends on: <c>"%dp0%\node_modules\...\bin\claude.exe" %*</c>.</summary>
    private static readonly Regex ShimTarget = new(@"""%~?dp0%?\\(?<rel>[^""]+\.exe)""", RegexOptions.IgnoreCase);

    /// <param name="searchPath">A PATH-style list to search instead of the process's own; for tests.</param>
    public static string Resolve(string name, string? searchPath = null)
    {
        if (!OperatingSystem.IsWindows() || Path.IsPathRooted(name) || Path.HasExtension(name)) return name;

        var dirs = (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dir in dirs)
        {
            try
            {
                var exe = Path.Combine(dir, name + ".exe");
                if (File.Exists(exe)) return exe;

                var shim = Path.Combine(dir, name + ".cmd");
                if (File.Exists(shim) && ShimTarget.Match(File.ReadAllText(shim)) is { Success: true } m)
                {
                    var target = Path.GetFullPath(Path.Combine(dir, m.Groups["rel"].Value));
                    if (File.Exists(target)) return target;
                }
            }
            catch { /* a malformed PATH entry is just not the one */ }
        }
        return name;
    }
}
