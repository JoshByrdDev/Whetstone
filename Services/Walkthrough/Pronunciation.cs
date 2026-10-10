using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whetstone.Services.Walkthrough;

/// <summary>
/// Written text to the text the voice should read. Captions and the stored script keep the written
/// form; only the synthesiser ever sees what <see cref="Speak"/> returns.
/// <para>
/// Every rule here was checked against Kokoro's own phonemizer (en-us) on 2026-09-18, not guessed:
/// "IRA" is read as the word "EE-ruh" and "ACH" as "atch"; spacing the letters out ("A C H")
/// reduces a lone A to a schwa; hyphenated letters ("I-R-A") come out as clean letter names with no
/// gaps. A ticket key is read as a number ("eight thousand nine hundred forty") unless it is broken
/// into pairs first. And "P.D.F." was never the voice's problem - it was a sentence splitter
/// breaking on every period, which <see cref="Sentences"/> no longer does.
/// </para>
/// </summary>
public class Pronunciation
{
    /// <summary>All-caps tokens that are words, not initialisms. Anything else 2-5 capitals long is spelled.</summary>
    private static readonly Dictionary<string, string> AsWord = new(StringComparer.Ordinal)
    {
        ["NULL"] = "null", ["SQL"] = "sequel", ["JSON"] = "jay-son", ["NASA"] = "NASA",
        ["ASAP"] = "A-SAP",
    };

    private static readonly string[] Ones = { "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
    private static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    private static readonly Regex TicketKey = new(@"\b([A-Z][A-Z0-9]{1,9})-(\d{2,5})\b", RegexOptions.Compiled);
    private static readonly Regex Caps = new(@"\b[A-Z]{2,5}\b", RegexOptions.Compiled);

    private readonly IReadOnlyList<(Regex Pattern, string Say)> _project;
    private readonly IReadOnlyDictionary<string, string> _projectWords;

    /// <param name="projectOverrides">Whole-word replacements for one project's vocabulary
    /// (product names, in-house initialisms), applied before the general rules. An entry for a
    /// tracker key's prefix also says how that half of a key is read - without one it is spelled,
    /// which is right for most prefixes and wrong for one that is a word.</param>
    public Pronunciation(IReadOnlyDictionary<string, string>? projectOverrides = null)
    {
        _projectWords = projectOverrides ?? new Dictionary<string, string>();
        _project = _projectWords
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => (new Regex($@"(?<![\w-]){Regex.Escape(kv.Key)}(?![\w-])"), kv.Value))
            .ToList();
    }

    /// <summary>A project's overrides from <c>data/projects/&lt;key&gt;/pronounce.json</c>, if it has one.</summary>
    public static Pronunciation ForProject(DashboardPaths paths, string projectKey)
    {
        var file = Path.Combine(paths.ProjectsDir, projectKey, "pronounce.json");
        if (!File.Exists(file)) return new Pronunciation();
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file, Encoding.UTF8));
            return new Pronunciation(map);
        }
        catch (JsonException) { return new Pronunciation(); }
    }

    public string Speak(string text)
    {
        var s = text;
        foreach (var (pattern, say) in _project) s = pattern.Replace(s, say);
        s = s.Replace("U/I", "UI")
             .Replace("i.e.,", "that is,").Replace("i.e.", "that is,")
             .Replace("e.g.,", "for example,").Replace("e.g.", "for example,");
        // Keys first, so the project half is not caught as an initialism below.
        s = TicketKey.Replace(s, m => (_projectWords.TryGetValue(m.Groups[1].Value, out var pw) ? pw
                                       : AsWord.TryGetValue(m.Groups[1].Value, out var w) ? w
                                       : Spell(m.Groups[1].Value))
                                      + " " + KeyNumber(m.Groups[2].Value));
        s = Caps.Replace(s, m => AsWord.TryGetValue(m.Value, out var w) ? w : Spell(m.Value));
        return s;
    }

    private static string Spell(string letters) => string.Join('-', letters.ToCharArray());

    private static string Two(int n) => n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 != 0 ? "-" + Ones[n % 10] : "");

    /// <summary>8940 "eighty-nine forty", 8905 "eighty-nine oh five", 8900 "eighty-nine hundred", 755 "seven fifty-five".</summary>
    public static string KeyNumber(string digits)
    {
        var n = int.Parse(digits);
        if (digits.Length == 4 && digits[0] != '0')
        {
            int hi = n / 100, lo = n % 100;
            return Two(hi) + " " + (lo == 0 ? "hundred" : lo < 10 ? "oh " + Ones[lo] : Two(lo));
        }
        if (digits.Length == 3 && digits[0] != '0')
            return Ones[n / 100] + " " + (n % 100 == 0 ? "hundred" : n % 100 < 10 ? "oh " + Ones[n % 100] : Two(n % 100));
        if (digits.Length == 2 && digits[0] != '0') return Two(n);
        return string.Join(' ', digits.Select(c => c == '0' ? "zero" : Ones[c - '0']));
    }

    /// <summary>
    /// Sentences, for the synthesiser to take one at a time: it silently truncates past ~510
    /// phonemes, so a paragraph handed over whole loses its end without an error. Does not break
    /// inside "i.e.", "U.S." or after a title like "Dr." - each of those would become its own clip,
    /// with its own lead-in and lead-out silence, which is exactly the stutter the first sample had.
    /// </summary>
    public static IReadOnlyList<string> Sentences(string text)
    {
        var outp = new List<string>();
        var start = 0;
        foreach (Match m in Regex.Matches(text, @"[.?!]+[""”)]?(\s+|$)"))
        {
            var end = m.Index + m.Length;
            var before = text[start..(m.Index + 1)];
            var lastWord = before.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            var next = end < text.Length ? text[end] : '\0';
            var abbrev = Regex.IsMatch(lastWord, @"^([A-Za-z]\.)+$") || Regex.IsMatch(lastWord, @"^(Mr|Mrs|Dr|vs|etc|approx)\.$", RegexOptions.IgnoreCase);
            if (end < text.Length && (abbrev || char.IsLower(next))) continue;
            var sentence = text[start..end].Trim();
            if (sentence.Length > 0) outp.Add(sentence);
            start = end;
        }
        if (start < text.Length && text[start..].Trim() is { Length: > 0 } rest) outp.Add(rest);
        return outp;
    }
}
