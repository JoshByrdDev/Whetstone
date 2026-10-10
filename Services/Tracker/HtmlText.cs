using System.Net;
using System.Text;

namespace Whetstone.Services.Tracker;

/// <summary>
/// Flattens the HTML that Azure DevOps returns for a work item's long text fields
/// (<c>System.Description</c>, <c>Microsoft.VSTS.Common.AcceptanceCriteria</c>,
/// <c>Microsoft.VSTS.TCM.ReproSteps</c>, and comment bodies) to markdown for an LLM to read.
/// <para>
/// This is the Azure DevOps counterpart to <see cref="AdfText"/>, and it is held to the same rule:
/// <b>never drop something silently</b>. A stripped-tags implementation would lose a link, a table
/// and an embedded screenshot without leaving any sign it had, and a phase reading the result could
/// not tell it was working from a partial description. Anything that cannot be rendered as text
/// leaves a visible marker instead, so the reader at least knows to go looking.
/// </para>
/// <para>
/// Formatting survives for the same reason it does in ADF: struck-through acceptance criteria that
/// arrive as live requirements are worse than no criteria at all. Bold, italic, strike, code,
/// heading level and list numbering all make it through.
/// </para>
/// <para>
/// Newer Azure DevOps organisations can store these fields as markdown rather than HTML. That needs
/// no special case - text with no tags in it passes through this untouched apart from entity
/// decoding, which is exactly what markdown wants.
/// </para>
/// </summary>
public static class HtmlText
{
    /// <summary>How deep the renderer will follow nesting before giving up. Far past any real
    /// document; it exists so a pathological or malformed input cannot blow the stack.</summary>
    private const int MaxDepth = 60;

    /// <summary>
    /// Flatten a field's HTML to markdown. <paramref name="attachmentNames"/> maps an attachment's
    /// id (the GUID in its URL) to its filename, so an embedded image names the file rather than an
    /// opaque URL; pass null when the caller has no attachment list.
    /// </summary>
    public static string? ToText(string? html, IReadOnlyDictionary<string, string>? attachmentNames = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var sb = new StringBuilder();
        // Text with no markup is already what we want - it is either plain text or (on a newer
        // organisation) markdown, and re-rendering it would only risk mangling it.
        if (html.IndexOf('<') < 0) sb.Append(Decode(html));
        else foreach (var node in Parse(html)) Render(node, sb, attachmentNames, 0);

        return Tidy(sb.ToString());
    }

    // ---- Rendering ---------------------------------------------------------

