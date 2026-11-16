using System.Globalization;
using System.Text;
using System.Text.Json;
using Whetstone.Models;

namespace Whetstone.Services.Intake;

/// <summary>The states a report can be in (docs/intake-plan.md, section 3).</summary>
public static class IntakeStates
{
    /// <summary>New, or seen again.</summary>
    public const string Firing = "firing";
    /// <summary>Came back after the tracker had it resolved.</summary>
    public const string Regressed = "regressed";
    /// <summary>An alarm back to OK.</summary>
    public const string Cleared = "cleared";
    /// <summary>An alarm that cannot tell (INSUFFICIENT_DATA): noted, never filed.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// One report, in the one shape every adapter turns its vendor's format into. The board files and
/// dedupes from this alone; nothing past an adapter knows which vendor a report came from.
/// </summary>
/// <param name="Channel">Where it came from, as attribution counts it: <see cref="IntakeChannels"/>.</param>
/// <param name="Fingerprint">What makes two reports the same problem: the tracker's issue id, the alarm's name.</param>
/// <param name="Body">The problem as Markdown: what happened and where. Filing adds the rest of the ticket around it.</param>
/// <param name="OwnerLink">The report in the vendor's own UI. For the owner only: a worker cannot open it.</param>
/// <param name="IsTest">A project's end-to-end proof that the path works: filed, and straight to Done.</param>
public sealed record IntakeReport(
    string Adapter,
    string Channel,
    string Fingerprint,
    string State,
    string Title,
    string Body,
    TicketPriority Severity,
    DateTime? OccurredAt,
    long? Count,
    string? Release,
    string? OwnerLink,
    bool IsBug,
    bool IsTest = false,
    DateTime? FirstSeen = null);

/// <summary>Recognises one vendor's format, or says it is not its own (null).</summary>
public interface IIntakeAdapter
{
    string Name { get; }
    IntakeReport? TryRead(IntakeEnvelope message);
}

/// <summary>The adapters intake knows, tried in order. The first that recognises a message reads it.</summary>
public static class IntakeAdapters
{
    public static readonly IReadOnlyList<IIntakeAdapter> All = [new SentryIssueAdapter(), new CloudWatchAlarmAdapter()];

    /// <summary>The report, or null when no adapter recognised the message - or one recognised it and could not read it.</summary>
    public static (IntakeReport? Report, string? Problem) Read(IntakeEnvelope m)
    {
        foreach (var a in All)
        {
            try
            {
                if (a.TryRead(m) is { } r) return (r, null);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                return (null, $"{a.Name} recognised it and could not read it: {e.Message}");
            }
        }
        return (null, null);
    }

