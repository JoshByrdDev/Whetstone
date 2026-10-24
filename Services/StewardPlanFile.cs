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
}