    private static void Render(Node node, StringBuilder sb, IReadOnlyDictionary<string, string>? names, int depth)
    {
        if (depth > MaxDepth) return;

        if (node is TextNode text)
        {
            // Whitespace sitting between two block elements is layout in the source markup, not a
            // space in the document. Dropping it at a line start is what keeps a pretty-printed
            // editor payload from arriving indented - and four leading spaces would read as a
            // markdown code block, which is a change of meaning rather than of appearance.
            if (text.Value.Trim().Length == 0 && (sb.Length == 0 || sb[^1] == '\n')) return;
            sb.Append(text.Value);
            return;
        }

        var el = (ElementNode)node;
        switch (el.Tag)
        {
            // Their content is machinery, not prose. This is the one case where dropping something
            // silently is right: a stylesheet has nothing in it for a reader.
            case "script":
            case "style":
                return;

            case "br":
                sb.Append('\n');
                return;

            case "hr":
                sb.Append("\n---\n");
                return;

            // We cannot show the image, but saying it exists is the whole point - the caller can
            // then go and open the file we downloaded for it.
            case "img":
                sb.Append("[attachment: ").Append(ImageName(el, names)).Append(']');
                return;

            // Lists own their markers: a child cannot number itself, because only the list knows the
            // item's position. Flattening everything to "- " turns an ordered sequence into an
            // unordered set and loses which sub-point belonged to which step.
            case "ul":
            case "ol":
            {
                var ordered = el.Tag == "ol";
                var number = ordered ? Start(el) : 0;
                foreach (var item in el.Children.OfType<ElementNode>().Where(c => c.Tag == "li"))
                {
                    sb.Append(' ', 4 * depth);
                    if (ordered) sb.Append(number++).Append(". ");
                    else sb.Append("- ");
                    sb.Append(Contained(item, names, depth + 1)).Append('\n');
                }
                return;
            }

            // A cell's own block structure must not break the row it sits in.
            case "td":
            case "th":
                sb.Append(Contained(el, names, depth));
                return;

            case "tr":
            {
                var first = true;
                foreach (var cell in el.Children)
                {
                    if (!first) sb.Append(" | ");
                    Render(cell, sb, names, depth);
                    first = false;
                }
                sb.Append('\n');
                return;
            }

            // A link's text is kept by the generic walk below; the destination only survives if we
            // append it, and the destination is usually the point.
            case "a":
            {
                var inner = Contained(el, names, depth);
                sb.Append(inner);
                var href = Attr(el, "href");
                if (!string.IsNullOrWhiteSpace(href)
                    && !string.Equals(href, inner, StringComparison.OrdinalIgnoreCase))
                    sb.Append(" (").Append(href).Append(')');
                return;
            }

            case "pre":
                sb.Append("\n```\n").Append(Contained(el, names, depth).Trim('\n')).Append("\n```\n");
                return;
        }

        // Inline formatting: wrap the element's own text, hugging the words.
        if (Wrapper(el.Tag) is var (open, close) && open is not null)
        {
            sb.Append(Marked(Contained(el, names, depth), open, close!));
            return;
        }

        // Block containers: prefix, children, then block separation.
        var heading = HeadingLevel(el.Tag);
        if (heading > 0) sb.Append('\n').Append('#', heading).Append(' ');
        if (el.Tag == "blockquote") sb.Append("> ");

        foreach (var child in el.Children) Render(child, sb, names, depth);

        if (heading > 0 || el.Tag is "p" or "div" or "blockquote" or "table" or "section" or "li")
            sb.Append('\n');
    }

    /// <summary>An element's rendered content with its block newlines trimmed - what a list item or
    /// a table cell needs, so its inner paragraphs cannot break the structure holding it.</summary>
    private static string Contained(ElementNode el, IReadOnlyDictionary<string, string>? names, int depth)
    {
        var sb = new StringBuilder();
        foreach (var child in el.Children) Render(child, sb, names, depth);
        return sb.ToString().Trim('\n');
    }

