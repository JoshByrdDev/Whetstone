using System.Text.RegularExpressions;
using static Whetstone.Services.Walkthrough.WalkthroughDeck;

namespace Whetstone.Services.Walkthrough;

/// <summary>
/// Turns the script Claude wrote into a deck, checking every pointer on the way.
/// <para>
/// Nothing a slide shows is taken on the script's word. A code range is cut from the file at the
/// analysed commit and marked from the patch; a flow node must exist in the analysis's diagram; a
/// box drawn on an image must lie on it; a quote must be words the ticket actually contains. What
/// fails is left out and described in <see cref="Result.Problems"/>, in sentences written to be
/// handed straight back to the model for one repair round - so they name the scene, the value and
/// what would have been valid.
/// </para>
/// </summary>
public static class WalkthroughValidator
{
    public const int MaxScenes = 16;
    public const int MaxBeatsPerScene = 8;
    public const int MaxCodeLines = 40;
    /// <summary>Well under what the narrator refuses (480 phonemes), so a sentence is caught here with a useful message.</summary>
    public const int MaxSentenceChars = 300;
    /// <summary>About three minutes spoken. The prompt asks for less; this is where asking stops and checking starts.</summary>
    public const int MaxWords = 420;

    public record Shot(int Order, string Caption, string Url, int W, int H, ImageRect? Focus);
    public record Attachment(string Name, string Url, int W, int H);

    /// <param name="Snippet">Cuts new-side lines from..to of a file, marked from the branch's patch.</param>
    /// <param name="Vocabulary">Words a picture may use beyond the analysis and the description: the changed files' paths.</param>
    public record Context(
        AnalysisDocument.Doc Doc,
        string Description,
        IReadOnlyList<Shot> Shots,
        IReadOnlyList<Attachment> Attachments,
        Func<string, int, int, Task<(DiffSnippet.Result? Snippet, string? Error)>> Snippet,
        string Vocabulary = "");

    public record Result(IReadOnlyList<Scene> Scenes, IReadOnlyList<string> Problems);

    public static async Task<Result> ValidateAsync(RawScript script, Context ctx)
    {
        var problems = new List<string>();
        var scenes = new List<Scene>();
        var raw = script.Scenes ?? Array.Empty<RawScene>();
        if (raw.Count == 0) return new Result(scenes, new[] { "The script has no scenes." });
        if (raw.Count > MaxScenes) problems.Add($"The script has {raw.Count} scenes; only the first {MaxScenes} are used.");

        var n = 0;
        foreach (var s in raw.Take(MaxScenes))
        {
            n++;
            var label = $"Scene {n} ({s.Kind ?? "no kind"}, \"{s.Chapter}\")";
            var scene = await SceneAsync(s, ctx, label, problems);
            if (scene is not null) scenes.Add(scene);
        }

        if (scenes.Count == 0) problems.Add("No scene survived the checks.");

        // Nothing is dropped for length - the script still plays - but it goes back through the repair
        // round, because a synopsis that runs long has stopped being the thing you listen to first.
        var words = scenes.Sum(s => s.Beats.Sum(b => b.Say.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length));
        if (words > MaxWords)
            problems.Add($"The script is {words} words, about {words / 150.0:0.0} minutes spoken; keep it under {MaxWords}. Cut the weakest scene before trimming the others, and keep each beat to one or two short sentences.");
        return new Result(scenes, problems);
    }

