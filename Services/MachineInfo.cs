using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// A machine's hardware, as either collector reports it: a worker host's runner
/// (<c>data/machine-info.ps1</c>) or the board host's own <see cref="MachineProbe"/>. The JSON names
/// are the contract between the two; a field a machine cannot read is null, never a guess.
/// </summary>
public record MachineInventoryReport(
    string? CpuModel = null, int? PhysicalCores = null, int? LogicalCores = null,
    int? PerfCores = null, int? EffCores = null, long? RamMb = null,
    string? Os = null, string? OsVersion = null, string? Arch = null,
    string? DiskKind = null, double? DiskTotalGb = null,
    int? DockerCpus = null, long? DockerMemMb = null,
    Dictionary<string, string>? Tools = null);

/// <summary>A machine's load at one moment, as either collector reports it.</summary>
public record MachineLoadReport(
    DateTime? At = null, double? CpuPct = null, long? MemAvailMb = null, long? MemTotalMb = null,
    long? SwapUsedMb = null, int? Pressure = null, double? DiskFreeGb = null,
    double? NetInKbps = null, double? NetOutKbps = null,
    long? DockerMemUsedMb = null, long? DockerMemLimitMb = null);

/// <summary>The three levels a load sample's <c>Pressure</c> takes, the same on every OS.</summary>
public static class MachinePressure
{
    public const int Normal = 0, Warn = 1, Critical = 2;

    /// <summary>
    /// macOS's own reading, <c>kern.memorystatus_vm_pressure_level</c>: 1 normal, 2 warn, 4 critical.
    /// </summary>
    public static int? FromMacLevel(int? level) => level switch
    {
        1 => Normal, 2 => Warn, 4 => Critical, _ => null,
    };

    /// <summary>
    /// Windows has no single pressure reading, so it is derived from what is available: under 10% of
    /// RAM is a warning, under 5% critical. Written the same way in <c>data/machine-info.ps1</c>.
    /// </summary>
    public static int? FromAvailable(long? availMb, long? totalMb)
    {
        if (availMb is not { } a || totalMb is not { } t || t <= 0) return null;
        var pct = 100.0 * a / t;
        return pct < 5 ? Critical : pct < 10 ? Warn : Normal;
    }
}

/// <summary>
/// The parsers for the command output both collectors read on macOS and for Docker. Pure functions
/// over text, so the tests feed them captured output, and <c>data/machine-info.tests.ps1</c> feeds
/// the PowerShell side the same lines.
/// </summary>
public static class MachineParse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static long? Long(string? s) =>
        long.TryParse(s?.Trim(), NumberStyles.Integer, Inv, out var v) ? v : null;

    public static int? Int(string? s) => Long(s) is { } v ? (int)v : null;

    /// <summary>
    /// <c>vm_stat</c>: memory a new process could have without anything being paged out - free,
    /// inactive, speculative and purgeable pages. Free alone is always near zero on a Mac, because
    /// macOS keeps RAM full of cache on purpose.
    /// </summary>
    public static long? VmStatAvailableMb(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var page = Regex.Match(text, @"page size of (\d+) bytes") is { Success: true } m ? long.Parse(m.Groups[1].Value, Inv) : 4096;
        long Pages(string name) =>
            Regex.Match(text, @"^Pages " + name + @":\s+(\d+)\.?", RegexOptions.Multiline) is { Success: true } x
                ? long.Parse(x.Groups[1].Value, Inv) : 0;
        var pages = Pages("free") + Pages("inactive") + Pages("speculative") + Pages("purgeable");
        return pages == 0 ? null : pages * page / (1024 * 1024);
    }

    /// <summary><c>sysctl vm.swapusage</c>: <c>total = 2048.00M  used = 1077.25M  free = 970.75M</c>.</summary>
    public static long? SwapUsedMb(string? text)
    {
        if (text is null || Regex.Match(text, @"used\s*=\s*([\d.]+)([KMG])") is not { Success: true } m) return null;
        var v = double.Parse(m.Groups[1].Value, Inv);
        return (long)Math.Round(m.Groups[2].Value switch { "K" => v / 1024, "G" => v * 1024, _ => v });
    }

    /// <summary><c>top -l 2 -n 0 -s 1</c>: the last <c>CPU usage: ... 84.21% idle</c> line, as busy %.</summary>
    public static double? TopCpuPct(string? text)
    {
        if (text is null) return null;
        var ms = Regex.Matches(text, @"CPU usage:.*?([\d.]+)% idle");
        return ms.Count == 0 ? null : Math.Round(100 - double.Parse(ms[^1].Groups[1].Value, Inv), 1);
    }

    /// <summary><c>df -k path</c>: the Available column of the line after the header, in GB.</summary>
    public static double? DfFreeGb(string? text)
    {
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return null;
        var cols = Regex.Split(lines[^1], @"\s+");
        return cols.Length > 3 && long.TryParse(cols[3], NumberStyles.Integer, Inv, out var kb) ? Math.Round(kb / 1024.0 / 1024.0, 1) : null;
    }

    /// <summary><c>df -k path</c>: the size column, in GB.</summary>
    public static double? DfTotalGb(string? text)
    {
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return null;
        var cols = Regex.Split(lines[^1], @"\s+");
        return cols.Length > 1 && long.TryParse(cols[1], NumberStyles.Integer, Inv, out var kb) ? Math.Round(kb / 1024.0 / 1024.0, 1) : null;
    }

    /// <summary><c>diskutil info /</c>: <c>Solid State: Yes</c>.</summary>
    public static string? DiskutilKind(string? text) =>
        text is not null && Regex.Match(text, @"Solid State:\s*(\w+)") is { Success: true } m
            ? (m.Groups[1].Value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? "SSD" : "HDD") : null;

    /// <summary><c>docker info --format "{{.NCPU}} {{.MemTotal}}"</c>: <c>8 8217473024</c>.</summary>
    public static (int? Cpus, long? MemMb) DockerInfo(string? text)
    {
        var parts = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (null, null);
        return (Int(parts[0]), Long(parts[1]) is { } b ? b / (1024 * 1024) : null);
    }

    /// <summary>
    /// <c>docker stats --no-stream --format "{{.MemUsage}}"</c>: one <c>123.4MiB / 7.654GiB</c> per
    /// container. Returns the sum used and the limit (the VM's, the same on every line).
    /// </summary>
    public static (long? UsedMb, long? LimitMb) DockerStats(string? text)
    {
        // No answer at all is no docker; an empty one is docker with nothing running.
        if (text is null) return (null, null);
        if (string.IsNullOrWhiteSpace(text)) return (0, null);
        double used = 0; double? limit = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var halves = line.Split('/');
            if (Size(halves[0]) is { } u) used += u;
            if (halves.Length > 1 && Size(halves[1]) is { } l) limit = l;
        }
        return ((long)Math.Round(used), limit is { } x ? (long)Math.Round(x) : null);
    }

    /// <summary><c>123.4MiB</c>, <c>7.6GiB</c>, <c>512kB</c> as MB.</summary>
    public static double? Size(string s)
    {
        var m = Regex.Match(s.Trim(), @"^([\d.]+)\s*([KMGT]?)i?B$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var v = double.Parse(m.Groups[1].Value, Inv);
        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => v / 1024, "M" => v, "G" => v * 1024, "T" => v * 1024 * 1024, _ => v / (1024 * 1024),
        };
    }

    /// <summary>The first version-looking thing in a <c>--version</c> line: <c>v22.11.0</c> -> <c>22.11.0</c>.</summary>
    public static string? Version(string? text) =>
        text is not null && Regex.Match(text, @"\d+\.\d+(\.\d+)?([-.+\w]*)?") is { Success: true } m ? m.Value : null;
}

