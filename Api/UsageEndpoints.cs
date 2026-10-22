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
        // ?account= signs in to a named account rather than the one in force - how a second account
        // gets a login before anything is switched to it.
        app.MapPost("/api/usage/signin", (string? account, ClaudeUsageService usage) =>
        {
            var (ok, message) = usage.LaunchSignIn(account);
            return Results.Ok(new { ok, message });
        });

        // Switch which Claude login the fleet spends. Its own endpoint rather than one more key on
        // the Settings form because it is not a setting somebody saves and forgets - it is the thing
        // you reach for when one subscription has run dry, and it has to undo what running dry did.
        app.MapPost("/api/usage/account", async (
            AccountSwitch body, ClaudeUsageService usage, DashboardPaths paths, CancellationToken ct) =>
        {
            if (ClaudeAccounts.Find(usage.Accounts, body.Name) is not { } account)
                return Results.BadRequest(new { error = $"No Claude account called '{body.Name}'." });

            var config = ConfigEndpoints.LoadObject(paths.GlobalConfigPath);
            var before = ConfigEndpoints.Snapshot(config);
            // The default is stored as an absence, so a config file that has never heard of accounts
            // and one that has been switched back are the same file.
            if (account.ConfigDir is null) config.Remove("claudeAccount");
            else config["claudeAccount"] = account.Name;
            ConfigEndpoints.SaveObject(paths.GlobalConfigPath, config, before);

            // A usage-limit pause belongs to the account that hit the limit. Left behind, it would
            // hold Claude's phases idle until the old account's reset - the wait the switch was made
            // to skip. Claude's alone: the other providers' accounts did not change. usage-limit.json
            // is the name from before pauses were per provider, when it was always Claude's.
            foreach (var name in new[] { "usage-limit-claude.json", "usage-limit.json" })
            {
                try { File.Delete(Path.Combine(paths.RunDir, name)); }
                catch (IOException) { /* the heartbeat is reading it; it re-checks on its next pass */ }
            }

            await usage.RefreshAsync(ct, force: true);
            return Results.Ok(new { account = usage.AccountName, status = usage.Current.Status });
        });

        app.MapGet("/api/usage", (ClaudeUsageService usage) =>
        {
            var s = usage.Current;
            return Results.Ok(new
            {
                status = s.Status,
                message = s.Message,
                fetchedAt = s.FetchedAt,
                // Whose numbers these are, and what else could be switched to. One name means there
                // is nothing to choose between, and the chip draws no picker.
                account = usage.AccountName,
                accounts = usage.Accounts.Select(a => a.Name),
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

/// <param name="Name">An account from <c>/api/usage</c>'s <c>accounts</c>.</param>
public record AccountSwitch(string? Name);
