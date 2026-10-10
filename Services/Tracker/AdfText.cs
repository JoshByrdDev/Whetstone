using System.Text;
using System.Text.Json;

namespace Whetstone.Services.Tracker;

/// <summary>
/// Flattens Atlassian Document Format (the nested node tree Jira's v3 API returns for descriptions
/// and comments) to markdown for an LLM to read.
/// <para>
/// The rule here is <b>never drop something silently</b>. An earlier version walked only text nodes,
/// so a link, a table, or an embedded screenshot simply vanished - and a phase reading the result
/// could not tell it was working from a partial description. One ticket lost its link to the issue it depended on
/// exactly this way. Anything we cannot render as text now leaves a visible marker instead, so the
/// reader at least knows to go looking.
/// </para>
/// <para>
/// That rule covers <b>formatting</b> too, which is why the output is markdown rather than plain
/// text. Formatting on a ticket is not decoration: one real ticket had half its acceptance criteria
/// struck through, and a version of this that dropped marks handed every reader - the speculation
/// phase included - a description in which those items read as live requirements. Bold, strike,
/// highlight, heading level and list numbering all survive the flattening for the same reason.
/// </para>
/// </summary>
public static class AdfText
{
    /// <summary>
    /// Flatten a description/comment body to markdown. <paramref name="attachmentNames"/> maps Jira
    /// attachment ids to filenames so embedded media can name the file rather than an opaque id;
    /// pass null when the caller has no attachment list.
    /// </summary>
    public static string? ToText(JsonElement node, IReadOnlyDictionary<string, string>? attachmentNames = null)
    {
        if (node.ValueKind == JsonValueKind.String) return NullIfEmpty(node.GetString());
        if (node.ValueKind != JsonValueKind.Object) return null;

        var sb = new StringBuilder();
        Walk(node, sb, attachmentNames, 0);

        // Collapse the runs of blank lines that block-level newlines leave behind.
        var lines = sb.ToString().Replace("\r\n", "\n").Split('\n');
        var outLines = new List<string>(lines.Length);
        var blank = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (blank) continue;
                blank = true;
            }
            else blank = false;
            outLines.Add(line);
        }
        return NullIfEmpty(string.Join("\n", outLines).Trim());
    }

    /// <param name="depth">How many lists deep this node sits, which is what a nested list item is
    /// indented by. Nesting is the difference between "three sub-points of step 1" and "three more
    /// steps", so it has to travel with the text.</param>
    private static void Walk(JsonElement node, StringBuilder sb, IReadOnlyDictionary<string, string>? names, int depth)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        var type = node.TryGetProperty("type", out var ty) ? ty.GetString() : null;
        var attrs = node.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;

        switch (type)
        {
            case "text":
                // A link's text is kept by the generic walk; the destination only survives if we
                // append it, and the destination is usually the point. The destination sits outside
                // the formatting marks - a struck-through link is struck-through words, not a
                // struck-through URL.
                var text = node.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                sb.Append(Marked(text, node));
                var href = LinkHref(node);
                if (href is not null && !string.Equals(href, text, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (").Append(href).Append(')');
                return;

            case "hardBreak":
                sb.Append('\n');
                return;

            case "rule":
                sb.Append("\n---\n");
                return;

            // Cards are a bare URL with no text child - without this the reference disappears.
            case "inlineCard":
            case "blockCard":
            case "embedCard":
                sb.Append(Attr(attrs, "url") ?? "[card]");
                sb.Append('\n');
                return;

            case "mention":
                sb.Append(Attr(attrs, "text") ?? "@unknown");
                return;

            case "emoji":
                sb.Append(Attr(attrs, "text") ?? Attr(attrs, "shortName"));
                return;

            case "date":
                sb.Append("[date:").Append(Attr(attrs, "timestamp")).Append(']');
                return;

            case "status":
                sb.Append('[').Append(Attr(attrs, "text")).Append(']');
                return;

            // Embedded images/files. We cannot show the image here, but saying it exists is the
            // whole point - the caller can then go and fetch it.
            case "media":
            case "mediaInline":
                var id = Attr(attrs, "id");
                var name = id is not null && names is not null && names.TryGetValue(id, out var n) ? n
                         : Attr(attrs, "alt") ?? id;
                sb.Append("[attachment: ").Append(name ?? "unnamed").Append(']');
                return;

            // Lists own their markers: the walk below cannot number an item, because only the list
            // knows the item's position. Flattening everything to "- " turns an ordered sequence
            // into an unordered set and loses which sub-point belonged to which step.
            case "bulletList":
            case "orderedList":
                var ordered = type == "orderedList";
                var number = ordered ? OrderedStart(attrs) : 0;
                if (node.TryGetProperty("content", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var item in items.EnumerateArray())
                    {
                        sb.Append(' ', 4 * depth);
                        if (ordered) sb.Append(number++).Append(". ");
                        else sb.Append("- ");
                        Walk(item, sb, names, depth + 1);
                    }
                return;
        }

        // Container nodes: prefixes, then children, then block separation.
        if (type == "heading") sb.Append('#', HeadingLevel(attrs)).Append(' ');
        if (type == "codeBlock") sb.Append("```").Append(Attr(attrs, "language")).Append('\n');
        if (type == "blockquote") sb.Append("> ");
        // An info panel and a warning panel say different things about the same words.
        if (type == "panel") sb.Append('[').Append(Attr(attrs, "panelType") ?? "panel").Append("] ");

        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var first = true;
            foreach (var child in content.EnumerateArray())
            {
                // Table cells read as a row when separated; without this they run together and the
                // structure that made the table worth writing is lost.
                if (!first && type is "tableRow") sb.Append(" | ");
                Walk(child, sb, names, depth);
                first = false;
            }
        }

        switch (type)
        {
            case "codeBlock": sb.Append("\n```\n"); break;
            case "tableRow": sb.Append('\n'); break;
            case "table": sb.Append('\n'); break;
            case "paragraph":
            case "heading":
            case "listItem":
            case "blockquote":
            case "panel":
            case "mediaSingle":
            case "mediaGroup":
                sb.Append('\n');
                break;
        }
    }

    /// <summary>
    /// Wrap a text run in the markdown for whatever marks Jira put on it.
    /// <para>
    /// Delimiters have to hug the words - <c>~~ foo~~</c> renders as literal tildes everywhere - and
    /// ADF routinely splits a formatted run so the trailing space lands inside the marked node. So
    /// the surrounding whitespace is lifted out and put back around the wrapped core.
    /// </para>
    /// </summary>
    private static string? Marked(string? text, JsonElement node)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!node.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array) return text;

        var wrappers = new List<(string Open, string Close)>();
        foreach (var m in marks.EnumerateArray())
        {
            var (open, close) = Wrapper(m);
            // A run carrying both colour marks would otherwise come out as "====text====".
            if (open is null || wrappers.Any(w => w.Open == open)) continue;
            wrappers.Add((open, close!));
        }
        if (wrappers.Count == 0) return text;

        var lead = text.Length - text.TrimStart().Length;
        var trail = text.Length - text.TrimEnd().Length;
        if (lead + trail >= text.Length) return text; // whitespace only: nothing to emphasise

        var core = text.Substring(lead, text.Length - lead - trail);
        foreach (var (open, close) in wrappers) core = open + core + close;
        return text[..lead] + core + text[(text.Length - trail)..];
    }

    private static (string? Open, string? Close) Wrapper(JsonElement mark) =>
        (mark.TryGetProperty("type", out var t) ? t.GetString() : null) switch
        {
            "strong" => ("**", "**"),
            "em" => ("*", "*"),
            "strike" => ("~~", "~~"),
            "code" => ("`", "`"),
            // Markdown has no underline of its own; the tag survives every renderer we hand this to.
            "underline" => ("<u>", "</u>"),
            // Markdown has no colour either, and the specific colour almost never carries meaning
            // the words do not. What matters is that a human marked these words out, so both the
            // text colour and the highlighter collapse to the one highlight marker.
            "textColor" or "backgroundColor" => ("==", "=="),
            "subsup" => SubSup(mark),
            _ => (null, null),
        };

    private static (string?, string?) SubSup(JsonElement mark)
        => mark.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object
           && a.TryGetProperty("type", out var t) && t.GetString() == "sub"
            ? ("~", "~") : ("^", "^");

    /// <summary>Where an ordered list starts counting - a list can be authored to resume at 4.</summary>
    private static int OrderedStart(JsonElement attrs)
        => attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("order", out var o)
           && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var i) && i > 0 ? i : 1;

    private static int HeadingLevel(JsonElement attrs)
        => attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("level", out var l)
           && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var i)
            ? Math.Clamp(i, 1, 6) : 1;

    private static string? LinkHref(JsonElement textNode)
    {
        if (!textNode.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array) return null;
        foreach (var m in marks.EnumerateArray())
        {
            if (m.TryGetProperty("type", out var mt) && mt.GetString() == "link"
                && m.TryGetProperty("attrs", out var ma) && ma.ValueKind == JsonValueKind.Object
                && ma.TryGetProperty("href", out var h))
                return h.GetString();
        }
        return null;
    }

    private static string? Attr(JsonElement attrs, string name)
        => attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
