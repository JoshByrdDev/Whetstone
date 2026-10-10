using System.Text.RegularExpressions;

namespace Whetstone.Services.Walkthrough;

/// <summary>
/// A run of code for a slide, every line carrying the marker git gave it.
/// <para>
/// Code on a walkthrough slide is never typed out by the model. It names a file and a range of
/// new-side lines; the text comes from the file at the analysed commit, and whether each line is
/// new, removed or already there comes from the branch's patch. Built this way after the first
/// hand-assembled deck had its line numbers off by two and five of twenty-five new lines missing -
/// nothing a reader could have caught, because nothing on the slide was wrong-looking.
/// </para>
/// </summary>
public static class DiffSnippet
{
    /// <param name="M">"+" added, "-" removed, " " already there.</param>
    /// <param name="N">New-side line number; null for a removed line.</param>
    /// <param name="O">Old-side line number; null for an added line.</param>
    public record Line(string M, int? N, int? O, string T);

    public record Result(IReadOnlyList<Line> Lines, int Added, int Removed);

    private record PatchLine(char M, int? N, int? O, string T);

    private static readonly Regex Hunk = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    private static List<PatchLine> Parse(string patch)
    {
        var outp = new List<PatchLine>();
        int o = 0, n = 0;
        var inHunk = false;
        foreach (var raw0 in patch.Split('\n'))
        {
            var raw = raw0.TrimEnd('\r');
            var h = Hunk.Match(raw);
            if (h.Success) { o = int.Parse(h.Groups[1].Value); n = int.Parse(h.Groups[2].Value); inHunk = true; continue; }
            if (!inHunk || raw.StartsWith('\\')) continue;   // file header, or "\ No newline at end of file"
            if (raw.StartsWith("diff --git")) { inHunk = false; continue; }
            var m = raw.Length == 0 ? ' ' : raw[0];
            var t = raw.Length == 0 ? "" : raw[1..];
            switch (m)
            {
                case '+': outp.Add(new('+', n++, null, t)); break;
                case '-': outp.Add(new('-', null, o++, t)); break;
                case ' ': outp.Add(new(' ', n++, o++, t)); break;
            }
        }
        return outp;
    }

    /// <summary>
    /// New-side lines <paramref name="from"/>..<paramref name="to"/> of <paramref name="fileText"/>,
    /// marked from <paramref name="patch"/>. Removed lines are hung before the line that follows them.
    /// Returns an error instead when the patch and the file disagree - a slide that cannot be proven
    /// right is not shown.
    /// </summary>
    public static (Result? Snippet, string? Error) Build(string fileText, string patch, int from, int to)
    {
        var file = fileText.Replace("\r\n", "\n").Split('\n');
        if (file.Length > 0 && file[^1].Length == 0) file = file[..^1];
        if (from < 1 || to < from) return (null, $"lines {from}-{to} are not a range");
        if (to > file.Length) return (null, $"lines {from}-{to} run past the end of the file ({file.Length} lines)");

        var parsed = Parse(patch);
        var added = parsed.Where(p => p.M == '+').ToDictionary(p => p.N!.Value, p => p.T);

        var removedBefore = new Dictionary<int, List<PatchLine>>();
        var run = new List<PatchLine>();
        foreach (var p in parsed)
        {
            if (p.M == '-') { run.Add(p); continue; }
            if (run.Count > 0) { removedBefore[p.N!.Value] = run; run = new(); }
        }

        var lines = new List<Line>();
        for (var n = from; n <= to; n++)
        {
            if (removedBefore.TryGetValue(n, out var gone))
                lines.AddRange(gone.Select(g => new Line("-", null, g.O, g.T)));
            var text = file[n - 1].TrimEnd('\r');
            if (added.TryGetValue(n, out var patchText))
            {
                if (patchText != text)
                    return (null, $"line {n} reads differently in the file than in the patch - the commit and the diff disagree");
                lines.Add(new Line("+", n, null, text));
            }
            else lines.Add(new Line(" ", n, null, text));
        }

        // Strip the indentation every line shares, so a deeply nested hunk is not a wall of margin.
        var indent = lines.Where(l => l.T.Trim().Length > 0)
            .Select(l => l.T.Length - l.T.TrimStart(' ').Length).DefaultIfEmpty(0).Min();
        lines = lines.Select(l => l with { T = l.T.Length >= indent ? l.T[indent..] : l.T.TrimStart(' ') }).ToList();

        return (new Result(lines, lines.Count(l => l.M == "+"), lines.Count(l => l.M == "-")), null);
    }
}
