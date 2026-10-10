using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>
/// Cuts a ticket's requirement text into the numbered pieces a coverage ruling can be attached to.
/// <para>
/// This runs HERE, in C#, rather than being asked of the model, and that is the whole design. The
/// obvious way to build a coverage map is to have the run extract a list of requirements and rule on
/// each — but then a requirement it never extracted is a requirement nobody is told about, which is
/// precisely the failure the map exists to prevent. Splitting deterministically first means the set
/// of things owed a ruling is known before the run starts, so
/// <see cref="AnalysisDocument"/> can compute what was left out instead of trusting a promise that
/// nothing was.
/// </para>
/// <para>
/// The split is by markdown block and nothing cleverer: a paragraph, a list item, a table row. It is
/// not trying to find requirements — deciding what is an ask and what is background is a judgement,
/// and judgements belong to the run, which has read the code. This only decides where the lines are.
/// </para>
/// </summary>
public static class DescriptionSegmenter
{
    /// <summary>
    /// Ceiling on one map. A description past this is not a description, and a rail of two hundred
    /// rows is not scannable — which is the only thing the map is for. The overflow is counted and
    /// said out loud rather than dropped quietly.
    /// </summary>
    private const int MaxSegments = 120;

    /// <summary>Nesting past this reads as the same indent, so it is clamped rather than tracked.</summary>
    private const int MaxDepth = 3;

    /// <summary>Below this a "segment" is a stray bracket or a bullet glyph, not a line of a ticket.</summary>
    private const int MinTextLength = 2;

    /// <param name="Id">
    /// <c>S1</c>, <c>S2</c>… numbered across ALL sources in one sequence, ancestors included. Flat
    /// on purpose: it is the token the run has to reproduce exactly for a ruling to land, and
    /// anything with structure in it (<c>P2.AC3</c>) is a thing to get subtly wrong.
    /// </param>
    /// <param name="Kind">heading, para, item, row or code. <c>heading</c> and <c>code</c> are
    /// scaffolding — see <see cref="IsStructural"/>.</param>
    /// <param name="Depth">List nesting, 0-3. Carried so the map can be drawn in the shape the
    /// ticket was written in; a sub-bullet that renders flush with its parent reads as a peer.</param>
    /// <param name="Origin">The issue key this came from when it was inherited from an ancestor,
    /// null when it is the ticket's own text. What lets the reader see that a requirement they are
    /// being held to was never written on the ticket in front of them.</param>
    /// <param name="OriginRole">How that ancestor relates, in the tracker's own word for it —
    /// "epic", "story", else "parent"/"grandparent".</param>
    public record Segment(string Id, string Kind, string Text, int Depth, string? Origin, string? OriginRole);

    /// <summary>One body of requirement text and where it came from.</summary>
    public record Source(string? Key, string? Role, string? Text);

    /// <param name="Dropped">Segments past <see cref="MaxSegments"/>. Non-zero means the map is
    /// knowingly incomplete, which the reader must be told rather than left to infer.</param>
    public record Result(IReadOnlyList<Segment> Segments, int Dropped);

    /// <summary>
    /// Whether this piece is part of the document's furniture rather than something someone asked
    /// for. Headings and code blocks are carried into the map — the map is meant to read in the
    /// ticket's own shape — but are never owed a verdict, and ruling on one would put a green tick
    /// beside the words "Acceptance criteria".
    /// </summary>
    public static bool IsStructural(string kind) => kind is "heading" or "code";

