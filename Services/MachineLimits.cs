using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;

namespace Whetstone.Services;

/// <summary>
/// The knobs of the suggestion rules, from <c>machineLimits</c> in <c>data/config.json</c>. Every
/// one has a default; only <see cref="Auto"/> has a switch on a page. Automatic mode is off and
/// does nothing yet - the key exists so it can be built without a redesign
/// (docs/machine-limits-plan.md section 5).
/// </summary>
public record MachineLimitsConfig(
    bool Auto = false,
    double ReserveGbMin = 4, double ReservePct = 15,
    double BerthGb = 2.5, double BerthCores = 1, double EffCoreWeight = 0.5,
    double QaStackGb = 3, double DockerBaseGb = 1,
    double CpuCeilingPct = 85, double MinFreeDiskGb = 20,
    double MostWorkGainPct = 12, double FastestSlowdown = 2)
{
    public const string ConfigKey = "machineLimits";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MachineLimitsConfig Read(IReadOnlyDictionary<string, JsonElement> global)
    {
        if (!global.TryGetValue(ConfigKey, out var v) || v.ValueKind != JsonValueKind.Object) return new();
        try { return v.Deserialize<MachineLimitsConfig>(Json) ?? new(); }
        catch { return new(); }
    }
}

/// <summary>One hand-set count and what the machine's own record suggests instead.</summary>
/// <param name="Basis"><c>hardware</c> (rule A), <c>learned</c> (rule B), <c>ceiling</c> (a warning sign capped it) or <c>none</c>.</param>
/// <param name="Confidence"><c>low</c>, <c>medium</c> or <c>high</c>.</param>
public record SlotSuggestion(int Set, int? Suggested, string Basis, string Confidence, IReadOnlyList<string> Reasons);

/// <summary>Runs on one machine that overlapped about <see cref="Others"/> others there.</summary>
public record RunSpeedPoint(int Others, int Runs, double SecPerTurn, double ModelSecPerTurn, double MachineSecPerTurn, double Throughput);

/// <param name="Bound"><c>machine</c> when what grew with overlap was time on the machine, <c>model</c> when it was waiting on the model.</param>
public record RunSpeed(IReadOnlyList<RunSpeedPoint> Points, int? MostWork, int? Fastest, string? Bound, string Summary);

public record MachineSuggestions(string Place, SlotSuggestion Workers, SlotSuggestion Qa, RunSpeed? Speed);

/// <summary>A finished run, as the run-speed evidence reads it.</summary>
public record RunTiming(string Place, DateTime Start, DateTime End, double Duration, double Api, int Turns, double Prep, double Handoff);

