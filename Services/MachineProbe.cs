using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// The board host reading its own hardware and load (docs/machine-limits-plan.md, sections 1 and 2).
/// A worker host reads the same things with <c>data/machine-info.ps1</c>; both report the shapes in
/// <see cref="MachineInventoryReport"/> and <see cref="MachineLoadReport"/>. On Windows this asks
/// Win32 directly; on macOS it runs the same commands the PowerShell collector runs and parses them
/// with <see cref="MachineParse"/>. Every command goes through <c>run</c>, which the tests replace.
/// </summary>
public class MachineProbe
{
    /// <summary>Runs a command and returns its standard output, or null if it failed or is absent.</summary>
    public delegate string? Runner(string file, string args);

    /// <summary>The tools whose versions an inventory lists. Absent ones are simply left out.</summary>
    public static readonly string[] Tools = { "node", "pnpm", "dotnet", "pwsh", "git", "docker", "claude" };

    /// <summary><c>docker stats</c> takes a second or two, so it is read every this many load samples.</summary>
    public const int DockerEvery = 5;

    private readonly Runner _run;
    private readonly string _dataRoot;
    private readonly bool _docker;
    private readonly bool _mac = OperatingSystem.IsMacOS();
    private readonly bool _win = OperatingSystem.IsWindows();
    private (DateTime At, long Rx, long Tx)? _net;
    private (ulong Idle, ulong Total)? _cpu;
    private int _loads;

    /// <param name="docker">False on a QA copy, which reaches no docker at all - not even to read it.</param>
    public MachineProbe(string dataRoot, bool docker, Runner? run = null, bool? mac = null, bool? windows = null)
    {
        _dataRoot = dataRoot;
        _docker = docker;
        _run = run ?? RunProcess;
        if (mac is { } m) _mac = m;
        if (windows is { } w) _win = w;
    }

    public MachineInventoryReport Inventory()
    {
        var r = new MachineInventoryReport(Arch: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            LogicalCores: Environment.ProcessorCount);
        if (_mac)
        {
            var (perf, eff) = (MachineParse.Int(Sysctl("hw.perflevel0.physicalcpu")), MachineParse.Int(Sysctl("hw.perflevel1.physicalcpu")));
            r = r with
            {
                CpuModel = Sysctl("machdep.cpu.brand_string")?.Trim(),
                PhysicalCores = MachineParse.Int(Sysctl("hw.physicalcpu")),
                LogicalCores = MachineParse.Int(Sysctl("hw.logicalcpu")) ?? r.LogicalCores,
                // An Intel Mac has no perf levels; an Apple one always has both.
                PerfCores = eff is null ? null : perf, EffCores = perf is null ? null : eff,
                RamMb = MachineParse.Long(Sysctl("hw.memsize")) is { } b ? b / (1024 * 1024) : null,
                Os = "macOS", OsVersion = _run("sw_vers", "-productVersion")?.Trim(),
                Arch = _run("uname", "-m")?.Trim() ?? r.Arch,
                DiskKind = MachineParse.DiskutilKind(_run("diskutil", $"info \"{_dataRoot}\"") ?? _run("diskutil", "info /")),
                DiskTotalGb = MachineParse.DfTotalGb(_run("df", $"-k \"{_dataRoot}\"")),
            };
        }
        else if (_win && OperatingSystem.IsWindows())
        {
            var (cores, perf, eff) = Win.Cores();
            var mem = Win.Memory();
            r = r with
            {
                CpuModel = Win.CpuName(), PhysicalCores = cores, PerfCores = perf, EffCores = eff,
                RamMb = mem is { } m ? (long)(m.ullTotalPhys / (1024 * 1024)) : null,
                Os = "Windows", OsVersion = Win.OsVersion(),
                DiskKind = Win.DiskKind(_dataRoot), DiskTotalGb = DriveGb(d => d.TotalSize),
            };
        }
        if (_docker)
        {
            var (cpus, memMb) = MachineParse.DockerInfo(_run("docker", "info --format \"{{.NCPU}} {{.MemTotal}}\""));
            r = r with { DockerCpus = cpus, DockerMemMb = memMb };
        }
        var tools = new Dictionary<string, string>();
        foreach (var t in Tools)
        {
            if (t == "docker" && !_docker) continue;
            if (MachineParse.Version(Tool(t)) is { } v) tools[t] = v;
        }
        return r with { Tools = tools };
    }