    internal static string? Str(JsonElement o, string p) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(p, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    internal static JsonElement Obj(JsonElement o, string p) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(p, out var v) ? v : default;

    internal static DateTime? Time(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // CloudWatch writes "2026-11-16T14:05:12.345+0000", which the round-trip format does not take.
        var t = s.Trim();
        if (t.Length > 5 && (t[^5] == '+' || t[^5] == '-') && char.IsDigit(t[^1])) t = t[..^2] + ":" + t[^2..];
        return DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
    }

    /// <summary>A line of a vendor's text, made safe to sit in a Markdown paragraph and a title.</summary>
    internal static string OneLine(string? s, int max = 300)
    {
        var t = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}

/// <summary>
/// An error tracker's issue, in Sentry's API format: a message with the attribute
/// <c>kind = sentry-issue</c> whose body is <c>{ "issue": &lt;issue&gt;, "event": &lt;latest event&gt; }</c>.
/// Both shapes of an event are read - the API's (<c>entries</c>, <c>lineNo</c>, <c>inApp</c>) and the
/// raw event's (<c>exception.values</c>, <c>lineno</c>, <c>in_app</c>) - since a forwarder may send either.
/// </summary>
public sealed class SentryIssueAdapter : IIntakeAdapter
{
    public const string Kind = "sentry-issue";
    public string Name => Kind;

    public IntakeReport? TryRead(IntakeEnvelope m)
    {
        if (!m.Attributes.TryGetValue("kind", out var kind) || !kind.Equals(Kind, StringComparison.OrdinalIgnoreCase)) return null;
        using var doc = JsonDocument.Parse(m.Body);
        var root = doc.RootElement;
        var issue = IntakeAdapters.Obj(root, "issue");
        var ev = IntakeAdapters.Obj(root, "event");
        var id = IntakeAdapters.Str(issue, "id");
        if (string.IsNullOrWhiteSpace(id)) throw new FormatException("the issue has no id");

        var tags = Tags(ev);
        var exceptions = Exceptions(ev);
        var top = exceptions.LastOrDefault();
        var title = IntakeAdapters.Str(issue, "title") ?? (top is null ? null : $"{top.Type}: {top.Value}") ?? "An error";
        var culprit = IntakeAdapters.Str(issue, "culprit");
        var where = top?.Frames.LastOrDefault(f => f.InApp)?.Function ?? culprit;
        var release = ReleaseOf(ev) ?? tags.GetValueOrDefault("release");
        var substatus = IntakeAdapters.Str(issue, "substatus");
        var state = substatus?.Equals("regressed", StringComparison.OrdinalIgnoreCase) == true ? IntakeStates.Regressed : IntakeStates.Firing;
        long? count = long.TryParse(IntakeAdapters.Str(issue, "count"), out var c) ? c : null;
        var firstSeen = IntakeAdapters.Time(IntakeAdapters.Str(issue, "firstSeen"));
        var lastSeen = IntakeAdapters.Time(IntakeAdapters.Str(issue, "lastSeen")) ?? IntakeAdapters.Time(IntakeAdapters.Str(ev, "dateCreated"));
        var test = tags.TryGetValue("test", out var tv) && tv.Equals("true", StringComparison.OrdinalIgnoreCase);

        var b = new StringBuilder();
        b.AppendLine($"- **What happened:** {IntakeAdapters.OneLine(top is null ? title : $"{top.Type}: {top.Value}", 500)}");
        if (!string.IsNullOrWhiteSpace(culprit)) b.AppendLine($"- **Where:** `{IntakeAdapters.OneLine(culprit)}`");
        if (release is not null) b.AppendLine($"- **Release:** `{IntakeAdapters.OneLine(release, 120)}`");
        foreach (var k in new[] { "environment", "transaction", "route", "url", "step" })
            if (tags.TryGetValue(k, out var v)) b.AppendLine($"- **{char.ToUpperInvariant(k[0])}{k[1..]}:** `{IntakeAdapters.OneLine(v, 200)}`");
        b.AppendLine($"- **Seen:** {(count is null ? "" : $"{count} time{(count == 1 ? "" : "s")}, ")}first {Stamp(firstSeen)}, last {Stamp(lastSeen)}"
            + (IntakeAdapters.Str(issue, "level") is { } lvl ? $" · level {lvl}" : ""));

        if (exceptions.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("### Exception");
            // Sentry lists a chain oldest first; the one raised last is what the reader meets first.
            foreach (var x in Enumerable.Reverse(exceptions))
            {
                b.AppendLine();
                b.AppendLine($"`{IntakeAdapters.OneLine($"{x.Type}: {x.Value}", 400)}`");
                var shown = x.Frames.Where(f => f.InApp).Reverse().Take(15).ToList();
                if (shown.Count == 0) shown = x.Frames.AsEnumerable().Reverse().Take(5).ToList();
                if (shown.Count == 0) continue;
                b.AppendLine();
                b.AppendLine("In-app frames, most recent call first:");
                b.AppendLine();
                foreach (var f in shown) b.AppendLine($"- `{f.File}{(f.Line is null ? "" : $":{f.Line}")}` in `{f.Function ?? "?"}`");
            }
        }

        var other = tags.Where(t => t.Key is not ("environment" or "transaction" or "route" or "url" or "step" or "release")).Take(20).ToList();
        if (other.Count > 0)
        {
            b.AppendLine();
            b.AppendLine("### Tags");
            b.AppendLine();
            foreach (var (k, v) in other) b.AppendLine($"- `{IntakeAdapters.OneLine(k, 60)}`: `{IntakeAdapters.OneLine(v, 200)}`");
        }

        return new IntakeReport(Name, IntakeChannels.Sentry, id!, state,
            IntakeAdapters.OneLine(where is not null && !title.Contains(where) ? $"{title} in {where}" : title, 200),
            b.ToString().TrimEnd(), SeverityOf(IntakeAdapters.Str(issue, "level")), lastSeen, count,
            release, IntakeAdapters.Str(issue, "permalink"), IsBug: true, IsTest: test, FirstSeen: firstSeen);
    }

    private static string Stamp(DateTime? t) => t is null ? "unknown" : t.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>The project's priority for Sentry's level: a fatal error is High, never Critical, which stays the owner's word.</summary>
    public static TicketPriority SeverityOf(string? level) => level?.ToLowerInvariant() switch
    {
        "fatal" => TicketPriority.High,
        "warning" or "info" or "debug" => TicketPriority.Low,
        _ => TicketPriority.Normal,
    };

    private static Dictionary<string, string> Tags(JsonElement ev)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var t = IntakeAdapters.Obj(ev, "tags");
        if (t.ValueKind == JsonValueKind.Array)
            foreach (var e in t.EnumerateArray())
            {
                // The API's [{ key, value }], and the raw event's [[key, value]].
                if (e.ValueKind == JsonValueKind.Object && IntakeAdapters.Str(e, "key") is { } k && IntakeAdapters.Str(e, "value") is { } v) tags[k] = v;
                else if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 2 && e[0].ValueKind == JsonValueKind.String)
                    tags[e[0].GetString()!] = e[1].ValueKind == JsonValueKind.String ? e[1].GetString()! : e[1].GetRawText();
            }
        else if (t.ValueKind == JsonValueKind.Object)
            foreach (var p in t.EnumerateObject()) tags[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
        return tags;
    }

    private static string? ReleaseOf(JsonElement ev)
    {
        var r = IntakeAdapters.Obj(ev, "release");
        return r.ValueKind switch
        {
            JsonValueKind.String => r.GetString(),
            JsonValueKind.Object => IntakeAdapters.Str(r, "version") ?? IntakeAdapters.Str(r, "shortVersion"),
            _ => null,
        };
    }

    private sealed record Frame(string File, int? Line, string? Function, bool InApp);
    private sealed record Thrown(string Type, string Value, List<Frame> Frames);

    private static List<Thrown> Exceptions(JsonElement ev)
    {
        JsonElement values = default;
        var entries = IntakeAdapters.Obj(ev, "entries");
        if (entries.ValueKind == JsonValueKind.Array)
            foreach (var e in entries.EnumerateArray())
                if (IntakeAdapters.Str(e, "type") == "exception") values = IntakeAdapters.Obj(IntakeAdapters.Obj(e, "data"), "values");
        if (values.ValueKind != JsonValueKind.Array) values = IntakeAdapters.Obj(IntakeAdapters.Obj(ev, "exception"), "values");
        var list = new List<Thrown>();
        if (values.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in values.EnumerateArray())
        {
            var frames = new List<Frame>();
            var fs = IntakeAdapters.Obj(IntakeAdapters.Obj(v, "stacktrace"), "frames");
            if (fs.ValueKind == JsonValueKind.Array)
                foreach (var f in fs.EnumerateArray())
                {
                    var file = IntakeAdapters.Str(f, "filename") ?? IntakeAdapters.Str(f, "absPath") ?? IntakeAdapters.Str(f, "abs_path")
                        ?? IntakeAdapters.Str(f, "module") ?? "?";
                    var line = int.TryParse(IntakeAdapters.Str(f, "lineNo") ?? IntakeAdapters.Str(f, "lineno"), out var ln) ? ln : (int?)null;
                    var inApp = (IntakeAdapters.Str(f, "inApp") ?? IntakeAdapters.Str(f, "in_app")) == "true";
                    frames.Add(new Frame(file, line, IntakeAdapters.Str(f, "function"), inApp));
                }
            list.Add(new Thrown(IntakeAdapters.Str(v, "type") ?? "Error", IntakeAdapters.Str(v, "value") ?? "", frames));
        }
        return list;
    }
}

/// <summary>
/// A cloud alarm's state change, as CloudWatch publishes it to SNS and SNS delivers it to SQS: the
/// body is SNS's notification envelope whose <c>Message</c> is the alarm JSON. With raw message
/// delivery switched on the body is the alarm JSON itself, and that is read too.
/// </summary>
public sealed class CloudWatchAlarmAdapter : IIntakeAdapter
{
    public string Name => "cloudwatch-alarm";

