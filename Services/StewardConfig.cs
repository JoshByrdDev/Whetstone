using System.Text.Json;
using System.Text.RegularExpressions;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>A command the steward's merge runs on the result before it merges it.</summary>
/// <param name="Name">What to call it when it fails ("database tests").</param>
/// <param name="Run">The command line, run through <c>cmd /c</c> in the ticket's worktree.</param>
/// <param name="WhenChanged">Path prefixes; the check runs only when the ticket changed a file
/// under one of them. Empty = always.</param>
/// <param name="MachineFault">A pattern in the output that means the command never got as far as
/// checking anything - a database that is down, say. That is the machine's fault and not the
/// ticket's, so the ticket is left where it is and the owner is told, instead of being sent back
/// to be "fixed".</param>
/// <param name="SlowFault">A pattern that names a test failing only because it ran out of its own
/// time ("Test timed out in 20000ms"). When every failing test the check names is one of those, the
/// result is a busy machine's, not the ticket's, and is treated like the whole check running out of
/// time: merged again on a later run, and the owner's only when it happens again on the same tip.
/// A real failure beside them sends the ticket back as before (<see cref="CheckFailures.OnlySlow"/>).</param>
public record StewardCheck(string Name, string Run, string[] WhenChanged, string MachineFault, int TimeoutMinutes, string SlowFault = "");

/// <summary>
/// Where a project keeps its slices, and how to read them (docs/slice-branches.md, section 1).
/// <para>
/// The slices are a folder of spec files on the base branch, one per slice, each listing outcomes
/// by id and numbered from one for good. A slice is worked only once a person has marked it agreed,
/// and it is read from git AS COMMITTED, never from the working tree: nothing a steward can run
/// commits that mark, so an edit it made by hand counts for nothing.
/// </para>
/// </summary>
public record StewardPlan(string UnitsDir, string UnitFile, string Agreed, string Draft, string Outcome)
{
    public const string DefaultUnitFile = @"^(\d+)-[^/]*\.md$";
    public const string DefaultAgreed = @"\*\*Status:\s*agreed\b";
    public const string DefaultDraft = @"\*\*Status:\s*draft\b";
    public const string DefaultOutcome = @"^- \*\*([A-Z]+\d+)\.\*\*";
    public const string DefaultIdea = @"\*\*Status:\s*idea\b";

    /// <summary>
    /// What a slice's status says while it is only an idea: a title and a paragraph, not yet drafted.
    /// Nothing is filed against one, and only the owner writes one (docs/plans-as-slices.md, section 1).
    /// </summary>
    public string Idea { get; init; } = DefaultIdea;

    public bool Configured => !string.IsNullOrWhiteSpace(UnitsDir);

    /// <summary>
    /// What a draft's status is rewritten to when its owner agrees it from the board. <c>{date}</c>
    /// is the day. It has to be something <see cref="Agreed"/> matches and <see cref="Draft"/> does
    /// not, and a project that changes those two patterns changes this with them.
    /// </summary>
    public string AgreedLine { get; init; } = DefaultAgreedLine;

    public const string DefaultAgreedLine = "**Status: agreed {date}.**";
}

/// <summary>
/// Everything a project says about its steward. Read from the <c>steward</c> object of the
/// project's config, which belongs in <c>project.defaults.json</c> - it is how the project is
/// worked, and travels with the project's folder.
/// <para>
/// The one thing that is NOT in here is the mode. That is <c>stewardMode</c>, a top-level key of its
/// own, because the project's two config files merge shallowly: a mode inside this object would
/// have to drag every other setting into the machine-local file with it.
/// </para>
/// </summary>
public class StewardConfig
{
    /// <summary>How many tickets the steward may file in one local day.</summary>
    public int CreatedPerDay { get; init; } = 20;

    /// <summary>
    /// How many workable tickets the project may have open before the steward stops filing more.
    /// A ticket waiting on another, or parked in Backlog, is not counted here; every open ticket is
    /// counted against twice this (<see cref="StewardService.OpenCeiling"/>).
    /// </summary>
    public int OpenAtOnce { get; init; } = 12;

    /// <summary>How many times the steward may send one ticket back. The next is the owner's.</summary>
    public int SendBacksPerTicket { get; init; } = 2;

    /// <summary>
    /// A file in the repo that the owner's decisions are written out to, or empty for none.
    /// <para>
    /// The dashboard's database is the record a steward works from, and it is machine-local. What
    /// the owner decided is also part of the project's own history - it is why the code is the way
    /// it is - so it is kept beside the slices, on the base branch, where it travels with the code
    /// and is there for whoever reads the repo without a dashboard. Written out whole each time,
    /// from the database; nothing reads it back.
    /// </para>
    /// </summary>
    public string DecisionsFile { get; init; } = "";

    public StewardPlan Plan { get; init; } = new("", StewardPlan.DefaultUnitFile, StewardPlan.DefaultAgreed,
        StewardPlan.DefaultDraft, StewardPlan.DefaultOutcome);

