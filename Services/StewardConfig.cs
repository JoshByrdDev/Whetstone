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
public record StewardCheck(string Name, string Run, string[] WhenChanged, string MachineFault, int TimeoutMinutes);

/// <summary>
/// Where a project keeps the plan its steward works from, and how to read it.
/// <para>
/// A plan is a folder of spec files on the base branch, one per unit of work (a slice, a
/// milestone, an epic - the platform does not care which), each listing outcomes by id. A unit is
/// worked only once a person has marked it agreed, and it is read from git AS COMMITTED, never from
/// the working tree: nothing a steward can run commits that mark, so an edit it made by hand counts
/// for nothing.
/// </para>
/// </summary>
public record StewardPlan(string UnitsDir, string UnitFile, string Agreed, string Draft, string Outcome)
{
    public const string DefaultUnitFile = @"^(\d+)-[^/]*\.md$";
    public const string DefaultAgreed = @"\*\*Status:\s*agreed\b";
    public const string DefaultDraft = @"\*\*Status:\s*draft\b";
    public const string DefaultOutcome = @"^- \*\*([A-Z]+\d+)\.\*\*";

    public bool Configured => !string.IsNullOrWhiteSpace(UnitsDir);

    /// <summary>
    /// The document the units are cut from - the whole of what is to be built, of which each unit
    /// is a part - or empty when the project keeps none. Nothing is decided from it: it is here so
    /// the owner can read the plan beside its progress.
    /// </summary>
    public string Master { get; init; } = "";
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

    /// <summary>How many tickets the project may have open before the steward stops filing more.</summary>
    public int OpenAtOnce { get; init; } = 12;

    /// <summary>How many times the steward may send one ticket back. The next is the owner's.</summary>
    public int SendBacksPerTicket { get; init; } = 2;

    /// <summary>How long the steward's memo to its next run may be, in characters.</summary>
    public int MemoMaxChars { get; init; } = 32000;

    /// <summary>
    /// A branch merged INTO the base branch at the start of each run, or empty for none. For a
    /// project whose base is an integration branch the plan and the owner's own fixes reach by
    /// way of another - without this the steward would be working from a plan that has moved on.
    /// </summary>
    public string SyncFrom { get; init; } = "";

    /// <summary>
    /// A file in the repo that the owner's decisions are written out to, or empty for none.
    /// <para>
    /// The dashboard's database is the record a steward works from, and it is machine-local. What
    /// the owner decided is also part of the project's own history - it is why the code is the way
    /// it is - so it is kept beside the plan, on the base branch, where it travels with the code
    /// and is there for whoever reads the repo without a dashboard. Written out whole each time,
    /// from the database; nothing reads it back.
    /// </para>
    /// </summary>
    public string DecisionsFile { get; init; } = "";

    public StewardPlan Plan { get; init; } = new("", StewardPlan.DefaultUnitFile, StewardPlan.DefaultAgreed,
        StewardPlan.DefaultDraft, StewardPlan.DefaultOutcome);

    /// <summary>
    /// Paths an unattended merge may never carry: how the project deploys, what the fleet and the
    /// steward obey, the plan itself. A trailing slash is a folder; anything else is one file.
    /// </summary>
    public string[] NeverMerge { get; init; } = Array.Empty<string>();

    public StewardCheck[] MergeChecks { get; init; } = Array.Empty<StewardCheck>();

    /// <summary>Whether <paramref name="path"/> is one the steward may not merge a change to.</summary>
    public bool IsNeverMerged(string path)
    {
        var p = path.Replace('\\', '/');
        return NeverMerge.Any(n =>
        {
            var rule = n.Replace('\\', '/');
            return rule.EndsWith('/')
                ? p.StartsWith(rule, StringComparison.OrdinalIgnoreCase)
                : string.Equals(p, rule, StringComparison.OrdinalIgnoreCase);
        });
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
        var plan = s.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;

        return new StewardConfig
        {
            CreatedPerDay = Int(limits, "createdPerDay", d.CreatedPerDay, 0, 1000),
            OpenAtOnce = Int(limits, "openAtOnce", d.OpenAtOnce, 1, 1000),
            SendBacksPerTicket = Int(limits, "sendBacksPerTicket", d.SendBacksPerTicket, 0, 20),
            MemoMaxChars = Int(s, "memoMaxChars", d.MemoMaxChars, 1000, 500000),
            SyncFrom = Str(s, "syncFrom"),
            DecisionsFile = Str(s, "decisionsFile").Replace('\\', '/').TrimStart('/'),
            Plan = new StewardPlan(
                Str(plan, "units").Replace('\\', '/').Trim('/'),
                Pattern(plan, "unitFile", StewardPlan.DefaultUnitFile),
                Pattern(plan, "agreed", StewardPlan.DefaultAgreed),
                Pattern(plan, "draft", StewardPlan.DefaultDraft),
                Pattern(plan, "outcome", StewardPlan.DefaultOutcome))
            { Master = Str(plan, "master").Replace('\\', '/').Trim('/') },
            NeverMerge = StrArray(s, "neverMerge"),
            MergeChecks = Checks(s),
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
                Pattern(c, "machineFault", ""), Int(c, "timeoutMinutes", 15, 1, 120)));
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
