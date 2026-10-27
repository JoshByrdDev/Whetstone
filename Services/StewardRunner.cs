using System.Diagnostics;

namespace Whetstone.Services;

/// <summary>
/// Starts a steward run when its owner asks for one from the Steward page, instead of waiting for
/// the heartbeat to find it due.
/// <para>
/// The heartbeat stays the launcher whenever it is going: this marks the run owed and wakes it, so
/// there is never a second process deciding who sits in the steward's berth. Only while the
/// heartbeat is stopped does the board start <c>steward.ps1</c> itself, in the same slot, bound to
/// the board's lifetime like the heartbeat is. A heartbeat started later finds that run and takes
/// it onto its books (<c>Add-OrphanSteward</c>).
/// </para>
/// <para>
/// The run's lock is only taken once its agent opens the run, some seconds after launch. Until then
/// the process this started is what says a run is going, so a second press cannot start another.
/// </para>
/// </summary>
public sealed class StewardRunner
{
    private readonly HeartbeatManager _heartbeat;
    private readonly QaIsolation _isolation;
    private readonly IServiceScopeFactory? _scopes;
    private readonly string _script;
    private readonly string _workingDir;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _gate = new();
    private Process? _process;
    private string? _project;

    public StewardRunner(DashboardPaths paths, HeartbeatManager heartbeat, QaIsolation? isolation = null,
        IServiceScopeFactory? scopes = null)
    {
        _heartbeat = heartbeat;
        _isolation = isolation ?? QaIsolation.Off;
        _scopes = scopes;
        _workingDir = paths.DataRoot;
        _script = Path.Combine(paths.DataRoot, "steward.ps1");
    }

    /// <summary>The project whose run this started and is still alive, or null.</summary>
    public string? RunningProject
    {
        get
        {
            lock (_gate)
            {
                if (_process is null) return null;
                try { return _process.HasExited ? null : _project; }
                catch (InvalidOperationException) { return null; }
            }
        }
    }

    /// <summary>
    /// Start a run for <paramref name="projectKey"/> now. Answers <c>started</c> when this launched
    /// it, <c>queued</c> when the heartbeat will, or a refusal with the reason.
    /// </summary>
    public async Task<StewardReply> StartAsync(string projectKey, StewardService steward, CancellationToken ct = default)
    {
        if (_isolation.On) return new StewardReply("isolated", QaIsolation.Refusal("starting a steward run"));

        await _one.WaitAsync(ct);
        try
        {
            if (RunningProject is { } going)
                return new StewardReply("running", going == projectKey
                    ? "A run is already starting for this project."
                    : $"The steward is starting a run for {going} just now, and it runs one project at a time.");

            var asked = await steward.OwnerAsksForRunAsync(projectKey, ct);
            if (asked.Answer != "recorded") return asked;

            if (_heartbeat.IsRunning)
            {
                _heartbeat.Wake();
                return new StewardReply("queued",
                    "The heartbeat is running, so it starts the run on its next pass. It has been woken to take it now.");
            }

            if (!File.Exists(_script)) return new StewardReply("no-script", $"There is no steward script at {_script}.");
            return Launch(projectKey);
        }
        finally { _one.Release(); }
    }

    private StewardReply Launch(string projectKey)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-ExecutionPolicy Bypass -File \"{_script}\" -Project {projectKey} -Agent {WorkerHostConfig.StewardSlot}",
                WorkingDirectory = _workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        p.Exited += (_, _) => _ = ReleaseAsync(projectKey);
        try { p.Start(); }
        catch (Exception ex) { return new StewardReply("failed", $"Could not start the steward: {ex.Message}"); }

        // As the heartbeat is: a board that closes takes the run with it, rather than leaving an
        // agent nobody is watching. Not fatal when it cannot be bound, and the answer says so.
        var unbound = KillOnCloseJob.Assign(p);
        lock (_gate) (_process, _project) = (p, projectKey);
        return new StewardReply("started", unbound is null ? null : $"Started, but not bound to the board's lifetime: {unbound}",
            new { pid = p.Id });
    }

    // What the heartbeat does when a steward's process ends: give back a lock the run did not give
    // back itself. steward.ps1 does the same on its way out; this covers a process that was killed.
    private async Task ReleaseAsync(string projectKey)
    {
        if (_scopes is null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<StewardService>()
                .AbandonRunAsync(projectKey, "the steward's process ended");
        }
        catch { /* the lease runs out on its own; a failed release must not take the board down */ }
    }
}