/// <summary>
/// The suggestion rules, as pure functions over what is stored (docs/machine-limits-plan.md section
/// 3). Nothing here is stored: a better rule applies to old data the moment it is written.
/// </summary>
public static class MachineLimits
{
    /// <summary>The ticket phases whose runs take a project's stack - what a QA lease is for.</summary>
    public static readonly HashSet<string> StackPhases = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(TicketPhase.NeedsQa), nameof(TicketPhase.QaTesting),
        nameof(TicketPhase.NeedsPeerReview), nameof(TicketPhase.PeerReviewing),
    };

    /// <summary>Rule B needs this many busy minutes, over at least <see cref="LearnLevels"/> busy counts.</summary>
    public const int LearnMinutes = 360, LearnLevels = 3, LevelMinutes = 20;

    /// <summary>A warning sign caps the suggestion for this long after it last happened.</summary>
    public static readonly TimeSpan CeilingFor = TimeSpan.FromDays(7);

    // ---- Rule A: from hardware alone ----------------------------------------

    /// <summary>
    /// What a machine could take on its hardware alone: enough memory for each berth above a
    /// reserve, and a core for each berth, one kept back. An efficiency core counts for less.
    /// </summary>
    public static (int? Value, List<string> Why) FromHardware(MachineInventoryReport? inv, MachineLimitsConfig c, int max)
    {
        var why = new List<string>();
        if (inv?.RamMb is not { } ramMb) return (null, new() { "No hardware report from this machine yet." });
        var ram = ramMb / 1024.0;
        var reserve = Math.Max(c.ReserveGbMin, ram * c.ReservePct / 100);
        var byRam = (int)Math.Floor((ram - reserve) / c.BerthGb);
        why.Add($"Memory: {ram:0.#}GB, {reserve:0.#}GB kept for the system, {c.BerthGb:0.#}GB a berth -> {Math.Max(byRam, 0)}.");

        double cores; string coreText;
        if (inv.PerfCores is { } p && inv.EffCores is { } e)
        {
            cores = p + e * c.EffCoreWeight;
            coreText = $"{p} performance + {e} efficiency cores (counted as {c.EffCoreWeight:0.##})";
        }
        else if (inv.PhysicalCores is { } pc) { cores = pc; coreText = $"{pc} cores"; }
        else if (inv.LogicalCores is { } lc) { cores = lc / 2.0; coreText = $"{lc} logical cores (counted as {lc / 2.0:0.#})"; }
        else { cores = double.NaN; coreText = ""; }

        var value = byRam;
        if (!double.IsNaN(cores))
        {
            var byCpu = (int)Math.Floor((cores - 1) / c.BerthCores);
            why.Add($"CPU: {coreText}, one kept back, {c.BerthCores:0.#} a berth -> {Math.Max(byCpu, 0)}.");
            value = Math.Min(value, byCpu);
        }
        return (Math.Clamp(value, 1, max), why);
    }

    /// <summary>QA leases on hardware alone: Docker's memory - on a Mac its VM's, not the Mac's - a stack each.</summary>
    public static (int? Value, List<string> Why) QaFromHardware(MachineInventoryReport? inv, MachineLimitsConfig c, int workers)
    {
        if (inv?.DockerMemMb is not { } mb) return (null, new() { "Docker's memory is not reported, so there is nothing to size QA by." });
        var gb = mb / 1024.0;
        var n = (int)Math.Floor((gb - c.DockerBaseGb) / c.QaStackGb);
        var why = new List<string> { $"Docker has {gb:0.#}GB; {c.DockerBaseGb:0.#}GB for itself, {c.QaStackGb:0.#}GB a stack -> {Math.Max(n, 0)}." };
        if (n > workers) why.Add($"No more than the {workers} berth(s) suggested, since every QA run is a berth too.");
        return (Math.Clamp(n, 1, Math.Max(workers, 1)), why);
    }

    // ---- Rule B: learned from the machine's own minutes ---------------------

    public static (int? Value, string Confidence, List<string> Why) FromLoad(IReadOnlyList<MachineLoadSample> samples,
        MachineInventoryReport? inv, MachineLimitsConfig c, int max)
    {
        var busy = samples.Where(s => s.Busy > 0 && s.CpuPct is not null).ToList();
        var levels = busy.GroupBy(s => s.Busy).Where(g => g.Count() >= LevelMinutes).OrderBy(g => g.Key).ToList();
        if (busy.Count < LearnMinutes || levels.Count < LearnLevels)
            return (null, "low", new() { $"{busy.Count} busy minute(s) over {levels.Count} different busy count(s) so far; learning needs {LearnMinutes} over {LearnLevels}." });

        var why = new List<string>();
        var cpu = levels.Select(g => (n: (double)g.Key, y: Quantile(g.Select(s => s.CpuPct!.Value), 0.9), w: (double)g.Count())).ToList();
        var (ca, cb) = Fit(cpu);
        double limit = double.PositiveInfinity;
        if (cb > 0.5)
        {
            var byCpu = (c.CpuCeilingPct - ca) / cb;
            why.Add($"CPU (p90) rises about {cb:0}% a berth from {ca:0}%; it would pass {c.CpuCeilingPct:0}% at {Math.Max(byCpu, 0):0.#}.");
            limit = Math.Min(limit, byCpu);
        }
        else why.Add($"CPU (p90) hardly moves with more berths (about {Math.Max(cb, 0):0.#}% each).");

        var mem = levels.Where(g => g.Any(s => s.MemAvailMb is not null))
            .Select(g => (n: (double)g.Key, y: Quantile(g.Where(s => s.MemAvailMb is not null).Select(s => s.MemAvailMb!.Value / 1024.0), 0.1), w: (double)g.Count()))
            .ToList();
        if (mem.Count >= LearnLevels)
        {
            var (ma, mb) = Fit(mem);
            var totalGb = (inv?.RamMb ?? samples.LastOrDefault(s => s.MemTotalMb is not null)?.MemTotalMb ?? 0) / 1024.0;
            var reserve = Math.Max(c.ReserveGbMin, totalGb * c.ReservePct / 100);
            if (mb < -0.05)
            {
                var byMem = (reserve - ma) / mb;
                why.Add($"Available memory (p10) falls about {-mb:0.#}GB a berth from {ma:0.#}GB; it would reach the {reserve:0.#}GB reserve at {Math.Max(byMem, 0):0.#}.");
                limit = Math.Min(limit, byMem);
            }
            else why.Add("Available memory hardly moves with more berths.");
        }

        var seen = levels.Max(g => g.Key);
        var value = double.IsPositiveInfinity(limit) ? seen + 2 : (int)Math.Floor(limit);
        if (double.IsPositiveInfinity(limit))
            why.Add($"Nothing measured here limits it yet, so a little past the busiest it has been seen ({seen}).");
        // A straight line is only trusted a little past where it was drawn.
        var cap = Math.Max(seen * 2, seen + 2);
        if (value > cap) { why.Add($"Held to {cap}: the busiest this machine has been seen is {seen}."); value = cap; }
        var conf = busy.Count >= 1440 && levels.Count >= 4 ? "high" : "medium";
        return (Math.Clamp(value, 1, max), conf, why);
    }

    /// <summary>
    /// QA leases learned from Docker's memory during QA: what a stack adds, fitted on the minutes
    /// when Docker was read and stack phases were running.
    /// </summary>
    public static (int? Value, List<string> Why) QaFromLoad(IReadOnlyList<MachineLoadSample> samples, MachineLimitsConfig c, int max)
    {
        var pts = samples.Where(s => s.DockerMemUsedMb is not null && s.DockerMemLimitMb is > 0)
            .Select(s => (q: StackRuns(s.PhasesJson), s)).ToList();
        var levels = pts.GroupBy(p => p.q).Where(g => g.Count() >= 5).ToList();
        if (levels.Count < 2 || levels.All(g => g.Key == 0)) return (null, new());
        var fit = Fit(levels.Select(g => ((double)g.Key, Quantile(g.Select(p => p.s.DockerMemUsedMb!.Value / 1024.0), 0.9), (double)g.Count())).ToList());
        var limitGb = pts.Last().s.DockerMemLimitMb!.Value / 1024.0;
        if (fit.B <= 0.1) return (null, new() { "Docker's memory hardly moves with QA runs." });
        var n = (int)Math.Floor((limitGb * 0.9 - fit.A) / fit.B);
        return (Math.Clamp(n, 1, max), new()
        {
            $"A QA stack has added about {fit.B:0.#}GB to Docker (p90), over {fit.A:0.#}GB with none; Docker's limit is {limitGb:0.#}GB -> {Math.Max(n, 0)} within 90% of it.",
        });
    }

    // ---- Rule C: ceilings from warning signs --------------------------------

    public record Ceiling(int Cap, string Why, bool Qa = false);

    /// <param name="timeouts">When a worktree setup command on this machine hit its time limit.</param>
    /// <param name="busyAt">How many berths were busy at a moment, from whatever sample is nearest.</param>
    public static List<Ceiling> Ceilings(IReadOnlyList<MachineLoadSample> samples, IReadOnlyList<DateTime> timeouts,
        Func<DateTime, int?> busyAt, MachineLimitsConfig c, DateTime now)
    {
        var list = new List<Ceiling>();
        var recent = samples.Where(s => now - s.At <= CeilingFor).OrderBy(s => s.At).ToList();

        // Five minutes in a row at warn or worse.
        if (Runs(recent, s => s.Pressure >= MachinePressure.Warn, 5) is { } p)
            list.Add(new(Math.Max(1, p.MinBusy - 1), $"Memory pressure stayed at warning or worse for {p.Length} minutes with {p.MinBusy} berth(s) busy ({When(p.At, now)})."));

        // Swap up by a quarter of a GB within five minutes, and not given back during them.
        for (var i = 0; i + 5 < recent.Count; i++)
        {
            if (recent[i].SwapUsedMb is not { } a || recent[i + 5].SwapUsedMb is not { } b) continue;
            if ((recent[i + 5].At - recent[i].At).TotalMinutes > 7 || b - a < 256) continue;
            var minBusy = recent.Skip(i).Take(6).Min(s => s.Busy);
            if (minBusy == 0) continue;
            list.Add(new(Math.Max(1, minBusy - 1), $"Swap grew {b - a}MB in five minutes with {minBusy} berth(s) busy ({When(recent[i].At, now)})."));
            break;
        }

        var hit = timeouts.Where(t => now - t <= CeilingFor).OrderBy(t => t).ToList();
        if (hit.Count > 0)
        {
            var busy = hit.Select(busyAt).Where(b => b is > 0).Select(b => b!.Value).DefaultIfEmpty(0).Min();
            list.Add(busy > 0
                ? new(Math.Max(1, busy - 1), $"Worktree setup timed out {hit.Count} time(s) in the last week, the first with {busy} berth(s) busy ({When(hit[0], now)}).")
                : new(int.MaxValue, $"Worktree setup timed out {hit.Count} time(s) in the last week; no sample says how busy the machine was."));
        }

        foreach (var s in recent.Where(s => s.DockerMemUsedMb is not null && s.DockerMemLimitMb is > 0
                     && s.DockerMemUsedMb >= 0.9 * s.DockerMemLimitMb))
        {
            var q = StackRuns(s.PhasesJson);
            list.Add(new(Math.Max(1, q - 1), $"Docker used {s.DockerMemUsedMb / 1024.0:0.#}GB of its {s.DockerMemLimitMb / 1024.0:0.#}GB with {q} QA run(s) going ({When(s.At, now)}).", Qa: true));
            break;
        }

        if (recent.LastOrDefault(s => s.DiskFreeGb is not null) is { } d && d.DiskFreeGb < c.MinFreeDiskGb)
            list.Add(new(Math.Max(1, d.Busy), $"Only {d.DiskFreeGb:0.#}GB free on the worktree drive (under {c.MinFreeDiskGb:0}GB): no more berths than are busy now."));
        return list;
    }

    // ---- Run-speed evidence --------------------------------------------------

    /// <summary>
    /// How runs on one machine slowed as more overlapped there, split into the time spent waiting on
    /// the model and the time spent on the machine. The overlap is time-weighted - the share of a
    /// run's life each other run was going - because a plain count is biased toward long runs.
    /// </summary>
    public static RunSpeed? Speed(IReadOnlyList<RunTiming> runs, MachineLimitsConfig c)
    {
        if (runs.Count < 20) return null;
        var sorted = runs.OrderBy(r => r.Start).ToList();
        var rows = new List<(int Others, RunTiming R)>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var a = sorted[i];
            var life = (a.End - a.Start).TotalSeconds;
            if (life <= 0 || a.Turns <= 0) continue;
            double overlap = 0;
            // Sorted by start, so nothing after the first run that starts once this one ended overlaps it.
            for (var j = 0; j < sorted.Count && sorted[j].Start < a.End; j++)
                if (j != i && sorted[j].End > a.Start)
                    overlap += (Min(a.End, sorted[j].End) - Max(a.Start, sorted[j].Start)).TotalSeconds;
            rows.Add(((int)Math.Min(Math.Round(overlap / life), 8), a));
        }
        var pts = rows.GroupBy(r => r.Others).Where(g => g.Count() >= 5).OrderBy(g => g.Key).Select(g =>
        {
            var spt = Median(g.Select(x => x.R.Duration / x.R.Turns));
            var model = Median(g.Select(x => x.R.Api / x.R.Turns));
            var machine = Median(g.Select(x => (Math.Max(0, x.R.Duration - x.R.Api) + x.R.Prep + x.R.Handoff) / x.R.Turns));
            return (k: g.Key, n: g.Count(), spt, model, machine);
        }).ToList();
        if (pts.Count < 2) return null;
        var solo = pts[0].spt;
        var points = pts.Select(p => new RunSpeedPoint(p.k, p.n, Math.Round(p.spt, 1), Math.Round(p.model, 1), Math.Round(p.machine, 1),
            Math.Round((p.k + 1) * solo / p.spt, 2))).ToList();

        int? mostWork = null, fastest = null;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].SecPerTurn <= c.FastestSlowdown * solo) fastest = points[i].Others + 1;
            if (mostWork is null && i + 1 < points.Count
                && points[i + 1].Throughput < points[i].Throughput * (1 + c.MostWorkGainPct / 100))
                mostWork = points[i].Others + 1;
        }
        mostWork ??= points[^1].Others + 1;

        var first = points[0]; var last = points[^1];
        var modelGrew = last.ModelSecPerTurn - first.ModelSecPerTurn;
        var machineGrew = last.MachineSecPerTurn - first.MachineSecPerTurn;
        var bound = modelGrew <= 0 && machineGrew <= 0 ? null : machineGrew > modelGrew ? "machine" : "model";
        var summary = $"From {first.Others + 1} to {last.Others + 1} run(s) at once here, time per turn went {first.SecPerTurn:0.#}s -> {last.SecPerTurn:0.#}s: "
            + $"model {first.ModelSecPerTurn:0.#}s -> {last.ModelSecPerTurn:0.#}s, machine {first.MachineSecPerTurn:0.#}s -> {last.MachineSecPerTurn:0.#}s"
            + (bound == "model" ? ". Most of it is waiting on the model, which fewer berths here would not fix." : bound == "machine" ? ". Most of it is the machine itself." : ".");
        return new RunSpeed(points, mostWork, fastest, bound, summary);
    }

    // ---- Putting it together -------------------------------------------------

    public static MachineSuggestions Suggest(string place, int setWorkers, int setQa, int max,
        MachineInventoryReport? inv, IReadOnlyList<MachineLoadSample> samples, IReadOnlyList<DateTime> timeouts,
        Func<DateTime, int?> busyAt, IReadOnlyList<RunTiming> runs, MachineLimitsConfig c, DateTime now)
    {
        var (hw, hwWhy) = FromHardware(inv, c, max);
        var (learned, conf, learnWhy) = FromLoad(samples, inv, c, max);
        var speed = Speed(runs, c);
        var ceilings = Ceilings(samples, timeouts, busyAt, c, now);

        var reasons = new List<string>();
        int? value; string basis; string confidence;
        if (learned is { } l)
        {
            (value, basis, confidence) = (l, "learned", conf);
            reasons.AddRange(learnWhy);
            if (hw is { } h) reasons.Add($"Hardware alone would say {h}.");
        }
        else
        {
            (value, basis, confidence) = (hw, hw is null ? "none" : "hardware", "low");
            reasons.AddRange(hwWhy);
            reasons.AddRange(learnWhy);
        }
        if (speed is not null)
        {
            reasons.Add(speed.Summary);
            if (speed.Bound == "machine" && value is { } v && speed.MostWork < v)
            {
                reasons.Add($"Runs here stop gaining {c.MostWorkGainPct:0}% throughput a berth past {speed.MostWork}, and the slowdown is the machine's.");
                value = speed.MostWork;
            }
        }
        foreach (var cl in ceilings.Where(x => !x.Qa))
        {
            reasons.Add(cl.Why);
            if (value is { } v && cl.Cap < v) { value = cl.Cap; basis = "ceiling"; confidence = "high"; }
        }
        var workers = new SlotSuggestion(setWorkers, value, basis, confidence, reasons);

        var qaReasons = new List<string>();
        var (qaHw, qaHwWhy) = QaFromHardware(inv, c, value ?? setWorkers);
        var (qaLearned, qaLearnWhy) = QaFromLoad(samples, c, value ?? setWorkers);
        int? qa = qaLearned ?? qaHw;
        var qaBasis = qaLearned is not null ? "learned" : qaHw is not null ? "hardware" : "none";
        var qaConf = qaLearned is not null ? "medium" : "low";
        qaReasons.AddRange(qaLearned is not null ? qaLearnWhy : qaHwWhy);
        foreach (var cl in ceilings.Where(x => x.Qa))
        {
            qaReasons.Add(cl.Why);
            if (qa is { } q && cl.Cap < q) { qa = cl.Cap; qaBasis = "ceiling"; qaConf = "high"; }
        }
        return new MachineSuggestions(place, workers, new SlotSuggestion(setQa, qa, qaBasis, qaConf, qaReasons), speed);
    }

    /// <summary>
    /// The fleet-wide finding: how a run's model time per turn rises with how many runs were going
    /// anywhere at once. Not a per-machine number - fewer berths on one machine do not change it.
    /// </summary>
    public static string? FleetModelTime(IReadOnlyList<RunTiming> runs)
    {
        if (runs.Count < 40) return null;
        var sorted = runs.OrderBy(r => r.Start).ToList();
        var pts = new List<(double n, double y, double w)>();
        var rows = new List<(int k, double y)>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var a = sorted[i];
            var life = (a.End - a.Start).TotalSeconds;
            if (life <= 0 || a.Turns <= 0) continue;
            double overlap = 0;
            for (var j = 0; j < sorted.Count && sorted[j].Start < a.End; j++)
                if (j != i && sorted[j].End > a.Start)
                    overlap += (Min(a.End, sorted[j].End) - Max(a.Start, sorted[j].Start)).TotalSeconds;
            rows.Add(((int)Math.Min(Math.Round(overlap / life), 10), a.Api / a.Turns));
        }
        foreach (var g in rows.GroupBy(r => r.k).Where(g => g.Count() >= 5))
            pts.Add((g.Key, Median(g.Select(x => x.y)), g.Count()));
        if (pts.Count < 3) return null;
        var (a0, b0) = Fit(pts);
        if (a0 <= 0) return null;
        return $"Across the fleet, model time per turn rises about {100 * b0 / a0:0}% for each other run going at the same time ({a0:0.#}s alone). "
            + "That is shared - the account, the API or the network - so it is a fleet-wide cost, not one machine's.";
    }

    // ---- Small maths ---------------------------------------------------------

    public static int StackRuns(string? phasesJson)
    {
        if (string.IsNullOrWhiteSpace(phasesJson)) return 0;
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, int>>(phasesJson) ?? new();
            return d.Where(kv => StackPhases.Contains(kv.Key)).Sum(kv => kv.Value);
        }
        catch { return 0; }
    }

    /// <summary>Weighted least squares of y on n: y = A + B n.</summary>
    public static (double A, double B) Fit(IReadOnlyList<(double n, double y, double w)> pts)
    {
        double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (n, y, w) in pts) { sw += w; sx += w * n; sy += w * y; sxx += w * n * n; sxy += w * n * y; }
        var den = sw * sxx - sx * sx;
        if (sw == 0) return (0, 0);
        if (Math.Abs(den) < 1e-9) return (sy / sw, 0);
        var b = (sw * sxy - sx * sy) / den;
        return ((sy - b * sx) / sw, b);
    }

    public static double Quantile(IEnumerable<double> xs, double q)
    {
        var s = xs.OrderBy(x => x).ToArray();
        if (s.Length == 0) return double.NaN;
        var pos = q * (s.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, s.Length - 1);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    public static double Median(IEnumerable<double> xs) => Quantile(xs, 0.5);

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static string When(DateTime at, DateTime now)
    {
        var ago = now - at;
        return ago.TotalHours < 1 ? $"{Math.Max(1, (int)ago.TotalMinutes)} min ago"
            : ago.TotalDays < 1 ? $"{(int)ago.TotalHours}h ago" : $"{(int)ago.TotalDays}d ago";
    }

    /// <summary>The longest run of consecutive samples (no gap over two minutes) where <paramref name="bad"/> holds.</summary>
    private static (int Length, int MinBusy, DateTime At)? Runs(List<MachineLoadSample> s, Func<MachineLoadSample, bool> bad, int atLeast)
    {
        (int, int, DateTime)? best = null;
        var start = -1;
        for (var i = 0; i <= s.Count; i++)
        {
            var ok = i < s.Count && bad(s[i]) && (start < 0 || (s[i].At - s[i - 1].At).TotalMinutes <= 2);
            if (ok) { if (start < 0) start = i; continue; }
            if (start >= 0)
            {
                var len = i - start;
                if (len >= atLeast && (best is null || len > best.Value.Item1))
                    best = (len, s.Skip(start).Take(len).Min(x => x.Busy), s[start].At);
                start = i < s.Count && bad(s[i]) ? i : -1;
            }
        }
        return best;
    }
}

