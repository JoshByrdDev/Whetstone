using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whetstone.Services;

/// <summary>
/// The shape a Classroom analysis comes back in, and the only place that knows it.
/// <para>
/// The whole feature turns on this file. A teaching artefact asked for as free markdown drifts into
/// a wall of paragraphs every time, however the prompt is worded — so the run is not asked for prose
/// at all. It is constrained to a small set of typed blocks by <see cref="JsonSchema"/>, handed to
/// the CLI as <c>--json-schema</c>, and the renderer owns what they look like. Digestibility is
/// structural rather than aspirational, and a section with nothing real to say comes back as an
/// absent block instead of filler.
/// </para>
/// </summary>
public static class AnalysisDocument
{
    /// <summary>The eleven block kinds. Anything outside this set is dropped on the way in.</summary>
    public static readonly string[] Kinds =
        ["prose", "keyValue", "comparison", "example", "readingPath", "glossary", "callout", "question", "code", "userFlow", "coverage"];

    /// <summary>
    /// The verdicts a run may return on one line of the requirement text.
    /// <para>
    /// <c>unaddressed</c> is deliberately NOT in this set. It is what a segment gets when the run
    /// returned nothing for it, and is written by <see cref="MapCoverage"/> alone — a model able to
    /// choose "no ruling" would choose it, and the entire value of the state is that it cannot.
    /// </para>
    /// </summary>
    public static readonly string[] Verdicts = ["met", "partial", "gap", "deferred", "unknown", "context"];

    /// <summary>
    /// Ceiling on a snippet quoted onto one line of the coverage map.
    /// <para>
    /// Far tighter than the twenty lines a <c>code</c> block gets, because it lives in a column
    /// beside the requirement it explains: every line it grows is a line of blank space opposite it,
    /// and a map where each row is a screenful stops being a map.
    /// </para>
    /// </summary>
    private const int MaxCoverageSnippetLines = 8;

    /// <summary>
    /// Ceiling on a user-flow diagram. A route through the product that needs more stops than this
    /// is not one route, and drawing it would produce something nobody reads — so the extra nodes
    /// are dropped rather than rendered small.
    /// </summary>
    private const int MaxFlowNodes = 14;

    /// <summary>
    /// Ceiling on the cases in one <c>example</c>. An example earns its place by putting a rule's
    /// edge in front of the reader - two cases that differ in one thing - and past four it has
    /// become a test table, which the reader skims for the same reason they skim the rule.
    /// </summary>
    private const int MaxExampleCases = 4;