    public MachineLoadReport Load(DateTime now)
    {
        var r = new MachineLoadReport(At: now);
        if (_mac)
        {
            var avail = MachineParse.VmStatAvailableMb(_run("vm_stat", ""));
            var total = MachineParse.Long(Sysctl("hw.memsize")) is { } b ? b / (1024 * 1024) : (long?)null;
            r = r with
            {
                CpuPct = MachineParse.TopCpuPct(_run("top", "-l 2 -n 0 -s 1")),
                MemAvailMb = avail, MemTotalMb = total,
                SwapUsedMb = MachineParse.SwapUsedMb(Sysctl("vm.swapusage")),
                Pressure = MachinePressure.FromMacLevel(MachineParse.Int(Sysctl("kern.memorystatus_vm_pressure_level"))),
                DiskFreeGb = MachineParse.DfFreeGb(_run("df", $"-k \"{_dataRoot}\"")),
            };
        }
        else if (_win && OperatingSystem.IsWindows())
        {
            var mem = Win.Memory();
            long? avail = mem is { } m ? (long)(m.ullAvailPhys / (1024 * 1024)) : null;
            long? total = mem is { } m2 ? (long)(m2.ullTotalPhys / (1024 * 1024)) : null;
            r = r with
            {
                CpuPct = WinCpu(), MemAvailMb = avail, MemTotalMb = total,
                SwapUsedMb = Win.PageFileInUseMb(),
                Pressure = MachinePressure.FromAvailable(avail, total),
                DiskFreeGb = DriveGb(d => d.AvailableFreeSpace),
            };
        }
        var (inK, outK) = Network(now);
        r = r with { NetInKbps = inK, NetOutKbps = outK };
        if (_docker && _loads++ % DockerEvery == 0)
        {
            var (used, limit) = MachineParse.DockerStats(_run("docker", "stats --no-stream --format \"{{.MemUsage}}\""));
            r = r with { DockerMemUsedMb = used, DockerMemLimitMb = limit };
        }
        return r;
    }

    private string? Sysctl(string name) => _run("sysctl", "-n " + name);

    private string? Tool(string name) =>
        _win ? _run("cmd.exe", $"/d /c {name} --version") : _run("/bin/sh", $"-lc \"{name} --version\"");

