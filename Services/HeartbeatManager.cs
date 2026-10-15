using System.Diagnostics;

namespace Whetstone.Services;

public class HeartbeatManager
{
    /// <summary>
    /// Hard ceiling on how many agents the orchestrator may run at once.
    /// <para>
    /// A ceiling rather than a setting because the cost of getting it wrong is not a slow pipeline
    /// but a wrecked one: every agent is a full Claude session drawing on the same rate-limit
    /// window, and they share one machine's CPU, disk and docker daemon. Three is what this system
    /// was sized for; raising it is a decision to re-measure, not a number to edit in a text box.
    /// </para>
    /// </summary>
    public const int MaxWorkers = 3;

    private readonly string _scriptPath;
    private readonly string _workerScriptPath;
    private readonly string _lockFilePath;
    private readonly string _stopFilePath;
    private readonly string _wakeFilePath;
    private readonly string _workingDir;
    private Process? _process;
    private readonly QaIsolation _isolation;

    public HeartbeatManager(DashboardPaths paths, QaIsolation? isolation = null)
    {
        _isolation = isolation ?? QaIsolation.Off;
        // One global, project-agnostic orchestrator. It claims the next ticket across all projects
        // and launches one agent process per ticket, each running in that ticket's own worktree, so
        // the orchestrator itself just runs from the data root.
        _scriptPath = paths.GlobalHeartbeatScript;
        _workerScriptPath = paths.GlobalWorkerScript;
        _lockFilePath = paths.GlobalLockFile;
        _stopFilePath = paths.GlobalStopFile;
        _wakeFilePath = paths.GlobalWakeFile;
        _workingDir = paths.DataRoot;
    }

    // Liveness. A handle we own is authoritative; otherwise we resolve the lock file's PID to a real
    // process. The lock file's mere existence is NOT evidence — it outlives a crash, and treating it
    // as proof reported dead heartbeats as running and live ones as stopped.
    public bool IsRunning => LiveProcess() != null;

    /// <summary>
    /// The running heartbeat, from our own handle or from the lock file, or null if there isn't one.
    /// This is what makes stop work after a dashboard restart: without it the manager can only see
    /// heartbeats it spawned itself, and a restart orphans the one that is actually running.
    /// </summary>
    private Process? LiveProcess()
    {
        if (_process != null)
        {
            if (!_process.HasExited) return _process;
            _process = null;   // exited — fall through to the lock file
        }
        return LockedProcess();
    }

