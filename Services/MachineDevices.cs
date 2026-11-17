using System.Collections.Concurrent;
using System.Text.Json;

namespace Whetstone.Services;

/// <summary>
/// A device a machine either has or has not, named by the resource a project's runs lease for it
/// (docs/ios-simulator-plan.md, section 2). A lease is only ever waited for; a device a machine
/// cannot have would be waited for forever, so a claim from a machine without it passes the run
/// over, and another machine takes it.
/// </summary>
/// <param name="Key">The machine-wide resource a project's <c>phaseResources</c> names: <c>ios:simulator</c>.</param>
/// <param name="Name">What a person calls it, after "waiting for".</param>
/// <param name="Who">Which machines have it, after "which".</param>
public sealed record MachineDevice(string Key, string Name, string Who)
{
    /// <summary>"waiting for an iPhone simulator, which only a Mac has" - the ticket's and the capacity strip's words.</summary>
    public string Waiting => $"waiting for {Name}, which {Who}";
}

/// <summary>
/// The rule, written once. The lease machinery stays generic - <c>ios:simulator</c> is leased like
/// any other resource, host-qualified on a worker host - and this is the one table that says which
/// resources a machine must also <i>have</i>. A resource not in it (<c>android:emulator</c>, a
/// project's stack) is never gated: a machine is assumed able to run what it is configured for, as
/// it always was.
/// <para>
/// A machine says what it has: a worker host in its check-in, from <c>Get-MachineDevices</c> in
/// <c>data/machine-info.ps1</c>; the board host from <see cref="BoardDevices"/>. Both run the same
/// commands and judge them by the same rule (<see cref="IosToolchain.Has"/> and its PowerShell
/// copy), and the tests hold the two to one fixture.
/// </para>
/// </summary>
public static class MachineDevices
{
    public const string IosSimulator = "ios:simulator";

    public static readonly IReadOnlyList<MachineDevice> Known = new[]
    {
        new MachineDevice(IosSimulator, "an iPhone simulator", "only a Mac has"),
    };

    /// <summary>The gated device a resource key names, host-qualified or not, or null for any other resource.</summary>
    public static MachineDevice? For(string resourceKey)
    {
        var plain = WorkerHostConfig.SplitHostResource(resourceKey)?.Resource ?? resourceKey;
        return Known.FirstOrDefault(d => d.Key.Equals(plain, StringComparison.Ordinal));
    }

    /// <summary>
    /// The first device among <paramref name="resourceKeys"/> a machine with <paramref name="has"/>
    /// lacks, or null when it can take them all. A machine that never said (an older runner) has
    /// none: being wrong that way costs a wait, the other way a run that cannot start.
    /// </summary>
    public static MachineDevice? Lacking(IEnumerable<string> resourceKeys, IReadOnlyCollection<string>? has)
    {
        foreach (var key in resourceKeys)
            if (For(key) is { } d && !(has?.Contains(d.Key, StringComparer.Ordinal) ?? false)) return d;
        return null;
    }

    /// <summary>A reported list cleaned to the devices this table knows: a runner's typo is not a device.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string>? reported) =>
        (reported ?? Array.Empty<string>()).Select(s => s?.Trim() ?? "")
            .Where(s => Known.Any(d => d.Key.Equals(s, StringComparison.Ordinal))).Distinct().ToList();

    /// <summary>Every device the projects name in their resources, for the Setup check.</summary>
    public static IReadOnlyList<string> NamedBy(ProjectConfig cfg) =>
        cfg.PhaseResources.Values.SelectMany(v => v).Concat(cfg.ResourcePaths.Keys)
            .Select(For).Where(d => d is not null).Select(d => d!.Key).Distinct().ToList();
}

/// <summary>
/// What an iPhone simulator needs on a machine, as read: macOS, Xcode selected, its licence and
/// first-launch tasks done, and at least one iOS runtime. Null where it was not read (not a Mac, or
/// an earlier step already failed).
/// </summary>
public sealed record IosToolchain(bool Mac, string? XcodePath, bool? Licence, bool? FirstLaunch, int? Runtimes)
{
    public static readonly IosToolchain NotMac = new(false, null, null, null, null);

    /// <summary>The rule. Mirrored as <c>Test-IosSimulatorReady</c> in data/machine-info.ps1.</summary>
    public bool Has => Mac && XcodePath is not null && Licence == true && FirstLaunch == true && Runtimes > 0;

    /// <summary>
    /// What is missing, each with the command that fixes it. The check says the command rather
    /// than running it: installing Xcode and accepting its licence need the owner's password.
    /// </summary>
    public IReadOnlyList<(string What, string Fix)> Gaps()
    {
        var gaps = new List<(string, string)>();
        if (!Mac) return gaps;
        if (XcodePath is null)
        {
            gaps.Add(("Xcode is not installed or not selected",
                "Install Xcode from the App Store, then: sudo xcode-select -s /Applications/Xcode.app/Contents/Developer"));
            return gaps;
        }
        if (Licence != true)
            gaps.Add(("Xcode's licence has not been accepted", "sudo xcodebuild -license accept"));
        if (FirstLaunch != true)
            gaps.Add(("Xcode's first-launch tasks have not run", "sudo xcodebuild -runFirstLaunch"));
        if (Runtimes is null or 0)
            gaps.Add(("no iOS simulator runtime is installed", "xcodebuild -downloadPlatform iOS"));
        return gaps;
    }

