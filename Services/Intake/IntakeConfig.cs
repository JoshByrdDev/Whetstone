using System.Text.Json;

namespace Whetstone.Services.Intake;

/// <summary>
/// One queue intake reads (docs/intake-plan.md, section 2). <see cref="Kind"/> picks the transport:
/// <c>sqs</c> for a real queue, <c>standin</c> for the in-memory stand-in a test or a QA copy feeds.
/// </summary>
public sealed record IntakeSource(
    string Name,
    string Kind,
    string QueueUrl,
    string Region,
    string AccessKeyId,
    string SecretAccessKey,
    int PollSeconds,
    // A different endpoint for an SQS-compatible stand-in (ElasticMQ, LocalStack); blank is AWS.
    string ServiceUrl)
{
    public const string Sqs = "sqs";
    public const string StandIn = "standin";

    public bool IsStandIn => Kind == StandIn;

    /// <summary>The secret as a page may show it: its last four characters at most.</summary>
    public string MaskedSecret => SecretAccessKey.Length <= 4 ? new string('•', SecretAccessKey.Length)
        : new string('•', 8) + SecretAccessKey[^4..];
}

public sealed record IntakeLimits(int NewPerHour, int NewPerDay)
{
    public static readonly IntakeLimits Default = new(5, 20);
}

/// <summary>
/// A project's intake, read from its machine-local <c>project.json</c> only. It holds a
/// credential, so <c>project.defaults.json</c> - committed, and part of what a project's repo
/// shares - is never read for it, and it is kept off <see cref="ProjectConfig"/>, which other parts
/// of the board serialise and hand on.
/// </summary>
public sealed record IntakeConfig(bool Enabled, IReadOnlyList<IntakeSource> Sources, IntakeLimits Limits, string? Runbook,
    IReadOnlyList<string> Problems)
{
    public static readonly IntakeConfig None = new(false, [], IntakeLimits.Default, null, []);

    public bool IsOn => Enabled && Sources.Count > 0;

    /// <summary>Reads the <c>intake</c> block of one file. Anything malformed is a problem said in words, not a crash.</summary>
    public static IntakeConfig FromFile(string projectJsonPath)
    {
        if (!File.Exists(projectJsonPath)) return None;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(projectJsonPath));
            return doc.RootElement.TryGetProperty("intake", out var i) ? From(i) : None;
        }
        catch (JsonException e)
        {
            return None with { Problems = [$"project.json does not parse: {e.Message}"] };
        }
    }

    public static IntakeConfig From(JsonElement i)
    {
        if (i.ValueKind != JsonValueKind.Object) return None with { Problems = ["\"intake\" is not an object."] };
        var problems = new List<string>();
        var sources = new List<IntakeSource>();
        if (i.TryGetProperty("sources", out var ss) && ss.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in ss.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var name = Str(s, "name");
                var kind = Str(s, "kind").ToLowerInvariant();
                if (name.Length == 0) { problems.Add("A source has no name."); continue; }
                if (sources.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                { problems.Add($"Two sources are named {name}."); continue; }
                if (kind is not (IntakeSource.Sqs or IntakeSource.StandIn))
                { problems.Add($"Source {name}: kind \"{kind}\" is not one intake knows (sqs, standin)."); continue; }
                var src = new IntakeSource(name, kind, Str(s, "queueUrl"), Str(s, "region"), Str(s, "accessKeyId"),
                    Str(s, "secretAccessKey"),
                    s.TryGetProperty("pollSeconds", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pv)
                        ? Math.Clamp(pv, 10, 3600) : 60,
                    Str(s, "serviceUrl"));
                if (kind == IntakeSource.Sqs)
                {
                    var missing = new[] { ("queueUrl", src.QueueUrl), ("region", src.Region), ("accessKeyId", src.AccessKeyId), ("secretAccessKey", src.SecretAccessKey) }
                        .Where(x => x.Item2.Length == 0).Select(x => x.Item1).ToList();
                    if (missing.Count > 0) { problems.Add($"Source {name} is missing {string.Join(", ", missing)}."); continue; }
                }
                sources.Add(src);
            }
        }
        var limits = IntakeLimits.Default;
        if (i.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Object)
            limits = new IntakeLimits(Int(l, "newPerHour", limits.NewPerHour), Int(l, "newPerDay", limits.NewPerDay));
        var runbook = Str(i, "runbook");
        return new IntakeConfig(i.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True,
            sources, limits, runbook.Length == 0 ? null : runbook, problems);
    }

    private static string Str(JsonElement o, string p) =>
        o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    private static int Int(JsonElement o, string p, int fallback) =>
        o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? Math.Max(0, n) : fallback;
}

/// <summary>Where each project's intake config is read from. A seam, so a test can hand one in.</summary>
public interface IIntakeSettings
{
    IntakeConfig For(string project);
    IReadOnlyList<string> Projects { get; }
}

public sealed class IntakeSettings(ProjectRegistry registry) : IIntakeSettings
{
    public IntakeConfig For(string project) => IntakeConfig.FromFile(registry.Paths.ProjectConfig(project));
    public IReadOnlyList<string> Projects => registry.Projects;
}
