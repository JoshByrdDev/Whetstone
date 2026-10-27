using System.Text;
using System.Text.Json;
using Whetstone.Models;

namespace Whetstone.Services.Walkthrough;

/// <summary>
/// The brief for the run that scripts a walkthrough: everything it may point at, and how a good
/// script for the ear is built. The run reads, looks and points; it never copies code or invents an
/// id - see <see cref="WalkthroughValidator"/>, which checks every pointer this asks for.
/// </summary>
public static class WalkthroughPrompt
{
    public record ShotMaterial(int Order, string Caption, string LocalPath, int W, int H, ImageRect? Focus);
    public record AttachmentMaterial(string Name, string LocalPath, int W, int H, string? FromIssue);
    public record ChangedFile(string Path, int Added, int Removed);

    public static string Build(Ticket t, TicketAnalysis a, AnalysisDocument.Doc doc, string title, string description,
        IReadOnlyList<ShotMaterial> shots, IReadOnlyList<AttachmentMaterial> attachments,
        IReadOnlyList<ChangedFile> changed, string commit)
    {
        var sb = new StringBuilder();
        sb.AppendLine($$"""
        You are scripting a short narrated walkthrough of a finished ticket, for the developer who owns it.
        They will LISTEN to it before reading the full analysis below: it is the synopsis, played as a
        slideshow while a voice reads your narration. They know the codebase; they have not read this
        analysis yet. Aim for about 2 and a half minutes spoken: 300 to 400 words across 7 to 10 scenes.
        More than 420 words is sent back to be cut, so choose what earns its place rather than covering
        everything the analysis says.

        The ticket: {{t.IssueKey ?? $"#{t.Id}"}} - {{title}}
        The work was analysed at commit {{commit}} on branch {{a.Branch ?? t.Branch ?? "(unknown)"}}.
        """);
        sb.AppendLine(Craft(
            "the ticket, one or two beats",
            "the requirement in the ticket's own words",
            "the description below",
            "if the ticket carried a mockup or screenshot",
            $"a range of the branch's code. \"file\" (repo-relative, from the changed-files list), \"from\"/\"to\" (NEW-side line numbers at commit {commit}, at most 40 lines)",
            $"`git show {commit}:<path>` with line numbers, or the worktree if its HEAD matches",
            "title, ask, a result shot, flow, code or a picture, a proof shot, cards, question"));
        Pointers(sb, doc, shots, attachments, changed, "Files the branch changed (+added -removed)",
            "The ticket's description (quote from this, verbatim)", description);
        return sb.ToString();
    }

    /// <summary>
    /// The brief for a unit report's walkthrough: the same craft and the same checks, over a unit. Its
    /// "ask" quotes the unit's spec, its shots are the report's pool, and its code is the range on the
    /// base the report read.
    /// </summary>
    public static string BuildForUnit(string title, string planName, AnalysisDocument.Doc doc, string spec,
        IReadOnlyList<ShotMaterial> shots, IReadOnlyList<ChangedFile> changed, string fromSha, string toSha)
    {
        string Short(string sha) => sha[..Math.Min(10, sha.Length)];
        var sb = new StringBuilder();
        sb.AppendLine($$"""
        You are scripting a short narrated walkthrough of a finished unit of a plan - several tickets'
        work, taken together - for its owner, who agreed what it should do and is about to decide
        whether to release it. They will LISTEN to it before reading the unit report below: it is the
        synopsis, played as a slideshow while a voice reads your narration. Aim for about 2 and a half
        minutes spoken: 300 to 400 words across 7 to 10 scenes. More than 420 words is sent back to be
        cut, so choose what earns its place rather than covering everything the report says.

        The unit: {{title}}, of the plan "{{planName}}".
        Its code is the range {{Short(fromSha)}}..{{Short(toSha)}} on the base.

        Tell it as the unit, not as a list of tickets: what the owner asked for, what a person can now
        do, how it works, how we know, and what they should look at before releasing. Follow the
        report's coverage: an outcome it calls a gap or partial is said plainly, never smoothed over.
        """);
        sb.AppendLine(Craft(
            "the unit, one or two beats",
            "what the owner agreed, in the spec's own words",
            "the spec below",
            "never - a unit has none, so leave \"attachments\" out",
            $"a range of the unit's code. \"file\" (repo-relative, from the changed-files list), \"from\"/\"to\" (line numbers at commit {toSha}, at most 40 lines)",
            $"`git show {toSha}:<path>` with line numbers",
            "title, ask, a result shot, flow, a picture, a second shot or code, cards, question"));
        Pointers(sb, doc, shots, Array.Empty<AttachmentMaterial>(), changed, "Files the unit changed (+added -removed)",
            "The unit's spec (quote from this, verbatim)", spec);
        return sb.ToString();
    }

