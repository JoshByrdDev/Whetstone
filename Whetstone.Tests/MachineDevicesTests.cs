using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Xunit;

namespace Whetstone.Tests;

/// <summary>
/// The iPhone simulator for the fleet (docs/ios-simulator-plan.md): a machine says which devices it
/// has, a claim from one that lacks a device a run needs passes the run over for a machine that has
/// it, the board says why such a run waits when no machine does, and a freed simulator lease shuts
/// down what its run booted and nothing else. The lease itself stays generic: ios:simulator is
/// leased, host-qualified and released like any other resource.
/// </summary>
public class MachineDevicesTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<DashboardDbContext> _options;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "whetstone-devices-" + Guid.NewGuid().ToString("N")[..8]);

    public MachineDevicesTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _options = new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite(_conn).Options;
        using var ctx = new DashboardDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_temp, true); } catch { }
    }

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(150);
    private static readonly IReadOnlyList<string> AMac = new[] { MachineDevices.IosSimulator };
    private static readonly IReadOnlyList<string> NotAMac = Array.Empty<string>();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Whetstone.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "data", "machine-fixtures", name));

    // ---- The rule ---------------------------------------------------------------------

    [Fact]
    public void Only_the_devices_in_the_table_are_gated_and_the_name_is_the_same_on_a_worker_host()
    {
        Assert.Equal(MachineDevices.IosSimulator, MachineDevices.For("ios:simulator")!.Key);
        Assert.Equal(MachineDevices.IosSimulator, MachineDevices.For("ios:simulator@bravo-host")!.Key);
        Assert.Null(MachineDevices.For("android:emulator"));
        Assert.Null(MachineDevices.For("alpha:app-stack"));
        Assert.Null(MachineDevices.For("ticket:12"));
    }

    [Fact]
    public void A_machine_lacking_a_device_a_run_needs_is_told_which_and_one_that_has_it_is_not()
    {
        var needs = new[] { "alpha:app-stack@bravo-host", "ios:simulator@bravo-host" };
        Assert.Equal(MachineDevices.IosSimulator, MachineDevices.Lacking(needs, NotAMac)!.Key);
        Assert.Null(MachineDevices.Lacking(needs, AMac));
        // A runner that never said has none: a wait, never a run that cannot start.
        Assert.NotNull(MachineDevices.Lacking(needs, null));
        // Nothing gated in the run: any machine.
        Assert.Null(MachineDevices.Lacking(new[] { "alpha:app-stack", "android:emulator" }, null));
    }

    [Fact]
    public void The_words_are_the_ones_the_board_shows()
    {
        Assert.Equal("waiting for an iPhone simulator, which only a Mac has", MachineDevices.For("ios:simulator")!.Waiting);
    }

    [Fact]
    public void A_reported_list_keeps_only_devices_the_table_knows()
    {
        Assert.Equal(new[] { "ios:simulator" }, MachineDevices.Clean(new[] { " ios:simulator ", "ios:simulator", "ios:simulatr", "" }));
        Assert.Empty(MachineDevices.Clean(null));
    }

    [Fact]
    public void A_project_names_the_simulator_in_its_phase_resources_or_its_resource_paths()
    {
        Assert.Equal(AMac, MachineDevices.NamedBy(new ProjectConfig { Name = "alpha", PhaseResources = new() { ["qa-test"] = new[] { "app-stack", "ios:simulator" } } }));
        Assert.Equal(AMac, MachineDevices.NamedBy(new ProjectConfig { Name = "alpha", ResourcePaths = new() { ["ios:simulator"] = new[] { "apps/app/" } } }));
        Assert.Empty(MachineDevices.NamedBy(new ProjectConfig { Name = "alpha", PhaseResources = new() { ["qa-test"] = new[] { "android:emulator" } } }));
    }

    // ---- Reading a Mac: the same fixtures data/machine-info.tests.ps1 reads ----------

    [Fact]
    public void Simctl_output_is_read_the_way_the_runner_reads_it()
    {
        Assert.Equal(1, SimulatorParse.IosRuntimes(Fixture("mac-simctl-runtimes.json")));
        Assert.Null(SimulatorParse.IosRuntimes("xcrun: error"));
        Assert.Equal(0, SimulatorParse.IosRuntimes("""{ "runtimes" : [ ] }"""));
        Assert.Equal(new[] { "3F1C2B9A-6D7E-4A10-9B2C-0E5D8A7F6B41" }, SimulatorParse.Booted(Fixture("mac-simctl-booted.json")));
        Assert.Empty(SimulatorParse.Booted(""));
    }

    [Fact]
    public void The_powershell_copy_names_the_same_device()
    {
        var ps = File.ReadAllText(Path.Combine(RepoRoot(), "data", "machine-info.ps1"));
        Assert.Contains($"$IosSimulatorDevice = '{MachineDevices.IosSimulator}'", ps);
        // And runs the same commands IosToolchain.Probe runs.
        foreach (var cmd in new[] { "cmd:xcode-select -p", "cmd:xcodebuild -license check", "cmd:xcodebuild -checkFirstLaunchStatus", "cmd:xcrun simctl list runtimes -j" })
            Assert.Contains(cmd, ps);
    }

    private static MachineProbe.Runner Xcode(params string[] failing) => (file, args) =>
    {
        var cmd = $"{file} {args}";
        if (failing.Contains(cmd)) return null;
        return cmd switch
        {
            "xcode-select -p" => "/Applications/Xcode.app/Contents/Developer\n",
            "xcrun simctl list runtimes -j" => Fixture("mac-simctl-runtimes.json"),
            _ => "",
        };
    };

    [Fact]
    public void A_Mac_with_Xcode_its_licence_and_a_runtime_has_the_simulator()
    {
        var ios = IosToolchain.Probe(Xcode(), mac: true);
        Assert.True(ios.Has);
        Assert.Empty(ios.Gaps());
        Assert.False(IosToolchain.Probe(Xcode(), mac: false).Has);
    }

    [Fact]
    public void Each_gap_says_the_command_that_fixes_it_and_nothing_is_run_for_the_owner()
    {
        var noLicence = IosToolchain.Probe(Xcode("xcodebuild -license check"), mac: true);
        Assert.False(noLicence.Has);
        Assert.Equal("sudo xcodebuild -license accept", Assert.Single(noLicence.Gaps()).Fix);

        var noXcode = IosToolchain.Probe(Xcode("xcode-select -p"), mac: true);
        Assert.False(noXcode.Has);
        Assert.Contains("App Store", Assert.Single(noXcode.Gaps()).Fix);
        Assert.Null(noXcode.Licence);   // nothing after the step that failed is read

        var noRuntime = IosToolchain.Probe((f, a) => $"{f} {a}" == "xcrun simctl list runtimes -j" ? """{ "runtimes" : [ ] }""" : Xcode()(f, a), mac: true);
        Assert.False(noRuntime.Has);
        Assert.Equal("xcodebuild -downloadPlatform iOS", Assert.Single(noRuntime.Gaps()).Fix);
    }

    [Fact]
    public void The_board_reads_its_own_toolchain_at_most_every_few_minutes()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var calls = 0;
        var devices = new BoardDevices((f, a) => { calls++; return Xcode()(f, a); }, mac: true, () => now);
        Assert.Equal(AMac, devices.Has());
        var first = calls;
        Assert.Equal(AMac, devices.Has());
        Assert.Equal(first, calls);
        now += BoardDevices.Fresh;
        devices.Has();
        Assert.True(calls > first);
    }

    // ---- The Setup check ---------------------------------------------------------------

    [Fact]
    public void With_no_project_naming_it_the_row_reads_as_not_needed_never_as_a_failure()
    {
        var item = SetupCheckService.IphoneSimulator(Array.Empty<string>(), IosToolchain.NotMac, windows: true);
        Assert.Equal("ok", item.State);
        Assert.False(item.Required);
        Assert.StartsWith("Not needed", item.Detail);
    }

    [Fact]
    public void On_Windows_it_says_the_simulator_is_macOS_only()
    {
        var item = SetupCheckService.IphoneSimulator(new[] { "alpha" }, IosToolchain.NotMac, windows: true);
        Assert.Equal("warn", item.State);
        Assert.Contains("macOS-only", item.Detail);
        Assert.Contains("Windows", item.Detail);
    }

    [Fact]
    public void On_a_Mac_it_reports_Xcode_truthfully()
    {
        var ready = SetupCheckService.IphoneSimulator(new[] { "alpha" }, IosToolchain.Probe(Xcode(), mac: true), windows: false);
        Assert.Equal("ok", ready.State);
        Assert.Contains("1 iOS runtime", ready.Detail);

        var lacking = SetupCheckService.IphoneSimulator(new[] { "alpha" },
            IosToolchain.Probe(Xcode("xcodebuild -license check", "xcodebuild -checkFirstLaunchStatus"), mac: true), windows: false);
        Assert.Equal("warn", lacking.State);
        Assert.Contains("licence", lacking.Detail);
        Assert.Equal("sudo xcodebuild -license accept Then: sudo xcodebuild -runFirstLaunch", lacking.Fix);
    }

    [Fact]
    public void The_check_lists_the_row_when_the_probe_has_it()
    {
        var probe = new SetupCheckService.Probe(e => "/bin/" + e, _ => null, (_, k) => k == "user.name" ? "Pat" : "pat@example.com",
            () => null, () => null, () => true,
            IphoneSimulator: () => (new[] { "alpha" }, IosToolchain.Probe(Xcode(), mac: true), false));
        var items = SetupCheckService.Evaluate(probe, new[] { ("qa-test", "claude") }, Array.Empty<(string, string)>());
        Assert.Equal("ok", Assert.Single(items, i => i.Key == "ios-simulator").State);
    }

    // ---- A worker host says what it has -----------------------------------------------

    [Fact]
    public async Task A_worker_hosts_devices_come_with_its_check_in_and_are_kept_while_it_says_nothing()
    {
        var cfg = WorkerHostConfig.Read(new Dictionary<string, JsonElement>
        {
            ["workerHosts"] = JsonDocument.Parse("""[{ "name": "bravo-host" }, { "name": "charlie-host" }]""").RootElement,
        });
        var svc = new WorkerHostService(() => cfg, () => "c", () => DateTime.UtcNow);
        await svc.CheckInAsync("bravo-host", "c", Array.Empty<HostRun>(), null, TimeSpan.Zero, devices: new[] { "ios:simulator", "junk" });
        await svc.CheckInAsync("charlie-host", "c", Array.Empty<HostRun>(), null, TimeSpan.Zero, devices: Array.Empty<string>());
        Assert.Equal(AMac, svc.DevicesOf("bravo-host"));
        Assert.Empty(svc.DevicesOf("charlie-host"));

        await svc.CheckInAsync("bravo-host", "c", Array.Empty<HostRun>(), null, TimeSpan.Zero);
        Assert.Equal(AMac, svc.DevicesOf("bravo-host"));
        Assert.Equal(AMac, svc.Snapshot().Single(h => h.Name == "bravo-host").Devices);
        Assert.Empty(svc.DevicesOf("never-heard-from"));
    }

    // ---- The claim ---------------------------------------------------------------------

    private static string[] QaNeedsTheSimulator(string project, string skill) =>
        skill == "qa-test" ? new[] { $"{project}:app-stack", MachineDevices.IosSimulator } : Array.Empty<string>();

    private async Task<(int Qa, int Impl)> SeedTwo()
    {
        await using var ctx = new DashboardDbContext(_options);
        var old = DateTime.UtcNow.AddHours(-2);
        var qa = new Ticket { ProjectKey = "alpha", Title = "phone", Phase = TicketPhase.NeedsQa, UpdatedAt = old };
        var impl = new Ticket { ProjectKey = "alpha", Title = "site", Phase = TicketPhase.NeedsImplementation, UpdatedAt = old.AddMinutes(1) };
        ctx.Tickets.AddRange(qa, impl);
        await ctx.SaveChangesAsync();
        return (qa.Id, impl.Id);
    }

    [Fact]
    public async Task A_machine_without_the_simulator_passes_the_run_over_and_takes_nothing_for_it()
    {
        var (qa, impl) = await SeedTwo();
        await using var ctx = new DashboardDbContext(_options);
        var leases = new ResourceLeaseService(ctx);
        var svc = new TicketService(ctx, leases: leases);
        var trace = new ClaimTrace();

        var claimed = await svc.ClaimNextAsync(null, (_, _) => true, 3, QaNeedsTheSimulator, Ttl, trace: trace,
            deviceLacking: keys => MachineDevices.Lacking(keys, NotAMac));

        // The QA ticket was ahead, and the other one is taken instead: this machine has other work.
        Assert.Equal(impl, claimed!.Ticket.Id);
        Assert.Equal(1, trace.Skips[ClaimSkip.Device]);
        Assert.Equal(MachineDevices.IosSimulator, trace.DeviceTickets[qa]);
        // Not claimed and then failed: no lease, no phase move, no attempt spent.
        Assert.DoesNotContain((await leases.ActiveAsync()), l => l.TicketId == qa);
        var after = await ctx.Tickets.AsNoTracking().SingleAsync(t => t.Id == qa);
        Assert.Equal(TicketPhase.NeedsQa, after.Phase);
        Assert.Equal(0, after.HeartbeatAttempts);
    }

    [Fact]
    public async Task A_Mac_with_the_simulator_takes_it_and_holds_the_lease_like_any_other()
    {
        var (qa, _) = await SeedTwo();
        await using var ctx = new DashboardDbContext(_options);
        var svc = new TicketService(ctx, leases: new ResourceLeaseService(ctx));

        var claimed = await svc.ClaimNextAsync(null, (_, _) => true, 3, QaNeedsTheSimulator, Ttl,
            deviceLacking: keys => MachineDevices.Lacking(keys, AMac));
        Assert.Equal(qa, claimed!.Ticket.Id);
        Assert.Contains(MachineDevices.IosSimulator, claimed.Leases);
    }

    [Fact]
    public async Task On_a_worker_host_the_lease_is_that_machines_own()
    {
        var (qa, _) = await SeedTwo();
        await using var ctx = new DashboardDbContext(_options);
        var svc = new TicketService(ctx, leases: new ResourceLeaseService(ctx));
        string[] OnHost(string p, string s) => QaNeedsTheSimulator(p, s).Select(k => WorkerHostConfig.HostResource(k, "bravo-host")).ToArray();

        var claimed = await svc.ClaimNextAsync(null, (_, _) => true, 3, OnHost, Ttl,
            deviceLacking: keys => MachineDevices.Lacking(keys, AMac));
        Assert.Equal(qa, claimed!.Ticket.Id);
        Assert.Contains("ios:simulator@bravo-host", claimed.Leases);
    }

    // ---- Why it waits -----------------------------------------------------------------

    [Fact]
    public void A_ticket_waits_for_a_device_only_while_no_machine_that_can_take_work_has_one()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
        var waits = new DeviceWaits(() => now);
        waits.Note(12, MachineDevices.IosSimulator);
        Assert.Equal("waiting for an iPhone simulator, which only a Mac has", waits.Why(12, new HashSet<string>()));
        Assert.Null(waits.Why(12, new HashSet<string>(AMac)));   // a Mac will take it
        Assert.Null(waits.Why(13, new HashSet<string>()));
        now += DeviceWaits.Stale + TimeSpan.FromMinutes(1);
        Assert.Null(waits.Why(12, new HashSet<string>()));        // no claim has said so lately
        waits.Note(12, MachineDevices.IosSimulator);
        waits.Clear(12);
        Assert.Null(waits.Why(12, new HashSet<string>()));
    }

    [Fact]
    public void The_capacity_strip_says_it_in_the_same_words()
    {
        var t = new ClaimTrace { Workable = 1 };
        t.Skip(ClaimSkip.Device);
        t.DeviceTickets[12] = MachineDevices.IosSimulator;
        var now = DateTime.UtcNow;
        var v = CapacityClassifier.Classify(new PlaceInput("bravo-host", 2, 0, true, "idle", null, false, null,
            new ClaimSeen(now.AddMinutes(-1), false, t)), now);
        Assert.Equal(CapacityReasons.Device, v.Reason);
        Assert.Equal("waiting for an iPhone simulator, which only a Mac has", v.Detail!["waitingFor"]);
    }

    // ---- The reaper's hygiene ---------------------------------------------------------

    private sealed class FakeSimctl
    {
        public List<string> Booted = new();
        public List<string> Shutdowns = new();
        public string? Run(string file, string args)
        {
            if (args == "simctl list devices booted -j")
                return JsonSerializer.Serialize(new { devices = new Dictionary<string, object[]>
                {
                    ["com.apple.CoreSimulator.SimRuntime.iOS-27-2"] = Booted.Select(u => (object)new { udid = u, state = "Booted" }).ToArray(),
                } });
            if (args.StartsWith("simctl shutdown ")) { var u = args["simctl shutdown ".Length..]; Shutdowns.Add(u); Booted.Remove(u); return ""; }
            return null;
        }
    }

    [Fact]
    public async Task A_freed_simulator_lease_shuts_down_what_its_run_booted_and_leaves_the_owners_alone()
    {
        var sim = new FakeSimctl { Booted = { "AAAA-0001" } };   // the owner's, up before the run
        var reset = new SimulatorReset(sim.Run, _temp, enabled: true);
        await using var ctx = new DashboardDbContext(_options);
        var leases = new ResourceLeaseService(ctx, simulators: reset);

        Assert.NotNull(await leases.TryAcquireAllAsync(new[] { "ticket:5", MachineDevices.IosSimulator }, 5, "qa-test", Ttl));
        sim.Booted.Add("BBBB-0002");                               // the run boots one, and is killed
        await leases.ReleaseForTicketAsync(5, "orphaned claim");

        Assert.Equal(new[] { "BBBB-0002" }, sim.Shutdowns);
        Assert.Equal(new[] { "AAAA-0001" }, sim.Booted);
    }

    [Fact]
    public async Task An_expired_lease_and_a_start_up_reset_shut_it_down_too()
    {
        var sim = new FakeSimctl();
        var reset = new SimulatorReset(sim.Run, _temp, enabled: true);
        await using var ctx = new DashboardDbContext(_options);
        var leases = new ResourceLeaseService(ctx, simulators: reset);

        await leases.TryAcquireAllAsync(new[] { MachineDevices.IosSimulator }, 6, "qa-test", TimeSpan.FromMilliseconds(1));
        sim.Booted.Add("CCCC-0003");
        await Task.Delay(20);
        await leases.SweepExpiredAsync();
        Assert.Equal(new[] { "CCCC-0003" }, sim.Shutdowns);

        await leases.TryAcquireAllAsync(new[] { MachineDevices.IosSimulator }, 7, "qa-test", Ttl);
        sim.Booted.Add("DDDD-0004");
        await leases.ReleaseAllAsync("orchestrator restarted");
        Assert.Equal(new[] { "CCCC-0003", "DDDD-0004" }, sim.Shutdowns);
    }

    [Fact]
    public async Task Any_other_lease_and_a_worker_hosts_simulator_are_not_this_machines_to_shut_down()
    {
        var sim = new FakeSimctl();
        var reset = new SimulatorReset(sim.Run, _temp, enabled: true);
        await using var ctx = new DashboardDbContext(_options);
        var leases = new ResourceLeaseService(ctx, simulators: reset);

        await leases.TryAcquireAllAsync(new[] { "alpha:app-stack", "android:emulator", "ios:simulator@bravo-host" }, 8, "qa-test", Ttl);
        sim.Booted.Add("EEEE-0005");
        await leases.ReleaseForTicketAsync(8);
        Assert.Empty(sim.Shutdowns);
    }

    [Fact]
    public void A_QA_copy_or_a_lease_taken_before_this_existed_shuts_nothing_down()
    {
        var sim = new FakeSimctl { Booted = { "FFFF-0006" } };
        var off = new SimulatorReset(sim.Run, _temp, enabled: false);
        var lease = new ResourceLease { Id = 99, ResourceKey = MachineDevices.IosSimulator, TicketId = 9 };
        off.Acquired(new[] { lease });
        Assert.Empty(off.Released(new[] { lease }));

        var on = new SimulatorReset(sim.Run, _temp, enabled: true);
        Assert.Empty(on.Released(new[] { new ResourceLease { Id = 100, ResourceKey = MachineDevices.IosSimulator, TicketId = 9 } }));
        Assert.Empty(sim.Shutdowns);
    }

    [Fact]
    public void Only_a_UDID_reaches_the_command_line()
    {
        Assert.Equal(new[] { "CCCC-3" }, SimulatorReset.ToShutDown(new[] { "aaaa-1" }, new[] { "AAAA-1", "CCCC-3", "CC; rm -rf /" }));
    }

    // ---- Captures ---------------------------------------------------------------------

    [Fact]
    public void A_simulator_screenshot_on_a_Mac_worker_host_maps_to_the_boards_capture_folder()
    {
        var board = Path.Combine(_temp, "board-data");
        Assert.Equal(Path.Combine(board, "qa-captures", "12", "sim-home.png"),
            HostDataMirror.ToBoardCapture("/Users/someone/Whetstone/data/qa-captures/12/sim-home.png",
                new[] { "/Users/someone/Whetstone/data" }, board));
        // simctl's screenshot is a PNG by default; a recordVideo is not a screenshot.
        Assert.Equal("sim-home.png", HostDataMirror.SafeCaptureName("sim-home.png"));
        Assert.Null(HostDataMirror.SafeCaptureName("sim-flow.mp4"));
    }
}