/// <summary>
/// Gathers what the rules read, for every machine, from the database and the config: each
/// machine's latest inventory, its last two weeks of load samples and finished runs, and the
/// setup timeouts its tickets were told about.
/// </summary>
public class MachineLimitsService
{
    /// <summary>
    /// What <c>data/worker.ps1</c> writes on a ticket when a worktree's setup command hits its time
    /// limit, and the host it names. Read from the comment because that is the one record of it.
    /// </summary>
    public const string TimeoutText = "setup command timed out";

    /// <summary>
    /// The one comment per timeout that says where it happened. A timeout also leaves a
    /// "Blocked: Workspace setup failed" comment, which names no machine - counting that one too
    /// gave every worker host's timeouts to the board host as well.
    /// </summary>
    public const string PrepareText = "Heartbeat could not prepare a git worktree for this ticket";
    private static readonly Regex HostInComment = new(@"on worker host `([^`]+)`", RegexOptions.CultureInvariant);

    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    private readonly DashboardDbContext _db;
    private readonly ProjectRegistry _registry;

    public MachineLimitsService(DashboardDbContext db, ProjectRegistry registry) => (_db, _registry) = (db, registry);

    /// <summary>The machine a setup timeout happened on, or null for a comment that does not say.</summary>
    public static string? PlaceOfComment(string body) =>
        !body.StartsWith(PrepareText, StringComparison.Ordinal) || !body.Contains(TimeoutText) ? null
        : HostInComment.Match(body) is { Success: true } m ? m.Groups[1].Value : CapacityPlaces.Board;

