namespace Whetstone.Services;

/// <summary>
/// Polls every configured project stack on a timer so the board can render a live dot without each
/// browser tab poking the stacks itself, and so the human lease is reconciled against reality even
/// when nobody is looking at the dashboard.
///
/// <para>
/// That second job is why this is a background service rather than work done inside the GET. A hold
/// taken by pressing Start blocks QA agents; it has to be given back when the stack goes away, and
/// "when the stack goes away" is not an event the dashboard is told about - the user stopping it
/// from Docker Desktop is the normal case. Polling here means the lease lapses within one interval
/// of the stack actually being gone, with the browser closed.
/// </para>
/// </summary>
public class StackProbeService : BackgroundService
{
    /// <summary>
    /// Fast enough that pressing Start elsewhere shows up almost immediately, slow enough to be
    /// invisible: one cheap HTTP GET per configured project.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(8);

    private readonly ProjectStackService _stacks;
    private readonly ILogger<StackProbeService> _log;

    public StackProbeService(ProjectStackService stacks, ILogger<StackProbeService> log)
        => (_stacks, _log) = (stacks, log);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _stacks.ProbeAllAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A bad config line or an unreachable repo path must not kill the loop - the next
                // pass is a second chance, and a dead prober would silently freeze every stack dot.
                _log.LogDebug(ex, "Stack probe pass failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
