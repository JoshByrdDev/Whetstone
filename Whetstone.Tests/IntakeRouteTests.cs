using System.Reflection;
using ModelContextProtocol.Server;
using Whetstone.Services.Intake;
using Xunit;

namespace Whetstone.Tests;

/// <summary>Intake is the owner's and the board's: no MCP tool, worker or steward, reaches it.</summary>
public class IntakeRouteTests
{
    [Fact]
    public void No_tool_reaches_intake()
    {
        var forbidden = new[] { typeof(IntakeService), typeof(IntakeTransports), typeof(IIntakeSettings), typeof(IIntakeTransport) };
        var tools = typeof(IntakeService).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Select(m => (Type: t, Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>())))
            .Where(x => x.Tool is not null)
            .ToList();
        Assert.NotEmpty(tools);
        foreach (var (_, method, tool) in tools)
        {
            Assert.DoesNotContain(method.GetParameters(), p => forbidden.Contains(p.ParameterType));
            Assert.DoesNotMatch("(?i)intake|mute", tool!.Name ?? method.Name);
        }
        foreach (var type in tools.Select(x => x.Type).Distinct())
            foreach (var ctor in type.GetConstructors())
                Assert.DoesNotContain(ctor.GetParameters(), p => forbidden.Contains(p.ParameterType));
    }
}