/// <summary>
/// Keeps a machine's inventory as a history of changes: a report equal to the last one only moves
/// its <c>LastSeen</c>; a different one opens a new row.
/// </summary>
public static class MachineInventoryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The report as stored: tool names sorted, so the order a collector found them in is not a change.</summary>
    public static string Normalise(MachineInventoryReport r) =>
        JsonSerializer.Serialize(r with
        {
            Tools = r.Tools is null ? null
                : new SortedDictionary<string, string>(r.Tools.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                    .ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value.Trim()), StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
        }, Json);

    public static string HashOf(string normalised) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..32].ToLowerInvariant();

    /// <returns>True when this opened a new row: the first report, or a change.</returns>
    public static async Task<bool> RecordAsync(DashboardDbContext db, string place, MachineInventoryReport report, DateTime now, CancellationToken ct = default)
    {
        var json = Normalise(report);
        var hash = HashOf(json);
        var last = await db.MachineInventories.Where(m => m.Place == place)
            .OrderByDescending(m => m.LastSeen).ThenByDescending(m => m.Id).FirstOrDefaultAsync(ct);
        if (last is not null && last.Hash == hash)
        {
            last.LastSeen = now;
            await db.SaveChangesAsync(ct);
            return false;
        }
        db.MachineInventories.Add(new MachineInventory
        {
            Place = place, FirstSeen = now, LastSeen = now, Hash = hash, Json = json,
            CpuModel = report.CpuModel, PhysicalCores = report.PhysicalCores, LogicalCores = report.LogicalCores,
            PerfCores = report.PerfCores, EffCores = report.EffCores, RamMb = report.RamMb,
            Os = report.Os, OsVersion = report.OsVersion, Arch = report.Arch,
            DiskKind = report.DiskKind, DiskTotalGb = report.DiskTotalGb,
            DockerCpus = report.DockerCpus, DockerMemMb = report.DockerMemMb,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static MachineInventoryReport? Read(MachineInventory? row)
    {
        if (row is null) return null;
        try { return JsonSerializer.Deserialize<MachineInventoryReport>(row.Json, Json); }
        catch { return null; }
    }
}

/// <summary>
/// The latest load reading of each machine, held between minutes: a worker host's arrives on its
/// check-in, the board host's from <see cref="MachineProbe"/>. <see cref="CapacitySampler"/> writes
/// what is here once a minute, beside the berths it already counts.
/// </summary>
public class MachineLoadBook
{
    /// <summary>A reading older than this is a machine that stopped reporting, not its load now.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<string, (DateTime At, MachineLoadReport Load)> _latest = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string place, MachineLoadReport load, DateTime at) => _latest[place] = (at, load);

    public MachineLoadReport? Get(string place, DateTime now) =>
        _latest.TryGetValue(place, out var v) && now - v.At <= Stale ? v.Load : null;
}
