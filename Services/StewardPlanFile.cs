using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Whetstone.Services;

/// <summary>
/// Writing which plan a project's steward works into the project's own settings.
/// <para>
/// A project works one plan at a time: <c>steward.plan</c>. Starting another does not throw the
/// last away - what was built under it is still worth reading - so it is moved to
/// <c>steward.pastPlans</c>, where the board can show it and nothing files against it.
/// </para>
/// <para>
/// The file is the project's committed defaults, and it is written whole. Everything else in it is
/// kept as it was, comments-as-keys included; only its layout is the serializer's from then on.
/// </para>
/// </summary>
public static class StewardPlanFile
{
    private static readonly JsonSerializerOptions Out = new()
    {
        WriteIndented = true,
        // The settings are read by people: an apostrophe or an arrow must not come back as '.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Make <paramref name="key"/> the plan being worked. Returns why the file was left alone, or
    /// null once it is written.
    /// </summary>
    public static async Task<string?> StartAsync(string defaultsFile, string key, string name, string units, string charter,
        CancellationToken ct = default)
    {
        JsonObject root;
        try
        {
            root = File.Exists(defaultsFile)
                ? JsonNode.Parse(await File.ReadAllTextAsync(defaultsFile, ct),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new()
                : new();
        }
        catch (JsonException) { return $"{defaultsFile} is not valid JSON, so it was left alone."; }

        if (root["steward"] is not JsonObject steward) root["steward"] = steward = new JsonObject();

        // The plan that was being worked is finished with, not forgotten.
        if (steward["plan"] is JsonObject was && was["units"] is not null)
        {
            if (steward["pastPlans"] is not JsonArray past) steward["pastPlans"] = past = new JsonArray();
            past.Add(was.DeepClone());
        }
        steward["plan"] = new JsonObject { ["key"] = key, ["name"] = name, ["units"] = units, ["charter"] = charter };

        await File.WriteAllTextAsync(defaultsFile, root.ToJsonString(Out) + Environment.NewLine, ct);
        return null;
    }

    /// <summary>
    /// Switch a project's settings to slice branches (docs/slice-branches.md, piece 8): <c>sliceBranches</c>
    /// on, the base pointed at <paramref name="baseBranch"/> in whichever file names it now, and the
    /// plan around the slices gone - its key, name, charter and master, the past plans and
    /// <c>syncFrom</c>. The plan's folder and its patterns stay: they are where the slices are, and
    /// how they read. Returns why nothing was written, or null once both files are.
    /// </summary>
    public static async Task<string?> SwitchAsync(string defaultsFile, string localFile, string baseBranch, string? conventions,
        CancellationToken ct = default)
    {
        var (defaults, no1) = await ReadAsync(defaultsFile, ct);
        if (no1 is not null) return no1;
        var (local, no2) = await ReadAsync(localFile, ct);
        if (no2 is not null) return no2;

        // The machine-local file wins when both say it, so that is the one to change.
        if (local!["baseBranch"] is not null) local["baseBranch"] = baseBranch;
        else defaults!["baseBranch"] = baseBranch;
        defaults!["sliceBranches"] = true;
        local.Remove("sliceBranches");

        if (defaults["steward"] is not JsonObject steward) defaults["steward"] = steward = new JsonObject();
        steward.Remove("syncFrom");
        steward.Remove("pastPlans");
        if (steward["plan"] is JsonObject plan)
            foreach (var gone in new[] { "key", "name", "charter", "master" }) plan.Remove(gone);
        if (!string.IsNullOrWhiteSpace(conventions)) steward["conventions"] = conventions;

        await File.WriteAllTextAsync(defaultsFile, defaults.ToJsonString(Out) + Environment.NewLine, ct);
        if (File.Exists(localFile)) await File.WriteAllTextAsync(localFile, local.ToJsonString(Out) + Environment.NewLine, ct);
        return null;
    }

    private static async Task<(JsonObject? Root, string? Refusal)> ReadAsync(string file, CancellationToken ct)
    {
        try
        {
            return (File.Exists(file)
                ? JsonNode.Parse(await File.ReadAllTextAsync(file, ct),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new()
                : new(), null);
        }
        catch (JsonException) { return (null, $"{file} is not valid JSON, so it was left alone."); }
    }

    /// <summary>Whether both files can be read, before anything else is changed.</summary>
    public static async Task<string?> CheckAsync(string defaultsFile, string localFile, CancellationToken ct = default) =>
        (await ReadAsync(defaultsFile, ct)).Refusal ?? (await ReadAsync(localFile, ct)).Refusal;
}
