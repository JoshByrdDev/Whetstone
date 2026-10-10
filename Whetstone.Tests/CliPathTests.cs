using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The npm install of Claude Code puts only a <c>claude.cmd</c> shim on PATH, which a shell-less
/// <c>Process.Start("claude")</c> cannot find - the nudge button's silent failure. These pin that the
/// shim is seen through to the executable it wraps, and that nothing else changes.
/// </summary>
public class CliPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccd-clipath-" + Guid.NewGuid().ToString("N")[..8]);

    public CliPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Npm_shim_resolves_to_the_executable_it_wraps()
    {
        if (!OperatingSystem.IsWindows()) return;
        var npm = Path.Combine(_root, "npm");
        var exe = Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(npm, "claude.cmd"),
            "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n" +
            "\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*\r\n");

        Assert.Equal(exe, CliPath.Resolve("claude", Path.Combine(_root, "empty") + ";" + npm));
    }

    [Fact]
    public void A_real_exe_earlier_on_path_wins()
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(_root, "claude.exe");
        File.WriteAllText(exe, "");
        Assert.Equal(exe, CliPath.Resolve("claude", _root));
    }

    [Fact]
    public void Unresolvable_names_come_back_unchanged()
    {
        Assert.Equal("claude", CliPath.Resolve("claude", _root));
        Assert.Equal("git.exe", CliPath.Resolve("git.exe", _root));
    }
}