    /// <summary>
    /// Read this machine's toolchain. Each command goes through <paramref name="run"/>, which
    /// answers null for a command that failed: the same commands, in the same order, as
    /// <c>Get-IosToolchain</c> in data/machine-info.ps1.
    /// </summary>
    public static IosToolchain Probe(MachineProbe.Runner run, bool mac)
    {
        if (!mac) return NotMac;
        var path = run("xcode-select", "-p")?.Trim();
        if (string.IsNullOrEmpty(path)) return new(true, null, null, null, null);
        var licence = run("xcodebuild", "-license check") is not null;
        var first = run("xcodebuild", "-checkFirstLaunchStatus") is not null;
        var runtimes = SimulatorParse.IosRuntimes(run("xcrun", "simctl list runtimes -j"));
        return new(true, path, licence, first, runtimes);
    }
}

/// <summary>The parsers for <c>simctl</c>'s JSON. Mirrored in data/machine-info.ps1, and held to the same fixtures.</summary>
public static class SimulatorParse
{
    /// <summary>How many available iOS runtimes <c>simctl list runtimes -j</c> lists, or null when it is not that output.</summary>
    public static int? IosRuntimes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("runtimes", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            return list.EnumerateArray().Count(r =>
                r.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == "iOS"
                && r.TryGetProperty("isAvailable", out var a) && a.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The UDIDs of the booted devices <c>simctl list devices booted -j</c> lists; empty when none or unreadable.</summary>
    public static IReadOnlyList<string> Booted(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("devices", out var byRuntime) || byRuntime.ValueKind != JsonValueKind.Object)
                return Array.Empty<string>();
            var result = new List<string>();
            foreach (var rt in byRuntime.EnumerateObject())
            {
                if (rt.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var d in rt.Value.EnumerateArray())
                    if (d.TryGetProperty("state", out var s) && s.GetString() == "Booted"
                        && d.TryGetProperty("udid", out var u) && u.GetString() is { Length: > 0 } udid)
                        result.Add(udid);
            }
            return result;
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}

/// <summary>
/// The board host's own devices, worked out here rather than reported: what a claim from this
/// machine may take, and the Setup check's row. Read at most every few minutes - <c>xcodebuild</c>
/// takes a second - and off a claim's path once read.
/// </summary>
public class BoardDevices
{
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    private readonly MachineProbe.Runner _run;
    private readonly bool _mac;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private (DateTime At, IosToolchain Ios)? _read;

    public BoardDevices() : this(MachineProbe.RunProcess, OperatingSystem.IsMacOS(), () => DateTime.UtcNow) { }

    public BoardDevices(MachineProbe.Runner run, bool mac, Func<DateTime> now) => (_run, _mac, _now) = (run, mac, now);

    /// <summary>This machine's iPhone toolchain, as last read.</summary>
    public IosToolchain Ios()
    {
        lock (_lock)
        {
            if (_read is { } r && _now() - r.At < Fresh) return r.Ios;
            var ios = IosToolchain.Probe(_run, _mac);
            _read = (_now(), ios);
            return ios;
        }
    }

    /// <summary>The devices this machine has, as <see cref="MachineDevices.Known"/> names them.</summary>
    public IReadOnlyList<string> Has() => Ios().Has ? new[] { MachineDevices.IosSimulator } : Array.Empty<string>();
}

/// <summary>
/// Which tickets a claim passed over because the machine claiming had no device they need, so the
/// board can say why such a ticket waits. In memory: every claim says again.
/// </summary>
public class DeviceWaits
{
    /// <summary>A note older than this no longer says why a ticket waits now.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<int, (string Device, DateTime At)> _waits = new();
    private readonly Func<DateTime> _now;

    public DeviceWaits() : this(() => DateTime.UtcNow) { }
    public DeviceWaits(Func<DateTime> now) => _now = now;

    public void Note(int ticketId, string device) => _waits[ticketId] = (device, _now());

    public void Clear(int ticketId) => _waits.TryRemove(ticketId, out _);

    /// <summary>
    /// Why <paramref name="ticketId"/> waits, when a claim passed it over for a device and no machine
    /// that could take work has that device now; null otherwise - a Mac that has one will take it.
    /// </summary>
    public string? Why(int ticketId, IReadOnlyCollection<string> anywhere)
    {
        if (!_waits.TryGetValue(ticketId, out var w) || _now() - w.At > Stale) return null;
        if (anywhere.Contains(w.Device, StringComparer.Ordinal)) return null;
        return MachineDevices.For(w.Device)?.Waiting;
    }
}
