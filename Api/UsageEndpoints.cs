using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>
/// Subscription rate-limit headroom for the app bar. One GET, served from the cached poll — the
/// browser never talks to Anthropic and never sees a token.
/// </summary>
public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this WebApplication app)
    {
        // Always 200, even when the poll is failing: the shape carries its own status, and the chip
        // is meant to say "stale" or "sign in again" rather than disappear on an error code.
        // Poll now and answer with the result, for the Settings panel to call the moment a token is
        // saved. Without it the only feedback on a mistyped token is the header chip continuing to say
        // nothing useful for up to a minute, which reads as "saving did nothing".
        app.MapPost("/api/usage/check", async (ClaudeUsageService usage, CancellationToken ct) =>
        {
            await usage.RefreshAsync(ct, force: true);
            var s = usage.Current;
            return Results.Ok(new
            {
                ok = s.Status == "ok",
                status = s.Status,
                message = s.Message,
                // Enough to prove it is the right account's meter and not just a 200.
                summary = string.Join(", ", s.Limits.Select(l => $"{l.Label} {Math.Round(l.Percent)}%")),
            });
        });

        // Run the CLI once so it renews its own login, then poll. Driven by the chip's nudge button,
        // never by the timer: it spends a little of the budget it is measuring, so it stays a thing
        // the user asks for.
        app.MapPost("/api/usage/nudge", async (ClaudeUsageService usage, CancellationToken ct) =>
        {
            var ok = await usage.NudgeAsync(ct);
            var s = usage.Current;
            return Results.Ok(new
            {
                ok,
                status = s.Status,
                message = s.Message,
                summary = string.Join(", ", s.Limits.Select(l => $"{l.Label} {Math.Round(l.Percent)}%")),
            });
        });

        // What the sign-in banner reads. Separate from the meter's own status because they answer
        // different questions: this one is "will an agent run start", and it stays quiet for every
        // failure the dashboard can still repair by itself.
        app.MapGet("/api/usage/signin", (ClaudeUsageService usage) =>
        {
            var s = usage.InspectSignIn();
            return Results.Ok(new { needsSignIn = s.NeedsSignIn, message = s.Message, canLaunch = s.CanLaunch });
        });

        // Opens a real terminal on the dashboard's machine for the interactive OAuth flow. The last
        // resort, offered only once a renewal has actually been refused - see LaunchSignIn.
        app.MapPost("/api/usage/signin", (ClaudeUsageService usage) =>
        {
            var (ok, message) = usage.LaunchSignIn();
            return Results.Ok(new { ok, message });
        });

        app.MapGet("/api/usage", (ClaudeUsageService usage) =>
        {
            var s = usage.Current;
            return Results.Ok(new
            {
                status = s.Status,
                message = s.Message,
                fetchedAt = s.FetchedAt,
                limits = s.Limits.Select(l => new
                {
                    kind = l.Kind,
                    group = l.Group,
                    label = l.Label,
                    percent = l.Percent,
                    severity = l.Severity,
                    resetsAt = l.ResetsAt,
                    isActive = l.IsActive,
                }),
            });
        });
    }
}
