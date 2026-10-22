using System.Text.Json;

namespace Whetstone.Services;

/// <summary>One rate-limit window, in the same shape the Claude meter serves.</summary>
public sealed record CodexLimit(string Kind, string Label, double Percent, DateTimeOffset? ResetsAt, bool IsActive);

/// <summary>What the Codex meter knows: its windows, and when Codex last reported them.</summary>
/// <param name="Status"><c>ok</c>, <c>none</c> (no report seen yet) or <c>unsupported</c> (no role runs on codex).</param>
public sealed record CodexUsage(string Status, string? Message, DateTimeOffset? CapturedAt, string? Plan, IReadOnlyList<CodexLimit> Limits);

/// <summary>
/// Codex's rate-limit headroom, read from what Codex itself writes down.
/// <para>
/// Unlike Claude's there is nothing to poll. Codex records its windows (<c>rate_limits</c> on a
/// <c>token_count</c> event: <c>used_percent</c>, <c>window_minutes</c>, <c>resets_at</c>) in each
/// session's log, <c>sessions/YYYY/MM/DD/rollout-*.jsonl</c> under its home, and only while a session
/// runs - so the meter is the last report, with the time it was made, not a live reading. Its
/// headless <c>--json</c> output stopped carrying these fields, which is why the logs are read.
/// </para>
/// <para>
/// Two places hold one: <c>run/usage-codex.json</c>, which a worker copies out of its run's own
/// throwaway Codex home before that is deleted (<c>Save-CodexUsage</c>, heartbeat-common.ps1); and the
/// user's own Codex home, which the dashboard's own Codex runs and any interactive use write to. The
/// newer of the two wins. A QA copy reads only the first: the user's home is outside its data folder.
/// </para>
/// <para>
/// Only a ChatGPT login has these windows. On an API key Codex records none, and the meter says so.
/// </para>
/// </summary>
public class CodexUsageService
{
    private readonly DashboardPaths _paths;
    private readonly AgentProviderFactory _providers;
    private readonly QaIsolation _isolation;
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _userHome;

    public CodexUsageService(DashboardPaths paths, AgentProviderFactory providers, QaIsolation? isolation = null,
        Func<DateTimeOffset>? now = null, string? userHome = null)
    {
        _paths = paths;
        _providers = providers;
        _isolation = isolation ?? QaIsolation.Off;
        _now = now ?? (() => DateTimeOffset.Now);
        _userHome = userHome ?? DefaultUserHome();
    }

    /// <summary>Where Codex keeps its home when nothing redirects it - Get-CodexUserHome in providers.ps1.</summary>
    private static string DefaultUserHome() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public string CaptureFile => Path.Combine(_paths.RunDir, "usage-codex.json");

    public CodexUsage Current()
    {
        if (!_providers.UsesProvider("codex"))
            return new CodexUsage("unsupported", "No role runs on Codex.", null, null, Array.Empty<CodexLimit>());

        var best = ReadCapture();
        if (!_isolation.On && ReadNewestSession() is { } fromHome && (best is null || fromHome.At > best.Value.At))
            best = fromHome;

        if (best is not { } found || ToLimits(found.Limits, found.At) is not { Count: > 0 } limits)
            return new CodexUsage("none",
                "No Codex usage reported yet. Codex writes its limits down while a session runs, and only on a ChatGPT login.",
                best?.At, null, Array.Empty<CodexLimit>());

        return new CodexUsage("ok", null, found.At, Text(found.Limits, "plan_type"), limits);
    }

    // ---- Reading ----------------------------------------------------------------

    private (DateTimeOffset At, JsonElement Limits)? ReadCapture()
    {
        try
        {
            if (!File.Exists(CaptureFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CaptureFile));
            var root = doc.RootElement;
            if (!root.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object) return null;
            var at = root.TryGetProperty("capturedAt", out var c) && c.ValueKind == JsonValueKind.String
                     && DateTimeOffset.TryParse(c.GetString(), out var parsed)
                ? parsed : new DateTimeOffset(File.GetLastWriteTime(CaptureFile));
            return (at, rl.Clone());
        }
        catch { return null; }   // a half-written capture is read again on the next poll
    }

    /// <summary>The last report in the newest session log under the user's Codex home.</summary>
    private (DateTimeOffset At, JsonElement Limits)? ReadNewestSession()
    {
        if (_userHome is null) return null;
        var sessions = Path.Combine(_userHome, "sessions");
        try
        {
            if (!Directory.Exists(sessions)) return null;
            // Newest few files only: a long-used home holds thousands, and the latest report is in
            // one of the last ones written.
            var files = new DirectoryInfo(sessions).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(5);
            foreach (var file in files)
                if (LastReport(file.FullName) is { } found) return found;
        }
        catch { /* unreadable home: the meter falls back to the capture, or to "none" */ }
        return null;
    }

    /// <summary>
    /// The last line of a session log that carries rate limits, read from the end.
    /// </summary>
    internal static (DateTimeOffset At, JsonElement Limits)? LastReport(string path)
    {
        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch { return null; }

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (FindRateLimits(doc.RootElement) is not { } rl) continue;
                var at = doc.RootElement.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                         && DateTimeOffset.TryParse(ts.GetString(), out var parsed)
                    ? parsed : new DateTimeOffset(File.GetLastWriteTime(path));
                return (at, rl.Clone());
            }
            catch (JsonException) { /* a line still being written */ }
        }
        return null;
    }

    /// <summary>
    /// The <c>rate_limits</c> object wherever it sits in an event. Searched rather than addressed: the
    /// log wraps an event in a payload, and that wrapping has moved between Codex versions.
    /// </summary>
    private static JsonElement? FindRateLimits(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
        {
            if (p.NameEquals("rate_limits") && p.Value.ValueKind == JsonValueKind.Object) return p.Value;
            if (p.Value.ValueKind == JsonValueKind.Object && FindRateLimits(p.Value) is { } inner) return inner;
        }
        return null;
    }

    // ---- Shaping ----------------------------------------------------------------

    /// <summary>
    /// The primary and secondary windows as meter rows. A window whose reset has passed since the
    /// report has rolled over: it reads as empty rather than as the stale share of an old window.
    /// </summary>
    internal List<CodexLimit> ToLimits(JsonElement rl, DateTimeOffset reportedAt)
    {
        var limits = new List<CodexLimit>();
        foreach (var kind in new[] { "primary", "secondary" })
        {
            if (!rl.TryGetProperty(kind, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (Number(w, "used_percent") is not { } used) continue;

            DateTimeOffset? resets = Number(w, "resets_at") is { } unix
                ? DateTimeOffset.FromUnixTimeSeconds((long)unix).ToLocalTime()
                : Number(w, "resets_in_seconds") is { } secs ? reportedAt.AddSeconds(secs) : null;

            var rolled = resets is { } r && r <= _now();
            limits.Add(new CodexLimit(kind, WindowLabel(Number(w, "window_minutes"), kind),
                rolled ? 0 : used, rolled ? null : resets, !rolled));
        }
        return limits;
    }

    /// <summary>"5-hour", "Weekly" - named by the window's length, as Codex's own status line does.</summary>
    public static string WindowLabel(double? minutes, string kind)
    {
        if (minutes is not { } m || m <= 0) return kind == "primary" ? "Short window" : "Long window";
        if (Math.Abs(m - 10080) < 1) return "Weekly";
        if (m % 1440 == 0) return $"{m / 1440:0}-day";
        if (m % 60 == 0) return $"{m / 60:0}-hour";
        return $"{m:0}-minute";
    }

    private static double? Number(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Text(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