    /// <summary>
    /// Resolve the lock file to a live process. The lock stores the PID and that process's start
    /// time; both must match, because a PID on its own is recycled and killing the wrong process
    /// would be far worse than failing to stop.
    /// </summary>
    private Process? LockedProcess()
    {
        if (!File.Exists(_lockFilePath)) return null;
        try
        {
            var lines = File.ReadAllLines(_lockFilePath);
            if (lines.Length < 2 || !int.TryParse(lines[0].Trim(), out var pid)) return null;
            if (!DateTime.TryParse(lines[1].Trim(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var started)) return null;

            var p = Process.GetProcessById(pid);
            if (p.HasExited) return null;
            // Within a second: the recorded value is the same clock reading, not an approximation.
            return Math.Abs((p.StartTime.ToUniversalTime() - started.ToUniversalTime()).TotalSeconds) < 2 ? p : null;
        }
        catch
        {
            // No such process, access denied, or a lock written by an older build — treat as none.
            return null;
        }
    }

    private void ClearSignalFiles()
    {
        try { if (File.Exists(_lockFilePath)) File.Delete(_lockFilePath); } catch { /* best effort */ }
        try { if (File.Exists(_stopFilePath)) File.Delete(_stopFilePath); } catch { /* best effort */ }
        try { if (File.Exists(_wakeFilePath)) File.Delete(_wakeFilePath); } catch { /* best effort */ }
    }

    /// <summary>
    /// Tell a sleeping heartbeat there is work it cannot see from inside its nap.
    /// <para>
    /// An idle orchestrator backs off to an hour and hibernates through off-hours, and nothing that
    /// happened on the board used to reach it: a ticket filed or unblocked at the start of a nap
    /// waited the nap out. The only way to cut it short was to stop the heartbeat and start it
    /// again, and a stop takes every worker with it. This is a signal file the idle sleep polls,
    /// exactly as it polls for a stop, so it can only ever end a sleep.
    /// </para>
    /// <para>
    /// A heartbeat that is not running stays not running: somebody stopped it, and new work is not
    /// a reason to overrule them. Never throws - a wake that fails costs a wait, nothing else.
    /// </para>
    /// </summary>
    public bool Wake()
    {
        if (_isolation.On || !IsRunning) return false;
        try
        {
            File.WriteAllText(_wakeFilePath, DateTime.UtcNow.ToString("o"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Both halves, because the orchestrator without its worker script exits within a second of
    // starting - which the UI would otherwise report as "started", then "stopped", with no reason.
    public bool ScriptExists => File.Exists(_scriptPath) && File.Exists(_workerScriptPath);

    public bool StopRequested => File.Exists(_stopFilePath);

    public (bool success, string message) RequestStopAfterCycle()
    {
        if (_isolation.On) return (false, QaIsolation.Refusal("signalling the heartbeat"));
        if (!IsRunning)
            return (false, "Heartbeat is not running");

        try
        {
            File.WriteAllText(_stopFilePath, DateTime.UtcNow.ToString("o"));
            return (true, "Will stop after current cycle completes");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to create stop signal: {ex.Message}");
        }
    }

    public (bool success, string message) CancelStopAfterCycle()
    {
        if (_isolation.On) return (false, QaIsolation.Refusal("signalling the heartbeat"));
        if (!File.Exists(_stopFilePath))
            return (false, "No pending stop request");

        try
        {
            File.Delete(_stopFilePath);
            return (true, "Stop request cancelled");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to remove stop signal: {ex.Message}");
        }
    }

    public (bool success, string message) Start()
    {
        // A QA copy carries the same scripts as the live one, so the files being there proves
        // nothing: started, this heartbeat would claim tickets and launch real agents.
        if (_isolation.On) return (false, QaIsolation.Refusal("starting the heartbeat"));
        if (IsRunning)
            return (false, "Heartbeat is already running");

        if (!File.Exists(_scriptPath))
            return (false, $"Script not found: {_scriptPath}");
        if (!File.Exists(_workerScriptPath))
            return (false, $"Agent script not found: {_workerScriptPath}");

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-ExecutionPolicy Bypass -File \"{_scriptPath}\"",
                    WorkingDirectory = _workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                }
            };
            // Clear a stop signal and any stale lock from a previous run. IsRunning has already
            // confirmed nothing is alive, so a lock still on disk is a leftover — and leaving it
            // would make the fresh heartbeat look like it was already running.
            ClearSignalFiles();

            _process.Start();

            // Bind it to this process's lifetime before it can launch a worker - see KillOnCloseJob.
            // Not fatal: a heartbeat that starts unbound still runs, and the message says so rather
            // than leaving the difference to be discovered the next time the dashboard dies.
            var unbound = KillOnCloseJob.Assign(_process);
            return unbound is null
                ? (true, $"Heartbeat started (PID {_process.Id})")
                : (true, $"Heartbeat started (PID {_process.Id}), but not bound to the dashboard's lifetime: {unbound}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop the heartbeat outright, whoever started it.
    /// <para>
    /// Stop has to be immediate and unconditional. Sleeping is the state a heartbeat spends most of
    /// its time in, so "stop" that only takes effect at the top of the next cycle is indistinguishable
    /// from a stop that did not work — the process wakes up 48 minutes later and carries on.
    /// Killing the tree also takes any in-flight claude worker with it.
    /// </para>
    /// </summary>
    public (bool success, string message) Stop()
    {
        if (_isolation.On) return (false, QaIsolation.Refusal("stopping a heartbeat"));
        var p = LiveProcess();
        if (p is null)
        {
            // Nothing alive, but a lock may still be on disk from a crash — clear it so the next
            // Start is not refused by a heartbeat that no longer exists.
            var hadLock = File.Exists(_lockFilePath);
            ClearSignalFiles();
            return hadLock
                ? (true, "Heartbeat was not running; cleared a stale lock file")
                : (false, "Heartbeat is not running");
        }

        try
        {
            var pid = p.Id;
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            _process = null;
            ClearSignalFiles();
            return (true, $"Heartbeat stopped (PID {pid})");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to stop: {ex.Message}");
        }
    }
}