    /// <summary>
    /// Number every source into one sequence, nearest first. Empty sources are skipped rather than
    /// contributing a gap in the numbering.
    /// </summary>
    public static Result Compose(IEnumerable<Source> sources)
    {
        var all = new List<Segment>();
        var dropped = 0;

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Text)) continue;

            foreach (var piece in Pieces(source.Text!))
            {
                if (all.Count >= MaxSegments) { dropped++; continue; }
                all.Add(new Segment($"S{all.Count + 1}", piece.Kind, piece.Text, piece.Depth,
                    source.Key, source.Role));
            }
        }

        return new Result(all, dropped);
    }

    /// <summary>
    /// The map as the run is shown it: one line per segment, tagged with the id a ruling must name.
    /// <para>
    /// Deliberately not indented to show nesting. The run reads words, not layout, and leading
    /// whitespace on a line whose id has to be copied exactly is a way to lose the id.
    /// </para>
    /// </summary>
    public static string ForPrompt(IReadOnlyList<Segment> segments)
    {
        var sb = new StringBuilder();

        foreach (var s in segments)
        {
            var tags = new List<string> { s.Kind };
            if (s.Depth > 0) tags.Add($"depth {s.Depth}");
            if (s.Origin is { Length: > 0 })
                tags.Add($"inherited from {s.Origin}{(s.OriginRole is { Length: > 0 } r ? $", the {r}" : "")}");

            sb.Append('[').Append(s.Id).Append("] (").Append(string.Join(", ", tags)).Append(") ")
              .AppendLine(s.Text);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A short, stable hash of requirement text, for answering "has the ticket been rewritten since
    /// this analysis read it".
    /// <para>
    /// Over the RAW text rather than over the segments, so that changing how this file splits does
    /// not retroactively report every stored analysis as stale. Line endings and surrounding
    /// whitespace are normalised out; a tracker that re-serialises its own description on save
    /// should not read as an edit.
    /// </para>
    /// </summary>
    public static string Fingerprint(string? text)
    {
        var norm = Normalize(text ?? "").Trim();
        if (norm.Length == 0) return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)), 0, 8).ToLowerInvariant();
    }

    // ---- the split ----------------------------------------------------------

    private record Piece(string Kind, string Text, int Depth);

    private static readonly Regex FenceRx = new(@"^\s*(```|~~~)", RegexOptions.Compiled);
    private static readonly Regex HeadingRx = new(@"^\s{0,3}#{1,6}\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RuleRx = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^([ \t]*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableRx = new(@"^\s*\|.*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex TableRuleRx = new(@"^\s*\|[\s:|-]*\|\s*$", RegexOptions.Compiled);
    private static readonly Regex QuoteRx = new(@"^\s*>\s?", RegexOptions.Compiled);
    private static readonly Regex SpaceRx = new(@"\s+", RegexOptions.Compiled);

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string Clean(string s) => SpaceRx.Replace(s, " ").Trim();

    private static List<Piece> Pieces(string markdown)
    {
        var pieces = new List<Piece>();
        var lines = Normalize(markdown).Split('\n');

        var para = new StringBuilder();
        var fence = new StringBuilder();
        string? item = null;
        var itemDepth = 0;
        var inFence = false;

        void FlushItem()
        {
            if (item is null) return;
            var text = Clean(item);
            var depth = itemDepth;
            item = null;
            itemDepth = 0;
            if (text.Length >= MinTextLength) pieces.Add(new Piece("item", text, depth));
        }

        void FlushPara()
        {
            var text = Clean(para.ToString());
            para.Clear();
            if (text.Length >= MinTextLength) pieces.Add(new Piece("para", text, 0));
        }

        // Only ever one of the two is open, but flushing both is what keeps that an invariant of the
        // machine rather than something every branch below has to remember.
        void Flush() { FlushItem(); FlushPara(); }

        void FlushFence()
        {
            var code = fence.ToString().TrimEnd();
            fence.Clear();
            inFence = false;
            if (code.Trim().Length > 0) pieces.Add(new Piece("code", code, 0));
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Inside a fence nothing is markdown. Indentation is the content, so the raw line goes
            // in untouched — this is the one piece Clean() must never see.
            if (inFence)
            {
                if (FenceRx.IsMatch(line)) FlushFence();
                else fence.AppendLine(raw);
                continue;
            }

            if (FenceRx.IsMatch(line)) { Flush(); inFence = true; continue; }

            if (line.Trim().Length == 0) { Flush(); continue; }

            if (RuleRx.IsMatch(line)) { Flush(); continue; }

            if (HeadingRx.Match(line) is { Success: true } heading)
            {
                Flush();
                var text = Clean(heading.Groups[1].Value);
                if (text.Length > 0) pieces.Add(new Piece("heading", text, 0));
                continue;
            }

            if (TableRx.IsMatch(line))
            {
                Flush();

                // The `|---|---|` rule is not a row; what it does say is that the row above it was
                // the header. Retitling that one is the difference between a coverage rail with a
                // verdict beside the word "Requirement" and one without.
                if (TableRuleRx.IsMatch(line))
                {
                    if (pieces.Count > 0 && pieces[^1].Kind == "row")
                        pieces[^1] = pieces[^1] with { Kind = "heading" };
                    continue;
                }

                // Each cell is cleaned BEFORE the join, never after: the separator is wider than a
                // space so the columns stay tellable apart on one line of the rail, and cleaning the
                // joined string would collapse it straight back to one.
                var cells = string.Join("  |  ",
                    line.Trim().Trim('|').Split('|').Select(c => Clean(c)));
                if (cells.Trim(' ', '|').Length >= MinTextLength) pieces.Add(new Piece("row", cells, 0));
                continue;
            }

            if (BulletRx.Match(line) is { Success: true } bullet)
            {
                Flush();
                itemDepth = Math.Min(bullet.Groups[1].Value.Replace("\t", "  ").Length / 2, MaxDepth);
                item = bullet.Groups[3].Value;
                continue;
            }

            var content = QuoteRx.Replace(line, "").Trim();
            if (content.Length == 0) continue;

            // While a bullet is open, every following line belongs to it. A wrapped bullet is ONE
            // requirement however many lines the tracker broke it over, and splitting it would put
            // half an ask on its own row of the rail, owed its own verdict.
            if (item is not null) item += " " + content;
            else
            {
                if (para.Length > 0) para.Append(' ');
                para.Append(content);
            }
        }

        Flush();
        // An unterminated fence is a malformed description, not a reason to lose its last block.
        if (inFence) FlushFence();

        return pieces;
    }
}