    /// <summary>
    /// The JSON Schema handed to <c>claude --json-schema</c>, which validates the model's output
    /// before it ever reaches us.
    /// <para>
    /// Deliberately ONE block object with optional fields rather than a <c>oneOf</c> over seven
    /// shapes. Schema-constrained decoding is far more reliable on a flat object, and the cost —
    /// a <c>prose</c> block could technically carry a <c>rows</c> field — is nothing, because
    /// <see cref="Parse"/> only reads the fields that kind actually uses.
    /// </para>
    /// </summary>
    public const string JsonSchema = """
    {
      "type": "object",
      "required": ["headline", "sections"],
      "additionalProperties": false,
      "properties": {
        "headline": { "type": "string" },
        "sections": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["title", "blocks"],
            "additionalProperties": false,
            "properties": {
              "title": { "type": "string" },
              "blocks": {
                "type": "array",
                "items": {
                  "type": "object",
                  "required": ["kind"],
                  "additionalProperties": false,
                  "properties": {
                    "kind": {
                      "type": "string",
                      "enum": ["prose", "keyValue", "comparison", "example", "readingPath", "glossary", "callout", "question", "code", "userFlow", "coverage"]
                    },
                    "text": { "type": "string" },
                    "title": { "type": "string" },
                    "tone": { "type": "string", "enum": ["gotcha", "insight", "risk"] },
                    "note": { "type": "string" },
                    "path": { "type": "string" },
                    "startLine": { "type": "integer", "minimum": 1 },
                    "language": { "type": "string" },
                    "columns": { "type": "array", "items": { "type": "string" } },
                    "rows": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } },
                    "items": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "required": ["key", "value"],
                        "additionalProperties": false,
                        "properties": { "key": { "type": "string" }, "value": { "type": "string" } }
                      }
                    },
                    "steps": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "required": ["location", "what"],
                        "additionalProperties": false,
                        "properties": {
                          "location": { "type": "string" },
                          "what": { "type": "string" },
                          "why": { "type": "string" }
                        }
                      }
                    },
                    "terms": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "required": ["term", "meaning"],
                        "additionalProperties": false,
                        "properties": { "term": { "type": "string" }, "meaning": { "type": "string" } }
                      }
                    },
                    "nodes": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "required": ["id", "kind", "label", "change"],
                        "additionalProperties": false,
                        "properties": {
                          "id": { "type": "string" },
                          "kind": { "type": "string", "enum": ["start", "screen", "step", "decision", "system", "end"] },
                          "label": { "type": "string" },
                          "explain": { "type": "string" },
                          "actor": { "type": "string" },
                          "change": { "type": "string", "enum": ["new", "changed", "unchanged", "removed"] },
                          "refs": { "type": "array", "items": { "type": "string" } },
                          "shot": { "type": "integer", "minimum": 0 },
                          "asks": { "type": "array", "items": { "type": "string" } }
                        }
                      }
                    },
                    "rulings": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "required": ["id", "verdict"],
                        "additionalProperties": false,
                        "properties": {
                          "id": { "type": "string" },
                          "verdict": { "type": "string", "enum": ["met", "partial", "gap", "deferred", "unknown", "context"] },
                          "evidence": { "type": "string" },
                          "refs": { "type": "array", "items": { "type": "string" } },
                          "qa": { "type": "string" },
                          "shot": { "type": "integer", "minimum": 0 },
                          "deferredTo": { "type": "string" },
                          "snippet": {
                            "type": "object",
                            "required": ["text"],
                            "additionalProperties": false,
                            "properties": {
                              "text": { "type": "string" },
                              "path": { "type": "string" },
                              "startLine": { "type": "integer", "minimum": 1 },
                              "language": { "type": "string" }
                            }
                          }
                        }
                      }
                    },
                    "edges": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "required": ["from", "to"],
                        "additionalProperties": false,
                        "properties": {
                          "from": { "type": "string" },
                          "to": { "type": "string" },
                          "label": { "type": "string" },
                          "change": { "type": "string", "enum": ["new", "changed", "unchanged", "removed"] }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
      }
    }
    """;

    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Validate and normalize what the run produced.
    /// <para>
    /// A block missing the payload its kind needs is DROPPED, not fatal. The schema already stops
    /// the output being nonsense; what is left here is the model filling six fields and leaving the
    /// seventh empty, and losing a three-minute run over one empty block would be the wrong trade.
    /// The run only fails when nothing survives — which is the case where there is genuinely
    /// nothing to show.
    /// </para>
    /// </summary>
    /// <param name="segments">The requirement text this run was asked to rule on, from
    /// <see cref="DescriptionSegmenter"/>. Joining happens here rather than in the renderer because
    /// this is the only place that can compare what was ASKED FOR against what came BACK — see
    /// <see cref="MapCoverage"/>. Null (or empty) on a ticket with no requirement text, and then a
    /// coverage block is dropped like any other block with no payload.</param>
    public static (Doc? doc, string? error) Parse(
        JsonElement root, IReadOnlyList<DescriptionSegmenter.Segment>? segments = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return (null, "analysis output was not an object");

        var headline = Str(root, "headline");
        if (!root.TryGetProperty("sections", out var sectionsEl) || sectionsEl.ValueKind != JsonValueKind.Array)
            return (null, "analysis output carried no sections");

        var sections = new List<Section>();
        foreach (var s in sectionsEl.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var title = Str(s, "title");
            if (title.Length == 0) continue;

            var blocks = new List<Block>();
            if (s.TryGetProperty("blocks", out var blocksEl) && blocksEl.ValueKind == JsonValueKind.Array)
                foreach (var b in blocksEl.EnumerateArray())
                    if (ParseBlock(b, segments) is { } block)
                        blocks.Add(block);

            // A titled section with nothing under it is the filler this whole design exists to avoid.
            if (blocks.Count > 0) sections.Add(new Section(title, blocks));
        }

        if (sections.Count == 0)
            return (null, "analysis output had no usable sections");

        if (headline.Length == 0) headline = sections[0].Title;
        return (new Doc(headline, sections), null);
    }

