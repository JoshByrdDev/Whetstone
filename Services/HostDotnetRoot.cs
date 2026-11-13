namespace Whetstone.Services;

/// <summary>
/// The .NET location variables this app was handed by whatever started it, which its children must
/// not inherit.
/// <para>
/// <c>dotnet run</c> gives the app <c>DOTNET_ROOT_&lt;ARCH&gt;</c>, naming the .NET it runs on. On
/// macOS (and Linux) an architecture's own root outranks plain <c>DOTNET_ROOT</c>, so every
/// framework-dependent .NET program the board starts inherits it - and PowerShell from Homebrew is
/// one, built on a newer .NET than the board and naming its own <c>DOTNET_ROOT</c> in its wrapper
/// script. Handed the board's root instead, it exits with "You must install or update .NET" before
/// running a line: the heartbeat, the steward and every notifier die at once, and the board says
/// only that the heartbeat is not running. On Windows PowerShell 7 carries its own .NET, which is
/// why this is off Windows only.
/// </para>
/// <para>
/// Only a root that is this app's own runtime goes. One that names somewhere else was set by a
/// person, for a reason this app cannot see. Plain <c>DOTNET_ROOT</c> always stays: on a machine
/// where .NET is not in its default folder, it is how a tool like <c>dotnet-ef</c> finds a runtime.
/// </para>
/// </summary>
public static class HostDotnetRoot
{
    private static readonly string[] Arches = ["ARM64", "X64", "X86"];

    /// <summary>The variables to clear: each architecture root that names <paramref name="runtimeRoot"/>.</summary>
    public static IReadOnlyList<string> Injected(Func<string, string?> env, string? runtimeRoot)
    {
        if (string.IsNullOrEmpty(runtimeRoot)) return [];
        var own = Normalize(runtimeRoot);
        return Arches
            .Select(a => $"DOTNET_ROOT_{a}")
            .Where(name => env(name) is { Length: > 0 } value && Normalize(value) == own)
            .ToList();
    }

    /// <summary>Clears them from this process, so nothing it starts from now on inherits them.</summary>
    public static void ClearInjected()
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var name in Injected(Environment.GetEnvironmentVariable, RuntimeRoot()))
            Environment.SetEnvironmentVariable(name, null);
    }

    /// <summary>
    /// The folder this app's runtime lives in: three up from the core library, which sits in
    /// <c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c>.
    /// </summary>
    public static string? RuntimeRoot()
    {
        var core = Path.GetDirectoryName(typeof(object).Assembly.Location);
        return core is null ? null : Path.GetFullPath(Path.Combine(core, "..", "..", ".."));
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return path.TrimEnd(Path.DirectorySeparatorChar); }
    }
}