    private static async Task<Scene?> SceneAsync(RawScene s, Context ctx, string label, List<string> problems)
    {
        var kind = s.Kind?.Trim().ToLowerInvariant() ?? "";
        if (!SceneKinds.Contains(kind)) { problems.Add($"{label}: kind must be one of {string.Join(", ", SceneKinds)}."); return null; }
        var chapter = string.IsNullOrWhiteSpace(s.Chapter) ? "" : s.Chapter.Trim();

        var rawBeats = (s.Beats ?? Array.Empty<RawBeat>()).Where(b => !string.IsNullOrWhiteSpace(b.Say)).ToList();
        if (rawBeats.Count == 0) { problems.Add($"{label}: has no beats with anything to say, so it was left out."); return null; }
        if (rawBeats.Count > MaxBeatsPerScene)
        {
            problems.Add($"{label}: has {rawBeats.Count} beats; only the first {MaxBeatsPerScene} are used. Split it into two scenes.");
            rawBeats = rawBeats.Take(MaxBeatsPerScene).ToList();
        }
        foreach (var (b, i) in rawBeats.Select((b, i) => (b, i)))
            foreach (var sentence in Pronunciation.Sentences(b.Say!))
                if (sentence.Length > MaxSentenceChars)
                    problems.Add($"{label}, beat {i + 1}: a sentence is {sentence.Length} characters; keep each under {MaxSentenceChars} or the voice cannot read it in one piece. It starts \"{Clip(sentence, 60)}\".");

        switch (kind)
        {
            case "title":
                return new Scene(chapter, kind, rawBeats.Select(b => new Beat(b.Say!.Trim(), null)).ToList());

            case "ask": return Ask(s, ctx, chapter, rawBeats, label, problems);
            case "shot": return Shot_(s, ctx, chapter, rawBeats, label, problems);
            case "code": return await CodeAsync(s, ctx, chapter, rawBeats, label, problems);
            case "flow": return Flow(s, ctx, chapter, rawBeats, label, problems);
            case "cards": return Cards(s, ctx, chapter, rawBeats, label, problems);
            case "question": return Question(s, ctx, chapter, rawBeats, label, problems);
            case "picture": return Picture_(ctx, chapter, rawBeats, label, problems);
        }
        return null;
    }

    // ---- ask: the ticket's own words, and its attachments ----

    private static Scene? Ask(RawScene s, Context ctx, string chapter, List<RawBeat> beats, string label, List<string> problems)
    {
        var description = Normalise(ctx.Description);
        var quotes = new List<string>();
        foreach (var q in s.Quotes ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(q)) continue;
            if (description.Contains(Normalise(q), StringComparison.OrdinalIgnoreCase)) quotes.Add(q.Trim());
            else problems.Add($"{label}: the quote \"{Clip(q, 80)}\" is not in the ticket's description word for word. Quote it exactly, or leave it out.");
        }
        if (quotes.Count == 0) { problems.Add($"{label}: an ask scene needs at least one exact quote from the description, so it was left out."); return null; }

        var images = new List<Image>();
        foreach (var a in s.Attachments ?? Array.Empty<string>())
        {
            var found = ctx.Attachments.FirstOrDefault(x => x.Name.Equals(a, StringComparison.OrdinalIgnoreCase));
            if (found is null) problems.Add($"{label}: there is no attachment called \"{a}\". The ticket's attachments are: {Names(ctx.Attachments.Select(x => x.Name))}.");
            else if (images.All(i => i.Name != found.Name)) images.Add(new Image(found.Name, found.Url, found.W, found.H));
        }

