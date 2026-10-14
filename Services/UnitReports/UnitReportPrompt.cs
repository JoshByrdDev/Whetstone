using System.Text.Json;
using Whetstone.Models;

namespace Whetstone.Services.UnitReports;

/// <summary>
/// The instruction for a unit report: one document, in the Classroom's block schema, about a whole
/// unit of a plan - written over its tickets' own analyses, ruled against its own outcomes.
/// <para>
/// A pure function of its arguments and public for the same reason the ticket prompt is: what a run
/// is told is the specification of what it writes, and that is worth testing without a run.
/// </para>
/// </summary>
public static class UnitReportPrompt
{
    /// <param name="Display">The project's display name.</param>
    /// <param name="Label">What the reader is learning about (<c>learningLabel</c>), or empty.</param>
    /// <param name="UnitTitle">The unit's own name, from its spec.</param>
    /// <param name="Spec">The unit's spec as committed: what the owner agreed.</param>
    /// <param name="ShotPaths">Pool number to the file on disk the run may open.</param>
    /// <param name="Bounces">Per ticket, every time it was sent back and why, oldest first.</param>
    /// <param name="NextUnit">The next unit's title and outcome ids, for a <c>deferred</c> ruling to point at. Null at the end of the plan.</param>
    /// <param name="AnalysisPaths">Per ticket, the file its whole analysis was written to, for the run to open.</param>
    public record Context(
        string Display,
        string Label,
        string PlanName,
        PlanUnit Unit,
        string UnitTitle,
        string Spec,
        UnitMaterial.Result Material,
        IReadOnlyList<StewardOutcome> States,
        UnitRange Range,
        string ReadDir,
        IReadOnlyDictionary<int, string> ShotPaths,
        IReadOnlyDictionary<int, IReadOnlyList<string>> Bounces,
        string? NextUnit,
        IReadOnlyDictionary<int, string> AnalysisPaths);

    private static string Clip(string? s, int max)
    {
        var one = string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= max ? one : one[..(max - 1)] + "…";
    }

    /// <summary>
    /// One ticket, in the few lines a unit report needs of it: what it carried, what its analysis
    /// concluded, the flows it drew, the risks it named, and where the whole analysis is.
    /// <para>
    /// A digest and not the document, because a unit can be a hundred tickets and an analysis runs to
    /// forty kilobytes: the whole set would not fit in a run, and would drown the synthesis if it did.
    /// The whole of any one is a file the run can open when its digest leaves a question.
    /// </para>
    /// </summary>
    public static string Digest(UnitMaterial.TicketPart t, IReadOnlyList<string>? bounces, string? analysisPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"### #{t.Ticket.Id} {Clip(t.Ticket.Title, 140)}\n");
        sb.Append($"Carries {(t.Outcomes.Count > 0 ? string.Join(", ", t.Outcomes) : "no outcome")}; "
            + (t.LandedAt is { } at ? $"landed {at:yyyy-MM-dd}" : $"{t.Ticket.Phase}, not landed")
            + (bounces is { Count: > 0 } ? $"; sent back {bounces.Count}x: " + string.Join(" | ", bounces.Take(3).Select(b => Clip(b, 120))) : "")
            + "\n");

        if (t.Doc is not { } doc)
        {
            sb.Append("No analysis. Implementation summary: " + Clip(t.Ticket.ImplementationSummary, 400) + "\n");
            return sb.ToString();
        }