    /// <summary>
    /// Paths an unattended merge may never carry: how the project deploys, what the fleet and the
    /// steward obey, the plan itself. A trailing slash is a folder; a rule with a <c>*</c> is a
    /// glob (<c>*</c> within one folder, <c>**</c> across them), so a family of files - every
    /// script in a folder - stays covered when the next one is added; anything else is one file.
    /// </summary>
    public string[] NeverMerge { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The conventions file (docs/slice-branches.md, section 4): the rules every slice keeps, edited
    /// by the owner and appended to only by a release. Never merged by the steward, whether or not
    /// <see cref="NeverMerge"/> names it. Empty: the project has none, and nothing is promoted.
    /// </summary>
    public string Conventions { get; init; } = "";

    public StewardCheck[] MergeChecks { get; init; } = Array.Empty<StewardCheck>();

    /// <summary>
    /// Run a merge's checks all at once rather than one after another, the first failure stopping
    /// the rest. Off unless a project says so: two checks that build into the same output folder
    /// (a build and a test of the same solution) break each other when run together, and only the
    /// project knows whether its checks do.
    /// </summary>
    public bool ParallelChecks { get; init; }

    /// <summary>How many slices may be open at once - agreed and neither released nor abandoned (section 3). The owner's.</summary>
    public int SlicesInFlight { get; init; } = 3;

    /// <summary>Whether <paramref name="path"/> is one the steward may not merge a change to.</summary>
    public bool IsNeverMerged(string path)
    {
        var p = path.Replace('\\', '/');
        if (Conventions.Length > 0 && string.Equals(p, Conventions, StringComparison.OrdinalIgnoreCase)) return true;
        return NeverMerge.Any(n =>
        {
            var rule = n.Replace('\\', '/');
            if (rule.Contains('*')) return Glob(rule).IsMatch(p);
            return rule.EndsWith('/')
                ? p.StartsWith(rule, StringComparison.OrdinalIgnoreCase)
                : string.Equals(p, rule, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static Regex Glob(string rule)
    {
        var parts = rule.Split("**");
        var body = string.Join(".*", parts.Select(part => string.Join("[^/]*", part.Split('*').Select(Regex.Escape))));
        return new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The mode in a config file, as a mode. Absent, empty and unrecognised all read as
    /// <see cref="StewardMode.Off"/>: a typo must not be what switches a steward on.
    /// </summary>
    public static StewardMode ParseMode(string? stored)
    {
        // By name only. Enum.TryParse also accepts "3", and a number in a config file is not
        // somebody asking for a steward that merges.
        var name = stored?.Trim() ?? "";
        return Enum.GetValues<StewardMode>().FirstOrDefault(
            m => string.Equals(m.ToString(), name, StringComparison.OrdinalIgnoreCase), StewardMode.Off);
    }

    public static StewardConfig From(Dictionary<string, JsonElement> cfg)
    {
        if (!cfg.TryGetValue("steward", out var s) || s.ValueKind != JsonValueKind.Object) return new StewardConfig();
        var d = new StewardConfig();

        var limits = s.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
        // How a slice's file and status lines are spelled, when a project's differ from the defaults.
        var format = s.TryGetProperty("sliceFormat", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;

        return new StewardConfig
        {
            CreatedPerDay = Int(limits, "createdPerDay", d.CreatedPerDay, 0, 1000),
            OpenAtOnce = Int(limits, "openAtOnce", d.OpenAtOnce, 1, 1000),
            SendBacksPerTicket = Int(limits, "sendBacksPerTicket", d.SendBacksPerTicket, 0, 20),
            DecisionsFile = Str(s, "decisionsFile").Replace('\\', '/').TrimStart('/'),
            Conventions = Str(s, "conventions").Replace('\\', '/').Trim('/'),
            Plan = PlanFrom(Str(s, "slices").Replace('\\', '/').Trim('/'), format),
            NeverMerge = StrArray(s, "neverMerge"),
            MergeChecks = Checks(s),
            ParallelChecks = s.TryGetProperty("parallelChecks", out var par) && par.ValueKind == JsonValueKind.True,
            SlicesInFlight = Int(s, "slicesInFlight", d.SlicesInFlight, 1, 50),
        };
    }

    private static StewardPlan PlanFrom(string folder, JsonElement format)
    {
        var line = Str(format, "agreedLine");
        return new StewardPlan(
            folder,
            Pattern(format, "unitFile", StewardPlan.DefaultUnitFile),
            Pattern(format, "agreed", StewardPlan.DefaultAgreed),
            Pattern(format, "draft", StewardPlan.DefaultDraft),
            Pattern(format, "outcome", StewardPlan.DefaultOutcome))
        {
            Idea = Pattern(format, "idea", StewardPlan.DefaultIdea),
            AgreedLine = line.Length > 0 ? line : StewardPlan.DefaultAgreedLine,
        };
    }

    private static StewardCheck[] Checks(JsonElement s)
    {
        if (!s.TryGetProperty("mergeChecks", out var list) || list.ValueKind != JsonValueKind.Array)
            return Array.Empty<StewardCheck>();
        var checks = new List<StewardCheck>();
        foreach (var c in list.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            var run = Str(c, "run");
            if (run.Length == 0) continue;
            var name = Str(c, "name");
            checks.Add(new StewardCheck(name.Length > 0 ? name : run, run, StrArray(c, "whenChanged"),
                Pattern(c, "machineFault", ""), Int(c, "timeoutMinutes", 15, 1, 120), Pattern(c, "slowFault", "")));
        }
        return checks.ToArray();
    }

    private static string Str(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim() : "";

    private static int Int(JsonElement obj, string prop, int fallback, int min, int max) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? Math.Clamp(n, min, max) : fallback;

    private static string[] StrArray(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim()).Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();

    /// <summary>A pattern from config, or the fallback when it is absent or will not compile.</summary>
    private static string Pattern(JsonElement obj, string prop, string fallback)
    {
        var p = Str(obj, prop);
        if (p.Length == 0) return fallback;
        try { _ = new Regex(p); return p; }
        catch (ArgumentException) { return fallback; }
    }
}