    /// <summary>How a script for the ear is built, and the scenes it may use. The same for every subject.</summary>
    private static string Craft(string titleScene, string askWhat, string quoteFrom, string attachmentsWhen,
        string codeScene, string readLines, string order) => ($$"""

        ## How to write for the ear

        - Short spoken sentences. One idea each. Every sentence under 300 characters, most well under.
        - Say what things ARE and DO, not where they live: "the check details the page reads", never a
          file path, a line number, a method signature or a SHA read aloud.
        - Write names and initialisms normally (IRA, PDF, PROJ-8939). A separate step teaches the voice
          to say them; do not spell them out yourself.
        - The analysis is your source of truth. Do not add claims it does not make.
        - Lead with what changed for the user, then how it works, then how we know, then the risks.
        - Every beat is one or two short sentences (under 30 words), and is what the voice says WHILE its cue is on screen. When
          the picture changes, start a new beat.

        ## Scenes

        Each scene is one kind of picture. Use the kinds that fit what this ticket actually has; skip
        the ones that do not. A typical order: {{order}}.

        - "title": {{titleScene}}. No other fields.
        - "ask": {{askWhat}}. "quotes": one to three EXACT excerpts copied
          character for character from {{quoteFrom}} (a sentence or two each - these are checked
          and a quote that is not verbatim is dropped). "attachments": names from the attachment list,
          {{attachmentsWhen}}. Per beat: "quotes" lights excerpts by index,
          "image" names the attachment on screen, with optional "zoom" and "box".
        - "shot": one QA screenshot, by "shot" order. Per beat: "focus": true zooms to the rectangle QA
          marked as the proof (use it wherever a shot has one), or give "zoom"/"box" yourself.
        - "code": {{codeScene}}. The slide marks
          every added and removed line from the real diff, so choose a range that shows the new code
          WITH the existing line or two around it - the reader must see what was there and what was
          added. Per beat: "lines": [first, last] lights a range inside it. Set "reveal": true on the
          scene and on one beat to hold the added lines back and bring them in on that beat - good for
          "it used to stop here; now it adds this". Find line numbers by reading the file at that
          commit ({{readLines}}); never guess them.
        - "flow": the analysis's user-flow diagram, by "block" address. Per beat: "nodes" lights node ids,
          walking the path in the order the narration tells it.
        - "cards": up to four callouts from the analysis, by "blocks" addresses, revealed one per beat
          with "show": 1, 2, 3...
        - "question": the analysis's closing question, by "block" address. "show": 1 on the beat that
          asks it.
        - "picture": a small animated diagram, for an idea that is easier to SEE than to hear and that
          no screenshot, code range or flow above already shows. See "Pictures" below.

        ## Pictures

        A picture scene draws a few labelled things and moves them as the narration goes. You never
        say how anything animates. Each beat may give a "picture": the WHOLE picture as it should look
        from that beat on (a beat without one keeps the last). The player compares it with the
        previous beat's picture BY ID and animates the difference:

        - an id that was not there before enters - "we added this";
        - an id that is gone leaves - "we took this out" (or keep it with "change": "removed" to show
          it struck out);
        - the same id with new "text" or a new "change" changes in place - "this became that";
        - the same id in a new place slides there - "it moved", "it now sits between these two";
        - an arrow that was not there before draws itself - "now this calls that".

        So keep an id the same for the same thing on every beat, and change only what the narration
        is about. One idea per scene, two to five beats, building up.

        A picture is {"layout": "row" | "column", "items": [...], "arrows": [...]}:
        - item: "id" (short, e.g. "cache"), "text" (a label of two to five words; for kind "text" a
          short sentence), and optional "kind" ("box" by default; "group" frames the items whose "in"
          names it, laid out by its own "layout"; "text" is a plain caption; "code" is a short
          identifier or expression in monospace, copied exactly), "sub" (a second, smaller line),
          "change" ("unchanged", "new", "changed", "removed" - the colours the flow diagram uses).
        - arrow: "from" and "to" item ids, optional "label" (a word or two) and "change".
        - Items are laid out in the order listed. At most 12 items and 10 arrows; fewer reads better.
        Per beat, "lit": item ids to point at while the line is spoken; everything else dims.

        Name things the way the analysis, the ticket and the changed files do - a label none of whose
        words appear there is dropped. A picture arranges what is known; it never introduces a part
        nobody built. Use one or two picture scenes where they earn it, not every scene.

        A "zoom" or "box" is [x, y, w, h] in the image's own pixels (sizes are listed below). Open an
        image with the Read tool before choosing a rectangle on it. Zoom to the part that proves the
        point, with enough around it to read the label beside the value.

        ## What you may point at
        """).ReplaceLineEndings("\n");

