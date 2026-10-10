using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whetstone.Models;

/// <summary>One choice offered against a blocking question.</summary>
/// <remarks>
/// Carries <see cref="DescriptionAttribute"/> because this record is the MCP tool's own input shape
/// (see TicketTools.SetPhase) as well as the stored one. A second set of mirror records existed
/// briefly and immediately drifted from these, which is the whole argument against it.
/// </remarks>
public record BlockOption(
    [Description("The choice itself, as a short phrase a person can pick off a list - 'New endpoint on the web tier', not a paragraph.")]
    string Label,
    [Description("Optional one-line consequence of taking this choice: what gets built, or what it costs.")]
    string? Detail = null,
    [Description("Set on the ONE option you would take if nobody answers. Say it whenever you have a defensible default - it is what lets the human answer only the questions that actually need them. Leave every option unrecommended when the choice genuinely turns on knowledge you do not have.")]
    bool Recommended = false);

/// <summary>One question a stopped ticket is asking its owner.</summary>
public record BlockQuestion(
    [Description("The question, as ONE line a person can answer - under 200 characters. Everything that explains it belongs in 'why', not here.")]
    string Question,
    [Description("Why you are asking: the evidence, the contradiction, the file:line citations. Markdown. Shown collapsed under the question, so length is cheap here and expensive in 'question'.")]
    string? Why = null,
    [Description("The choices, when the question has a knowable set of them. Two to four is the useful range. Omit for a genuinely open question - an empty list renders as a free-text answer, which is better than three invented options.")]
    IReadOnlyList<BlockOption>? Options = null,
    [Description("Whether the human may answer something other than the listed options. Leave true unless the options are genuinely exhaustive.")]
    bool AllowOther = true);

/// <summary>
/// Everything a worker asked when it stopped a ticket: the framing, the questions, and anything it
/// needs a person to go and find.
/// <para>
/// This is an additive shape. <see cref="BlockQuestions.Render"/> flattens it into the prose that
/// still goes into <c>Ticket.BlockedReason</c> and onto the transition log, so the board, the
/// comment trail and the next run's prompt all keep working off one string and know nothing about
/// questions. The structure exists so the drawer can offer the answer as a click instead of an essay
/// - if it is ever missing or unparseable, the prose is still there and nothing is lost.
/// </para>
/// </summary>
public record BlockPayload(
    [Description("What you were doing and what stopped you, in a sentence or two, before the questions. Optional when the questions speak for themselves.")]
    string? Preamble,
    [Description("The questions. Order them by how much the answer changes what gets built.")]
    IReadOnlyList<BlockQuestion> Questions,
    [Description("Things you need a person to go and find rather than decide - 'the parent epic', 'a sample of the vendor payload'. Not questions; they render as a separate checklist.")]
    IReadOnlyList<string>? Needs = null);

/// <summary>Validation, rendering and storage for a structured block.</summary>
public static class BlockQuestions
{
    /// <summary>Headline cap. The point of the structured form is a list a person can scan.</summary>
    public const int MaxQuestionLength = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// What is wrong with this payload, or null. Phrased as the first half of a refusal — the caller
    /// appends the standing "here is how to satisfy it" text, exactly as the free-text path does.
    /// </summary>
    public static string? Validate(BlockPayload p)
    {
        var qs = p.Questions;
        if (qs is null || qs.Count == 0)
            return "This block carries no questions, so there is nothing for a person to answer. ";

        for (var i = 0; i < qs.Count; i++)
        {
            var n = i + 1;
            var q = qs[i].Question?.Trim();

            if (string.IsNullOrWhiteSpace(q))
                return $"Question {n} is empty. ";

            // The floor the free-text path uses, applied per question: a structured block made of
            // one-word questions is the same omission wearing a better shape.
            if (q.Length < 12 || q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3)
                return $"Question {n} (\"{q}\") is not a question anyone can answer. ";

            if (q.Length > MaxQuestionLength)
                return $"Question {n} is {q.Length} characters. It has to be one scannable line of "
                     + $"{MaxQuestionLength} or fewer - move the explanation into that question's 'why', "
                     + "which is shown underneath it and has no limit. ";

            var opts = qs[i].Options ?? [];
            if (opts.Any(o => string.IsNullOrWhiteSpace(o.Label)))
                return $"Question {n} has an option with no label. ";

            // More than one default is no default: the reader has to pick anyway, and the run that
            // proceeds unanswered has nothing to proceed on.
            if (opts.Count(o => o.Recommended) > 1)
                return $"Question {n} recommends {opts.Count(o => o.Recommended)} of its options. "
                     + "Recommend at most one - it is the choice that gets taken if nobody answers. ";
        }

        return null;
    }

    /// <summary>
    /// The payload as the prose it replaces. This is what lands in <c>BlockedReason</c>, on the
    /// transition log and in the next run's prompt, so it has to read as a complete account of the
    /// block on its own — someone reading only this string should never know a UI existed.
    /// </summary>
    public static string Render(BlockPayload p)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(p.Preamble))
            sb.Append(p.Preamble.Trim()).Append("\n\n");

        for (var i = 0; i < p.Questions.Count; i++)
        {
            var q = p.Questions[i];
            sb.Append($"**{i + 1}. {q.Question.Trim()}**\n");

            if (!string.IsNullOrWhiteSpace(q.Why))
                sb.Append('\n').Append(q.Why.Trim()).Append('\n');

            var opts = q.Options ?? [];
            if (opts.Count > 0)
            {
                sb.Append('\n');
                for (var j = 0; j < opts.Count; j++)
                {
                    var o = opts[j];
                    sb.Append($"- **({(char)('a' + j)})** {o.Label.Trim()}");
                    if (!string.IsNullOrWhiteSpace(o.Detail)) sb.Append($" - {o.Detail.Trim()}");
                    if (o.Recommended) sb.Append("  *(recommended)*");
                    sb.Append('\n');
                }
            }

            sb.Append('\n');
        }

        var needs = p.Needs?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? [];
        if (needs.Length > 0)
        {
            sb.Append("**Would settle this fastest:**\n\n");
            foreach (var n in needs) sb.Append($"- {n.Trim()}\n");
        }

        return sb.ToString().TrimEnd();
    }

    public static string Serialize(BlockPayload p) => JsonSerializer.Serialize(p, Json);

    /// <summary>
    /// The stored payload, or null when there is none or it will not parse. Never throws: the prose
    /// in <c>BlockedReason</c> is the copy that matters, and a ticket whose JSON has gone bad should
    /// degrade to that rather than take the drawer down with it.
    /// </summary>
    public static BlockPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var p = JsonSerializer.Deserialize<BlockPayload>(json, Json);
            return p?.Questions is { Count: > 0 } ? p : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
