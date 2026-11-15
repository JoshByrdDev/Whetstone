using System.Text.Json;

namespace Whetstone.Services;

/// <summary>One area of a product, named once so every visit, observation and proposal can carry it.</summary>
public sealed record DiscoveryArea(string Name, string[] Pages, string[] Code);

/// <summary>
/// A project's <c>discovery</c> block in <c>project.defaults.json</c> (docs/discovery-track-plan.md).
/// Everything about how a project's product is stood up for a scout is here, never in the
/// platform: the agent that scouts has no shell, so the worker brings the product up before the
/// session starts and takes it down after.
/// </summary>
public sealed record DiscoveryConfig
{
    /// <summary>At most this many proposals per sift. The rest go on the dropped list.</summary>
    public int MaxProposals { get; init; } = 6;

    /// <summary>
    /// Where the product answers once prepared, as slot 0 sees it (<c>http://localhost:53590</c>).
    /// A run on stack slot N adds N times <c>stackPorts.slotStep</c> to the port. The browser is
    /// allowed this origin and no other.
    /// </summary>
    public string SiteUrl { get; init; } = "";

    /// <summary>
    /// The command that brings the product up on its showcase data, run by the worker in the
    /// discovery's worktree before Scout starts (pwsh, with WHETSTONE_STACK_SLOT set). It returns
    /// once the site answers, or fails the run.
    /// </summary>
    public string Prepare { get; init; } = "";

    /// <summary>The command that takes it down again after the run, when there is one.</summary>
    public string Teardown { get; init; } = "";

    public const int DefaultRecheckRounds = 2;

    /// <summary>
    /// How many rechecks get the owner's verdict. A recheck past this many can still propose, but
    /// only to the sign-off, where the owner sees what it found; so rounds end. 0 means every
    /// recheck reports to the sign-off.
    /// </summary>
    public int RecheckRounds { get; init; } = DefaultRecheckRounds;

    /// <summary>
    /// The default for a new discovery's route-review switch: whether the owner reads the route
    /// Chart wrote before a scout walks it. Off unless the project says so; the filing form can
    /// change it per ticket.
    /// </summary>
    public bool ReviewRoute { get; init; }

    public const long DefaultStorageBudgetMb = 2048;

    /// <summary>
    /// A backstop over the project's discovery media - recordings, clips, shots - in MB. Past it the
    /// retention pass removes the media of the discoveries Done longest, never an open one's and
    /// never the record. 0 or less turns it off.
    /// </summary>
    public long StorageBudgetMb { get; init; } = DefaultStorageBudgetMb;

    public const int DefaultTasteFoldEvery = 10;

    /// <summary>
    /// How many verdicts - proposals the owner approved, rejected as generic or wrong, or restored
    /// from the dropped list - since the last fold make the next one due. 1..100.
    /// </summary>
    public int TasteFoldEvery { get; init; } = DefaultTasteFoldEvery;

    /// <summary>The product's areas, for coverage: each one's page patterns and code paths.</summary>
    public DiscoveryArea[] Areas { get; init; } = Array.Empty<DiscoveryArea>();

    /// <summary>Whether a scout can run here at all: a site to point the browser at.</summary>
    public bool CanScout => SiteUrl.Length > 0;

    public static DiscoveryConfig From(Dictionary<string, JsonElement> cfg)
    {
        if (!cfg.TryGetValue("discovery", out var d) || d.ValueKind != JsonValueKind.Object) return new DiscoveryConfig();
        var areas = new List<DiscoveryArea>();
        if (d.TryGetProperty("areas", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var e in a.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && Str(e, "name") is { Length: > 0 } name)
                    areas.Add(new DiscoveryArea(name, Strs(e, "pages"), Strs(e, "code")));
        var max = d.TryGetProperty("maxProposals", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 6;
        var rounds = d.TryGetProperty("recheckRounds", out var rr) && rr.ValueKind == JsonValueKind.Number ? rr.GetInt32() : DefaultRecheckRounds;
        var review = d.TryGetProperty("reviewRoute", out var rv) && rv.ValueKind == JsonValueKind.True;
        var fold = d.TryGetProperty("tasteFoldEvery", out var tf) && tf.ValueKind == JsonValueKind.Number && tf.TryGetInt32(out var n) ? n : DefaultTasteFoldEvery;
        var budget = d.TryGetProperty("storageBudgetMb", out var sb) && sb.ValueKind == JsonValueKind.Number && sb.TryGetInt64(out var mb) ? mb : DefaultStorageBudgetMb;
        return new DiscoveryConfig
        {
            MaxProposals = Math.Clamp(max, 1, 20),
            RecheckRounds = Math.Clamp(rounds, 0, 10),
            ReviewRoute = review,
            StorageBudgetMb = budget,
            TasteFoldEvery = Math.Clamp(fold, 1, 100),
            SiteUrl = Str(d, "siteUrl").TrimEnd('/'),
            Prepare = Str(d, "prepare"),
            Teardown = Str(d, "teardown"),
            Areas = areas.ToArray(),
        };
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static string[] Strs(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();
}
