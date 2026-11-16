using Microsoft.EntityFrameworkCore;
using Whetstone.Models;
using Whetstone.Persistence;
using Whetstone.Services;
using Whetstone.Services.Intake;

namespace Whetstone.Api;

public record SetIntakeMutedRequest(bool Muted);

/// <summary>A message for the stand-in queue: what a project's outbox would carry.</summary>
public record IntakeStandInRequest(string Body, Dictionary<string, string>? Attributes = null, string? Source = null);

/// <summary>
/// Intake's routes (docs/intake-plan.md). The owner's alone: no MCP tool reaches intake, so no
/// worker or steward can mute a report, read a queue or see a source's credential -
/// <c>IntakeRouteTests.No_tool_reaches_intake</c> holds that. A QA copy refuses polling and the
/// test-connection route at the door (QaIsolation); the stand-in route is in this process's memory.
/// </summary>
public static class IntakeEndpoints
{
    public static void MapIntakeEndpoints(this WebApplication app)
    {
        // "Never file this again", from an intake ticket's drawer: mutes its issue and closes the
        // ticket, unless a run is working it.
        app.MapPost("/api/tickets/{id:int}/intake/mute", async (int id, IntakeService intake, CancellationToken ct) =>
            await intake.MuteFromTicketAsync(id, ct) is { } r
                ? Results.Ok(r)
                : Results.BadRequest(new { error = $"Ticket #{id} was not filed by intake, so there is no report to mute." }));

        // Muting and unmuting from the Intake page, by issue.
        app.MapPut("/api/intake/issues/{id:int}/muted", async (int id, SetIntakeMutedRequest req, IntakeService intake, CancellationToken ct) =>
            await intake.SetMutedAsync(id, req.Muted, ct) is { } issue
                ? Results.Ok(new { issue.Id, issue.Muted, issue.MutedAt })
                : Results.NotFound());

        // The projects that have intake set up at all: what decides whether the tab is shown.
        app.MapGet("/api/intake/projects", (ProjectRegistry registry, IIntakeSettings settings) =>
            Results.Ok(new
            {
                projects = registry.Projects
                    .Select(k => (Key: k, Cfg: settings.For(k)))
                    .Where(p => p.Cfg.Enabled || p.Cfg.Sources.Count > 0)
                    .Select(p => new { key = p.Key, displayName = registry.GetProjectConfig(p.Key).DisplayName, enabled = p.Cfg.IsOn })
                    .ToList(),
            }));

        // The Intake page: sources and whether each answers, every issue, the unrecognised messages,
        // and the last fortnight's counts (the message log's own retention).
        app.MapGet("/api/projects/{key}/intake", async (string key, ProjectRegistry registry, IIntakeSettings settings,
            IntakeStatus status, DashboardDbContext db, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(key)) return Results.NotFound();
            var cfg = settings.For(key);
            var lastBySource = await db.IntakeMessages.AsNoTracking().Where(m => m.ProjectKey == key)
                .GroupBy(m => m.Source).Select(g => new { Source = g.Key, At = g.Max(m => m.HandledAt) }).ToListAsync(ct);
            var issues = await db.IntakeIssues.AsNoTracking().Where(i => i.ProjectKey == key)
                .OrderByDescending(i => i.LastSeen).Take(500).ToListAsync(ct);
            var ticketIds = issues.Where(i => i.TicketId != null).Select(i => i.TicketId!.Value).ToList();
            var tickets = await db.Tickets.AsNoTracking().Where(t => ticketIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Title, Phase = t.Phase.ToString() }).ToDictionaryAsync(t => t.Id, ct);
            var messages = await db.IntakeMessages.AsNoTracking().Where(m => m.ProjectKey == key)
                .OrderByDescending(m => m.HandledAt).Take(2000).ToListAsync(ct);
            int Count(string outcome) => messages.Count(m => m.Outcome == outcome);
            var storms = await db.IntakeStorms.AsNoTracking().Where(s => s.ProjectKey == key)
                .OrderByDescending(s => s.CreatedAt).Take(50).ToListAsync(ct);
            var filedTickets = await db.Tickets.AsNoTracking()
                .Where(t => t.ProjectKey == key && t.Origin == TicketOriginKind.Intake).CountAsync(ct);

            return Results.Ok(new
            {
                enabled = cfg.Enabled,
                on = cfg.IsOn,
                problems = cfg.Problems,
                limits = cfg.Limits,
                cfg.Runbook,
                sources = cfg.Sources.Select(s =>
                {
                    var st = status.Of(key, s.Name);
                    return new
                    {
                        s.Name, s.Kind, s.QueueUrl, s.Region, s.PollSeconds,
                        lastPollAt = st?.LastPollAt, lastOkAt = st?.LastOkAt, lastError = st?.LastError,
                        waiting = st?.Queue?.Waiting, inFlight = st?.Queue?.InFlight,
                        backingUp = st?.Queue?.Waiting >= IntakeStatus.BackingUpAt,
                        lastReceivedAt = lastBySource.FirstOrDefault(x => x.Source == s.Name)?.At,
                    };
                }),
                counts = new
                {
                    days = (int)IntakeService.Keep.TotalDays,
                    received = messages.Count,
                    filed = Count(IntakeOutcomes.Filed),
                    noted = Count(IntakeOutcomes.Noted),
                    held = Count(IntakeOutcomes.Held),
                    muted = Count(IntakeOutcomes.Muted),
                    ignored = Count(IntakeOutcomes.Ignored),
                    tests = Count(IntakeOutcomes.Test),
                    unrecognised = Count(IntakeOutcomes.Unrecognised),
                    // Of the reports about a known problem, how many were repeats rather than new tickets.
                    repeatRatio = Count(IntakeOutcomes.Filed) + Count(IntakeOutcomes.Noted) is var n and > 0
                        ? Math.Round((double)Count(IntakeOutcomes.Noted) / n, 3) : (double?)null,
                    mutedIssues = issues.Count(i => i.Muted),
                    storms = storms.Count,
                    ticketsAllTime = filedTickets,
                },
                issues = issues.Select(i => new
                {
                    i.Id, i.Channel, i.Fingerprint, i.Title, i.Occurrences, i.ReportedCount, i.FirstSeen, i.LastSeen, i.LastState,
                    i.Muted, i.MutedAt, i.HeldIn, i.TicketId, i.TicketHistory,
                    ticket = i.TicketId is { } tid && tickets.TryGetValue(tid, out var t) ? t : null,
                }),
                storms = storms.Select(s => new { s.Window, s.TicketId, s.CreatedAt, name = IntakeTicketText.WindowName(s.Window) }),
                recent = messages.Take(50).Select(m => new { m.Id, m.Source, m.MessageId, m.HandledAt, m.Outcome, m.Adapter, m.Note, m.TicketId, m.IssueId }),
                unrecognised = messages.Where(m => m.Outcome == IntakeOutcomes.Unrecognised).Take(50)
                    .Select(m => new { m.Id, m.Source, m.MessageId, m.HandledAt, m.Note, m.RawBody, m.RawAttributes }),
            });
        });

        // The Settings page's source editor: the block as it is, the secret masked.
        app.MapGet("/api/projects/{key}/intake/config", (string key, ProjectRegistry registry, IIntakeSettings settings) =>
        {
            if (!registry.IsEnabled(key)) return Results.NotFound();
            var cfg = settings.For(key);
            return Results.Ok(new
            {
                cfg.Enabled, cfg.Problems, newPerHour = cfg.Limits.NewPerHour, newPerDay = cfg.Limits.NewPerDay, cfg.Runbook,
                sources = cfg.Sources.Select(s => new
                {
                    s.Name, s.Kind, s.QueueUrl, s.Region, s.AccessKeyId, s.PollSeconds, s.ServiceUrl,
                    secretSet = s.SecretAccessKey.Length > 0, secretMasked = s.MaskedSecret,
                }),
            });
        });

        // Saving it: into the machine-local project.json, never the committed defaults.
        app.MapPut("/api/projects/{key}/intake/config", (string key, IntakeConfigInput input, ProjectRegistry registry) =>
        {
            if (!registry.IsEnabled(key)) return Results.NotFound();
            var (saved, problems) = IntakeConfigWriter.Write(registry.Paths.ProjectConfig(key), input);
            return saved is null ? Results.BadRequest(new { error = string.Join(" ", problems), problems }) : Results.Ok(new { saved = true });
        });

        // "Test connection": the queue's attributes, and nothing else.
        app.MapPost("/api/projects/{key}/intake/sources/{name}/test", async (string key, string name, ProjectRegistry registry,
            IIntakeSettings settings, IntakeTransports transports, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(key)) return Results.NotFound();
            var source = settings.For(key).Sources.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (source is null) return Results.NotFound(new { error = $"{key} has no intake source named {name}." });
            var (transport, refused) = transports.For(key, source);
            if (transport is null) return Results.BadRequest(new { ok = false, error = refused });
            try
            {
                var q = await transport.StatusAsync(ct);
                return Results.Ok(new { ok = true, waiting = q.Waiting, inFlight = q.InFlight, retentionSeconds = q.RetentionSeconds });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Results.Ok(new { ok = false, error = e.Message });
            }
            finally { if (transport is IDisposable d) d.Dispose(); }
        });

        // "Poll now": one pass over every source of the project, rather than waiting for its interval.
        app.MapPost("/api/projects/{key}/intake/poll", async (string key, ProjectRegistry registry, IIntakeSettings settings,
            IntakePoller poller, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(key)) return Results.NotFound();
            var cfg = settings.For(key);
            if (!cfg.IsOn) return Results.BadRequest(new { error = $"{key} has no intake switched on." });
            var results = new List<object>();
            foreach (var s in cfg.Sources) results.Add(new { source = s.Name, state = await poller.PollOnceAsync(key, s, ct) });
            return Results.Ok(results);
        });

        // A message on a project's stand-in queue, and one pass over it: how a QA copy, or an owner
        // trying intake before the project's half exists, files an intake ticket with no queue at
        // all. Only a source of kind standin takes one, so nothing here can reach a real queue.
        app.MapPost("/api/projects/{key}/intake/standin", async (string key, IntakeStandInRequest req, ProjectRegistry registry,
            IIntakeSettings settings, IntakeTransports transports, IntakePoller poller, CancellationToken ct) =>
        {
            if (!registry.IsEnabled(key)) return Results.NotFound();
            var cfg = settings.For(key);
            var source = cfg.Sources.FirstOrDefault(s => s.IsStandIn
                && (req.Source is null || s.Name.Equals(req.Source, StringComparison.OrdinalIgnoreCase)));
            if (!cfg.IsOn || source is null)
                return Results.BadRequest(new { error = $"{key} has no stand-in intake source switched on: add one of kind \"standin\" to its intake." });
            if (string.IsNullOrWhiteSpace(req.Body)) return Results.BadRequest(new { error = "A message needs a body." });
            var sent = transports.StandIn(key, source.Name).Send(req.Body, req.Attributes ?? new());
            var state = await poller.PollOnceAsync(key, source, ct);
            return Results.Ok(new { messageId = sent.MessageId, state });
        });
    }
}