    public async Task<object> ReadAsync(DateTime now, CancellationToken ct = default)
    {
        var global = _registry.GetGlobalConfig();
        var c = MachineLimitsConfig.Read(global);
        var since = now - Window;

        var machines = new List<(string Place, int Workers, int Qa, int Max)>
        {
            (CapacityPlaces.Board, BoardWorkers(global), ResourceLeaseService.QaLeases(global), HeartbeatManager.MaxWorkers),
        };
        machines.AddRange(WorkerHostConfig.Read(global).Select(h => (h.Name, h.Slots, h.QaLeases, WorkerHostConfig.MaxSlots)));

        var inventories = await _db.MachineInventories.AsNoTracking().OrderBy(m => m.FirstSeen).ToListAsync(ct);
        var samples = await _db.MachineLoadSamples.AsNoTracking().Where(s => s.At >= since).OrderBy(s => s.At).ToListAsync(ct);
        var capacity = await _db.CapacitySamples.AsNoTracking().Where(s => s.At >= now - MachineLimits.CeilingFor)
            .Select(s => new { s.Place, s.At, s.Busy }).ToListAsync(ct);
        var timeoutRows = await _db.Comments.AsNoTracking()
            .Where(x => x.CreatedAt >= now - MachineLimits.CeilingFor && x.Body.StartsWith(PrepareText) && x.Body.Contains(TimeoutText))
            .Select(x => new { x.CreatedAt, x.Body }).ToListAsync(ct);
        var runs = (await _db.CycleRuns.AsNoTracking()
                .Where(r => r.StartedAt >= since && r.DurationSeconds > 60 && r.NumTurns > 5 && r.ApiDurationSeconds != null)
                .Select(r => new { r.Host, r.StartedAt, r.EndedAt, r.DurationSeconds, r.ApiDurationSeconds, r.NumTurns, r.PrepSeconds, r.HandoffSeconds })
                .ToListAsync(ct))
            .Select(r => new RunTiming(string.IsNullOrWhiteSpace(r.Host) ? CapacityPlaces.Board : r.Host!, r.StartedAt, r.EndedAt,
                r.DurationSeconds, r.ApiDurationSeconds!.Value, r.NumTurns!.Value, r.PrepSeconds ?? 0, r.HandoffSeconds ?? 0))
            .ToList();

        var list = machines.Select(m =>
        {
            var mine = samples.Where(s => Same(s.Place, m.Place)).ToList();
            var invRow = inventories.LastOrDefault(i => Same(i.Place, m.Place));
            var inv = MachineInventoryStore.Read(invRow);
            var cap = capacity.Where(s => Same(s.Place, m.Place)).ToList();
            int? BusyAt(DateTime t) =>
                cap.Where(s => Math.Abs((s.At - t).TotalMinutes) <= 2).OrderBy(s => Math.Abs((s.At - t).Ticks)).Select(s => (int?)s.Busy).FirstOrDefault();
            var timeouts = timeoutRows.Where(x => PlaceOfComment(x.Body) is { } p && Same(p, m.Place)).Select(x => x.CreatedAt).ToList();
            var s = MachineLimits.Suggest(m.Place, m.Workers, m.Qa, m.Max, inv, mine, timeouts, BusyAt,
                runs.Where(r => Same(r.Place, m.Place)).ToList(), c, now);
            var last = mine.LastOrDefault();
            return new
            {
                place = m.Place,
                inventory = inv,
                inventorySince = invRow?.FirstSeen,
                inventorySeen = invRow?.LastSeen,
                history = inventories.Where(i => Same(i.Place, m.Place)).Select(i => new { i.FirstSeen, i.LastSeen, report = MachineInventoryStore.Read(i) }),
                load = last,
                samples = mine.Count,
                workers = s.Workers,
                qa = s.Qa,
                speed = s.Speed,
            };
        }).ToList();

        return new { auto = c.Auto, config = c, machines = list, fleet = MachineLimits.FleetModelTime(runs) };
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static int BoardWorkers(IReadOnlyDictionary<string, JsonElement> g) =>
        g.TryGetValue("heartbeatWorkers", out var w) && w.ValueKind == JsonValueKind.Number
            ? Math.Clamp((int)w.GetDouble(), 1, HeartbeatManager.MaxWorkers) : HeartbeatManager.DefaultWorkers;
}