        sb.Append($"Analysis: {Clip(doc.Headline, 240)}\n");
        foreach (var b in doc.Sections.SelectMany(s => s.Blocks))
        {
            if (b.Kind == "coverage")
            {
                if (!string.IsNullOrWhiteSpace(b.Text)) sb.Append($"Coverage: {Clip(b.Text, 300)}\n");
                foreach (var g in (b.Segments ?? Array.Empty<AnalysisDocument.CoverageSegment>())
                             .Where(g => g.Verdict is "gap" or "partial" or "unknown").Take(4))
                    sb.Append($"  {g.Verdict}: {Clip(g.Text, 100)} - {Clip(g.Evidence, 160)}\n");
            }
            else if (b.Kind == "userFlow" && b.Nodes is { Count: > 0 } nodes)
                sb.Append("Flow: " + string.Join(" > ", nodes.Select(n =>
                    $"{Clip(n.Label, 40)} [{n.Change}{(n.Shot is int s ? $", shot {s}" : "")}]")) + "\n");
            else if (b.Kind == "callout" && b.Tone == "risk")
                sb.Append($"Risk: {Clip(b.Title, 120)} - {Clip(b.Text, 200)}\n");
        }
        if (analysisPath is not null) sb.Append($"Whole analysis: {analysisPath.Replace('\\', '/')}\n");
        return sb.ToString();
    }

    public static string Build(Context c)
    {
        string P(string path) => path.Replace('\\', '/');
        string Short(string sha) => sha[..Math.Min(10, sha.Length)];
        var m = c.Material;

        var outcomes = string.Join("\n", m.Outcomes.Select(o =>
        {
            var st = c.States.FirstOrDefault(s => s.Unit == c.Unit.Number && s.Outcome == o.Id);
            var by = m.Tickets.Where(t => t.Outcomes.Contains(o.Id)).Select(t => $"#{t.Ticket.Id}").ToList();
            return $"{o.Id}  [{(st?.State ?? StewardOutcomeState.NotStarted)}] {(o.Origin is { } h ? $"({h}) " : "")}{o.Text}"
                + (by.Count > 0 ? $"   - carried by {string.Join(", ", by)}" : "   - no ticket carries it");
        }));

        // Each ticket as a digest of its analysis, in the order they landed. The analyses are the
        // material: each was written by a run that read that branch closely, and this run's job is
        // to put them together, not to do them again.
        var tickets = string.Join("\n", m.Tickets.OrderBy(t => t.LandedAt ?? DateTime.MaxValue).ThenBy(t => t.Ticket.Id)
            .Select(t => Digest(t, c.Bounces.GetValueOrDefault(t.Ticket.Id), c.AnalysisPaths.GetValueOrDefault(t.Ticket.Id))));

        var shots = m.Shots.Count == 0 ? "(none - no ticket in this unit took a QA screenshot)"
            : string.Join("\n", m.Shots.Select(s =>
                $"- {s.N}: #{s.TicketId}"
                + (s.LandedAt is { } at ? $" {at:yyyy-MM-dd}" : " not landed")
                + $" - {Clip(s.Caption, 110)}"));
        var shotDir = c.ShotPaths.Count == 0 ? null : Path.GetDirectoryName(c.ShotPaths.First().Value);

        var calls = m.Calls.Count == 0 ? "(none)"
            : string.Join("\n", m.Calls.Select(e => $"- #{e.TicketId}, {e.At:yyyy-MM-dd}: {e.Text}"));

        var gaps = m.Missing.Count == 0 ? "" : $$"""

            ## What this report is written without

            Say so where it matters - in the coverage of the outcomes these tickets carry, and in
            the headline when it changes the answer. Do not paper over it.

            {{string.Join("\n", m.Missing.Select(x => $"- {x.Why}"))}}

            """;

        var foreign = c.Range.RangeCommits - c.Range.OwnCommits;
        var rangeNote = c.Range.FromSha == c.Range.ToSha
            ? "The unit's start could not be placed on the base, so there is no range to diff: read the ticket analyses and the code at the end commit."
            : $"{c.Range.RangeCommits} commit(s) on the base's first-parent line"
              + (c.Range.OwnCommits > 0 ? $", {c.Range.OwnCommits} of them this unit's own merges" : "")
              + (c.Range.OwnCommits > 0 && foreign > 0 ? $". The other {foreign} are not this unit's work: do not credit them to it." : ".");

        var next = c.NextUnit is null ? "This is the last unit of the plan."
            : $"The next unit, for a `deferred` ruling to point at: {c.NextUnit}";

        var label = string.IsNullOrWhiteSpace(c.Label) ? "the product" : c.Label;

        return ($$"""
        You are writing a UNIT REPORT for the "{{c.Display}}" project: one teaching document about a
        whole unit of its plan ("{{c.PlanName}}", unit {{c.Unit.Number}}: {{c.UnitTitle}}), for its owner.
        They agreed this unit's outcomes before any of it was built, and a steward and a pipeline of
        workers built it while they were elsewhere. The one decision left that is theirs is whether to
        release it. This document is what they read to make it - and, after that, how they learn what
        {{label}} now does.

        ## Absolute constraints

        - **READ ONLY.** Do not modify, create or delete any file. Do not stage, commit, checkout,
          switch, stash, reset or push anything. Your Bash use is limited to reads - `git log`,
          `git diff`, `git show`, `git status`. You have no Edit or Write tool; do not work around that.
        - Do not run the app, the test suite, or any build.
        - Output nothing but the structured document. It is validated against a schema; there is no
          prose channel and no summary to write afterwards.

        ## Where things are

        - Reading here (already your working directory): {{P(c.ReadDir)}}, which is the base at
          `{{Short(c.Range.ToSha)}}`. Every `file:line` you write points at the code as it is here.
        - The unit's range on the base: `{{Short(c.Range.FromSha)}}..{{Short(c.Range.ToSha)}}`
          ({{c.Range.BaseRef}}). {{rangeNote}}

        ## What the owner agreed

        The unit's spec, as committed:

        ````markdown
        {{c.Spec.Trim()}}
        ````

        Its outcomes, the lines you will rule on. The ids are handles for this exchange; the reader
        never sees them, and is shown their own words beside your ruling. The state in brackets is
        the steward's call, not yours to repeat - you rule on what the code and the evidence show:

        ```
        {{outcomes}}
        ```

        {{next}}

        ## The tickets that built it

        Each ticket as a digest of its Classroom analysis: a validated document another run wrote
        after reading that ticket's branch closely. **These are your primary material.** A digest
        gives what the analysis concluded, the flow it drew, and the risks it named; when you need
        more of one - its rulings, its reading path, its code - open the whole analysis at the path
        given. Every `shot` in them has already been renumbered into the pool below.

        {{tickets}}
        {{gaps}}
        ## The screenshots

        Every QA screenshot the unit's tickets took, numbered for this report, **newest-landed
        first**: number, the ticket that took it, when that ticket landed, and its caption. Each is
        the file `<number>.png` (or .jpg/.webp) in {{(shotDir is null ? "(none)" : P(shotDir))}}; open
        one with Read where the caption alone does not tell you which screen it is.

        {{shots}}

        **When two shots show the same screen, cite the one from the ticket that landed later** - it
        is the screen as it ships, and an earlier one may show a state a later ticket changed. An
        earlier ticket's shot is still the right evidence for an outcome only that ticket carried,
        when the later change to that screen was unrelated to it; then say so in the evidence.
        Never cite one shot as two different places.

        ## Decisions the steward made for the owner

        Calls it made on these tickets without asking - answers it gave, choices in filing, what it
        kept or sent back at review:

        {{calls}}

        ## What to read before writing

        1. The ticket analyses above, all of them, before any code.
        2. `git log --first-parent --oneline {{Short(c.Range.FromSha)}}..{{Short(c.Range.ToSha)}}` and
           `git diff --stat {{Short(c.Range.FromSha)}}..{{Short(c.Range.ToSha)}}`.
        3. Code only where it settles something the analyses leave open: two tickets that touched
           the same place, a later ticket that may have changed what an earlier analysis describes,
           a citation you need to re-point at the code as it is now. An analysis's `file:line` was
           true on its own branch; check it here before you repeat it.

        ## The document

        Use these sections, in this order, titled like this. Drop one only when the unit genuinely
        gives you nothing for it.

        1. **What you agreed, and what you got** - one `coverage` block: a ruling on every outcome id
           above. Set its `text` to the whole answer in one sentence, naming any gap in the spec's
           own words. Per ruling: `verdict`, `evidence` (one sentence, which ticket did it and how),
           `refs` (1-3 `file:line` at the end commit - required for `met`), `qa` (the QA item that
           exercised it, quoted from a ticket's analysis or record), `shot` (the pool number that
           shows it working), `deferredTo` (the next unit, or the comment that moved it - required
           for `deferred`). `partial` says which part did not land. An outcome the steward called
           done that you cannot find evidence for is `unknown` or `gap`, never `met` on its word.
           Rule on every id. **Write this section last**, after the others have made you read.
        2. **How it works now** - one to three `userFlow` blocks, one per area of the product the
           unit changed, each preceded by a short `prose` block saying what the person is trying to
           do. **Merge the tickets' flows; do not stack them.** Where two tickets drew the same
           screen it is one stop. `change` is measured against the product **before this unit**, not
           before each ticket: a stop one ticket added and a later one reworked is `new`. Put the
           pool's shot on every `screen` and `end` stop you honestly can, under the rule above.
           Skip the section only if nothing a person sees or does changed.
        3. **Decisions made for you** - the steward's calls and any choice a ticket's analysis shows
           was made without the owner, as a `keyValue` block (the decision, then where and why in a
           few words). Then a `callout` with tone `risk` for any you believe the owner would reverse
           if asked, naming why. Skip the section if there were none.
        4. **What would catch you out** - `callout` blocks, at most four. The most valuable ones are
           **between** tickets: two changes that interact, an assumption one ticket made that a later
           one broke, an outcome that is met in pieces nobody tested together.
        5. **Where to look** - one `readingPath` block, 4-10 steps across the whole unit, as a route
           a reader could walk in one sitting. Every location a `file:line` you verified here.
        6. **Worth sitting with** - one `question` block, about the unit as a whole.

        ## Using the blocks

        - `prose` - two to four sentences per block. `**bold**` and `` `code` `` are supported.
        - `userFlow` - `nodes` and `edges` only; layout is the renderer's. Node `kind` is `start`,
          `screen`, `step`, `decision` (label every edge out of one), `system` or `end`. `label` is a
          2-5 word header; `explain` is 1-3 sentences of what happens there, naming which ticket made
          it so. `refs` 1-3 `file:line`. 6-14 nodes per flow. Every path ends on an `end` the person
          can see. Be strict with `change` - marking everything `new` throws the diagram away.
        - `code` - only where it ends an argument the prose cannot. Real lines from the file here,
          `path`, `startLine` as it is now, `language`, 20 lines at most, never re-wrapped or
          annotated. Optional in this document; two at most.
        - `keyValue`, `callout` (`gotcha`, `insight`, `risk`), `readingPath`, `question` - as named above.
        - `coverage` - `text` and `rulings` only. Never retype an outcome's words.

        ## What decides whether this is worth reading

        - **The owner reads the coverage first and may read nothing else.** Its one sentence must
          let them decide.
        - **Synthesis, not concatenation.** A section that reads as ticket 1, then ticket 2, then
          ticket 3 has not done the job. The unit is one change to the product; describe that change.
        - **Concrete or nothing.** Anything that would read the same about a different unit goes.
        - **Length: 900-1800 words across all blocks, the coverage block excepted.**
        """).ReplaceLineEndings("\n");
    }
}