    /// <summary>Everything a script may point at, listed with the addresses the checks resolve.</summary>
    private static void Pointers(StringBuilder sb, AnalysisDocument.Doc doc, IReadOnlyList<ShotMaterial> shots,
        IReadOnlyList<AttachmentMaterial> attachments, IReadOnlyList<ChangedFile> changed,
        string changedHeading, string quoteHeading, string description)
    {
        sb.AppendLine();
        sb.AppendLine("### Analysis blocks (address = section.block)");
        for (var si = 0; si < doc.Sections.Count; si++)
        for (var bi = 0; bi < doc.Sections[si].Blocks.Count; bi++)
        {
            var b = doc.Sections[si].Blocks[bi];
            var line = $"- {si}.{bi} {b.Kind} (in \"{doc.Sections[si].Title}\")";
            if (b.Kind == "callout") line += $": [{b.Tone}] {b.Title}";
            if (b.Kind == "question") line += $": {Clip(b.Text, 120)}";
            if (b.Kind == "userFlow" && b.Nodes is { } nodes)
                line += ". Nodes: " + string.Join("; ", nodes.Select(n => $"{n.Id} ({n.Kind}, {n.Change}) \"{n.Label}\""));
            sb.AppendLine(line);
        }

        sb.AppendLine();
        sb.AppendLine("### QA screenshots");
        if (shots.Count == 0) sb.AppendLine("(none)");
        foreach (var s in shots)
            sb.AppendLine($"- shot {s.Order}: {s.W}x{s.H} at {s.LocalPath}. {(s.Focus is { } f ? $"QA marked the proof at [{f.X}, {f.Y}, {f.W}, {f.H}]." : "No focus marked.")} Caption: {s.Caption}");

        sb.AppendLine();
        sb.AppendLine("### Attachments");
        if (attachments.Count == 0) sb.AppendLine("(none)");
        foreach (var at in attachments)
            sb.AppendLine($"- \"{at.Name}\": {at.W}x{at.H} at {at.LocalPath}{(at.FromIssue is { } k ? $" (attached to parent issue {k}, not this ticket)" : "")}");

        sb.AppendLine();
        sb.AppendLine($"### {changedHeading}");
        foreach (var c in changed.Take(80)) sb.AppendLine($"- {c.Path} +{c.Added} -{c.Removed}");
        if (changed.Count > 80) sb.AppendLine($"- ... and {changed.Count - 80} more");

        sb.AppendLine();
        sb.AppendLine($"### {quoteHeading}");
        sb.AppendLine("```");
        sb.AppendLine(description.Trim());
        sb.AppendLine("```");

        sb.AppendLine();
        sb.AppendLine("### The analysis (your source)");
        sb.AppendLine("```json");
        sb.AppendLine(AnalysisDocument.Serialize(doc));
        sb.AppendLine("```");

        sb.AppendLine();
        sb.AppendLine("Return only the script, as the JSON the schema describes.");
    }

    /// <summary>
    /// The second attempt: the first draft, exactly what failed in it, and a request for the whole
    /// corrected script. One round only - what is still wrong after it is dropped, not argued over.
    /// </summary>
    public static string Repair(string original, JsonElement draft, IReadOnlyList<string> problems)
    {
        var sb = new StringBuilder(original);
        sb.AppendLine();
        sb.AppendLine("## Your first draft, and what failed in it");
        sb.AppendLine("Every pointer in a script is checked against the real diff, diagram, screenshots and description.");
        sb.AppendLine("These did not check out:");
        foreach (var p in problems) sb.AppendLine($"- {p}");
        sb.AppendLine();
        sb.AppendLine("The draft:");
        sb.AppendLine("```json");
        sb.AppendLine(draft.GetRawText());
        sb.AppendLine("```");
        sb.AppendLine("Fix every problem above - look things up again rather than adjusting numbers by guesswork - and return the WHOLE corrected script.");
        return sb.ToString();
    }

    private static string Clip(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..(max - 1)] + "…";
}