    private static Block? ParseBlock(JsonElement b, IReadOnlyList<DescriptionSegmenter.Segment>? segments)
    {
        if (b.ValueKind != JsonValueKind.Object) return null;
        var kind = Str(b, "kind");
        if (!Kinds.Contains(kind)) return null;

        var text = Str(b, "text");
        var title = Str(b, "title");
        var note = Str(b, "note");

        switch (kind)
        {
            case "prose":
            case "question":
                return text.Length > 0 ? new Block(kind) { Text = text } : null;

            case "code":
            {
                // Trailing whitespace is trimmed but INDENTATION is not: the leading whitespace is
                // part of what the snippet is showing, and Str() would have eaten it.
                var snippet = (b.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()! : "").TrimEnd();
                if (snippet.Length == 0) return null;
                return new Block(kind)
                {
                    Text = snippet,
                    // A snippet of code that no longer exists (the "how it worked before" case) has
                    // no line in the file today, so it arrives with a title and no startLine — and
                    // the renderer draws no line numbers rather than counting from 1.
                    Title = NullIfEmpty(title),
                    Path = NullIfEmpty(Str(b, "path")),
                    StartLine = b.TryGetProperty("startLine", out var sl) && sl.TryGetInt32(out var n) && n > 0 ? n : null,
                    Language = NullIfEmpty(Str(b, "language")),
                    Note = NullIfEmpty(note),
                };
            }

            case "callout":
            {
                if (text.Length == 0) return null;
                var tone = Str(b, "tone");
                if (tone.Length == 0) tone = "insight";
                return new Block(kind) { Text = text, Title = NullIfEmpty(title), Tone = tone };
            }

            case "keyValue":
            {
                var items = Pairs(b, "items", "key", "value").Select(p => new KeyValueItem(p.a, p.b)).ToList();
                return items.Count > 0 ? new Block(kind) { Title = NullIfEmpty(title), Items = items } : null;
            }

            case "glossary":
            {
                var terms = Pairs(b, "terms", "term", "meaning").Select(p => new GlossaryTerm(p.a, p.b)).ToList();
                return terms.Count > 0 ? new Block(kind) { Title = NullIfEmpty(title), Terms = terms } : null;
            }

            case "comparison":
            {
                var (columns, rows) = Table(b);
                return rows.Count > 0
                    ? new Block(kind) { Title = NullIfEmpty(title), Columns = columns, Rows = rows, Note = NullIfEmpty(note) }
                    : null;
            }

            case "example":
            {
                // A comparison's table with a setup above it: `text` is the made-up situation, the
                // rows are cases run through the real rule, `note` is what the cases show. Without the
                // setup the rows are outcomes for inputs the reader was never introduced to, so a
                // block missing it is dropped like any other block missing its payload.
                if (text.Length == 0) return null;
                var (columns, rows) = Table(b);
                return rows.Count > 0
                    ? new Block(kind)
                    {
                        Title = NullIfEmpty(title), Text = text,
                        Columns = columns, Rows = rows.Take(MaxExampleCases).ToList(),
                        Note = NullIfEmpty(note),
                    }
                    : null;
            }

            case "readingPath":
            {
                var steps = new List<ReadingStep>();
                if (b.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                    foreach (var s in stepsEl.EnumerateArray())
                    {
                        if (s.ValueKind != JsonValueKind.Object) continue;
                        var location = Str(s, "location");
                        var what = Str(s, "what");
                        if (location.Length == 0 || what.Length == 0) continue;
                        steps.Add(new ReadingStep(location, what, NullIfEmpty(Str(s, "why"))));
                    }
                return steps.Count > 0 ? new Block(kind) { Title = NullIfEmpty(title), Steps = steps } : null;
            }

            case "coverage":
            {
                // No requirement text means there was nothing to be held to. A locally-raised ticket
                // with a one-line title is the usual case, and an empty map on one would be a rail
                // of grey dots implying an audit that never had anything to audit.
                if (segments is not { Count: > 0 }) return null;
                return new Block(kind)
                {
                    Title = NullIfEmpty(title),
                    Note = NullIfEmpty(note),
                    // `text` on a coverage block is the whole map in one sentence — the answer to
                    // "did we build what was asked for" before a single row is read. Reusing the
                    // shared field rather than adding a kind-specific one keeps the flat block shape
                    // that makes schema-constrained decoding reliable.
                    Text = NullIfEmpty(text),
                    Segments = MapCoverage(b, segments),
                };
            }

            case "userFlow":
            {
                var (nodes, edges) = ParseFlow(b, segments);
                // One box and no line is not a flow, it is a label. Two nodes joined by one edge is
                // the smallest thing that says "then".
                return nodes.Count >= 2 && edges.Count > 0
                    ? new Block(kind) { Title = NullIfEmpty(title), Nodes = nodes, Edges = edges, Note = NullIfEmpty(note) }
                    : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Join what the run ruled onto what it was asked to rule on.
    /// <para>
    /// The segments lead and the rulings follow, never the other way round. Walking the rulings
    /// would produce a map of exactly the lines the run chose to mention, which is a map that can
    /// never show an omission — and an omission is the single thing this feature exists to catch.
    /// Walking the segments means every line of the ticket appears, and a line the run said nothing
    /// about appears saying so.
    /// </para>
    /// </summary>
    private static List<CoverageSegment> MapCoverage(
        JsonElement b, IReadOnlyList<DescriptionSegmenter.Segment> segments)
    {
        var rulings = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (b.TryGetProperty("rulings", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var id = Str(r, "id");
                // First ruling wins. A run that ruled twice on one line has contradicted itself, and
                // taking the later one would silently prefer whichever it happened to write last.
                if (id.Length > 0) rulings.TryAdd(id, r);
            }

        var mapped = new List<CoverageSegment>(segments.Count);

        foreach (var s in segments)
        {
            CoverageSegment Plain(string verdict) =>
                new(s.Id, s.Kind, s.Text, s.Depth, s.Origin, s.OriginRole, verdict, null, null, null, null, null, null);

            // A heading or a fenced block is the description's furniture. It is carried so the map
            // reads in the ticket's own shape, and never ruled on: a verdict beside the words
            // "Acceptance criteria" is noise on the one rail that has to stay scannable.
            if (DescriptionSegmenter.IsStructural(s.Kind)) { mapped.Add(Plain("context")); continue; }

            if (!rulings.TryGetValue(s.Id, out var ruling)) { mapped.Add(Plain("unaddressed")); continue; }

            var verdict = Str(ruling, "verdict").ToLowerInvariant();
            if (!Verdicts.Contains(verdict)) verdict = "unknown";

            var refs = Strings(ruling, "refs").Take(3).ToList();
            var evidence = NullIfEmpty(Str(ruling, "evidence"));
            var deferredTo = NullIfEmpty(Str(ruling, "deferredTo"));

            // The two honesty gates, and the reason the map can be trusted at a glance. What goes
            // wrong with a coverage report is not a wrong verdict, it is a CONFIDENT one - and both
            // shapes a rubber stamp takes are mechanically detectable. "Met" with nothing to open is
            // a claim the reader cannot check, so it is demoted to the honest "I could not tell".
            // "Deferred" with nowhere to go is an unmet requirement wearing a reason.
            if (verdict == "met" && refs.Count == 0) verdict = "unknown";
            if (verdict == "deferred" && deferredTo == null) verdict = "gap";

            // Which QA screenshot shows this line working. Stored as the shot's own order and
            // resolved against the ticket's shots in the browser, not here: this is a stored
            // document, the shots are live, and a number that no longer names one renders as no
            // picture rather than as a broken image.
            var shot = ruling.TryGetProperty("shot", out var sh) && sh.TryGetInt32(out var n) && n >= 0
                ? n : (int?)null;

            mapped.Add(new CoverageSegment(
                s.Id, s.Kind, s.Text, s.Depth, s.Origin, s.OriginRole, verdict,
                // The evidence survives the demotion. It is still the most useful sentence about the
                // line, and shown under an "unknown" chip it reads as what it is: an unbacked claim.
                evidence, refs.Count > 0 ? refs : null, NullIfEmpty(Str(ruling, "qa")), deferredTo, shot,
                ParseCoverageSnippet(ruling)));
        }

        return mapped;
    }

    /// <summary>
    /// The few lines of code that answer one requirement, where the run quoted some.
    /// <para>
    /// An over-long snippet is DROPPED rather than cut to fit. Everywhere else in this file the rule
    /// is to salvage what can be salvaged, but a snippet is a QUOTE: its whole value is that it
    /// matches what is on disk, and half of one presented as the whole says something the file does
    /// not. Losing it costs the row its picture of the code; keeping a trimmed one costs every other
    /// snippet in the document its credibility.
    /// </para>
    /// </summary>
    private static CoverageSnippet? ParseCoverageSnippet(JsonElement ruling)
    {
        if (!ruling.TryGetProperty("snippet", out var sn) || sn.ValueKind != JsonValueKind.Object)
            return null;

        // Trailing whitespace goes; INDENTATION stays. The leading whitespace is part of what the
        // snippet is showing, and Str() would have eaten it.
        var text = (sn.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()! : "").TrimEnd();

        if (text.Length == 0) return null;
        if (text.Split('\n').Length > MaxCoverageSnippetLines) return null;

        return new CoverageSnippet(
            text,
            NullIfEmpty(Str(sn, "path")),
            // As on a code block: lines that no longer exist in the file arrive with no startLine,
            // and the renderer then draws no numbers rather than counting from one.
            sn.TryGetProperty("startLine", out var sl) && sl.TryGetInt32(out var n) && n > 0 ? n : null,
            NullIfEmpty(Str(sn, "language")));
    }

    /// <summary>
    /// The user-flow graph, cleaned until it can actually be drawn.
    /// <para>
    /// The renderer computes every coordinate from this, so an edge naming a node that does not
    /// exist is not a cosmetic problem — it is a line to nowhere, or a crash in the layout. Rather
    /// than defend against that downstream, everything unplaceable is dropped here: unknown ids,
    /// self-loops, duplicate ids and repeated edges, and nodes that nothing joins. What survives is
    /// a graph where every edge connects two nodes that are on the page.
    /// </para>
    /// </summary>
    private static (List<FlowNode> nodes, List<FlowEdge> edges) ParseFlow(
        JsonElement b, IReadOnlyList<DescriptionSegmenter.Segment>? segments)
    {
        string[] nodeKinds = ["start", "screen", "step", "decision", "system", "end"];
        string[] changes = ["new", "changed", "unchanged", "removed"];

        // The lines a stop can name, by id. Furniture is left out for the reason it is never ruled on:
        // "Acceptance criteria" is not something anybody asked for.
        var askable = (segments ?? [])
            .Where(s => !DescriptionSegmenter.IsStructural(s.Kind))
            .ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        var nodes = new List<FlowNode>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        // One screenshot is a picture of one place. A run that hangs the same shot on two stops has
        // guessed on at least one of them, so the first keeps it and the rest draw no picture.
        var shotsUsed = new HashSet<int>();

        if (b.TryGetProperty("nodes", out var nodesEl) && nodesEl.ValueKind == JsonValueKind.Array)
            foreach (var n in nodesEl.EnumerateArray())
            {
                if (nodes.Count >= MaxFlowNodes) break;
                if (n.ValueKind != JsonValueKind.Object) continue;

                var id = Str(n, "id");
                var label = Str(n, "label");
                if (id.Length == 0 || label.Length == 0 || !ids.Add(id)) continue;

                var nodeKind = Str(n, "kind");
                if (!nodeKinds.Contains(nodeKind)) nodeKind = "step";
                var change = Str(n, "change");
                if (!changes.Contains(change)) change = "unchanged";

                // Three is the point at which a stop is really two stops. Extra citations are
                // dropped rather than stacked into a popover nobody scrolls.
                var refs = Strings(n, "refs").Take(3).ToList();

                // Which QA screenshot is a picture of this stop. Stored as the shot's own order and
                // resolved against the ticket's live shots in the browser, on exactly the reasoning
                // that governs the same field on a coverage row: the document is stored, the shots
                // are not, and a number that no longer names one has to render as no picture rather
                // than as somebody else's.
                var shot = n.TryGetProperty("shot", out var sh) && sh.TryGetInt32(out var order)
                    && order >= 0 && shotsUsed.Add(order)
                    ? order
                    : (int?)null;

                // The requirement lines that called for this stop, joined here exactly as a coverage
                // row is: the run names ids, and the words come from the ticket. An id that names no
                // line is dropped rather than shown as a quote of nothing.
                var asks = Strings(n, "asks")
                    .Select(a => askable.GetValueOrDefault(a))
                    .OfType<DescriptionSegmenter.Segment>()
                    .DistinctBy(s => s.Id)
                    .Take(3)
                    .Select(s => new FlowAsk(s.Text, s.Origin, s.OriginRole))
                    .ToList();

                nodes.Add(new FlowNode(
                    id, nodeKind, label,
                    NullIfEmpty(Str(n, "explain")),
                    NullIfEmpty(Str(n, "actor")),
                    change,
                    refs.Count > 0 ? refs : null,
                    shot) { Asks = asks.Count > 0 ? asks : null });
            }

        var edges = new List<FlowEdge>();
        var drawn = new HashSet<string>(StringComparer.Ordinal);

        if (b.TryGetProperty("edges", out var edgesEl) && edgesEl.ValueKind == JsonValueKind.Array)
            foreach (var e in edgesEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;

                var from = Str(e, "from");
                var to = Str(e, "to");
                if (from.Length == 0 || to.Length == 0 || from == to) continue;
                if (!ids.Contains(from) || !ids.Contains(to)) continue;
                if (!drawn.Add($"{from} {to}")) continue;

                var change = Str(e, "change");
                if (!changes.Contains(change)) change = "unchanged";
                edges.Add(new FlowEdge(from, to, NullIfEmpty(Str(e, "label")), change));
            }

        // A node no edge touches has no place on the route. Drawing it anyway would put a step on
        // screen that nothing reaches, which reads as a gap in the flow rather than as a mistake.
        var joined = edges.SelectMany(e => new[] { e.From, e.To }).ToHashSet(StringComparer.Ordinal);
        return (nodes.Where(n => joined.Contains(n.Id)).ToList(), edges);
    }

    public static string Serialize(Doc doc) => JsonSerializer.Serialize(doc, Wire);

    /// <summary>Read a stored document back. Returns null for anything unparseable rather than throwing.</summary>
    public static Doc? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<Doc>(json, Wire); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Whether a stored document carries a block of one kind.
    /// <para>
    /// Exists for the user-flow diagram. A run with nothing real to draw is supposed to omit the
    /// section — but an omission is invisible, and a run that quietly skips the same section on
    /// every ticket looks identical to a run that correctly skipped it on this one. Saying so on
    /// the analysis is what makes the difference noticeable.
    /// </para>
    /// </summary>
    public static bool HasKind(string blocksJson, string kind) =>
        Deserialize(blocksJson)?.Sections.Any(s => s.Blocks.Any(b => b.Kind == kind)) ?? false;

    // ---- helpers ----

    /// <summary>A header of at least two columns and the rows under it, squared off against it.
    /// No columns means no table, and the rows are then empty.</summary>
    private static (List<string> columns, List<List<string>> rows) Table(JsonElement b)
    {
        var columns = Strings(b, "columns");
        var rows = new List<List<string>>();
        if (columns.Count < 2) return (columns, rows);

        if (b.TryGetProperty("rows", out var rowsEl) && rowsEl.ValueKind == JsonValueKind.Array)
            foreach (var r in rowsEl.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Array) continue;
                var cells = r.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.String)
                    .Select(c => c.GetString()!.Trim())
                    .ToList();
                if (cells.Count == 0) continue;
                // Square it off against the header rather than dropping the row: a table that
                // renders one cell short is still readable, a missing row is not recoverable.
                while (cells.Count < columns.Count) cells.Add("");
                if (cells.Count > columns.Count) cells = cells.Take(columns.Count).ToList();
                rows.Add(cells);
            }

        return (columns, rows);
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static List<string> Strings(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!.Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }

    private static List<(string a, string b)> Pairs(JsonElement el, string name, string first, string second)
    {
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var outp = new List<(string, string)>();
        foreach (var x in arr.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            var a = Str(x, first);
            var b = Str(x, second);
            if (a.Length > 0 && b.Length > 0) outp.Add((a, b));
        }
        return outp;
    }

    // ---- wire shape (also what the browser receives) ----

    public record Doc(string Headline, IReadOnlyList<Section> Sections);
    public record Section(string Title, IReadOnlyList<Block> Blocks);
    public record KeyValueItem(string Key, string Value);
    public record GlossaryTerm(string Term, string Meaning);
    public record ReadingStep(string Location, string What, string? Why);

    /// <summary>
    /// One line of the ticket, and what the change did about it.
    /// <para>
    /// The text is the ticket's OWN words, copied through from
    /// <see cref="DescriptionSegmenter"/> and never written by the run. That is not a detail: a
    /// reader comparing their requirements against a summary of their requirements is doing the
    /// same double-checking this whole section exists to end.
    /// </para>
    /// </summary>
    /// <param name="Verdict">One of <see cref="Verdicts"/>, or <c>unaddressed</c> — the run
    /// returned nothing for this line. <c>context</c> means the run judged it background rather
    /// than an ask; it is dimmed, never hidden, so a misjudgement is visible at a glance.</param>
    /// <param name="Evidence">One sentence: what was done about this, in the run's own voice.</param>
    /// <param name="Refs">Up to three <c>file:line</c> citations. Required for a verdict of
    /// <c>met</c> to survive — see <see cref="MapCoverage"/>.</param>
    /// <param name="Qa">The QA item that exercised this line, where one did. The second axis of the
    /// map: built and verified are different questions, and answering them in one row is what stops
    /// the reader cross-referencing two tables.</param>
    /// <param name="DeferredTo">Where this ask actually went — a sibling's key, or the comment that
    /// descoped it. Required for a verdict of <c>deferred</c> to survive.</param>
    /// <param name="Shot">The order of the QA screenshot that shows this line working. A picture of
    /// the requirement being met is worth more than a sentence claiming it was, and it is the one
    /// piece of evidence on the row that needs no reading at all.
    /// <para>
    /// Deliberately a number the run CHOSE rather than a caption matched after the fact. Pairing
    /// shots to requirements by text similarity would eventually put a screenshot of the wrong
    /// screen beside a requirement, and a coverage map that shows you the wrong proof is worse than
    /// one that shows you none.
    /// </para></param>
    /// <param name="StartLine">The line this sits on in the file as it is now. Absent where the code
    /// no longer exists there, exactly as on a <c>code</c> block — and then it renders without line
    /// numbers, which is the honest presentation of a quote from history.</param>
    public record CoverageSnippet(string Text, string? Path, int? StartLine, string? Language);

    /// <param name="Snippet">The handful of lines that ARE the answer to this requirement, where
    /// showing beats describing. Rationed hard: a citation asks the reader to go and look, a
    /// sentence asks them to take it on trust, and a snippet is the only one of the three that
    /// settles it in place — which is exactly why one on every row would settle nothing.</param>
    public record CoverageSegment(
        string Id, string Kind, string Text, int Depth, string? Origin, string? OriginRole,
        string Verdict, string? Evidence, IReadOnlyList<string>? Refs, string? Qa, string? DeferredTo,
        int? Shot, CoverageSnippet? Snippet);

    /// <summary>
    /// One stop on the user's route.
    /// <para>
    /// No coordinates, and that is the point: the model decides what the steps ARE and the renderer
    /// decides where they sit, exactly as every other block splits the work.
    /// </para>
    /// </summary>
    /// <param name="Label">A HEADER, not a sentence. The box is small on purpose; the detail is one
    /// click away, which is what lets the diagram stay readable at a glance.</param>
    /// <param name="Kind">start, screen, step, decision, system or end — what shape it is drawn as.</param>
    /// <param name="Explain">The substance, shown when the reader opens the stop. This is where a
    /// sentence belongs — written into <see cref="Label"/> it would either wrap the box into a
    /// paragraph or get cut off.</param>
    /// <param name="Actor">The role taking this step, where the flow crosses more than one.</param>
    /// <param name="Change">new, changed, unchanged or removed. What makes this a diagram of a
    /// CHANGE rather than documentation: the unchanged stops are the scaffolding that makes the new
    /// ones legible.</param>
    /// <param name="Refs">Up to three <c>file:line</c> citations for whatever implements this stop.
    /// Each opens in the editor and carries that file's diff into the opened stop.</param>
    /// <param name="Shot">The order of the QA screenshot that is a picture of THIS stop. Drawn on
    /// the stop itself rather than inside the panel it opens: a diagram of screens whose screens
    /// you have to click to see is a diagram that has hidden its own subject, and the reader
    /// walking the route wants to recognise where they are without opening anything.
    /// <para>
    /// Chosen by the run from the captions, never matched by similarity, for the same reason as
    /// <see cref="CoverageSegment.Shot"/> — the wrong screenshot on a stop teaches the reader a
    /// screen that does not exist.
    /// </para></param>
    /// <param name="Ref">Legacy single citation. Read only — analyses written before
    /// <see cref="Refs"/> existed still carry it, and dropping it would silently lose their links.</param>
    /// <param name="Asks">The requirement lines that called for this stop, in the ticket's own words.
    /// What answers "which criterion is this" on the stop itself, rather than sending the reader to
    /// the coverage map to find the line that matches.</param>
    public record FlowNode(string Id, string Kind, string Label, string? Explain, string? Actor,
        string Change, IReadOnlyList<string>? Refs, int? Shot)
    {
        public string? Ref { get; init; }
        public IReadOnlyList<FlowAsk>? Asks { get; init; }
    }

    /// <summary>
    /// One requirement line quoted on a flow stop. Copied from <see cref="DescriptionSegmenter"/>,
    /// never written by the run, for the reason <see cref="CoverageSegment"/> gives: a paraphrase of
    /// a criterion is the thing the reader would then have to go and check against the real one.
    /// </summary>
    /// <param name="Origin">The ancestor's key when the line was inherited, as on a coverage row.</param>
    public record FlowAsk(string Text, string? Origin, string? OriginRole);

    /// <param name="Label">What sends the user down this line — required in practice on the
    /// branches out of a <c>decision</c>, where an unlabelled fork tells the reader nothing.</param>
    public record FlowEdge(string From, string To, string? Label, string Change);

    /// <summary>
    /// One block. Flat, with everything but <see cref="Kind"/> optional, mirroring
    /// <see cref="JsonSchema"/> — the renderer switches on the kind and reads only what it needs.
    /// </summary>
    public record Block(string Kind)
    {
        public string? Text { get; init; }
        public string? Title { get; init; }
        public string? Tone { get; init; }
        public string? Note { get; init; }
        public IReadOnlyList<string>? Columns { get; init; }
        public IReadOnlyList<IReadOnlyList<string>>? Rows { get; init; }
        public IReadOnlyList<KeyValueItem>? Items { get; init; }
        public IReadOnlyList<GlossaryTerm>? Terms { get; init; }
        public IReadOnlyList<ReadingStep>? Steps { get; init; }

        // userFlow only. The graph and nothing but the graph — every coordinate on screen is
        // computed by the renderer from these two lists.
        public IReadOnlyList<FlowNode>? Nodes { get; init; }
        public IReadOnlyList<FlowEdge>? Edges { get; init; }

        // coverage only. Already joined against the requirement text on the way in, so the stored
        // block is self-contained: the renderer never needs the tracker, and a map read back a month
        // later still shows the ticket as it was worded when the run read it.
        public IReadOnlyList<CoverageSegment>? Segments { get; init; }

        // code only. Path and StartLine are what make a snippet openable rather than just readable.
        public string? Path { get; init; }
        public int? StartLine { get; init; }
        public string? Language { get; init; }
    }
}