    public IntakeReport? TryRead(IntakeEnvelope m)
    {
        if (!LooksLikeJson(m.Body)) return null;
        using var outer = JsonDocument.Parse(m.Body);
        var root = outer.RootElement;
        string? alarmJson = null;
        if (IntakeAdapters.Str(root, "Type") == "Notification" && IntakeAdapters.Str(root, "Message") is { } msg && LooksLikeJson(msg))
            alarmJson = msg;
        else if (IntakeAdapters.Str(root, "AlarmName") is not null && IntakeAdapters.Str(root, "NewStateValue") is not null)
            alarmJson = m.Body;
        if (alarmJson is null) return null;

        using var inner = JsonDocument.Parse(alarmJson);
        var a = inner.RootElement;
        var name = IntakeAdapters.Str(a, "AlarmName");
        var value = IntakeAdapters.Str(a, "NewStateValue");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) return null;

        var state = value.ToUpperInvariant() switch
        {
            "ALARM" => IntakeStates.Firing,
            "OK" => IntakeStates.Cleared,
            _ => IntakeStates.Unknown,
        };
        var at = IntakeAdapters.Time(IntakeAdapters.Str(a, "StateChangeTime")) ?? IntakeAdapters.Time(IntakeAdapters.Str(root, "Timestamp"));
        var reason = IntakeAdapters.Str(a, "NewStateReason");
        var description = IntakeAdapters.Str(a, "AlarmDescription");
        var trigger = IntakeAdapters.Obj(a, "Trigger");

