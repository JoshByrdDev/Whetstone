using System.Text.Json;

namespace Whetstone.Services;

/// <summary>
/// The PowerShell that starts the fleet's own scripts: the heartbeat, the steward, a project's
/// resolver and merge flags, the notifiers. Every place that starts one asks here rather than
/// naming <c>powershell</c>, so there is one answer and one switch.
/// <para>
/// <c>psHost</c> in the global config: <c>auto</c> (the default) is PowerShell 7 where it is
/// installed and Windows PowerShell where it is not - the scripts run on both, and macOS has only
/// the first. <c>powershell</c> pins Windows PowerShell 5.1, which is the way back if 7 misbehaves;
/// <c>pwsh</c> asks for 7 even where it cannot be found, and fails loudly rather than falling back.
/// Read at every start, so a change reaches the next launch; the scripts' own launches read the same
/// key in <c>heartbeat-common.ps1</c> (<c>Get-PsHost</c>), and the two must agree.
/// </para>
/// </summary>
public static class PowerShellHost
{
    public const string Key = "psHost";

    private static string[] _configFiles = [];
    private static readonly Lazy<string?> InstalledPwsh = new(() => FindPwsh(Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));

    /// <summary>Where the setting is read from: the global defaults, then the local override.</summary>
    public static void UseConfig(DashboardPaths paths) =>
        _configFiles = [paths.GlobalConfigDefaultsPath, paths.GlobalConfigPath];

    /// <summary>The program to start, as a path where one was found.</summary>
    public static string Exe => Choose(Setting(), InstalledPwsh.Value, OperatingSystem.IsWindows());

    public static string Choose(string? setting, string? pwsh, bool windows) =>
        (setting ?? "").Trim().ToLowerInvariant() switch
        {
            "powershell" => "powershell",
            "pwsh" => pwsh ?? "pwsh",
            _ => pwsh ?? (windows ? "powershell" : "pwsh"),
        };

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

    private static string? Setting()
    {
        string? value = null;
        foreach (var file in _configFiles)
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty(Key, out var v) && v.ValueKind == JsonValueKind.String)
                    value = v.GetString();
            }
            catch { /* a malformed config file is read as not saying */ }
        }
        return value;
    }
}
