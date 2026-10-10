namespace Whetstone.Services;

/// <summary>
/// Keeps <see cref="ClaudeUsageService"/> warm on a timer so every open tab reads one cached answer
/// instead of each of them calling Anthropic. That matters more here than for the stack probe: the
/// usage endpoint rate-limits callers, and a handful of browser tabs polling it directly is exactly
/// the traffic that earns a 429.
/// </summary>
public class ClaudeUsageProbe : BackgroundService
{
    /// <summary>
    /// Utilisation moves in whole percent over minutes, so a minute of staleness is invisible - and
    /// it keeps well clear of the endpoint's patience.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ClaudeUsageService _usage;
    private readonly ILogger<ClaudeUsageProbe> _log;

    public ClaudeUsageProbe(ClaudeUsageService usage, ILogger<ClaudeUsageProbe> log)
        => (_usage, _log) = (usage, log);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _usage.RefreshAsync(stoppingToken);
            }
            // Only a stop of the host is a reason to leave; an HttpClient timeout is also an
            // OperationCanceledException, and it is just a failed pass.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogDebug(ex, "Usage probe pass failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
