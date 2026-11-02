using System.Threading.Channels;

namespace Whetstone.Services;

/// <summary>
/// Where a landed ticket waits for its delivered commits to be recorded and, if it is a bug, for its
/// fix to be blamed. A background reader so a merge or a move to Done never waits on <c>git blame</c>,
/// one ticket at a time so two traces never contend for the same repo.
/// </summary>
public sealed class BugTraceQueue : BackgroundService
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<int> _pending = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BugTraceQueue> _log;

    public BugTraceQueue(IServiceScopeFactory scopes, ILogger<BugTraceQueue> log) => (_scopes, _log) = (scopes, log);

    /// <summary>Queue a ticket that has just landed. A ticket already waiting is not queued twice.</summary>
    public void Enqueue(int ticketId)
    {
        lock (_pending) { if (!_pending.Add(ticketId)) return; }
        _channel.Writer.TryWrite(ticketId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            lock (_pending) _pending.Remove(id);
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<BugTraceService>();
                await svc.RecordDeliveredAsync(id, stoppingToken);
                await svc.TraceAsync(id, stoppingToken);   // a no-op unless the ticket is a bug
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Bug trace for ticket {Id} failed", id); }
        }
    }
}
