using System.Text.Json;

namespace Whetstone.Services;

/// <summary>Free space on one drive or volume, as a runner or this machine reads it.</summary>
/// <param name="Root">The drive or mount point (<c>C:\</c>, <c>/</c>, <c>/Volumes/Work</c>) - never a worktree's own path.</param>
public record HostDisk(string Root, long FreeBytes, long? TotalBytes = null);

/// <summary>
/// The low-disk guard: no new run goes to a machine whose worktree drive is nearly full.
/// <para>
/// A worker host once filled its disk, because nothing removed the worktrees of the tickets it ran,
/// and every run after that failed at <c>git worktree add</c> and blocked its ticket - one blocked
/// ticket per pass, each with a reason nobody could read. Starting nothing there is better: the
/// tickets wait in their queue, and the Heartbeat page says why the machine is not taking work.
/// </para>
/// <para>
/// The floor is <c>workerDiskFloorGb</c> in the global config, for every machine at once: what a run
/// needs is the project's, a worktree and its installed packages, not the machine's. Zero turns it off.
/// </para>
/// </summary>
public static class DiskFloor
{
    public const string ConfigKey = "workerDiskFloorGb";
    public const double DefaultGb = 10;

    public static double FloorGb(IReadOnlyDictionary<string, JsonElement> global) =>
        global.TryGetValue(ConfigKey, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var gb)
            ? gb
            : DefaultGb;

    /// <summary>
    /// Why a machine with these drives should take nothing new, or null. A machine that reported no
    /// drive - a runner from before the check-in carried them - is not held: nothing is known about it.
    /// </summary>
    public static string? Low(IEnumerable<HostDisk>? disks, double floorGb)
    {
        if (floorGb <= 0 || disks is null) return null;
        var floor = (long)(floorGb * 1024 * 1024 * 1024);
        var worst = disks.Where(d => d.FreeBytes < floor).OrderBy(d => d.FreeBytes).FirstOrDefault();
        return worst is null ? null
            : $"only {Gb(worst.FreeBytes)} GB free on {worst.Root}, under the {floorGb:0.#} GB floor (workerDiskFloorGb)";
    }

    public static string Gb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0.#");

    /// <summary>
    /// The reaping line, <c>worktreeReapBelowGb</c>: under it, a finished ticket's worktree goes before
    /// its retention is up (<see cref="WorktreeReaper"/>, <see cref="HostWorktrees"/>) - age or space,
    /// whichever runs out first. It sits well above the floor on purpose: the floor stops new runs, and
    /// by then the runs already out are installing packages into whatever is left. Zero turns it off.
    /// <para>
    /// The retention alone was measured in days when a fleet finished thirty tickets a day; at ninety a
    /// day, each worktree a gigabyte and more of installed packages, two days of finished worktrees
    /// filled a 460 GB disk to the last hundred megabytes and stopped every run on it.
    /// </para>
    /// </summary>
    public const string ReapConfigKey = "worktreeReapBelowGb";
    public const double DefaultReapGb = 40;

    public static double ReapBelowGb(IReadOnlyDictionary<string, JsonElement> global) =>
        global.TryGetValue(ReapConfigKey, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var gb)
            ? gb
            : DefaultReapGb;

    /// <summary>
    /// Why finished worktrees on these drives should go before their retention is up, or null:
    /// the fullest drive is under the reaping line. Nothing reported is nothing known, and not pressed.
    /// </summary>
    public static string? Pressed(IEnumerable<HostDisk>? disks, double belowGb)
    {
        if (belowGb <= 0 || disks is null) return null;
        var line = (long)(belowGb * 1024 * 1024 * 1024);
        var worst = disks.Where(d => d.FreeBytes < line).OrderBy(d => d.FreeBytes).FirstOrDefault();
        return worst is null ? null
            : $"only {Gb(worst.FreeBytes)} GB free on {worst.Root}, under the {belowGb:0.#} GB reaping line ({ReapConfigKey})";
    }

    /// <summary>
    /// The drives these folders are on, each once. A folder that does not exist yet is measured by
    /// the nearest one above it that does - a project's worktree root is made by its first run.
    /// Drives that cannot be read are left out rather than guessed at.
    /// </summary>
    public static IReadOnlyList<HostDisk> Measure(IEnumerable<string> folders)
    {
        var drives = Array.Empty<DriveInfo>();
        try { drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } }).ToArray(); }
        catch { /* no drive list: nothing to report */ }
        var result = new Dictionary<string, HostDisk>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            string full;
            try { full = Path.GetFullPath(folder); } catch { continue; }
            // The mount that holds it is the longest root that is a prefix of it - on a Mac "/" holds
            // everything, and "/Volumes/Work" holds only what is under it.
            var drive = drives
                .Where(d => Under(full, d.RootDirectory.FullName))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive is null || result.ContainsKey(drive.RootDirectory.FullName)) continue;
            try { result[drive.RootDirectory.FullName] = new HostDisk(drive.RootDirectory.FullName, drive.AvailableFreeSpace, drive.TotalSize); }
            catch { /* unreadable this time: left out */ }
        }
        return result.Values.ToList();
    }

    private static bool Under(string path, string root)
    {
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Equals(root.TrimEnd(Path.DirectorySeparatorChar), cmp) || path.StartsWith(r, cmp);
    }
}

/// <summary>
/// This machine's own worktree drives, against the same floor - the board host's workers deserve
/// the check a worker host gets. Read for the orchestrator's every pass through /api/hosts/dispatch,
/// so it is cached for a minute: free space does not change faster than a run takes to start.
/// </summary>
public class BoardDisk
{
    private readonly ProjectRegistry _registry;
    private readonly object _lock = new();
    private (DateTime At, IReadOnlyList<HostDisk> Disks)? _cached;

    public BoardDisk(ProjectRegistry registry) => _registry = registry;

    public IReadOnlyList<HostDisk> Disks()
    {
        lock (_lock)
        {
            if (_cached is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1)) return c.Disks;
            IReadOnlyList<HostDisk> disks;
            try
            {
                var roots = _registry.Projects.Select(p =>
                {
                    try { return _registry.GetProjectConfig(p).ResolvedWorktreeRoot; } catch { return ""; }
                });
                disks = DiskFloor.Measure(roots);
            }
            catch { disks = Array.Empty<HostDisk>(); }
            _cached = (DateTime.UtcNow, disks);
            return disks;
        }
    }

    /// <summary>Why this machine should take nothing new for want of disk, or null.</summary>
    public string? Low() => DiskFloor.Low(Disks(), DiskFloor.FloorGb(_registry.GetGlobalConfig()));
}
