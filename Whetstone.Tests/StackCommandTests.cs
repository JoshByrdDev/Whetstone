using System.Diagnostics;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A stack's up and down commands reach the shell exactly as they are written in the config.
/// Quoting a path with spaces is the ordinary way to write one, and it used to arrive at the
/// program with its quote marks escaped into the path.
/// </summary>
public class StackCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whetstone stack " + Guid.NewGuid().ToString("N"));

    public StackCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static (int Exit, string Out) Run(string command, string workDir)
    {
        using var p = Process.Start(ProjectStackService.ShellStart(command, workDir))!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [Fact]
    public void A_quoted_path_reaches_the_program_without_its_quotes()
    {
        var file = Path.Combine(_dir, "marker file.txt");
        File.WriteAllText(file, "stack-marker");
        var command = OperatingSystem.IsWindows() ? $"type \"{file}\"" : $"cat \"{file}\"";

        var (exit, output) = Run(command, _dir);

        Assert.Equal(0, exit);
        Assert.Contains("stack-marker", output);
    }

    [Fact]
    public void A_chain_with_quotes_on_both_sides_still_runs_every_step()
    {
        var first = Path.Combine(_dir, "first one.txt");
        var second = Path.Combine(_dir, "second one.txt");
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");
        var read = OperatingSystem.IsWindows() ? "type" : "cat";

        var (exit, output) = Run($"{read} \"{first}\" && {read} \"{second}\"", _dir);

        Assert.Equal(0, exit);
        Assert.Contains("one", output);
        Assert.Contains("two", output);
    }

    [Fact]
    public void A_failing_command_reports_its_exit_code()
    {
        var (exit, _) = Run("exit 3", _dir);
        Assert.Equal(3, exit);
    }
}
