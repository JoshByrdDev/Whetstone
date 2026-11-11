using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Api;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// Each machine's hardware, load and suggested slot counts (docs/machine-limits-plan.md): the
/// parsers on captured macOS and Docker output (the same files data/machine-info.tests.ps1 reads),
/// the board host's own probe on a pretend Mac, the inventory kept only on change, and the
/// suggestion rules - hardware, learned, ceilings, and run speed split into model and machine time.
/// </summary>
public class MachineLimitsTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly MachineLimitsConfig Cfg = new();

    public MachineLimitsTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private DashboardDbContext Db() => new(_options);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Whetstone.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "data", "machine-fixtures", name));

    // ---- Parsers: the same answers data/machine-info.tests.ps1 checks ----

    [Fact]
    public void The_mac_parsers_read_the_shared_fixtures_as_the_PowerShell_collector_does()
    {
        Assert.Equal(8640, MachineParse.VmStatAvailableMb(Fixture("mac-vm_stat.txt")));
        Assert.Equal(1536, MachineParse.SwapUsedMb(Fixture("mac-swapusage.txt")));
        Assert.Equal(42.5, MachineParse.TopCpuPct(Fixture("mac-top.txt")));
        Assert.Equal(239.6, MachineParse.DfFreeGb(Fixture("mac-df.txt")));
        Assert.Equal(460.4, MachineParse.DfTotalGb(Fixture("mac-df.txt")));
        Assert.Equal("SSD", MachineParse.DiskutilKind(Fixture("mac-diskutil.txt")));
        Assert.Equal((8, 7836L), MachineParse.DockerInfo(Fixture("docker-info.txt")));
        Assert.Equal((1660L, 7838L), MachineParse.DockerStats(Fixture("docker-stats.txt")));
    }

    [Fact]
    public void No_docker_is_no_reading_and_docker_with_nothing_running_is_zero()
    {
        Assert.Equal((null, null), MachineParse.DockerStats(null));
        Assert.Equal((0L, null), MachineParse.DockerStats(""));
    }

    [Theory]
    [InlineData(1, MachinePressure.Normal)]
    [InlineData(2, MachinePressure.Warn)]
    [InlineData(4, MachinePressure.Critical)]
    public void Mac_pressure_levels_map_to_the_shared_three(int level, int want) =>
        Assert.Equal(want, MachinePressure.FromMacLevel(level));

    [Fact]
    public void Windows_pressure_is_derived_from_what_is_available()
    {
        Assert.Equal(MachinePressure.Normal, MachinePressure.FromAvailable(4000, 10000));
        Assert.Equal(MachinePressure.Warn, MachinePressure.FromAvailable(800, 10000));
        Assert.Equal(MachinePressure.Critical, MachinePressure.FromAvailable(400, 10000));
    }

    // ---- The board host's probe, on a pretend Mac ----

    private static MachineProbe.Runner FakeMac(Dictionary<string, string> extra) => (file, args) =>
    {
        var key = $"{file} {args}".Trim();
        var known = new Dictionary<string, string>
        {
            ["sysctl -n hw.perflevel0.physicalcpu"] = "4\n", ["sysctl -n hw.perflevel1.physicalcpu"] = "6\n",
            ["sysctl -n machdep.cpu.brand_string"] = "Apple M5\n", ["sysctl -n hw.physicalcpu"] = "10\n",
            ["sysctl -n hw.logicalcpu"] = "10\n", ["sysctl -n hw.memsize"] = "51539607552\n",
            ["sw_vers -productVersion"] = "26.1\n", ["uname -m"] = "arm64\n",
            ["sysctl -n vm.swapusage"] = Fixture("mac-swapusage.txt"),
            ["sysctl -n kern.memorystatus_vm_pressure_level"] = "4\n",
            ["vm_stat"] = Fixture("mac-vm_stat.txt"), ["top -l 2 -n 0 -s 1"] = Fixture("mac-top.txt"),
        };
        foreach (var kv in extra) known[kv.Key] = kv.Value;
        if (known.TryGetValue(key, out var v)) return v;
        if (file == "df") return Fixture("mac-df.txt");
        if (file == "diskutil") return Fixture("mac-diskutil.txt");
        if (file == "docker" && args.StartsWith("info")) return Fixture("docker-info.txt");
        if (file == "docker" && args.StartsWith("stats")) return Fixture("docker-stats.txt");
        if (file == "/bin/sh" && args.Contains("node --version")) return "v22.11.0\n";
        return null;
    };

    [Fact]
    public void The_board_probe_reads_a_mac_as_the_runner_does()
    {
        var probe = new MachineProbe("/Users/me/data", docker: true, FakeMac(new()), mac: true, windows: false);
        var inv = probe.Inventory();
        Assert.Equal("Apple M5", inv.CpuModel);
        Assert.Equal((4, 6), (inv.PerfCores, inv.EffCores));
        Assert.Equal(49152, inv.RamMb);
        Assert.Equal(("macOS", "26.1", "arm64"), (inv.Os, inv.OsVersion, inv.Arch));
        Assert.Equal("SSD", inv.DiskKind);
        Assert.Equal((8, 7836L), (inv.DockerCpus, inv.DockerMemMb));
        Assert.Equal("22.11.0", inv.Tools!["node"]);
        Assert.False(inv.Tools.ContainsKey("pnpm"));

        var load = probe.Load(Now);
        Assert.Equal(42.5, load.CpuPct);
        Assert.Equal(8640, load.MemAvailMb);
        Assert.Equal(1536, load.SwapUsedMb);
        Assert.Equal(MachinePressure.Critical, load.Pressure);
        Assert.Equal(1660, load.DockerMemUsedMb);
        // Docker is read on the first of every few samples, not every minute.
        Assert.Null(probe.Load(Now.AddMinutes(1)).DockerMemUsedMb);
    }

    [Fact]
    public void An_intel_mac_reports_no_core_split_and_a_qa_copy_reads_no_docker()
    {
        var probe = new MachineProbe("/x", docker: false, FakeMac(new()
        {
            ["sysctl -n hw.perflevel0.physicalcpu"] = "", ["sysctl -n hw.perflevel1.physicalcpu"] = "",
        }), mac: true, windows: false);
        var inv = probe.Inventory();
        Assert.Null(inv.PerfCores);
        Assert.Null(inv.EffCores);
        Assert.Null(inv.DockerMemMb);
        Assert.Null(probe.Load(Now).DockerMemUsedMb);
    }

    // ---- Inventory, kept as a history of changes ----

    private static MachineInventoryReport Report(long ram = 16384, params (string, string)[] tools) =>
        new(CpuModel: "Example CPU", PhysicalCores: 6, LogicalCores: 12, RamMb: ram, Os: "Windows",
            Tools: tools.ToDictionary(t => t.Item1, t => t.Item2));

    [Fact]
    public async Task The_same_hardware_moves_last_seen_and_a_change_opens_a_new_row()
    {
        using (var db = Db())
        {
            Assert.True(await MachineInventoryStore.RecordAsync(db, "bravo-host", Report(16384, ("node", "22.1.0"), ("git", "2.4")), Now));
            // The same tools in another order are the same report.
            Assert.False(await MachineInventoryStore.RecordAsync(db, "bravo-host", Report(16384, ("git", "2.4"), ("node", "22.1.0")), Now.AddHours(1)));
            Assert.True(await MachineInventoryStore.RecordAsync(db, "bravo-host", Report(32768, ("git", "2.4"), ("node", "22.1.0")), Now.AddDays(2)));
        }
        using var read = Db();
        var rows = await read.MachineInventories.OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal((Now, Now.AddHours(1)), (rows[0].FirstSeen, rows[0].LastSeen));
        Assert.Equal(32768, rows[1].RamMb);
        Assert.Equal(Now.AddDays(2), rows[1].FirstSeen);
        Assert.Equal("22.1.0", MachineInventoryStore.Read(rows[1])!.Tools!["node"]);
    }

    [Fact]
    public async Task A_check_in_holds_the_load_and_keeps_the_hardware()
    {
        var book = new MachineLoadBook();
        using (var db = Db())
            await WorkerHostEndpoints.RecordMachineAsync(db, book, "bravo-host", Report(), new MachineLoadReport(CpuPct: 50), Now);
        Assert.Equal(50, book.Get("bravo-host", Now.AddMinutes(1))!.CpuPct);
        // A machine that stopped reporting has no load now, not its last one.
        Assert.Null(book.Get("bravo-host", Now.AddMinutes(10)));
        using var read = Db();
        Assert.Equal(1, await read.MachineInventories.CountAsync(m => m.Place == "bravo-host"));
    }

    // ---- Rule A: hardware alone ----

    [Fact]
    public void Hardware_alone_for_a_48GB_mac_mini_is_limited_by_its_cores()
    {
        var inv = new MachineInventoryReport(PerfCores: 4, EffCores: 6, PhysicalCores: 10, RamMb: 48 * 1024);
        var (value, why) = MachineLimits.FromHardware(inv, Cfg, WorkerHostConfig.MaxSlots);
        // Memory: (48 - 7.2) / 2.5 = 16. CPU: 4 + 6 * 0.5 = 7, one kept back = 6.
        Assert.Equal(6, value);
        Assert.Contains(why, w => w.Contains("4 performance + 6 efficiency"));
    }

    [Fact]
    public void Hardware_alone_for_a_16GB_six_core_machine_is_limited_by_memory()
    {
        var inv = new MachineInventoryReport(PhysicalCores: 6, LogicalCores: 12, RamMb: 16 * 1024);
        Assert.Equal(4, MachineLimits.FromHardware(inv, Cfg, 10).Value);
    }

    [Fact]
    public void No_report_is_no_hardware_suggestion() =>
        Assert.Null(MachineLimits.FromHardware(null, Cfg, 10).Value);

    [Fact]
    public void QA_on_hardware_is_sized_by_dockers_memory_and_never_above_the_berths()
    {
        var inv = new MachineInventoryReport(DockerMemMb: 24 * 1024);
        Assert.Equal(6, MachineLimits.QaFromHardware(inv, Cfg, 6).Value);   // (24 - 1) / 3 = 7, held to 6
        Assert.Equal(2, MachineLimits.QaFromHardware(new(DockerMemMb: 7836), Cfg, 6).Value);
        Assert.Null(MachineLimits.QaFromHardware(new(), Cfg, 6).Value);
    }

    // ---- Rule B: learned from the machine's own minutes ----

    private static List<MachineLoadSample> Minutes(Func<int, double> cpu, Func<int, long> availMb, int perLevel = 120, int[]? levels = null,
        string place = "bravo-host", long totalMb = 32768)
    {
        var list = new List<MachineLoadSample>();
        var at = Now.AddDays(-3);
        foreach (var n in levels ?? new[] { 1, 2, 3, 4 })
            for (var i = 0; i < perLevel; i++)
                list.Add(new MachineLoadSample
                {
                    Place = place, At = at = at.AddMinutes(1), Busy = n,
                    CpuPct = cpu(n) + (i % 10) - 5, MemAvailMb = availMb(n) + (i % 7) * 10, MemTotalMb = totalMb,
                });
        return list;
    }

    [Fact]
    public void Learning_waits_for_enough_busy_minutes()
    {
        var few = Minutes(n => 20 + 15 * n, _ => 20000, perLevel: 30);
        var (value, conf, why) = MachineLimits.FromLoad(few, null, Cfg, 8);
        Assert.Null(value);
        Assert.Equal("low", conf);
        Assert.Contains(why, w => w.Contains("learning needs"));
    }

    [Fact]
    public void Learning_finds_where_cpu_would_pass_its_ceiling()
    {
        // p90 is about 4 over the middle; 24 + 15n passes 85% a little past 4.
        var samples = Minutes(n => 20 + 15 * n, _ => 20000);
        var (value, conf, why) = MachineLimits.FromLoad(samples, null, Cfg, 8);
        Assert.Equal(4, value);
        Assert.Equal("medium", conf);
        Assert.Contains(why, w => w.StartsWith("CPU (p90) rises"));
    }

    [Fact]
    public void Learning_finds_where_memory_would_reach_the_reserve()
    {
        // 32GB: reserve max(4, 4.8) = 4.8GB. Available falls 4GB a berth from 24GB: reaches it near 4.8.
        var samples = Minutes(_ => 30, n => (long)((24 - 4 * n) * 1024));
        var (value, _, why) = MachineLimits.FromLoad(samples, null, Cfg, 8);
        Assert.Equal(4, value);
        Assert.Contains(why, w => w.Contains("reserve"));
    }

    [Fact]
    public void An_idle_machine_is_not_extrapolated_far_past_what_it_has_seen()
    {
        var samples = Minutes(_ => 12, _ => 20000, levels: new[] { 1, 2, 3 });
        var (value, _, why) = MachineLimits.FromLoad(samples, null, Cfg, 10);
        Assert.Equal(5, value);
        Assert.Contains(why, w => w.Contains("hardly moves"));
        Assert.Contains(why, w => w.StartsWith("Nothing measured here limits it yet"));

        // A line that would allow far more is held to twice the busiest seen.
        var gentle = Minutes(n => 10 + 2 * n, _ => 20000, levels: new[] { 1, 2, 3 });
        var held = MachineLimits.FromLoad(gentle, null, Cfg, 10);
        Assert.Equal(6, held.Value);
        Assert.Contains(held.Why, w => w.StartsWith("Held to 6"));
    }

    [Fact]
    public void QA_is_learned_from_what_a_stack_adds_to_docker()
    {
        var list = new List<MachineLoadSample>();
        var at = Now.AddHours(-10);
        foreach (var q in new[] { 0, 1, 2 })
            for (var i = 0; i < 10; i++)
                list.Add(new MachineLoadSample
                {
                    Place = "bravo-host", At = at = at.AddMinutes(5), Busy = q + 1,
                    PhasesJson = q == 0 ? "{\"Implementing\":1}" : $"{{\"QaTesting\":{q},\"Implementing\":1}}",
                    DockerMemUsedMb = 1024 + q * 3 * 1024, DockerMemLimitMb = 16 * 1024,
                });
        // 16GB * 0.9 = 14.4; (14.4 - 1) / 3 = 4.4.
        Assert.Equal(4, MachineLimits.QaFromLoad(list, Cfg, 8).Value);
    }

    // ---- Rule C: ceilings ----

    private static int? NoBusy(DateTime _) => null;

    [Fact]
    public void Five_minutes_of_memory_pressure_caps_one_below_the_berths_busy()
    {
        var samples = Enumerable.Range(0, 8).Select(i => new MachineLoadSample
        {
            At = Now.AddHours(-2).AddMinutes(i), Busy = i < 6 ? 5 : 1, Pressure = i < 6 ? MachinePressure.Warn : MachinePressure.Normal,
        }).ToList();
        var c = Assert.Single(MachineLimits.Ceilings(samples, Array.Empty<DateTime>(), NoBusy, Cfg, Now));
        Assert.Equal(4, c.Cap);
        Assert.Contains("6 minutes with 5 berth(s)", c.Why);
    }

    [Fact]
    public void A_pressure_blip_is_not_a_ceiling()
    {
        var samples = Enumerable.Range(0, 8).Select(i => new MachineLoadSample
        {
            At = Now.AddHours(-2).AddMinutes(i), Busy = 5, Pressure = i == 3 ? MachinePressure.Critical : MachinePressure.Normal,
        }).ToList();
        Assert.Empty(MachineLimits.Ceilings(samples, Array.Empty<DateTime>(), NoBusy, Cfg, Now));
    }

    [Fact]
    public void Growing_swap_caps_below_its_busy_count()
    {
        var samples = Enumerable.Range(0, 7).Select(i => new MachineLoadSample
        {
            At = Now.AddHours(-1).AddMinutes(i), Busy = 4, SwapUsedMb = 1000 + i * 100,
        }).ToList();
        var c = Assert.Single(MachineLimits.Ceilings(samples, Array.Empty<DateTime>(), NoBusy, Cfg, Now));
        Assert.Equal(3, c.Cap);
        Assert.StartsWith("Swap grew 500MB", c.Why);
    }

    [Fact]
    public void A_setup_timeout_caps_below_how_busy_the_machine_was_and_lapses_after_a_week()
    {
        var timeouts = new[] { Now.AddHours(-5), Now.AddHours(-4), Now.AddDays(-9) };
        var c = Assert.Single(MachineLimits.Ceilings(Array.Empty<MachineLoadSample>(), timeouts, _ => 6, Cfg, Now));
        Assert.Equal(5, c.Cap);
        Assert.Contains("timed out 2 time(s)", c.Why);
    }

    [Fact]
    public void Docker_near_its_limit_caps_qa()
    {
        var samples = new List<MachineLoadSample>
        {
            new() { At = Now.AddHours(-1), Busy = 3, DockerMemUsedMb = 7500, DockerMemLimitMb = 7838, PhasesJson = "{\"QaTesting\":2,\"Implementing\":1}" },
        };
        var c = Assert.Single(MachineLimits.Ceilings(samples, Array.Empty<DateTime>(), NoBusy, Cfg, Now));
        Assert.True(c.Qa);
        Assert.Equal(1, c.Cap);
    }

    [Fact]
    public void A_nearly_full_worktree_drive_holds_the_berths_at_what_is_busy()
    {
        var samples = new List<MachineLoadSample> { new() { At = Now.AddMinutes(-1), Busy = 2, DiskFreeGb = 9.5 } };
        Assert.Equal(2, Assert.Single(MachineLimits.Ceilings(samples, Array.Empty<DateTime>(), NoBusy, Cfg, Now)).Cap);
    }

    [Fact]
    public void A_timeout_comment_names_its_host()
    {
        Assert.Equal("bravo-host", MachineLimitsService.PlaceOfComment(
            "Heartbeat could not prepare a git worktree for this ticket on worker host `bravo-host`. Ticket blocked.\n\nworktree setup command timed out after 20 min"));
        Assert.Equal(CapacityPlaces.Board, MachineLimitsService.PlaceOfComment(
            "Heartbeat could not prepare a git worktree for this ticket. Ticket blocked.\n\nworktree setup command timed out after 20 min"));
        // The block reason a timeout also leaves names no machine, and is not a second timeout anywhere.
        Assert.Null(MachineLimitsService.PlaceOfComment(
            "Blocked: Workspace setup failed (implement): worktree setup command timed out after 20 min and was stopped: pnpm install"));
        Assert.Null(MachineLimitsService.PlaceOfComment("Heartbeat could not prepare a git worktree for this ticket. It failed (exit 1)."));
    }

    // ---- Run speed: model time against machine time ----

    /// <summary>
    /// Runs on one machine at 0..maxOthers others at once, each level in its own stretch of time so
    /// the overlap is exact: k+1 runs started together, each lasting the same.
    /// </summary>
    private static List<RunTiming> Runs(int maxOthers, Func<int, double> modelPerTurn, Func<int, double> machinePerTurn, int turns = 30)
    {
        var list = new List<RunTiming>();
        var at = Now.AddDays(-10);
        for (var k = 0; k <= maxOthers; k++)
            for (var rep = 0; rep < 6; rep++)
            {
                var api = modelPerTurn(k) * turns;
                var dur = api + machinePerTurn(k) * turns;
                for (var r = 0; r <= k; r++)
                    list.Add(new RunTiming("bravo-host", at, at.AddSeconds(dur), dur, api, turns, 0, 0));
                at = at.AddSeconds(dur + 60);
            }
        return list;
    }

    [Fact]
    public void A_slowdown_that_is_model_time_is_called_so()
    {
        var speed = MachineLimits.Speed(Runs(4, k => 25 + 9 * k, _ => 1), Cfg)!;
        Assert.Equal("model", speed.Bound);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, speed.Points.Select(p => p.Others));
        Assert.Equal(26, speed.Points[0].SecPerTurn);
        Assert.Contains("waiting on the model", speed.Summary);
    }

    [Fact]
    public void A_model_bound_slowdown_does_not_lower_the_suggestion_but_a_machine_bound_one_does()
    {
        var inv = new MachineInventoryReport(PhysicalCores: 16, RamMb: 64 * 1024);
        var model = MachineLimits.Suggest("bravo-host", 6, 2, 8, inv, Array.Empty<MachineLoadSample>(), Array.Empty<DateTime>(), NoBusy,
            Runs(5, k => 25 + 12 * k, _ => 1), Cfg, Now);
        Assert.Equal(8, model.Workers.Suggested);   // hardware: min(22, 15) held to 8
        Assert.Equal("hardware", model.Workers.Basis);

        var machine = MachineLimits.Suggest("bravo-host", 6, 2, 8, inv, Array.Empty<MachineLoadSample>(), Array.Empty<DateTime>(), NoBusy,
            Runs(5, _ => 20, k => 5 + 15 * k), Cfg, Now);
        Assert.Equal("machine", machine.Speed!.Bound);
        Assert.True(machine.Workers.Suggested < 8);
        Assert.Equal(machine.Speed.MostWork, machine.Workers.Suggested);
    }

    [Fact]
    public void Both_speed_points_are_found()
    {
        // Time per turn doubles at 3 others; throughput 1, 1.6, 2.0, 2.0, 2.0.
        var spt = new[] { 20.0, 25, 30, 40, 50 };
        var speed = MachineLimits.Speed(Runs(4, k => spt[k] - 1, _ => 1), Cfg)!;
        Assert.Equal(3, speed.MostWork);   // past 3 at once, one more adds under 12%
        Assert.Equal(4, speed.Fastest);    // 4 at once is the last at no more than 2x alone
    }

    [Fact]
    public void A_ceiling_outranks_what_was_learned()
    {
        var samples = Minutes(n => 20 + 15 * n, _ => 20000);
        samples.AddRange(Enumerable.Range(0, 6).Select(i => new MachineLoadSample
        {
            Place = "bravo-host", At = Now.AddHours(-1).AddMinutes(i), Busy = 3, Pressure = MachinePressure.Critical, CpuPct = 70,
        }));
        var s = MachineLimits.Suggest("bravo-host", 6, 2, 8, null, samples, Array.Empty<DateTime>(), NoBusy, Array.Empty<RunTiming>(), Cfg, Now);
        Assert.Equal(2, s.Workers.Suggested);
        Assert.Equal("ceiling", s.Workers.Basis);
        Assert.Equal("high", s.Workers.Confidence);
        Assert.Equal(6, s.Workers.Set);
    }

    // ---- The route's reading, and the switch ----

    [Fact]
    public async Task The_machines_route_reads_every_configured_machine_with_auto_off_by_default()
    {
        var root = Path.Combine(Path.GetTempPath(), "whetstone-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var config = new JsonObject
        {
            ["heartbeatWorkers"] = 3,
            ["workerHosts"] = new JsonArray(new JsonObject { ["name"] = "bravo-host", ["slots"] = 6, ["qaLeases"] = 2 }),
        };
        File.WriteAllText(Path.Combine(root, "config.json"), config.ToJsonString());
        using (var db = Db())
        {
            db.Tickets.Add(new Ticket { Id = 1, ProjectKey = "alpha", Title = "t" });
            await db.SaveChangesAsync();
            await MachineInventoryStore.RecordAsync(db, "bravo-host", new MachineInventoryReport(PerfCores: 4, EffCores: 6, RamMb: 48 * 1024, DockerMemMb: 8 * 1024), Now);
            db.Comments.Add(new Comment { TicketId = 1, CreatedAt = Now.AddHours(-1),
                Body = "Blocked: Workspace setup failed (implement): worktree setup command timed out after 20 min" });
            db.Comments.Add(new Comment { TicketId = 1, CreatedAt = Now.AddHours(-1),
                Body = "Heartbeat could not prepare a git worktree for this ticket on worker host `bravo-host`.\n\nworktree setup command timed out after 20 min" });
            db.CapacitySamples.Add(new CapacitySample { Place = "bravo-host", At = Now.AddHours(-1), Slots = 6, Busy = 5 });
            await db.SaveChangesAsync();
        }
        using var read = Db();
        var svc = new MachineLimitsService(read, new ProjectRegistry(new DashboardPaths(root)));
        var json = System.Text.Json.JsonSerializer.SerializeToNode(await svc.ReadAsync(Now), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.False(json["auto"]!.GetValue<bool>());
        var machines = json["machines"]!.AsArray();
        Assert.Equal(new[] { CapacityPlaces.Board, "bravo-host" }, machines.Select(m => m!["place"]!.GetValue<string>()));
        var host = machines[1]!;
        Assert.Equal(6, host["workers"]!["set"]!.GetValue<int>());
        // Hardware says 6; the timeout with 5 busy caps it at 4.
        Assert.Equal(4, host["workers"]!["suggested"]!.GetValue<int>());
        Assert.Equal("ceiling", host["workers"]!["basis"]!.GetValue<string>());
        Assert.Equal(2, host["qa"]!["suggested"]!.GetValue<int>());
        Assert.Equal(3, machines[0]!["workers"]!["set"]!.GetValue<int>());
        Assert.Null(machines[0]!["workers"]!["suggested"]);
        // The host's block-reason comment did not make a timeout on the board host too.
        Assert.DoesNotContain(machines[0]!["workers"]!["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("timed out"));
    }

    [Fact]
    public void The_auto_switch_is_read_from_config_and_off_without_it()
    {
        Assert.False(MachineLimitsConfig.Read(new Dictionary<string, System.Text.Json.JsonElement>()).Auto);
        var on = System.Text.Json.JsonDocument.Parse("{\"auto\":true,\"berthGb\":3}").RootElement;
        var c = MachineLimitsConfig.Read(new Dictionary<string, System.Text.Json.JsonElement> { [MachineLimitsConfig.ConfigKey] = on });
        Assert.True(c.Auto);
        Assert.Equal(3, c.BerthGb);
        Assert.Equal(4, c.ReserveGbMin);
    }
}
