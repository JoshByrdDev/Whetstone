using System.Collections.Concurrent;

namespace Whetstone.Services.Intake;

/// <summary>What the poller last saw of one source, for the Intake page and the Setup check. In memory: it is about now.</summary>
public sealed record IntakeSourceState(
    DateTime? LastPollAt, DateTime? LastOkAt, string? LastError, IntakeQueueStatus? Queue, IntakeService.PassResult? LastPass);

/// <summary>The poller's last word on every source, by project and source name.</summary>
public sealed class IntakeStatus
{
    private readonly ConcurrentDictionary<string, IntakeSourceState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A queue holding more than this is backing up: messages arrive faster than they are filed, or polling stalled.</summary>
    public const int BackingUpAt = 100;

    public IntakeSourceState? Of(string project, string source) => _states.GetValueOrDefault($"{project}/{source}");

    public void Record(string project, string source, Func<IntakeSourceState?, IntakeSourceState> update) =>
        _states.AddOrUpdate($"{project}/{source}", _ => update(null), (_, old) => update(old));
}

/// <summary>
/// Intake's poller (docs/intake-plan.md, section 1): board code, in this process, reading each
/// enabled project's sources at their own interval. Never registered on a QA copy, which files
/// intake tickets only from the stand-in route - and IntakeTransports refuses a real queue there
/// too, in case something else asks.
/// </summary>
public sealed class IntakePoller(IServiceScopeFactory scopes, IIntakeSettings settings, IntakeTransports transports,
    IntakeStatus status, ILogger<IntakePoller> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // A little after start, so a restart's first seconds go to the board, not the queue.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            foreach (var project in settings.Projects)
            {
                IntakeConfig cfg;
                try { cfg = settings.For(project); }
                catch (Exception e) { log.LogWarning(e, "Intake: could not read {Project}'s config", project); continue; }
                if (!cfg.IsOn) continue;
                foreach (var source in cfg.Sources)
                {
                    var last = status.Of(project, source.Name)?.LastPollAt;
                    if (last is not null && DateTime.UtcNow - last < TimeSpan.FromSeconds(source.PollSeconds)) continue;
                    await PollOnceAsync(project, source, stop);
                }
            }
            try { await Task.Delay(Tick, stop); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over one source, recorded either way. Also what "poll now" runs.</summary>
    public async Task<IntakeSourceState> PollOnceAsync(string project, IntakeSource source, CancellationToken ct)
    {
        var at = DateTime.UtcNow;
        var (transport, refused) = transports.For(project, source);
        if (transport is null)
        {
            status.Record(project, source.Name, old => new IntakeSourceState(at, old?.LastOkAt, refused, old?.Queue, null));
            return status.Of(project, source.Name)!;
        }
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var intake = scope.ServiceProvider.GetRequiredService<IntakeService>();
            var pass = await intake.PollAsync(project, source, transport, ct: ct);
            IntakeQueueStatus? queue = null;
            try { queue = await transport.StatusAsync(ct); } catch (Exception e) when (e is not OperationCanceledException) { /* the pass is what matters */ }
            status.Record(project, source.Name, _ => new IntakeSourceState(at, at, pass.Error, queue, pass));
            if (pass.Received > 0)
                log.LogInformation("Intake {Project}/{Source}: {Received} received, {Handled} handled, {Redelivered} redelivered, {Failed} failed",
                    project, source.Name, pass.Received, pass.Handled, pass.Redelivered, pass.Failed);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("Intake {Project}/{Source}: {Error}", project, source.Name, e.Message);
            status.Record(project, source.Name, old => new IntakeSourceState(at, old?.LastOkAt, e.Message, old?.Queue, null));
        }
        finally
        {
            if (transport is IDisposable d) d.Dispose();
        }
        return status.Of(project, source.Name)!;
    }
}
