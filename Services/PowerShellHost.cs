namespace Whetstone.Services;

/// <summary>
/// The PowerShell that starts the fleet's own scripts: the heartbeat, the steward, a project's
/// resolver and merge flags, the notifiers. Every place that starts one asks here rather than
/// naming a shell, so there is one answer.
/// <para>
/// It is PowerShell 7 (<c>pwsh</c>) on every machine, Windows and macOS alike. Windows PowerShell
/// 5.1 is not a fallback: where 7 cannot be found this answers <c>pwsh</c> by name, so the start
/// fails loudly rather than running the fleet on a shell it no longer supports. The Setup check
/// says 7 is missing before it comes to that. The scripts' own launches ask the same question in
/// <c>heartbeat-common.ps1</c> (<c>Get-PsHost</c>), and the two must agree.
/// </para>
/// </summary>
public static class PowerShellHost
{
    private static readonly Lazy<string?> InstalledPwsh = new(() => FindPwsh(Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));

    /// <summary>The program to start, as a path where one was found.</summary>
    public static string Exe => Choose(InstalledPwsh.Value);

    public static string Choose(string? pwsh) => pwsh ?? "pwsh";

    /// <summary>
    /// PowerShell 7 on PATH, or in its standard install folder. The folder is asked as well because a
    /// board started before 7 was installed still has the PATH it started with.
    /// </summary>
    public static string? FindPwsh(string? searchPath, string? programFiles)
    {
        if (!OperatingSystem.IsWindows()) return null;   // "pwsh" by name is the answer there
        var onPath = CliPath.Resolve("pwsh", searchPath ?? "");
        if (Path.IsPathRooted(onPath)) return onPath;
        if (string.IsNullOrEmpty(programFiles)) return null;
        var standard = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(standard) ? standard : null;
    }
}
