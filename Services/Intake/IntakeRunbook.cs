using System.Diagnostics;

namespace Whetstone.Services.Intake;

/// <summary>A project's runbook, read from its base at filing time. A seam, so a test can hand one in.</summary>
public interface IIntakeRunbook
{
    /// <summary>The file's text on the project's base, or null when it is not there.</summary>
    Task<string?> ReadAsync(string project, string path, CancellationToken ct);
}

/// <summary>
/// The runbook as the project's base holds it: <c>git show &lt;base-ref&gt;:&lt;path&gt;</c> in the repo the
/// path names, never the working tree, which may be on someone's branch. The base ref is
/// <see cref="TicketGitService.ResolveBaseRefAsync"/>'s, so it is the same rule as everywhere else.
/// On a project of several repos the path starts with its repo's folder.
/// </summary>
public sealed class GitIntakeRunbook(ProjectRegistry registry, TicketGitService git) : IIntakeRunbook
{
    public async Task<string?> ReadAsync(string project, string path, CancellationToken ct)
    {
        var cfg = registry.GetProjectConfig(project);
        if (string.IsNullOrWhiteSpace(cfg.ProjectPath) || string.IsNullOrWhiteSpace(path)) return null;
        var rel = path.Replace('\\', '/').TrimStart('/');
        var member = cfg.Repos.FirstOrDefault(m => m.Dir.Length == 0)
            ?? cfg.Repos.FirstOrDefault(m => rel.StartsWith(m.Dir.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase));
        if (member is null) return null;
        if (member.Dir.Length > 0) rel = rel[(member.Dir.Length + 1)..];
        if (rel.Contains("..")) return null;
        var repo = member.HubPath(cfg.ProjectPath);
        var baseBranch = string.IsNullOrWhiteSpace(member.BaseBranch) ? cfg.BaseBranch : member.BaseBranch;
        if (await git.ResolveBaseRefAsync(repo, cfg, baseBranch, ct) is not { } baseRef) return null;

        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-C", repo, "show", $"{baseRef}:{rel}" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return null;
        var text = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return p.ExitCode == 0 ? text : null;
    }
}
