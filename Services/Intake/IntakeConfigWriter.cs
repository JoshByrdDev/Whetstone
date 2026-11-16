using System.Text.Json;
using System.Text.Json.Nodes;

namespace Whetstone.Services.Intake;

/// <summary>A source as the Settings page sends it. A blank secret keeps the one on disk.</summary>
public sealed record IntakeSourceInput(string Name, string Kind, string? QueueUrl, string? Region, string? AccessKeyId,
    string? SecretAccessKey, int? PollSeconds, string? ServiceUrl);

public sealed record IntakeConfigInput(bool Enabled, IReadOnlyList<IntakeSourceInput> Sources, int? NewPerHour, int? NewPerDay, string? Runbook);

/// <summary>
/// Writes a project's <c>intake</c> block into its machine-local <c>project.json</c>, leaving every
/// other key as it was. The secret is never sent back to the page, so a blank one keeps the saved
/// value rather than erasing it.
/// </summary>
public static class IntakeConfigWriter
{
    public static (IntakeConfig? Saved, IReadOnlyList<string> Problems) Write(string projectJsonPath, IntakeConfigInput input)
    {
        var existing = IntakeConfig.FromFile(projectJsonPath);
        JsonObject root;
        try { root = File.Exists(projectJsonPath) ? JsonNode.Parse(File.ReadAllText(projectJsonPath)) as JsonObject ?? new() : new(); }
        catch (JsonException e) { return (null, [$"project.json does not parse, so it was left alone: {e.Message}"]); }

        var sources = new JsonArray();
        foreach (var s in input.Sources)
        {
            var name = (s.Name ?? "").Trim();
            var secret = string.IsNullOrWhiteSpace(s.SecretAccessKey)
                ? existing.Sources.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.SecretAccessKey ?? ""
                : s.SecretAccessKey.Trim();
            var o = new JsonObject { ["name"] = name, ["kind"] = (s.Kind ?? "").Trim().ToLowerInvariant() };
            void Put(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) o[k] = v.Trim(); }
            Put("queueUrl", s.QueueUrl);
            Put("region", s.Region);
            Put("accessKeyId", s.AccessKeyId);
            Put("secretAccessKey", secret);
            Put("serviceUrl", s.ServiceUrl);
            if (s.PollSeconds is { } p) o["pollSeconds"] = p;
            sources.Add(o);
        }
        var block = new JsonObject
        {
            ["enabled"] = input.Enabled,
            ["sources"] = sources,
            ["limits"] = new JsonObject
            {
                ["newPerHour"] = input.NewPerHour ?? existing.Limits.NewPerHour,
                ["newPerDay"] = input.NewPerDay ?? existing.Limits.NewPerDay,
            },
        };
        if (!string.IsNullOrWhiteSpace(input.Runbook)) block["runbook"] = input.Runbook.Trim();

        var check = IntakeConfig.From(JsonDocument.Parse(block.ToJsonString()).RootElement);
        if (check.Problems.Count > 0) return (null, check.Problems);

        root["intake"] = block;
        Directory.CreateDirectory(Path.GetDirectoryName(projectJsonPath)!);
        File.WriteAllText(projectJsonPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (check, []);
    }
}
