using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>What the story check found: refusals (<see cref="Errors"/>) and what the steward is only told (<see cref="Notes"/>).</summary>
public record StoryCheck(int Words, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes, IReadOnlyList<string> Uncited);

/// <summary>
/// A slice's story, read out of its spec: the walk-through a person makes with what the slice
/// builds (docs/plans-as-slices.md, section 4). Each step cites the outcomes that make it true,
/// inline, so it still reads as a story - "...adds their two dogs (B1, B2)...". This reads the
/// story, checks a draft's against its outcomes, and finds the paragraph an idea is.
/// </summary>
public static class SliceStory
{
    /// <summary>At most this many words, refused past it (the owner's choice 10).</summary>
    public const int MaxWords = 250;
    /// <summary>Past this, the steward is told the slice may be two.</summary>
    public const int WarnWords = 180;

    private static readonly Regex Heading = new(@"^##\s+(?:the\s+walk-?through\b.*|(?:the\s+)?story\b.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex SectionEnd = new(@"^#{1,2}\s");
    private static readonly Regex Paren = new(@"\(([^()]*)\)");
    private static readonly Regex Id = new(@"\b([A-Z]{1,2}\d+)\b");

    /// <summary>The story's text and the lines it spans (from, to exclusive), or null when the spec has no story section.</summary>
    public static (string Text, int From, int To)? Find(string spec)
    {
        var lines = spec.Replace("\r\n", "\n").Split('\n');
        var h = Array.FindIndex(lines, l => Heading.IsMatch(l.Trim()));
        if (h < 0) return null;
        var end = h + 1;
        while (end < lines.Length && !SectionEnd.IsMatch(lines[end])) end++;
        var body = string.Join(" ", lines[(h + 1)..end].Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('>')));
        return (Regex.Replace(body, @"\s+", " ").Trim(), h + 1, end);
    }

    /// <summary>The ids a piece of text cites: "(A1)", "(B1, B2)", "(C1 and C2)". A parenthesis with anything else in it cites nothing.</summary>
    public static IReadOnlyList<string> Cited(string text) =>
        Paren.Matches(text).Select(m => m.Groups[1].Value)
            .Where(inside => Regex.IsMatch(inside, @"^\s*[A-Z]{1,2}\d+(?:\s*(?:,|and|&|;)\s*[A-Z]{1,2}\d+)*\s*$"))
            .SelectMany(inside => Id.Matches(inside).Select(x => x.Groups[1].Value)).Distinct().ToList();

    /// <summary>The story's sentences: a split at a full stop, question or exclamation mark followed by a capital.</summary>
    public static IReadOnlyList<string> Sentences(string story) =>
        Regex.Split(story, @"(?<=[.!?])\s+(?=[A-Z""(])").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>Its words, not counting the citations.</summary>
    public static int Words(string story) =>
        Regex.Matches(Paren.Replace(story, m => Cited(m.Value).Count > 0 ? "" : m.Value), @"\S+").Count;

    /// <summary>
    /// A draft's story against its outcomes: every cited id is an outcome of the draft, every
    /// sentence cites at least one, and it is at most <see cref="MaxWords"/> words. An outcome no
    /// step cites is allowed (an edge, a closing check) and is listed, so the steward can say why.
    /// </summary>
    public static StoryCheck Check(string spec, IReadOnlyCollection<string> outcomes)
    {
        var found = Find(spec);
        if (found is not { } f || f.Text.Length == 0)
            return new StoryCheck(0, new[] { "The draft has no story: a section headed \"The walk-through this unit makes possible\", one paragraph of a person using what it builds, each step citing its outcomes, like (A1)." },
                Array.Empty<string>(), outcomes.ToList());
        var errors = new List<string>();
        var notes = new List<string>();
        var cited = Cited(f.Text);
        var unknown = cited.Where(c => !outcomes.Contains(c)).ToList();
        if (unknown.Count > 0) errors.Add($"The story cites {string.Join(", ", unknown)}, which {(unknown.Count == 1 ? "is not an outcome" : "are not outcomes")} of this draft.");
        var bare = Sentences(f.Text).Where(s => Cited(s).Count == 0).ToList();
        foreach (var s in bare) errors.Add($"This sentence cites no outcome: \"{(s.Length > 120 ? s[..117] + "..." : s)}\"");
        var words = Words(f.Text);
        if (words > MaxWords) errors.Add($"The story is {words} words; the most is {MaxWords}. A story this long is a slice that is too big: split it, and propose the second half as an idea.");
        else if (words > WarnWords) notes.Add($"The story is {words} words. Past {WarnWords}, the slice may be two: say why it is one, or propose the rest as an idea.");
        var uncited = outcomes.Where(o => !cited.Contains(o) && !o.StartsWith('Z')).ToList();
        if (uncited.Count > 0) notes.Add($"No step cites {string.Join(", ", uncited)}: say in the outcome why it is not part of the walk-through (an edge, a check).");
        return new StoryCheck(words, errors, notes, uncited);
    }

    /// <summary>
    /// An idea's paragraph: the first paragraph after its title and status, which is who, what they
    /// can do at the end, what it builds on, and what it is not.
    /// </summary>
    public static string IdeaParagraph(string spec)
    {
        var paras = Regex.Split(spec.Replace("\r\n", "\n"), @"\n\s*\n").Select(p => p.Trim()).Where(p => p.Length > 0);
        return paras.FirstOrDefault(p => !p.StartsWith('#') && !p.StartsWith('>')) is { } first
            ? Regex.Replace(first, @"\s+", " ") : "";
    }

    /// <summary>
    /// The spec with one sentence of its story reworded, or null when the sentence is not in the
    /// story exactly once. Only the story section is touched.
    /// </summary>
    public static string? Reword(string spec, string sentence, string rewording)
    {
        var nl = spec.Contains("\r\n") ? "\r\n" : "\n";
        var lines = spec.Replace("\r\n", "\n").Split('\n').ToList();
        if (Find(spec) is not { } f) return null;
        // The story may wrap over lines; match it with any whitespace between words.
        var block = string.Join("\n", lines.GetRange(f.From, f.To - f.From));
        var pattern = string.Join(@"\s+", sentence.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        var hits = Regex.Matches(block, pattern);
        if (hits.Count != 1) return null;
        var changed = block[..hits[0].Index] + rewording.Trim() + block[(hits[0].Index + hits[0].Length)..];
        lines.RemoveRange(f.From, f.To - f.From);
        lines.InsertRange(f.From, changed.Split('\n'));
        return string.Join(nl, lines);
    }

    /// <summary>Whether a sentence is in a spec's story, with any whitespace between its words.</summary>
    public static bool Has(string spec, string sentence) =>
        Find(spec) is { } f && Regex.Matches(f.Text, string.Join(@"\s+", sentence.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape))).Count == 1;
}
