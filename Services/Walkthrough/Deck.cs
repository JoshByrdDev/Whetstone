using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whetstone.Services.Walkthrough;

/// <summary>
/// The two shapes of a walkthrough: what Claude writes (<see cref="JsonSchema"/>, read by
/// <see cref="WalkthroughValidator"/>) and what is stored and served once every reference in it
/// has been checked (<see cref="Deck"/>).
/// <para>
/// The script only ever POINTS. A code scene names a file and a range of new-side lines, never the
/// code; a flow scene names node ids in the analysis's own diagram; an image cue names a screenshot
/// and a box on it. The validator resolves every pointer against the real thing, which is what
/// makes a slide unable to disagree with the branch - every bug in the hand-built prototype was a
/// pointer that was copied instead of checked.
/// </para>
/// </summary>
public static class WalkthroughDeck
{
    public static readonly string[] SceneKinds = ["title", "ask", "shot", "code", "flow", "cards", "question"];

    /// <summary>
    /// Flat like the analysis schema, for the same reason: constrained decoding is far more reliable
    /// on one object with optional fields than on a union, and the validator only reads the fields a
    /// scene's kind uses.
    /// </summary>
    public const string JsonSchema = """
    {
      "type": "object",
      "required": ["scenes"],
      "additionalProperties": false,
      "properties": {
        "scenes": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["chapter", "kind", "beats"],
            "additionalProperties": false,
            "properties": {
              "chapter": { "type": "string" },
              "kind": { "type": "string", "enum": ["title", "ask", "shot", "code", "flow", "cards", "question"] },
              "shot": { "type": "integer", "minimum": 0 },
              "file": { "type": "string" },
              "from": { "type": "integer", "minimum": 1 },
              "to": { "type": "integer", "minimum": 1 },
              "reveal": { "type": "boolean" },
              "block": { "type": "string" },
              "blocks": { "type": "array", "items": { "type": "string" } },
              "quotes": { "type": "array", "items": { "type": "string" } },
              "attachments": { "type": "array", "items": { "type": "string" } },
              "beats": {
                "type": "array",
                "items": {
                  "type": "object",
                  "required": ["say"],
                  "additionalProperties": false,
                  "properties": {
                    "say": { "type": "string" },
                    "lines": { "type": "array", "items": { "type": "integer" } },
                    "nodes": { "type": "array", "items": { "type": "string" } },
                    "quotes": { "type": "array", "items": { "type": "integer" } },
                    "image": { "type": "string" },
                    "zoom": { "type": "array", "items": { "type": "integer" } },
                    "box": { "type": "array", "items": { "type": "integer" } },
                    "focus": { "type": "boolean" },
                    "reveal": { "type": "boolean" },
                    "show": { "type": "integer" }
                  }
                }
              }
            }
          }
        }
      }
    }
    """;

    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    // ---- what Claude writes ----

    public record RawScript(IReadOnlyList<RawScene>? Scenes);

    public record RawScene(string? Chapter, string? Kind)
    {
        public int? Shot { get; init; }
        public string? File { get; init; }
        public int? From { get; init; }
        public int? To { get; init; }
        public bool? Reveal { get; init; }
        public string? Block { get; init; }
        public IReadOnlyList<string>? Blocks { get; init; }
        public IReadOnlyList<string>? Quotes { get; init; }
        public IReadOnlyList<string>? Attachments { get; init; }
        public IReadOnlyList<RawBeat>? Beats { get; init; }
    }

    public record RawBeat(string? Say)
    {
        public IReadOnlyList<int>? Lines { get; init; }
        public IReadOnlyList<string>? Nodes { get; init; }
        public IReadOnlyList<int>? Quotes { get; init; }
        public string? Image { get; init; }
        public IReadOnlyList<int>? Zoom { get; init; }
        public IReadOnlyList<int>? Box { get; init; }
        public bool? Focus { get; init; }
        public bool? Reveal { get; init; }
        public int? Show { get; init; }
    }

    // ---- what is stored and served ----

    public record Deck(string Voice, IReadOnlyList<Scene> Scenes);

    /// <param name="Block">An analysis block, as "section.block" (flow, question).</param>
    /// <param name="Blocks">Several, in order (cards).</param>
    /// <param name="Images">What this scene can show: its QA shot, or the ticket's attachments.</param>
    public record Scene(string Chapter, string Kind, IReadOnlyList<Beat> Beats)
    {
        public int? Shot { get; init; }
        public string? Block { get; init; }
        public IReadOnlyList<string>? Blocks { get; init; }
        public IReadOnlyList<string>? Quotes { get; init; }
        public IReadOnlyList<Image>? Images { get; init; }
        public Code? Code { get; init; }
    }

    public record Image(string Name, string Url, int W, int H);

    public record Code(string Path, string? Language, int Added, int Removed, bool Reveal, IReadOnlyList<DiffSnippet.Line> Lines);

    /// <param name="Spoken">What the voice was actually given - see <see cref="Pronunciation"/>.</param>
    /// <param name="Clip">The voiced line's name under walkthrough-clips; null when it could not be voiced.</param>
    /// <param name="Dur">Seconds. The clip's length, or an estimate the player times a silent beat by.</param>
    public record Beat(string Say, Cue? Cue)
    {
        public string? Spoken { get; init; }
        public string? Clip { get; init; }
        public double Dur { get; init; }
    }

    /// <param name="Lines">[first, last] new-side line numbers to light, on a code scene.</param>
    /// <param name="Nodes">Flow node ids to light.</param>
    /// <param name="Quotes">Indexes into the scene's quotes to light.</param>
    /// <param name="Image">Which of the scene's images to show, by name.</param>
    /// <param name="Show">How many cards are showing by this beat.</param>
    public record Cue(IReadOnlyList<int>? Lines, IReadOnlyList<string>? Nodes, IReadOnlyList<int>? Quotes,
        string? Image, ImageRect? Zoom, ImageRect? Box, bool? Reveal, int? Show);

    public static string Serialize(Deck deck) => JsonSerializer.Serialize(deck, Wire);

    public static Deck? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Deck>(json, Wire); }
        catch (JsonException) { return null; }
    }
}
