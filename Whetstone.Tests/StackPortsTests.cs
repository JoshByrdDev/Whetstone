using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A project's <c>stackPorts</c>: slot 0's ports and the step per slot, read from its config so a
/// run can check them against the machine's reserved ranges before its agent starts. Whether they
/// are reserved is worker.ps1's (Get-StackPortProblem, data/worker-stack-ports.tests.ps1); the
/// board only reads what the project said, and a block that says nothing usable checks nothing.
/// </summary>
public class StackPortsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-stackports-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ProjectConfig Config(string defaults)
    {
        var dir = Path.Combine(_root, "projects", "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.defaults.json"), defaults);
        return new ProjectRegistry(new DashboardPaths(_root)).GetProjectConfig("alpha");
    }

    [Fact]
    public void A_project_without_stackPorts_checks_nothing() =>
        Assert.Null(Config("""{ "projectPath": "C:/nowhere/alpha" }""").StackPorts);

    [Fact]
    public void Slot_N_is_each_port_plus_the_step_times_N()
    {
        var sp = Config("""{ "projectPath": "C:/nowhere/alpha", "stackPorts": { "ports": [8080, 8081], "slotStep": 10 } }""").StackPorts;
        Assert.NotNull(sp);
        Assert.Equal(new[] { 8080, 8081 }, sp!.ForSlot(0));
        Assert.Equal(new[] { 8100, 8101 }, sp.ForSlot(2));
    }

    [Fact]
    public void What_is_not_a_port_is_left_out_and_no_port_is_no_check()
    {
        var sp = Config("""{ "projectPath": "C:/nowhere/alpha", "stackPorts": { "ports": [0, "8080", 70000, 8082] } }""").StackPorts;
        Assert.Equal(new[] { 8082 }, sp!.Ports);
        Assert.Equal(0, sp.SlotStep);
        Assert.Null(Config("""{ "projectPath": "C:/nowhere/alpha", "stackPorts": { "ports": ["x"] } }""").StackPorts);
    }
}