        var outBeats = new List<Beat>();
        foreach (var (b, i) in beats.Select((b, i) => (b, i)))
        {
            var where = $"{label}, beat {i + 1}";
            var lit = (b.Quotes ?? Array.Empty<int>()).Where(q =>
            {
                if (q >= 0 && q < quotes.Count) return true;
                problems.Add($"{where}: quotes index {q} does not exist; this scene has quotes 0-{quotes.Count - 1}.");
                return false;
            }).Distinct().ToList();

            Image? image = null;
            if (b.Image is { } name)
            {
                image = images.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (image is null) problems.Add($"{where}: image \"{name}\" is not one of this scene's attachments ({Names(images.Select(x => x.Name))}).");
            }
            var (zoom, box) = image is null ? (null, null) : Rects(b, image.W, image.H, where, problems);
            outBeats.Add(new Beat(b.Say!.Trim(), new Cue(null, null, lit.Count > 0 ? lit : null, image?.Name, zoom, box, null, null)));
        }
        return new Scene(chapter, "ask", outBeats) { Quotes = quotes, Images = images.Count > 0 ? images : null };
    }

    // ---- shot: a QA screenshot ----

    private static Scene? Shot_(RawScene s, Context ctx, string chapter, List<RawBeat> beats, string label, List<string> problems)
    {
        var shot = s.Shot is int o ? ctx.Shots.FirstOrDefault(x => x.Order == o) : null;
        if (shot is null)
        {
            problems.Add($"{label}: shot {s.Shot?.ToString() ?? "(none)"} does not exist; the QA shots are {Names(ctx.Shots.Select(x => x.Order.ToString()))}. Left out.");
            return null;
        }
        var image = new Image($"shot-{shot.Order}", shot.Url, shot.W, shot.H);

        var outBeats = new List<Beat>();
        foreach (var (b, i) in beats.Select((b, i) => (b, i)))
        {
            var where = $"{label}, beat {i + 1}";
            ImageRect? zoom, box;
            if (b.Focus == true && shot.Focus is { } f)
                (zoom, box) = (Pad(f, shot.W, shot.H), f);
            else
            {
                if (b.Focus == true) problems.Add($"{where}: asked for QA's focus, but shot {shot.Order} has none marked. Give a zoom box instead.");
                (zoom, box) = Rects(b, shot.W, shot.H, where, problems);
            }
            outBeats.Add(new Beat(b.Say!.Trim(), new Cue(null, null, null, image.Name, zoom, box, null, null)));
        }
        return new Scene(chapter, "shot", outBeats) { Shot = shot.Order, Images = new[] { image } };
    }

    /// <summary>A focus rectangle with room around it, so the label beside a value is in frame too.</summary>
    private static ImageRect Pad(ImageRect f, int w, int h)
    {
        var px = Math.Max(24, f.W / 3);
        var py = Math.Max(24, f.H * 2);
        var x = Math.Max(0, f.X - px);
        var y = Math.Max(0, f.Y - py);
        return new ImageRect(x, y, Math.Min(w, f.X + f.W + px) - x, Math.Min(h, f.Y + f.H + py) - y);
    }

    private static (ImageRect? Zoom, ImageRect? Box) Rects(RawBeat b, int w, int h, string where, List<string> problems)
    {
        ImageRect? Check(IReadOnlyList<int>? r, string what)
        {
            if (r is null || r.Count == 0) return null;
            if (r.Count != 4) { problems.Add($"{where}: {what} must be [x, y, w, h]; got {r.Count} numbers."); return null; }
            var rect = new ImageRect(r[0], r[1], r[2], r[3]);
            if (rect.ProblemWithin(w, h) is { } p) { problems.Add($"{where}: {what} {p}."); return null; }
            return rect;
        }
        return (Check(b.Zoom, "zoom"), Check(b.Box, "box"));
    }

    // ---- code: cut from the patch, never typed ----

    private static async Task<Scene?> CodeAsync(RawScene s, Context ctx, string chapter, List<RawBeat> beats, string label, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(s.File) || s.From is not int from || s.To is not int to)
        {
            problems.Add($"{label}: a code scene needs file, from and to. Left out.");
            return null;
        }
        if (to - from + 1 > MaxCodeLines)
        {
            problems.Add($"{label}: {s.File} {from}-{to} is {to - from + 1} lines; show at most {MaxCodeLines}. Left out.");
            return null;
        }
        var (snippet, error) = await ctx.Snippet(s.File.Trim(), from, to);
        if (snippet is null)
        {
            problems.Add($"{label}: {s.File} {from}-{to}: {error}. Left out.");
            return null;
        }

        var reveal = s.Reveal == true && snippet.Added > 0;
        var outBeats = new List<Beat>();
        foreach (var (b, i) in beats.Select((b, i) => (b, i)))
        {
            IReadOnlyList<int>? lines = null;
            if (b.Lines is { Count: > 0 } l)
            {
                if (l.Count != 2 || l[0] > l[1]) problems.Add($"{label}, beat {i + 1}: lines must be [first, last]; got [{string.Join(", ", l)}].");
                else if (l[0] < from || l[1] > to) problems.Add($"{label}, beat {i + 1}: lines {l[0]}-{l[1]} fall outside the scene's range {from}-{to}.");
                else lines = l;
            }
            outBeats.Add(new Beat(b.Say!.Trim(), new Cue(lines, null, null, null, null, null, reveal && b.Reveal == true ? true : null, null)));
        }

        // A held-back reveal with no beat that reveals it would hide the new code for the whole scene.
        if (reveal && !outBeats.Any(b => b.Cue?.Reveal == true))
        {
            problems.Add($"{label}: reveal was set but no beat reveals, so the new lines are shown from the start.");
            reveal = false;
        }
        return new Scene(chapter, "code", outBeats)
        {
            Code = new Code(s.File.Trim().Replace('\\', '/'), Language(s.File), snippet.Added, snippet.Removed, reveal, snippet.Lines),
        };
    }

    private static string? Language(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp", ".ts" => "ts", ".tsx" => "tsx", ".js" or ".mjs" => "js", ".jsx" => "jsx",
        ".sql" => "sql", ".ps1" => "powershell", ".json" => "json", ".css" => "css", ".html" => "html",
        ".py" => "python", ".md" => "markdown", ".xml" or ".csproj" => "xml", ".yml" or ".yaml" => "yaml",
        _ => null,
    };

    // ---- analysis blocks: flow, cards, question ----

    private static AnalysisDocument.Block? BlockAt(AnalysisDocument.Doc doc, string? address)
    {
        if (address is null) return null;
        var m = Regex.Match(address.Trim(), @"^(\d+)\.(\d+)$");
        if (!m.Success) return null;
        int si = int.Parse(m.Groups[1].Value), bi = int.Parse(m.Groups[2].Value);
        return si < doc.Sections.Count && bi < doc.Sections[si].Blocks.Count ? doc.Sections[si].Blocks[bi] : null;
    }

    private static IEnumerable<string> AddressesOf(AnalysisDocument.Doc doc, string kind) =>
        doc.Sections.SelectMany((s, si) => s.Blocks.Select((b, bi) => (b, a: $"{si}.{bi}")))
            .Where(x => x.b.Kind == kind).Select(x => x.a);

    private static Scene? Flow(RawScene s, Context ctx, string chapter, List<RawBeat> beats, string label, List<string> problems)
    {
        var block = BlockAt(ctx.Doc, s.Block);
        if (block?.Kind != "userFlow" || block.Nodes is not { Count: > 0 } nodes)
        {
            problems.Add($"{label}: block \"{s.Block}\" is not a user flow; the analysis's flows are at {Names(AddressesOf(ctx.Doc, "userFlow"))}. Left out.");
            return null;
        }
        var ids = nodes.Select(x => x.Id).ToHashSet();
        var outBeats = new List<Beat>();
        foreach (var (b, i) in beats.Select((b, i) => (b, i)))
        {
            var lit = new List<string>();
            foreach (var id in b.Nodes ?? Array.Empty<string>())
            {
                if (ids.Contains(id)) lit.Add(id);
                else problems.Add($"{label}, beat {i + 1}: node \"{id}\" is not in the flow; its nodes are {Names(ids)}.");
            }
            outBeats.Add(new Beat(b.Say!.Trim(), new Cue(null, lit.Count > 0 ? lit : null, null, null, null, null, null, null)));
        }
        return new Scene(chapter, "flow", outBeats) { Block = s.Block!.Trim() };
    }

    private static Scene? Cards(RawScene s, Context ctx, string chapter, List<RawBeat> beats, string label, List<string> problems)
    {
        var blocks = new List<string>();
        foreach (var a in s.Blocks ?? Array.Empty<string>())
        {
            var b = BlockAt(ctx.Doc, a);
            if (b is { Kind: "callout" } && !string.IsNullOrWhiteSpace(b.Text)) blocks.Add(a.Trim());
            else problems.Add($"{label}: block \"{a}\" is not a callout; the analysis's callouts are at {Names(AddressesOf(ctx.Doc, "callout"))}.");
        }
        blocks = blocks.Distinct().Take(4).ToList();
        if (blocks.Count == 0) { problems.Add($"{label}: a cards scene needs at least one callout. Left out."); return null; }

        var outBeats = new List<Beat>();
        foreach (var (b, i) in beats.Select((b, i) => (b, i)))
        {
            int? show = null;
            if (b.Show is int k)
            {
                if (k >= 0 && k <= blocks.Count) show = k;
                else problems.Add($"{label}, beat {i + 1}: show {k} is out of range; there are {blocks.Count} cards.");
            }
            outBeats.Add(new Beat(b.Say!.Trim(), new Cue(null, null, null, null, null, null, null, show)));
        }
        return new Scene(chapter, "cards", outBeats) { Blocks = blocks };
    }

    private static Scene? Question(RawScene s, Context ctx, string chapter, List<RawBeat> beats, string label, List<string> problems)
    {
        var block = BlockAt(ctx.Doc, s.Block);
        if (block?.Kind != "question" || string.IsNullOrWhiteSpace(block.Text))
        {
            problems.Add($"{label}: block \"{s.Block}\" is not a question; the analysis's questions are at {Names(AddressesOf(ctx.Doc, "question"))}. Left out.");
            return null;
        }
        return new Scene(chapter, "question", beats.Select(b => new Beat(b.Say!.Trim(),
            b.Show is int k ? new Cue(null, null, null, null, null, null, null, Math.Clamp(k, 0, 1)) : null)).ToList())
        { Block = s.Block!.Trim() };
    }

    // ---- picture: a small diagram, animated by what changes between beats ----

    public const int MaxPictureItems = 12;
    public const int MaxPictureArrows = 10;
    public const int MaxLabelChars = 48;
    public const int MaxTextChars = 90;

    private static Scene? Picture_(Context ctx, string chapter, List<RawBeat> beats, string label, List<string> problems)
    {
        if (beats[0].Picture is null)
        {
            problems.Add($"{label}: a picture scene's first beat must draw a picture. Left out.");
            return null;
        }
        var known = Words(AnalysisDocument.Serialize(ctx.Doc) + " " + ctx.Description + " " + ctx.Vocabulary);

        var outBeats = new List<Beat>();
        Picture? current = null;
        foreach (var (b, i) in beats.Select((b, i) => (b, i)))
        {
            var where = $"{label}, beat {i + 1}";
            Picture? drawn = null;
            if (b.Picture is { } raw)
            {
                drawn = Drawing(raw, known, where, problems);
                if (drawn is null && current is null) { problems.Add($"{label}: its first picture had nothing left to draw. Left out."); return null; }
                if (drawn is not null) current = drawn;
            }
            var ids = current!.Items.Select(x => x.Id).ToHashSet();
            var lit = new List<string>();
            foreach (var id in b.Lit ?? Array.Empty<string>())
            {
                if (ids.Contains(id)) { if (!lit.Contains(id)) lit.Add(id); }
                else problems.Add($"{where}: lit \"{id}\" is not in the picture on screen; its ids are {Names(ids)}.");
            }
            outBeats.Add(new Beat(b.Say!.Trim(), new Cue(null, null, null, null, null, null, null, null)
            {
                Picture = drawn,
                Lit = lit.Count > 0 ? lit : null,
            }));
        }
        return new Scene(chapter, "picture", outBeats);
    }

    /// <summary>
    /// One beat's picture, with every part that does not hold together taken out: a repeated id, a
    /// group that is not one, an arrow to nothing, and a label whose words appear nowhere in what the
    /// ticket and its analysis say. That last check is loose - one word in common is enough - because
    /// a picture's job is to arrange what is known, and the one thing it must not do is introduce a
    /// part nobody built.
    /// </summary>
    private static Picture? Drawing(RawPicture raw, HashSet<string> known, string where, List<string> problems)
    {
        var items = new List<PictureItem>();
        var seen = new HashSet<string>();
        foreach (var r in raw.Items ?? Array.Empty<RawPictureItem>())
        {
            var id = r.Id?.Trim() ?? "";
            var text = Squash(r.Text);
            if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{1,32}$")) { problems.Add($"{where}: picture id \"{Clip(id, 40)}\" must be a short name of letters, digits, - or _."); continue; }
            if (!seen.Add(id)) { problems.Add($"{where}: picture id \"{id}\" is used twice; each thing on screen needs its own."); continue; }
            var kind = r.Kind?.Trim().ToLowerInvariant() is { Length: > 0 } k ? k : "box";
            if (!PictureKinds.Contains(kind)) { problems.Add($"{where}: \"{id}\" has kind \"{kind}\"; use one of {string.Join(", ", PictureKinds)}."); continue; }
            if (text.Length == 0 && kind != "group") { problems.Add($"{where}: \"{id}\" has no text."); continue; }
            var max = kind == "text" ? MaxTextChars : MaxLabelChars;
            if (text.Length > max) { problems.Add($"{where}: \"{id}\" is {text.Length} characters; a {kind} holds at most {max}. Say the rest aloud."); text = Clip(text, max); }
            var sub = Squash(r.Sub) is { Length: > 0 } su ? Clip(su, MaxLabelChars) : null;
            if (Unfounded(text + " " + sub, known) is { } invented)
            {
                problems.Add($"{where}: \"{id}\" says \"{Clip(text, 60)}\", and none of its words ({invented}) appear in the analysis, the ticket or the changed files. Name things the way the analysis does. Left out.");
                continue;
            }
            var change = r.Change?.Trim().ToLowerInvariant() is { } c && PictureChanges.Contains(c) ? c : "unchanged";
            items.Add(new PictureItem(id, kind, text)
            {
                Sub = sub,
                Change = change,
                In = string.IsNullOrWhiteSpace(r.In) ? null : r.In.Trim(),
                Layout = kind == "group" ? (r.Layout?.Trim().ToLowerInvariant() == "column" ? "column" : "row") : null,
            });
        }

        if (items.Count > MaxPictureItems)
        {
            problems.Add($"{where}: the picture has {items.Count} things on it; at most {MaxPictureItems} are drawn. A picture that needs more is two pictures.");
            items = items.Take(MaxPictureItems).ToList();
        }

        // A parent must be a group in this picture, and groups may not contain each other in a loop.
        // Anything that fails is drawn at the top level rather than lost.
        var groups = items.Where(x => x.Kind == "group").Select(x => x.Id).ToHashSet();
        string? ParentOf(string id) => items.FirstOrDefault(x => x.Id == id)?.In;
        for (var n = 0; n < items.Count; n++)
        {
            var it = items[n];
            if (it.In is null) continue;
            var bad = !groups.Contains(it.In) ? $"\"{it.In}\" is not a group in this picture"
                : Depth(it.Id, ParentOf) is null ? "its groups contain each other" : null;
            if (bad is null) continue;
            problems.Add($"{where}: \"{it.Id}\" is placed in {it.In}, but {bad}; drawn at the top level instead.");
            items[n] = it with { In = null };
        }
        // A group with no title and nothing in it draws a frame around nothing.
        items = items.Where(x => x.Kind != "group" || x.Text.Length > 0 || items.Any(c => c.In == x.Id)).ToList();
        if (items.Count == 0) return null;

        var ids = items.Select(x => x.Id).ToHashSet();
        var arrows = new List<Arrow>();
        foreach (var a in raw.Arrows ?? Array.Empty<RawArrow>())
        {
            var from = a.From?.Trim() ?? "";
            var to = a.To?.Trim() ?? "";
            if (!ids.Contains(from) || !ids.Contains(to) || from == to)
            {
                problems.Add($"{where}: the arrow {from} -> {to} does not join two things in the picture; its ids are {Names(ids)}.");
                continue;
            }
            if (arrows.Any(x => x.From == from && x.To == to)) continue;
            var change = a.Change?.Trim().ToLowerInvariant() is { } c && PictureChanges.Contains(c) ? c : "unchanged";
            arrows.Add(new Arrow(from, to) { Label = Squash(a.Label) is { Length: > 0 } l ? Clip(l, 32) : null, Change = change });
        }
        if (arrows.Count > MaxPictureArrows)
        {
            problems.Add($"{where}: the picture has {arrows.Count} arrows; at most {MaxPictureArrows} are drawn.");
            arrows = arrows.Take(MaxPictureArrows).ToList();
        }

        return new Picture(raw.Layout?.Trim().ToLowerInvariant() == "column" ? "column" : "row", items, arrows);
    }

    /// <summary>How deep an item is nested, or null when following its parents goes round in a circle.</summary>
    private static int? Depth(string id, Func<string, string?> parentOf)
    {
        var path = new HashSet<string> { id };
        var depth = 0;
        for (var p = parentOf(id); p is not null; p = parentOf(p))
        {
            if (!path.Add(p)) return null;
            depth++;
        }
        return depth;
    }

    /// <summary>Words too general to show that a label is about this ticket - they are in every label.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "this", "with", "from", "into", "then", "when", "only", "each", "every", "more", "less",
        "than", "they", "their", "there", "here", "what", "which", "will", "would", "were", "been", "have",
        "does", "done", "make", "made", "used", "uses", "user", "page", "step", "before", "after", "once",
        "again", "still", "first", "last", "next", "same", "other", "both", "none", "some", "many", "most",
        "very", "just", "also", "about", "over", "under", "back", "away", "show", "check", "call", "save",
        "read", "write", "send", "gets", "sets", "list", "item", "value", "data", "code", "change", "changed",
        "added", "removed", "start", "fail", "fails", "error", "result", "true", "false", "null",
    };

    /// <summary>Lower-cased words of four letters or more, camelCase and paths split, a plural's s taken off too.</summary>
    private static HashSet<string> Words(string text)
    {
        var spaced = Regex.Replace(text, @"([a-z0-9])([A-Z])", "$1 $2");
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(spaced, @"[A-Za-z]{4,}"))
        {
            var w = m.Value.ToLowerInvariant();
            set.Add(w);
            if (w.EndsWith('s')) set.Add(w[..^1]);
        }
        return set;
    }

    /// <summary>A label's meaningful words when not one of them is known; null when it is founded or says nothing checkable.</summary>
    private static string? Unfounded(string text, HashSet<string> known)
    {
        var words = Words(text).Where(w => !Common.Contains(w) && !(w.EndsWith('s') && Common.Contains(w[..^1]))).ToList();
        if (words.Count == 0 || words.Any(known.Contains)) return null;
        return string.Join(", ", words.Take(4));
    }

    private static string Squash(string? s) => string.IsNullOrWhiteSpace(s) ? "" : Regex.Replace(s, @"\s+", " ").Trim();

    // ---- helpers ----

    /// <summary>Whitespace and typographic quotes flattened, so a quote survives the tracker's line breaks.</summary>
    private static string Normalise(string s) =>
        Regex.Replace(s.Replace('“', '"').Replace('”', '"').Replace('’', '\'').Replace('‘', '\''), @"\s+", " ").Trim();

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
    }
}
