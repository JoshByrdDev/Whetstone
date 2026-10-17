using System.Text;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>One lettered option of a decision, as its spec words it.</summary>
public record UnitDecisionOption(string Letter, string Text);

/// <summary>
/// One numbered choice a unit's spec leaves open: its number and title, the whole of what the spec
/// says about it, its lettered options when it letters them, and the pick it recommends with the
/// reason given for it. <see cref="PickLetters"/> is empty when the pick is not one of the letters -
/// "30 days, one reminder" is a pick as much as "(a)" is - and has two when a decision asks two
/// things at once and picks "(a) and (c)".
/// </summary>
public record UnitDecision(int Number, string Title, string Text, IReadOnlyList<UnitDecisionOption> Options,
    string? Pick, string? PickWhy, IReadOnlyList<string> PickLetters);

/// <summary>
/// The owner's answer to one decision. <see cref="Option"/> is a letter, or several with commas
/// between ("a,c"), or null for the pick as the spec words it; with neither a letter nor a pick to
/// take, the note is the whole answer.
/// </summary>
public record UnitDecisionChoice(int Number, string? Option = null, string? Note = null);

/// <summary>
/// Reading a unit's open decisions out of its spec, and writing the owner's answers back in.
/// <para>
/// A spec lists what it leaves open under one heading - "To decide" in the template, "Decide" in
/// specs written before the template named it - one bullet per decision, numbered in its bold
/// lead: <c>- **Decide (#24) Title.** Options: (a) ...; (b) .... **Pick: (a)**, why.</c> Settling one
/// takes it out of that section and writes it, as answered, under "Decided": the spec is the
/// document a steward files from, so a decision made anywhere else is one it can miss.
/// </para>
/// </summary>
public static class UnitDecisions
{
    private static readonly Regex OpenHeading = new(@"^##\s+(?:To\s+decide|Decide|Open\s+decisions?)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex DecidedHeading = new(@"^##\s+Decided\b", RegexOptions.IgnoreCase);
    private static readonly Regex SectionEnd = new(@"^#{1,2}\s");
    private static readonly Regex Item = new(@"^(?:[-*]|(\d+)\.)\s+(.*)$");
    private static readonly Regex Lead = new(@"^\*\*(.+?)\*\*\s*");
    private static readonly Regex PickRun = new(@"\*\*Pick:\s*(.+?)\*\*", RegexOptions.IgnoreCase);
    private static readonly Regex Marker = new(@"(?<![\w(])\(([a-z])\)\s*");

    /// <summary>The decisions a spec still leaves open, in the order it lists them.</summary>
    public static IReadOnlyList<UnitDecision> Open(string text) =>
        Items(Lines(text).Lines).Select(i => i.Decision).ToList();

    /// <summary>
    /// The spec with these decisions answered, and the one line each answer is said in. Null
    /// text and a reason when any choice names something the spec does not leave open, or picks
    /// what the decision does not offer: nothing is written for a sheet that is half wrong.
    /// </summary>
    public static (string? Text, IReadOnlyList<string> Said, string? Refusal) Settle(string text,
        IReadOnlyList<UnitDecisionChoice> choices, DateTime day)
    {
        var (lines, nl) = Lines(text);
        var items = Items(lines);
        if (choices.Count == 0) return (null, Array.Empty<string>(), "There is nothing to settle.");
        if (choices.GroupBy(c => c.Number).FirstOrDefault(g => g.Count() > 1) is { } twice)
            return (null, Array.Empty<string>(), $"#{twice.Key} is answered twice.");

        var written = new List<string>();
        var said = new List<string>();
        var drop = new List<(int From, int To)>();
        foreach (var c in choices)
        {
            var it = items.FirstOrDefault(i => i.Decision.Number == c.Number);
            if (it.Decision is null) return (null, Array.Empty<string>(), $"#{c.Number} is not an open decision in this spec.");
            var d = it.Decision;
            var note = string.IsNullOrWhiteSpace(c.Note) ? null : Regex.Replace(c.Note.Trim(), @"\s+", " ");
            string answer, brief;
            if (c.Option is { Length: > 0 } letters)
            {
                var picked = new List<UnitDecisionOption>();
                foreach (var l in letters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
                {
                    var o = d.Options.FirstOrDefault(x => string.Equals(x.Letter, l, StringComparison.OrdinalIgnoreCase));
                    if (o is null) return (null, Array.Empty<string>(), $"#{d.Number} has no option ({l}).");
                    picked.Add(o);
                }
                answer = Said(picked);
                brief = $"#{d.Number} {string.Join(" and ", picked.Select(o => $"({o.Letter})"))}";
            }
            else if (d.PickLetters.Count > 0)
            {
                answer = Said(d.Options.Where(o => d.PickLetters.Contains(o.Letter)));
                brief = $"#{d.Number} {string.Join(" and ", d.PickLetters.Select(l => $"({l})"))}, as picked";
            }
            else if (d.Pick is not null) { answer = d.Pick; brief = $"#{d.Number} as picked"; }
            else if (note is not null) { answer = ""; brief = $"#{d.Number}"; }
            else return (null, Array.Empty<string>(), $"#{d.Number} has no pick to take: choose an option or say what you decide.");

            var full = string.Join(". ", new[] { answer.TrimEnd('.', ' '), note }.Where(s => !string.IsNullOrEmpty(s)));
            written.Add($"- **(#{d.Number}) {d.Title}.** Decided by the owner {day:yyyy-MM-dd}: {full.TrimEnd('.')}.");
            said.Add(note is null ? brief : $"{brief}: {note}");
            drop.Add((it.From, it.To));
        }

        // Out of the open section, bottom up so the earlier ranges keep their places.
        var open = OpenSection(lines)!.Value;
        foreach (var (from, to) in drop.OrderByDescending(r => r.From)) lines.RemoveRange(from, to - from);
        var openEnd = open.End - drop.Sum(r => r.To - r.From);
        var insertAt = -1;
        if (Items(lines).Count == 0)
        {
            // Nothing left open: the section goes, and so does what introduced it.
            lines.RemoveRange(open.Heading, openEnd - open.Heading);
            insertAt = open.Heading;
        }

        var decided = lines.FindIndex(l => DecidedHeading.IsMatch(l));
        if (decided >= 0)
        {
            var end = decided + 1;
            while (end < lines.Count && !SectionEnd.IsMatch(lines[end])) end++;
            var last = end;
            while (last > decided + 1 && lines[last - 1].Trim().Length == 0) last--;
            var add = new List<string>();
            if (last > decided + 1) add.Add("");
            add.AddRange(written.SelectMany((w, i) => i == 0 ? new[] { w } : new[] { "", w }));
            if (end < lines.Count) add.Add("");
            lines.RemoveRange(last, end - last);
            lines.InsertRange(last, add);
        }
        else
        {
            var at = insertAt >= 0 ? insertAt : OpenSection(lines)!.Value.End;
            var add = new List<string> { "## Decided", "" };
            add.AddRange(written.SelectMany((w, i) => i == 0 ? new[] { w } : new[] { "", w }));
            add.Add("");
            lines.InsertRange(at, add);
        }
        return (string.Join(nl, lines), said, null);
    }

    private static string Said(IEnumerable<UnitDecisionOption> options) =>
        string.Join("; and ", options.Select(o => $"({o.Letter}) {o.Text}"));

    private static (List<string> Lines, string Nl) Lines(string text)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        return (text.Replace("\r\n", "\n").Split('\n').ToList(), nl);
    }

    /// <summary>The open section: its heading's line, and the line after its last.</summary>
    private static (int Heading, int End)? OpenSection(List<string> lines)
    {
        var h = lines.FindIndex(l => OpenHeading.IsMatch(l));
        if (h < 0) return null;
        var end = h + 1;
        while (end < lines.Count && !SectionEnd.IsMatch(lines[end])) end++;
        return (h, end);
    }

    /// <summary>
    /// Each numbered decision in the open section, and the lines it spans. A bullet runs on over
    /// the lines after it until the next bullet, a paragraph that is not indented, or the end of
    /// the section; a trailing blank line goes with it, so taking one out leaves no gap.
    /// </summary>
    private static List<(UnitDecision Decision, int From, int To)> Items(List<string> lines)
    {
        var found = new List<(UnitDecision, int, int)>();
        if (OpenSection(lines) is not { } s) return found;
        for (var i = s.Heading + 1; i < s.End; i++)
        {
            var m = Item.Match(lines[i]);
            if (!m.Success) continue;
            var body = new StringBuilder(m.Groups[2].Value);
            var j = i + 1;
            for (; j < s.End; j++)
            {
                var l = lines[j];
                if (Item.IsMatch(l) || l.Trim().Length == 0) break;
                if (!char.IsWhiteSpace(l[0])) break;
                body.Append(' ').Append(l.Trim());
            }
            while (j < s.End && lines[j].Trim().Length == 0) j++;
            if (Parse(body.ToString(), m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : null) is { } d)
                found.Add((d, i, j));
            i = j - 1;
        }
        return found;
    }

    private static UnitDecision? Parse(string body, int? listNumber)
    {
        var lead = Lead.Match(body);
        if (!lead.Success) return null;
        var head = lead.Groups[1].Value;
        var num = Regex.Match(head, @"#(\d+)");
        var number = num.Success ? int.Parse(num.Groups[1].Value) : listNumber;
        if (number is null) return null;
        var title = Regex.Replace(head, @"^\s*Decide\b\s*", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\(?#\d+\)?\s*", "").Trim().TrimEnd('.', ':').Trim();
        if (title.Length == 0) title = $"Decision {number}";
        var rest = body[lead.Length..].Trim();

        var pick = PickRun.Match(rest);
        string? pickText = null, pickWhy = null;
        var pickLetters = new List<string>();
        if (pick.Success)
        {
            pickText = pick.Groups[1].Value.Trim().TrimEnd(':', ',', '.', ';').Trim();
            // "(a)", or "(a) and (c)" for a decision that asks two things: letters and nothing but
            // the words between them. Anything else names its first letter, or none.
            if (Regex.IsMatch(pickText, @"^\([a-z]\)(?:\s*(?:,|and|&|\+)\s*\([a-z]\))*$"))
                pickLetters.AddRange(Regex.Matches(pickText, @"\(([a-z])\)").Select(m => m.Groups[1].Value));
            else if (Regex.Match(pickText, @"^\(([a-z])\)") is { Success: true } first) pickLetters.Add(first.Groups[1].Value);
            var why = rest[(pick.Index + pick.Length)..].Trim().TrimStart(':', ',', '.', ';').Trim();
            pickWhy = why.Length > 0 ? why : null;
        }

        // The options are the lettered run after "Options:", up to the pick. Letters are taken
        // only in order - (a), then (b) - so "(the business)" or a stray "(c)" in a sentence is
        // not mistaken for one.
        var options = new List<UnitDecisionOption>();
        var o = Regex.Match(rest, @"\bOptions?:\s*", RegexOptions.IgnoreCase);
        if (o.Success)
        {
            var seg = rest[(o.Index + o.Length)..(pick.Success && pick.Index > o.Index ? pick.Index : rest.Length)];
            var marks = new List<Match>();
            foreach (Match mk in Marker.Matches(seg))
                if (mk.Groups[1].Value[0] == (char)('a' + marks.Count)) marks.Add(mk);
            for (var k = 0; k < marks.Count; k++)
            {
                var from = marks[k].Index + marks[k].Length;
                var to = k + 1 < marks.Count ? marks[k + 1].Index : seg.Length;
                // An option is one sentence: what follows its full stop is the spec talking about it.
                var t = seg[from..to].Trim();
                var stop = Regex.Match(t, @"\.\s+(?=[A-Z(])");
                if (stop.Success) t = t[..stop.Index];
                t = Regex.Replace(t, @"(?:[;,]\s*(?:or)?|\s+or)$", "").Trim().TrimEnd('.', ';', ',').Trim();
                options.Add(new UnitDecisionOption(marks[k].Groups[1].Value, t));
            }
            if (options.Count < 2) options.Clear();
        }
        // A letter the options do not have is no pick of one: the pick stands as words.
        if (pickLetters.Any(l => options.All(x => x.Letter != l))) pickLetters.Clear();
        return new UnitDecision(number.Value, title, Regex.Replace(rest, @"\s+", " "), options, pickText, pickWhy, pickLetters);
    }
}