    /// <summary>
    /// Wrap a run in its markdown delimiters. Delimiters have to hug the words - <c>~~ foo~~</c>
    /// renders as literal tildes everywhere - and a rich-text editor routinely leaves the trailing
    /// space inside the formatted element. So the surrounding whitespace is lifted out and put back
    /// around the wrapped core.
    /// </summary>
    private static string Marked(string text, string open, string close)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lead = text.Length - text.TrimStart().Length;
        var trail = text.Length - text.TrimEnd().Length;
        if (lead + trail >= text.Length) return text; // whitespace only: nothing to emphasise
        var core = text.Substring(lead, text.Length - lead - trail);
        return text[..lead] + open + core + close + text[(text.Length - trail)..];
    }

    private static (string? Open, string? Close) Wrapper(string tag) => tag switch
    {
        "strong" or "b" => ("**", "**"),
        "em" or "i" => ("*", "*"),
        "s" or "strike" or "del" => ("~~", "~~"),
        "code" or "tt" => ("`", "`"),
        // Markdown has no underline of its own; the tag survives every renderer we hand this to.
        "u" or "ins" => ("<u>", "</u>"),
        // Markdown has no colour either, and the specific colour almost never carries meaning the
        // words do not. What matters is that a human marked these words out.
        "mark" => ("==", "=="),
        "sub" => ("~", "~"),
        "sup" => ("^", "^"),
        _ => (null, null),
    };

    private static int HeadingLevel(string tag)
        => tag.Length == 2 && tag[0] == 'h' && tag[1] is >= '1' and <= '6' ? tag[1] - '0' : 0;

    /// <summary>Where an ordered list starts counting - a list can be authored to resume at 4.</summary>
    private static int Start(ElementNode el)
        => int.TryParse(Attr(el, "start"), out var n) && n > 0 ? n : 1;

    /// <summary>
    /// What to call an embedded image. Azure DevOps serves them from
    /// <c>.../_apis/wit/attachments/{guid}?fileName=x.png</c>, so the filename is usually right
    /// there in the URL; the attachment map is consulted first because it holds the name the human
    /// actually uploaded.
    /// </summary>
    private static string ImageName(ElementNode el, IReadOnlyDictionary<string, string>? names)
    {
        var src = Attr(el, "src") ?? "";
        var id = AttachmentIdFromUrl(src);
        if (id is not null && names is not null && names.TryGetValue(id, out var known)) return known;

        var alt = Attr(el, "alt");
        if (!string.IsNullOrWhiteSpace(alt)) return alt!;

        var fromQuery = QueryValue(src, "fileName");
        if (!string.IsNullOrWhiteSpace(fromQuery)) return fromQuery!;

        return string.IsNullOrWhiteSpace(src) ? "unnamed" : src;
    }

    /// <summary>
    /// The attachment GUID out of an Azure DevOps attachment URL, or null when it is some other
    /// link. Shared with the tracker itself, which keys its attachment map the same way.
    /// </summary>
    public static string? AttachmentIdFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var path = url.Split('?')[0].TrimEnd('/');
        var last = path[(path.LastIndexOf('/') + 1)..];
        return Guid.TryParse(last, out var g) ? g.ToString() : null;
    }

    private static string? QueryValue(string url, string name)
    {
        var q = url.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in url[(q + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase))
                return WebUtility.UrlDecode(pair[(eq + 1)..]);
        }
        return null;
    }

    private static string? Attr(ElementNode el, string name)
        => el.Attributes.TryGetValue(name, out var v) ? v : null;

    /// <summary>Collapse the runs of blank lines that block-level newlines leave behind.</summary>
    private static string? Tidy(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var blank = false;
        foreach (var line in lines.Select(l => l.TrimEnd()))
        {
            if (line.Length == 0)
            {
                if (blank) continue;
                blank = true;
            }
            else blank = false;
            kept.Add(line);
        }
        var text = string.Join("\n", kept).Trim();
        return text.Length == 0 ? null : text;
    }

    // ---- Parsing -----------------------------------------------------------

    private abstract class Node;

    private sealed class TextNode(string value) : Node
    {
        public string Value { get; } = value;
    }

    private sealed class ElementNode(string tag) : Node
    {
        public string Tag { get; } = tag;
        public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Node> Children { get; } = new();
    }

    /// <summary>Elements that never have a closing tag, so the stack must not wait for one.</summary>
    private static readonly HashSet<string> Void = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>
    /// A deliberately forgiving parser. This is not a general HTML5 implementation and does not try
    /// to be: the input is one field written by a rich-text editor, and the failure that matters is
    /// losing content, never getting the tree subtly wrong. Unmatched close tags are ignored,
    /// unclosed elements are closed by their parent, and anything unrecognised falls through to its
    /// children - so text always survives even when the structure around it does not.
    /// </summary>
    private static List<Node> Parse(string html)
    {
        var root = new ElementNode("#root");
        var stack = new Stack<ElementNode>();
        stack.Push(root);

        var i = 0;
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length == 0) return;
            var value = Decode(text.ToString());
            text.Clear();
            // HTML collapses runs of whitespace in normal flow; keeping them would turn an editor's
            // pretty-printed markup into a document full of accidental indentation.
            value = Collapse(value);
            if (value.Length > 0) stack.Peek().Children.Add(new TextNode(value));
        }

        while (i < html.Length)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0) { text.Append(html, i, html.Length - i); break; }

            text.Append(html, i, lt - i);

            // Comments, CDATA and doctypes carry nothing a reader wants.
            if (html.AsSpan(lt).StartsWith("<!--"))
            {
                var end = html.IndexOf("-->", lt, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }
            if (lt + 1 < html.Length && html[lt + 1] == '!')
            {
                var end = html.IndexOf('>', lt);
                i = end < 0 ? html.Length : end + 1;
                continue;
            }

            var gt = FindTagEnd(html, lt);
            if (gt < 0) { text.Append(html, lt, html.Length - lt); break; }

            var inner = html[(lt + 1)..gt].Trim();
            i = gt + 1;
            if (inner.Length == 0) continue;

            FlushText();

            if (inner[0] == '/')
            {
                var name = inner[1..].Trim().ToLowerInvariant();
                // Close the named element, and anything left open inside it. An unmatched close tag
                // is dropped rather than allowed to unwind the whole document.
                if (stack.Any(e => e.Tag == name))
                    while (stack.Count > 1 && stack.Pop().Tag != name) { }
                continue;
            }

            var selfClosing = inner[^1] == '/';
            if (selfClosing) inner = inner[..^1].Trim();

            var (tag, attributes) = ReadTag(inner);
            if (tag.Length == 0) continue;

            var element = new ElementNode(tag);
            foreach (var (k, v) in attributes) element.Attributes[k] = v;
            stack.Peek().Children.Add(element);

            if (!selfClosing && !Void.Contains(tag)) stack.Push(element);
        }

        FlushText();
        return root.Children;
    }

    /// <summary>The <c>&gt;</c> that closes this tag, skipping any inside a quoted attribute value -
    /// a title or an alt text containing one would otherwise truncate the tag mid-way.</summary>
    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var i = start + 1; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '>') return i;
        }
        return -1;
    }

    private static (string Tag, List<(string, string)> Attributes) ReadTag(string inner)
    {
        var attributes = new List<(string, string)>();

        var i = 0;
        while (i < inner.Length && !char.IsWhiteSpace(inner[i])) i++;
        var tag = inner[..i].ToLowerInvariant();

        while (i < inner.Length)
        {
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            var nameStart = i;
            while (i < inner.Length && inner[i] != '=' && !char.IsWhiteSpace(inner[i])) i++;
            if (i == nameStart) break;
            var name = inner[nameStart..i].ToLowerInvariant();

            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (i >= inner.Length || inner[i] != '=') { attributes.Add((name, "")); continue; }
            i++;
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (i >= inner.Length) break;

            string value;
            if (inner[i] is '"' or '\'')
            {
                var quote = inner[i++];
                var end = inner.IndexOf(quote, i);
                if (end < 0) end = inner.Length;
                value = inner[i..end];
                i = Math.Min(end + 1, inner.Length);
            }
            else
            {
                var start = i;
                while (i < inner.Length && !char.IsWhiteSpace(inner[i])) i++;
                value = inner[start..i];
            }
            attributes.Add((name, Decode(value)));
        }

        return (tag, attributes);
    }

    /// <summary>Decode entities and fold the non-breaking space an editor sprinkles everywhere into
    /// an ordinary one, so it cannot confuse markdown or a trim.</summary>
    private static string Decode(string s) => WebUtility.HtmlDecode(s).Replace('\u00A0', ' ');

    /// <summary>
    /// Collapse runs of whitespace, as HTML's own normal flow does. Leading and trailing single
    /// spaces are KEPT: the space between two adjacent inline elements is a real word boundary, and
    /// swallowing it would join the two words. The block-boundary case is the one that has to go,
    /// and it is handled where it can be seen - in <see cref="Render"/>.
    /// </summary>
    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else { sb.Append(c); space = false; }
        }
        return sb.ToString();
    }
}
