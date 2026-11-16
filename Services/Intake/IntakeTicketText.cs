using System.Globalization;
using System.Text;
using Whetstone.Models;

namespace Whetstone.Services.Intake;

/// <summary>
/// What an intake ticket says (docs/intake-plan.md, section 5). The problem comes from the adapter;
/// this puts around it what every intake ticket says the same way: where it came from, what the
/// worker can and cannot reach, what the proof is, and - in a part marked for the owner - the
/// report in the vendor's own UI.
/// </summary>
public static class IntakeTicketText
{
    /// <summary>The marker the owner's part of the description starts with; a worker is told it cannot open what follows.</summary>
    public const string OwnerMarker = "**For the owner only**";

    public static string Prefix(string channel) => channel switch
    {
        IntakeChannels.Sentry => "[sentry]",
        IntakeChannels.CloudWatchAlarm => "[alarm]",
        IntakeChannels.Storm => "[intake storm]",
        _ => $"[{channel}]",
    };

    public static string Title(IntakeReport r, bool test = false) =>
        $"{Prefix(r.Channel)}{(test ? " [test]" : "")} {r.Title}".Trim();

    public static string Description(IntakeReport r, int? regressionOf, string? runbookSection, string? runbookPath, bool test = false)
    {
        var b = new StringBuilder();
        if (test)
        {
            b.AppendLine("**This is a test.** The project's own end-to-end check of its error reporting sent this report, tagged `test: true`. "
                + "It proves the path from production to this board works; there is nothing to fix, so the board filed it straight to Done.");
            b.AppendLine();
        }
        if (regressionOf is { } old)
        {
            b.AppendLine($"**This came back.** The same problem was ticket #{old}, which is Done. Read what that fix did before planning this one: "
                + "either it did not reach every path, or something since has undone it.");
            b.AppendLine();
        }
        b.AppendLine(r.Body);

        if (!string.IsNullOrWhiteSpace(runbookSection))
        {
            b.AppendLine();
            b.AppendLine($"### From the runbook (`{runbookPath}`)");
            b.AppendLine();
            foreach (var line in runbookSection.TrimEnd().Split('\n')) b.AppendLine("> " + line.TrimEnd('\r'));
        }

        b.AppendLine();
        b.AppendLine("---");
        b.AppendLine();
        b.AppendLine(r.Channel == IntakeChannels.CloudWatchAlarm ? AlarmParagraph(r) : ErrorParagraph(r));

        if (!string.IsNullOrWhiteSpace(r.OwnerLink))
        {
            b.AppendLine();
            b.AppendLine("---");
            b.AppendLine();
            b.AppendLine($"{OwnerMarker} (a worker cannot open this): [the report in {(r.Channel == IntakeChannels.Sentry ? "the error tracker" : "the cloud console")}]({r.OwnerLink})");
        }
        return b.ToString().TrimEnd();
    }

    private static string ErrorParagraph(IntakeReport r) =>
        "**Filed by intake from this project's production error reports, not by a person.** "
        + (r.Release is { } rel
            ? $"The release above (`{rel}`) is the commit that was live: read the stack against that commit, not against the base as it is now. "
            : "The report names no release, so the stack may not match the base as it is now; say so if a frame does not line up. ")
        + "Nothing outside this ticket can be opened from here - not the error tracker, not the cloud console, not production - "
        + "and everything known about the report is written above. The proof is a test that fails locally the way production failed, "
        + "reproducing it, and then the fix that makes it pass.";

    private static string AlarmParagraph(IntakeReport r) =>
        "**Filed by intake from this project's cloud alarms, not by a person.** "
        + "Nothing outside this ticket can be opened from here - not the cloud console, not the metrics, not production - "
        + "and everything known about the alarm is written above. An alarm's cause may not be code: an outage somewhere else, a quota, "
        + "a deploy, traffic. Where the cause is in the code, the proof is a test that fails locally the way production did, then the fix. "
        + "Where it is outside the code, route it the way the pipeline routes production work: the investigation, a runbook comment "
        + "saying what a person should run and what they should see, and an item on the owner's to-do list. "
        + "An alarm that clears is noted here and moves nothing: it still had a cause.";

    // ---- the storm ----------------------------------------------------------------------

    public static string StormTitle(string window, int held) =>
        $"{Prefix(IntakeChannels.Storm)} {held} report{(held == 1 ? "" : "s")} held back in {WindowName(window)}";

    public static string StormDescription(string window, IntakeLimits limits, IReadOnlyList<IntakeIssue> held)
    {
        var b = new StringBuilder();
        b.AppendLine($"**Intake held these back.** It files at most {limits.NewPerHour} new ticket{(limits.NewPerHour == 1 ? "" : "s")} an hour "
            + $"and {limits.NewPerDay} a day from this project's production reports, and in {WindowName(window)} more came than that. "
            + "Each is still counted, and each is filed in a later window if it is still firing then.");
        b.AppendLine();
        b.AppendLine("A storm usually has one cause behind many symptoms - a deploy, a dependency down, a migration - and that cause is what this ticket is for. "
            + "Nothing outside this ticket can be opened from here; route a cause outside the code the way the pipeline routes production work.");
        b.AppendLine();
        b.AppendLine("### Held back");
        b.AppendLine();
        foreach (var i in held.OrderByDescending(i => i.Occurrences).ThenBy(i => i.Id))
            b.AppendLine($"- {Prefix(i.Channel)} {i.Title} - `{i.Fingerprint}`, {i.Occurrences} report{(i.Occurrences == 1 ? "" : "s")}, "
                + $"last {i.LastSeen.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}"
                + (i.TicketId is { } tid ? $", since filed as #{tid}" : ""));
        return b.ToString().TrimEnd();
    }

    public static string WindowName(string window) =>
        window.StartsWith("h:") ? $"the hour from {window[2..].Replace('T', ' ')}:00 UTC"
        : window.StartsWith("d:") ? $"the day {window[2..]} (UTC)"
        : window;

    // ---- notes on an open ticket -------------------------------------------------------

    public static string Repeat(int more, DateTime last, long? reported, string state) =>
        $"Intake: seen {more} more time{(more == 1 ? "" : "s")}, last at {last.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}"
        + (reported is { } n ? $" ({n} in all, by the tracker's count)" : "")
        + (state == IntakeStates.Regressed ? ". The tracker marks it regressed: it had been resolved there." : ".");

    public static string Cleared(DateTime at) =>
        $"Intake: the alarm cleared at {at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}. "
        + "Clearing moves nothing: an alarm that cleared still had a cause.";

    // ---- the runbook -------------------------------------------------------------------

    /// <summary>The section of a Markdown runbook whose heading is <paramref name="name"/>, without the heading; null when none is.</summary>
    public static string? RunbookSection(string? markdown, string name)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        static (int Level, string Text)? Heading(string l)
        {
            var t = l.TrimStart();
            var n = t.TakeWhile(c => c == '#').Count();
            return n is >= 1 and <= 6 && t.Length > n && t[n] == ' ' ? (n, t[(n + 1)..].Trim().Trim('`').Trim()) : null;
        }
        for (var i = 0; i < lines.Length; i++)
        {
            if (Heading(lines[i]) is not { } h || !h.Text.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var body = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (Heading(lines[j]) is { } next && next.Level <= h.Level) break;
                body.Add(lines[j]);
            }
            var text = string.Join("\n", body).Trim();
            return text.Length == 0 ? null : text.Length > 4000 ? text[..4000] + "\n…" : text;
        }
        return null;
    }
}