        var b = new StringBuilder();
        b.AppendLine($"- **What happened:** the alarm `{IntakeAdapters.OneLine(name, 255)}` went from {IntakeAdapters.Str(a, "OldStateValue") ?? "?"} to {value}"
            + (at is null ? "." : $" at {at.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}."));
        if (!string.IsNullOrWhiteSpace(description)) b.AppendLine($"- **What it watches:** {IntakeAdapters.OneLine(description, 1000)}");
        if (!string.IsNullOrWhiteSpace(reason)) b.AppendLine($"- **Why:** {IntakeAdapters.OneLine(reason, 1000)}");
        if (trigger.ValueKind == JsonValueKind.Object)
        {
            var metric = IntakeAdapters.Str(trigger, "MetricName");
            var ns = IntakeAdapters.Str(trigger, "Namespace");
            var stat = IntakeAdapters.Str(trigger, "Statistic") ?? IntakeAdapters.Str(trigger, "ExtendedStatistic");
            var dims = IntakeAdapters.Obj(trigger, "Dimensions");
            var dimText = dims.ValueKind == JsonValueKind.Array
                ? string.Join(", ", dims.EnumerateArray().Select(d => $"{IntakeAdapters.Str(d, "name")}={IntakeAdapters.Str(d, "value")}"))
                : "";
            b.AppendLine($"- **Threshold:** {stat ?? "?"} of `{ns}/{metric}`{(dimText.Length > 0 ? $" ({dimText})" : "")} "
                + $"{Comparison(IntakeAdapters.Str(trigger, "ComparisonOperator"))} {IntakeAdapters.Str(trigger, "Threshold") ?? "?"}, "
                + $"over {IntakeAdapters.Str(trigger, "EvaluationPeriods") ?? "?"} period(s) of {IntakeAdapters.Str(trigger, "Period") ?? "?"}s");
        }
        if (IntakeAdapters.Str(a, "Region") is { } region) b.AppendLine($"- **Region:** {region}");

        return new IntakeReport(Name, IntakeChannels.CloudWatchAlarm, name!, state,
            IntakeAdapters.OneLine($"Alarm {name}" + (string.IsNullOrWhiteSpace(description) ? "" : $": {description}"), 200),
            b.ToString().TrimEnd(), TicketPriority.Normal, at, null, null, OwnerLinkOf(a), IsBug: false);
    }

    private static bool LooksLikeJson(string s) => s.TrimStart().StartsWith('{');

    private static string Comparison(string? op) => op switch
    {
        "GreaterThanThreshold" => ">",
        "GreaterThanOrEqualToThreshold" => ">=",
        "LessThanThreshold" => "<",
        "LessThanOrEqualToThreshold" => "<=",
        _ => op ?? "?",
    };

    /// <summary>The alarm in the console, built from its ARN, for the owner. The board never opens it.</summary>
    private static string? OwnerLinkOf(JsonElement a)
    {
        var arn = IntakeAdapters.Str(a, "AlarmArn");
        var parts = arn?.Split(':');
        if (parts is not { Length: >= 7 } || parts[2] != "cloudwatch") return null;
        return $"https://{parts[3]}.console.aws.amazon.com/cloudwatch/home?region={parts[3]}#alarmsV2:alarm/{Uri.EscapeDataString(string.Join(':', parts[6..]))}";
    }
}
