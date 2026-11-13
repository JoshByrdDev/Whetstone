using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The .NET root `dotnet run` hands the board must not reach the PowerShell it starts, or a
/// PowerShell built on another .NET exits before the heartbeat runs a line.
/// </summary>
public class HostDotnetRootTests
{
    private static Func<string, string?> Env(Dictionary<string, string> vars) =>
        name => vars.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void An_architecture_root_naming_this_apps_own_runtime_is_cleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet9");
        var env = Env(new() { ["DOTNET_ROOT_ARM64"] = root, ["DOTNET_ROOT_X64"] = root + Path.DirectorySeparatorChar });

        Assert.Equal(new[] { "DOTNET_ROOT_ARM64", "DOTNET_ROOT_X64" }, HostDotnetRoot.Injected(env, root));
    }

    [Fact]
    public void Plain_dotnet_root_stays_because_tools_like_dotnet_ef_need_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet9");
        Assert.Empty(HostDotnetRoot.Injected(Env(new() { ["DOTNET_ROOT"] = root }), root));
    }

    [Fact]
    public void A_root_a_person_pointed_somewhere_else_stays()
    {
        var env = Env(new() { ["DOTNET_ROOT_ARM64"] = Path.Combine(Path.GetTempPath(), "dotnet10") });
        Assert.Empty(HostDotnetRoot.Injected(env, Path.Combine(Path.GetTempPath(), "dotnet9")));
    }

    [Fact]
    public void The_runtime_root_holds_the_shared_framework()
    {
        var root = HostDotnetRoot.RuntimeRoot();
        Assert.NotNull(root);
        Assert.True(Directory.Exists(Path.Combine(root!, "shared", "Microsoft.NETCore.App")));
    }
}
