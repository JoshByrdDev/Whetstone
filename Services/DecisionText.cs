using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>One decision as a spec's "Decided" section writes it. <see cref="Number"/> is null for a paragraph that numbers nothing.</summary>
public record DecidedItem(int? Number, string? Title, string Rule, DateTime? On, IReadOnlyList<string> CitedKeys);

/// <summary>One row of a plan document's decisions section.</summary>
public record PlanDecisionRow(DateTime? On, string Text, IReadOnlyList<int> Cites);

/// <summary>A decision as the conflict check reads it: its number, its words, when it was made, and what it applies to.</summary>
public record DecisionWords(int Number, string Text, DateTime MadeAt, string Scope);

/// <summary>Two decisions that may disagree, the older first, with the words they share.</summary>
public record DecisionPair(int Older, int Newer, string Why, double Score);

/// <summary>
/// Reading decisions out of the documents that kept them before the register did, and the check
/// the register's import runs for decisions that may disagree.
/// <para>
/// The check only proposes. It is word overlap, and two decisions about the same thing can agree as
/// easily as disagree - so a pair it finds is put to the owner, and an override is never assumed
/// (docs/plans-as-slices.md, "Moving a plan that already exists").
/// </para>
/// </summary>
public static class DecisionText
{
    private static readonly Regex DecidedHeading = new(@"^##\s+Decided\b", RegexOptions.IgnoreCase);
    private static readonly Regex SectionEnd = new(@"^#{1,2}\s");
    private static readonly Regex Bullet = new(@"^(?:[-*]|\d+\.)\s+(.*)$");
    private static readonly Regex Lead = new(@"^\*\*(.+?)\*\*\s*");
    private static readonly Regex Date = new(@"\b(\d{4}-\d{2}-\d{2})\b");
    private static readonly Regex Key = new(@"`([a-z0-9]+(?:-[a-z0-9]+)+)`");
    private static readonly Regex Cite = new(@"#(\d+)\b");

    /// <summary>Every numbered decision a spec's "Decided" section holds, and every paragraph there that cites a question by its key.</summary>
    public static IReadOnlyList<DecidedItem> Decided(string spec)
    {
        var lines = spec.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => DecidedHeading.IsMatch(l));
        if (start < 0) return Array.Empty<DecidedItem>();
        var end = start + 1;
        while (end < lines.Length && !SectionEnd.IsMatch(lines[end])) end++;

        // Blocks: a bullet with the indented lines under it, or a paragraph up to a blank line.
        var blocks = new List<(bool Bullet, string Text)>();
        for (var i = start + 1; i < end; i++)
        {
            var l = lines[i];
            if (l.Trim().Length == 0) continue;
            var m = Bullet.Match(l);
            var body = new StringBuilder(m.Success ? m.Groups[1].Value : l.Trim());
            var j = i + 1;
            for (; j < end; j++)
            {
                var n = lines[j];
                if (n.Trim().Length == 0 || Bullet.IsMatch(n)) break;
                if (m.Success && !char.IsWhiteSpace(n[0])) break;
                body.Append(' ').Append(n.Trim());
            }
            blocks.Add((m.Success, body.ToString()));
            i = j - 1;
        }

