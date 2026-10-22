using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>
/// A project folder's own git repository: <c>data/projects/&lt;key&gt;/</c> is gitignored by
/// Whetstone and versioned by itself, pushed to a private remote of its owner's choosing, and never
/// shared with another machine (see "Why project folders are not in this repo" in data/README.md).
/// <para>
/// Adding a project used to leave that to the person, with instructions to copy a .gitignore "from
/// any existing project folder" - which a new machine does not have. The first <c>git add -A</c>
/// then swept up <c>project.json</c> and its tracker token. So a new project's folder is now made a
/// repo with the ignore file already in it, and the only step left to a person is the remote.
/// </para>
/// </summary>
public static class ProjectRepo
{
    /// <summary>
    /// What a project folder never commits: its machine-local config and secrets, and what the fleet
    /// writes there at runtime. The same list every hand-made project folder carried.
    /// </summary>
    public const string GitIgnore = """
        # Machine-local: the repo path on this computer, tracker token, QA credentials.
        project.json
        *.bak

        # Written by the fleet at runtime.
        events.jsonl
        logs/
        *.lock
        *.stop
        improvement-suggestions.md
        reflection-keys.txt
        notes-maintenance.stamp
        mcp-config.json
        mcp-config.*.json

        # Unregistered on this machine only.
        .disabled

        """;

    /// <param name="Versioned">The folder has its own .git.</param>
    /// <param name="Remote">origin's URL, or null when nothing would back it up.</param>
    public sealed record State(bool Versioned, string? Remote);

    /// <summary>What happened when a new project's folder was made a repo.</summary>
    /// <param name="Error">Why it could not be, or null. The project is still created either way.</param>
    public sealed record InitResult(bool Created, string? Error);

    private static readonly Regex OriginUrl = new(
        @"^\s*\[remote\s+""origin""\]\s*$(?:\r?\n(?!\s*\[).*)*?\r?\n\s*url\s*=\s*(?<url>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The folder's repo state, read from its .git/config. Starts no process.</summary>
    public static State Read(string projectDir)
    {
        var config = Path.Combine(projectDir, ".git", "config");
        if (!File.Exists(config)) return new State(Directory.Exists(Path.Combine(projectDir, ".git")), null);
        try
        {
            var m = OriginUrl.Match(File.ReadAllText(config));
            return new State(true, m.Success ? m.Groups["url"].Value : null);
        }
        catch { return new State(true, null); }
    }

    /// <summary>
    /// Write the .gitignore, <c>git init</c>, and commit what is there as a baseline. Does nothing
    /// to a folder that is already a repo. Never throws: a project that could not be versioned is
    /// still a project, and the error says what to do by hand.
    /// </summary>
    public static InitResult Initialize(string projectDir)
    {
        try
        {
            if (Directory.Exists(Path.Combine(projectDir, ".git"))) return new InitResult(false, null);

            var ignore = Path.Combine(projectDir, ".gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, GitIgnore);

            if (Git(projectDir, "init", "--quiet") is { } initError) return new InitResult(false, initError);
            if (Git(projectDir, "add", "-A") is { } addError) return new InitResult(true, addError);

            // The whole point of the ignore file. If project.json is staged anyway (a .gitignore that
            // was already there and left it out), stop before it is in history rather than after.
            if (Output(projectDir, "diff", "--cached", "--name-only", "--", "project.json") is { Length: > 0 })
            {
                Git(projectDir, "reset", "--quiet");
                return new InitResult(true,
                    "project.json would have been committed - the folder's .gitignore does not list it. Nothing was committed.");
            }

            // The owner's identity when git has one; otherwise a stand-in for this one commit only, so a
            // machine without user.name still gets its baseline. Nothing is written to the repo's config.
            var who = Output(projectDir, "config", "user.email") is { Length: > 0 }
                ? Array.Empty<string>()
                : new[] { "-c", "user.name=Whetstone", "-c", "user.email=whetstone@localhost" };
            var commit = who.Concat(new[] { "commit", "--quiet", "-m", "Baseline: project folder as scaffolded" }).ToArray();
            return Git(projectDir, commit) is { } commitError
                ? new InitResult(true, commitError)
                : new InitResult(true, null);
        }
        catch (Exception ex)
        {
            return new InitResult(false, ex.Message);
        }
    }

    /// <summary>Run git in the folder; null on success, else what it said.</summary>
    private static string? Git(string dir, params string[] args)
    {
        var (code, _, err) = Run(dir, args);
        return code == 0 ? null : $"git {string.Join(' ', args)} failed: {(err.Length > 0 ? err : $"exit {code}")}";
    }

    private static string? Output(string dir, params string[] args)
    {
        var (code, output, _) = Run(dir, args);
        return code == 0 ? output : null;
    }

    private static (int Code, string Output, string Error) Run(string dir, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } return (-1, "", "timed out"); }
        return (p.ExitCode, output.Result.Trim(), error.Result.Trim());
    }
}
