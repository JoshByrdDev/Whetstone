using System.Text.Json;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// The board host's simulator hygiene (docs/ios-simulator-plan.md, section 3). A run killed
/// mid-flight leaves its simulator booted with its build installed, so when a lease on this
/// machine's <c>ios:simulator</c> is freed - by the run finishing, by the reaper, by an expiry or by
/// an orchestrator's start-up reset - the simulators that run booted are shut down before anyone
/// else can take the lease.
/// <para>
/// Only those it booted. A Mac board host is also its owner's Mac, and a simulator the owner had
/// open when the run took the lease is theirs: what was booted then is written down at acquisition,
/// in the run folder so an API restart (the commonest way a run dies) does not forget it, and is
/// left alone. A lease with nothing written down - taken before this existed - shuts down nothing.
/// </para>
/// <para>
/// A worker host's lease (<c>ios:simulator@bravo-host</c>) is that machine's simulator, and its
/// runner does the same there (<c>Stop-NewSimulators</c>, data/machine-info.ps1). The project
/// skill's first step - shut down, then erase or reinstall - stands either way.
/// </para>
/// </summary>
public class SimulatorReset
{
    private readonly MachineProbe.Runner _run;
    private readonly string _dir;
    private readonly bool _enabled;
    private readonly ILogger<SimulatorReset>? _log;

    public SimulatorReset(DashboardPaths paths, QaIsolation isolation, ILogger<SimulatorReset> log)
        : this(MachineProbe.RunProcess, Path.Combine(paths.RunDir, "device-leases"),
               // A QA copy starts no simulator and must not stop the live fleet's.
               OperatingSystem.IsMacOS() && !isolation.On, log) { }

    public SimulatorReset(MachineProbe.Runner run, string dir, bool enabled, ILogger<SimulatorReset>? log = null) =>
        (_run, _dir, _enabled, _log) = (run, dir, enabled, log);

    /// <summary>Whether a lease is this machine's simulator: <c>ios:simulator</c>, never a worker host's.</summary>
    public static bool IsBoardSimulator(string resourceKey) =>
        resourceKey == MachineDevices.IosSimulator;

    private string FileFor(int leaseId) => Path.Combine(_dir, $"{leaseId}.json");

    /// <summary>Write down what was booted as each simulator lease was taken. Called once the leases are saved.</summary>
    public void Acquired(IEnumerable<ResourceLease> leases)
    {
        if (!_enabled) return;
        foreach (var l in leases.Where(l => IsBoardSimulator(l.ResourceKey)))
        {
            try
            {
                var booted = SimulatorParse.Booted(_run("xcrun", "simctl list devices booted -j"));
                Directory.CreateDirectory(_dir);
                File.WriteAllText(FileFor(l.Id), JsonSerializer.Serialize(booted));
            }
            catch (Exception ex) { _log?.LogWarning(ex, "Could not record the simulators booted before lease {Lease}", l.Id); }
        }
    }

    /// <summary>
    /// Shut down what each freed simulator lease booted. Called before the release is saved, so
    /// nobody holds the lease while its simulator is still going down. Returns the UDIDs shut down.
    /// </summary>
    public IReadOnlyList<string> Released(IEnumerable<ResourceLease> leases)
    {
        var stopped = new List<string>();
        if (!_enabled) return stopped;
        foreach (var l in leases.Where(l => IsBoardSimulator(l.ResourceKey)))
        {
            var file = FileFor(l.Id);
            try
            {
                if (!File.Exists(file)) continue;
                var before = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? Array.Empty<string>();
                foreach (var udid in ToShutDown(before, SimulatorParse.Booted(_run("xcrun", "simctl list devices booted -j"))))
                {
                    _run("xcrun", $"simctl shutdown {udid}");
                    stopped.Add(udid);
                }
                File.Delete(file);
                if (stopped.Count > 0)
                    _log?.LogInformation("Lease {Lease} on {Key} freed: shut down {Count} simulator(s) its run booted", l.Id, l.ResourceKey, stopped.Count);
            }
            catch (Exception ex) { _log?.LogWarning(ex, "Could not shut down the simulators lease {Lease} booted", l.Id); }
        }
        return stopped;
    }

    /// <summary>The simulators booted now that were not when the lease was taken. Pure, for the tests.</summary>
    public static IReadOnlyList<string> ToShutDown(IEnumerable<string> before, IEnumerable<string> now)
    {
        var kept = before.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A UDID is hex and dashes; anything else is not something to put on a command line.
        return now.Where(u => !kept.Contains(u) && u.All(c => char.IsAsciiHexDigit(c) || c == '-')).Distinct().ToList();
    }
}