    private double? DriveGb(Func<DriveInfo, long> pick)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_dataRoot));
            return root is null ? null : Math.Round(pick(new DriveInfo(root)) / 1024.0 / 1024 / 1024, 1);
        }
        catch { return null; }
    }

    /// <summary>Busy % since the last reading; the first one waits a second to have something to compare.</summary>
    private double? WinCpu()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (_cpu is null) { _cpu = Win.CpuTimes(); Thread.Sleep(1000); }
        var now = Win.CpuTimes();
        var prev = _cpu;
        _cpu = now;
        if (now is not { } n || prev is not { } p || n.Total <= p.Total) return null;
        return Math.Round(100.0 * (1 - (double)(n.Idle - p.Idle) / (n.Total - p.Total)), 1);
    }

    /// <summary>Bytes in and out across real interfaces since the last reading, as kilobits a second.</summary>
    private (double?, double?) Network(DateTime now)
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up
                    || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch { return (null, null); }
        var prev = _net;
        _net = (now, rx, tx);
        if (prev is not { } p) return (null, null);
        var secs = (now - p.At).TotalSeconds;
        if (secs <= 0 || rx < p.Rx || tx < p.Tx) return (null, null);
        return (Math.Round((rx - p.Rx) * 8 / 1000.0 / secs, 1), Math.Round((tx - p.Tx) * 8 / 1000.0 / secs, 1));
    }

    private static string? RunProcess(string file, string args)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(file, args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                },
            };
            p.Start();
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static class Win
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryStatusEx
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr device, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static MemoryStatusEx? Memory()
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref m) ? m : null;
        }

        /// <summary>Kernel time includes idle, so total is kernel + user.</summary>
        public static (ulong Idle, ulong Total)? CpuTimes() =>
            GetSystemTimes(out var idle, out var kernel, out var user) ? ((ulong)idle, (ulong)(kernel + user)) : null;

        public static string? CpuName()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (k?.GetValue("ProcessorNameString") as string)?.Trim();
            }
            catch { return null; }
        }

        public static string? OsVersion()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var v = Environment.OSVersion.Version;
                // ProductName still says "Windows 10" on 11; the build number is what tells them apart.
                var name = v.Major == 10 && v.Build >= 22000 ? "11" : v.Major == 10 ? "10" : v.Major.ToString();
                var display = k?.GetValue("DisplayVersion") as string;
                return $"{name} {display} (build {v.Build})".Replace("  ", " ");
            }
            catch { return null; }
        }

        /// <summary>
        /// Physical cores, and on a hybrid CPU how many are performance and how many efficiency cores:
        /// each core record carries an efficiency class, and the highest class is the performance one.
        /// A CPU whose cores are all one class is not hybrid, and reports neither.
        /// </summary>
        public static (int? Cores, int? Perf, int? Eff) Cores()
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len == 0) return (null, null, null);
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return (null, null, null);
                var classes = new List<byte>();
                for (var off = 0; off < len;)
                {
                    var size = Marshal.ReadInt32(buf, off + 4);
                    classes.Add(Marshal.ReadByte(buf, off + 9));
                    if (size <= 0) break;
                    off += size;
                }
                if (classes.Count == 0) return (null, null, null);
                if (classes.Distinct().Count() < 2) return (classes.Count, null, null);
                var top = classes.Max();
                var perf = classes.Count(c => c == top);
                return (classes.Count, perf, classes.Count - perf);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Every page file's pages in use (SystemPageFileInformation), in MB.</summary>
        public static long? PageFileInUseMb()
        {
            for (var size = 4096; size <= 1 << 20; size *= 4)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQuerySystemInformation(18, buf, size, out _);
                    if (status == unchecked((int)0xC0000004)) continue;   // too small
                    if (status != 0) return null;
                    long pages = 0;
                    for (var off = 0; ;)
                    {
                        pages += (uint)Marshal.ReadInt32(buf, off + 8);
                        var next = Marshal.ReadInt32(buf, off);
                        if (next <= 0) break;
                        off += next;
                    }
                    return pages * Environment.SystemPageSize / (1024 * 1024);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }

        /// <summary>
        /// SSD or HDD for the volume holding <paramref name="path"/>, by whether the device says it
        /// incurs a seek penalty (IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceSeekPenaltyProperty).
        /// </summary>
        public static string? DiskKind(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd('\\');
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null;
                var h = CreateFile(@"\\.\" + root, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1)) return null;
                try
                {
                    var query = new byte[12];
                    BitConverter.GetBytes(7).CopyTo(query, 0);   // StorageDeviceSeekPenaltyProperty
                    var result = new byte[12];
                    if (!DeviceIoControl(h, 0x2D1400, query, query.Length, result, result.Length, out var got, IntPtr.Zero) || got < 9)
                        return null;
                    return result[8] == 0 ? "SSD" : "HDD";
                }
                finally { CloseHandle(h); }
            }
            catch { return null; }
        }
    }
}

/// <summary>
/// Reads the board host's own machine: its load every minute into the <see cref="MachineLoadBook"/>,
/// and its inventory at start and hourly into the history. Runs on a QA copy too, without docker -
/// reading this machine reaches nothing past it.
/// </summary>
public class MachineProbeService : BackgroundService
{
    public static readonly TimeSpan InventoryEvery = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly MachineLoadBook _book;
    private readonly MachineProbe _probe;
    private readonly ILogger<MachineProbeService> _log;

    public MachineProbeService(IServiceScopeFactory scopes, MachineLoadBook book, DashboardPaths paths, QaIsolation isolation,
        ILogger<MachineProbeService> log)
    {
        (_scopes, _book, _log) = (scopes, book, log);
        _probe = new MachineProbe(paths.DataRoot, docker: !isolation.On);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var inventoryAt = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            try
            {
                // Off the request threads: a load reading can take a second, an inventory several.
                var load = await Task.Run(() => _probe.Load(now), ct);
                _book.Set(CapacityPlaces.Board, load, now);
                if (now - inventoryAt >= InventoryEvery)
                {
                    var inv = await Task.Run(_probe.Inventory, ct);
                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
                    await MachineInventoryStore.RecordAsync(db, CapacityPlaces.Board, inv, now, ct);
                    inventoryAt = now;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogDebug(ex, "Machine probe failed");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
