using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Whetstone.Services;

/// <summary>One decision as a spec's "Decided" section writes it. <see cref="Number"/> is null for a paragraph that numbers nothing.</summary>
public record DecidedItem(int? Number, string? Title, string Rule, DateTime? On, IReadOnlyList<string> CitedKeys);

/// <summary>Reading decisions out of a slice spec's "Decided" section.</summary>
public static class DecisionText
{
    private static readonly Regex DecidedHeading = new(@"^##\s+Decided\b", RegexOptions.IgnoreCase);
    private static readonly Regex SectionEnd = new(@"^#{1,2}\s");
    private static readonly Regex Bullet = new(@"^(?:[-*]|\d+\.)\s+(.*)$");
    private static readonly Regex Lead = new(@"^\*\*(.+?)\*\*\s*");
    private static readonly Regex Date = new(@"\b(\d{4}-\d{2}-\d{2})\b");
    private static readonly Regex Key = new(@"`([a-z0-9]+(?:-[a-z0-9]+)+)`");

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


    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static DateTime? Day(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? d.ToUniversalTime() : null;

    private static DateTime? DayIn(string s) => Date.Match(s) is { Success: true } m ? Day(m.Groups[1].Value) : null;
}