        var found = new List<DecidedItem>();
        foreach (var (_, text) in blocks)
        {
            var keys = Key.Matches(text).Select(x => x.Groups[1].Value).Distinct().ToList();
            var lead = Lead.Match(text);
            var num = lead.Success ? Regex.Match(lead.Groups[1].Value, @"#(\d+)") : Match.Empty;
            if (!num.Success)
            {
                if (keys.Count > 0) found.Add(new DecidedItem(null, null, Squash(text), DayIn(text), keys));
                continue;
            }
            var title = Regex.Replace(lead.Groups[1].Value, @"\(?#\d+\)?\s*", "").Trim().TrimEnd('.', ':').Trim();
            var rest = text[lead.Length..].Trim();
            DateTime? on = null;
            // "Decided by the owner 2026-10-18: ..." as the board writes it, or "(2026-10-09, asked by ...). ..." by hand.
            var by = Regex.Match(rest, @"^Decided by the owner\s+(\d{4}-\d{2}-\d{2})\s*:\s*", RegexOptions.IgnoreCase);
            var paren = Regex.Match(rest, @"^\(([^)]*)\)\.?\s*");
            if (by.Success) { on = Day(by.Groups[1].Value); rest = rest[by.Length..]; }
            else if (paren.Success && Date.Match(paren.Groups[1].Value) is { Success: true } d) { on = Day(d.Groups[1].Value); rest = rest[paren.Length..]; }
            var rule = Squash(rest);
            found.Add(new DecidedItem(int.Parse(num.Groups[1].Value), title.Length > 0 ? title : null,
                rule.Length > 0 ? rule : title, on, keys));
        }
        return found;
    }

    /// <summary>
    /// The rows of a plan document's decisions section: a table (a date column and the decision in
    /// its last), or a list. Null when the document has no such section.
    /// </summary>
    public static IReadOnlyList<PlanDecisionRow>? PlanRows(string plan)
    {
        var lines = plan.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^#{1,3}\s+(?:[\d.]+\s*)?(?:The\s+)?Decisions?\b", RegexOptions.IgnoreCase));
        if (start < 0) return null;
        var level = lines[start].TakeWhile(c => c == '#').Count();
        var end = start + 1;
        while (end < lines.Length && !(Regex.Match(lines[end], @"^(#+)\s") is { Success: true } h && h.Groups[1].Length <= level)) end++;

        var rows = new List<PlanDecisionRow>();
        for (var i = start + 1; i < end; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith('|'))
            {
                if (Regex.IsMatch(l, @"^\|[\s:|-]+\|?$")) continue;   // the separator
                if (i + 1 < end && Regex.IsMatch(lines[i + 1].Trim(), @"^\|[\s:|-]+\|?$")) continue;   // the header above it
                var cells = l.Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                if (cells.Count == 0) continue;
                var dateCell = cells.FirstOrDefault(c => Regex.IsMatch(c, @"^\d{4}-\d{2}-\d{2}$"));
                var text = cells.Last(c => c != dateCell);
                rows.Add(new PlanDecisionRow(dateCell is null ? null : Day(dateCell), Squash(text), Cites(text)));
            }
            else if (Bullet.Match(lines[i]) is { Success: true } b)
            {
                var text = new StringBuilder(b.Groups[1].Value);
                var j = i + 1;
                for (; j < end && lines[j].Trim().Length > 0 && !Bullet.IsMatch(lines[j]) && char.IsWhiteSpace(lines[j][0]); j++)
                    text.Append(' ').Append(lines[j].Trim());
                i = j - 1;
                var t = text.ToString();
                rows.Add(new PlanDecisionRow(DayIn(t), Squash(t), Cites(t)));
            }
        }
        return rows;
    }

    /// <summary>The decision numbers a text cites, like "(slice 1, #20)".</summary>
    public static IReadOnlyList<int> Cites(string text) =>
        Cite.Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();

    // ---- the conflict check ------------------------------------------------------------

    /// <summary>
    /// Words that mark a decision as one somebody expected to revisit. An older decision that says
    /// so is likelier to have been overtaken, so it takes less overlap to be put to the owner.
    /// </summary>
    private static readonly string[] Tentative =
    {
        // Phrases, not words: "later" and "until" are in half of all decisions, most of them settled.
        "first thought", "first version", "for now", "for the moment", "revisit", "best practice wins",
        "to start with", "provisional", "a later phase", "one day", "for this slice",
    };

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "also", "always", "another", "answer", "asked", "before", "being", "between", "both",
        "business", "businesses", "cannot", "chose", "could", "customer", "customers", "decided", "does", "each",
        "every", "first", "from", "have", "into", "itself", "just", "later", "least", "like", "made", "make", "many",
        "more", "most", "must", "never", "none", "nothing", "once", "only", "option", "other", "owner", "picked",
        "same", "says", "should", "slice", "some", "such", "than", "that", "their", "them", "then", "there", "these",
        "they", "thing", "this", "those", "through", "under", "unit", "until", "upon", "what", "when", "where",
        "which", "while", "will", "with", "within", "without", "would", "your", "yours", "added", "further", "popup",
    };

    private static HashSet<string> Words(string text)
    {
        var set = new HashSet<string>();
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z][a-z'-]{3,}"))
        {
            var w = m.Value.Trim('\'', '-');
            if (w.EndsWith("'s")) w = w[..^2];
            if (w.Length < 4 || Stop.Contains(w)) continue;
            // A crude stem, enough that "technician" and "technicians" are one word.
            if (w.EndsWith("ies") && w.Length > 5) w = w[..^3] + "y";
            else if (w.EndsWith("es") && w.Length > 5 && !w.EndsWith("ses")) w = w[..^2];
            else if (w.EndsWith('s') && !w.EndsWith("ss") && w.Length > 4) w = w[..^1];
            if (w.EndsWith("ing") && w.Length > 6) w = w[..^3];
            else if (w.EndsWith("ed") && w.Length > 5) w = w[..^2];
            if (!Stop.Contains(w)) set.Add(w);
        }
        return set;
    }

    /// <summary>
    /// The pairs that may disagree: enough of their words in common for one to be about the other.
    /// Two decisions made the same day about the same thing (one slice, or the project as a whole)
    /// are passed over - they were settled together, by one person looking at both. Only pairs with at least one of
    /// <paramref name="fresh"/> are looked at, so a pair already put to the owner is not put again
    /// because something unrelated arrived. Best first, at most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<DecisionPair> Conflicts(IReadOnlyList<DecisionWords> decisions, ISet<int>? fresh = null, int max = 60)
    {
        var words = decisions.Select(d => (d, w: Words(d.Text))).Where(x => x.w.Count > 0).ToList();
        // A word most decisions use (the product's name, "member", "book") says little about
        // whether two are about the same thing; a rare one says a lot. So each word weighs by how
        // few decisions use it, and a pair is scored on its shared weight against both its sizes,
        // which keeps one long note from matching everything.
        var df = words.SelectMany(x => x.w).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        double Idf(string w) => Math.Log(1.0 + (double)words.Count / df[w]);
        double Mass(IEnumerable<string> ws) => ws.Sum(Idf);
        var mass = words.Select(x => Mass(x.w)).ToList();
        var pairs = new List<DecisionPair>();
        for (var i = 0; i < words.Count; i++)
        for (var j = i + 1; j < words.Count; j++)
        {
            var (a, wa) = words[i];
            var (b, wb) = words[j];
            if (fresh is not null && !fresh.Contains(a.Number) && !fresh.Contains(b.Number)) continue;
            if (a.Scope == b.Scope && a.MadeAt.Date == b.MadeAt.Date) continue;
            var (older, newer, wo) = a.MadeAt < b.MadeAt || (a.MadeAt == b.MadeAt && a.Number < b.Number) ? (a, b, wa) : (b, a, wb);
            var shared = wa.Intersect(wb).ToList();
            if (shared.Count < 3) continue;
            var score = Mass(shared) / Math.Sqrt(mass[i] * mass[j]);
            var tentative = Tentative.Any(t => older.Text.Contains(t, StringComparison.OrdinalIgnoreCase));
            var need = tentative ? 0.3 : 0.4;
            if (score < need) continue;
            var why = $"Both say {string.Join(", ", shared.OrderByDescending(Idf).Take(6).Select(s => $"\"{s}\""))}"
                + (tentative ? $"; #{older.Number} reads as provisional" : "") + ".";
            pairs.Add(new DecisionPair(older.Number, newer.Number, why, score + (tentative ? 0.2 : 0)));
        }
        return pairs.OrderByDescending(p => p.Score).ThenBy(p => p.Older).Take(max).ToList();
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static DateTime? Day(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? d.ToUniversalTime() : null;

    private static DateTime? DayIn(string s) => Date.Match(s) is { Success: true } m ? Day(m.Groups[1].Value) : null;
}
