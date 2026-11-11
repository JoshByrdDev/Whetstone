using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// A project's <c>workerEnv</c>: read from its config as names and string values, with what is
/// not one (a <c>_comment</c>, a number) left out. Which names a worker refuses is worker.ps1's
/// (Get-WorkerEnv, data/worker-env.tests.ps1), and <c>{machineCache}</c> is expanded there too,
/// on the machine the run is on - so the board passes it through as written.
/// </summary>
public class WorkerEnvTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whetstone-workerenv-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ProjectRegistry Registry(string key, string defaults, string? local = null)
    {
        var dir = Path.Combine(_root, "projects", key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.defaults.json"), defaults);
        if (local is not null) File.WriteAllText(Path.Combine(dir, "project.json"), local);
        return new ProjectRegistry(new DashboardPaths(_root));
    }

    [Fact]
    public void A_project_without_workerEnv_has_none()
    {
        var cfg = Registry("alpha", """{ "projectPath": "C:\nowhere\alpha" }""").GetProjectConfig("alpha");
        Assert.Empty(cfg.WorkerEnv);
    }

    [Fact]
    public void WorkerEnv_keeps_string_values_as_written()
    {
        var cfg = Registry("alpha",
            """{ "projectPath": "C:/nowhere/alpha", "workerEnv": { "_comment": "why", "TURBO_CACHE_DIR": "{machineCache}/turbo", "THREADS": 2, "MODE": "ci" } }""")
            .GetProjectConfig("alpha");
        Assert.Equal(2, cfg.WorkerEnv.Count);
        Assert.Equal("{machineCache}/turbo", cfg.WorkerEnv["TURBO_CACHE_DIR"]);
        Assert.Equal("ci", cfg.WorkerEnv["MODE"]);
    }

    [Fact]
    public void A_machines_project_json_can_replace_it()
    {
        var cfg = Registry("bravo",
            """{ "projectPath": "C:\nowhere\bravo", "workerEnv": { "A": "1" } }""",
            """{ "workerEnv": { "B": "2" } }""").GetProjectConfig("bravo");
        Assert.Equal("2", Assert.Single(cfg.WorkerEnv).Value);
    }
}
